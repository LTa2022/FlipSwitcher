using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using FlipSwitcher.Core;
using FlipSwitcher.Models;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for enumerating and managing windows.
/// </summary>
/// <remarks>
/// Performance notes (rev 2):
/// <list type="bullet">
///   <item>Process-name and elevation caches live for the lifetime of the service so
///         repeatedly opening the switcher doesn't re-query the same processes.</item>
///   <item>Filters are ordered cheap-first: visibility, cloak and style flags short-circuit
///         before any string allocation or process query.</item>
///   <item>Per-call delegates for <c>EnumWindows</c> / <c>EnumDisplayMonitors</c> are now
///         instance fields, so we don't allocate a new closure on every refresh.</item>
///   <item>Existing <see cref="AppWindow"/> instances are reused across refreshes when
///         the underlying HWND is unchanged. This preserves their async-loaded icon and
///         their elevation cache.</item>
/// </list>
/// </remarks>
public class WindowService
{
    private static readonly HashSet<string> ExcludedClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "Button",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "MssgrIMWindow",
        "SysShadow",
        "Xaml_WindowedPopupClass",
        "Windows.UI.Core.CoreWindow"
    };

    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchUI",
        "LockApp",
        "TextInputHost"
    };

    private const int WPF_RESTORETOMAXIMIZED = 0x2;
    private const int MaxClassNameLength = 256;

    private record struct WindowInfo(string Title, string ClassName, uint ProcessId, string ProcessName);

    // Long-lived caches reused across GetWindows() calls.
    private readonly Dictionary<uint, string> _processNameCache = new();
    private readonly Dictionary<uint, bool> _elevationCache = new();
    private readonly Dictionary<IntPtr, AppWindow> _windowInstanceCache = new();

    // Reused buffers / collections — avoids per-call allocation in the hot path.
    private readonly StringBuilder _titleBuilder = new(256);
    private readonly StringBuilder _classBuilder = new(MaxClassNameLength);
    private readonly List<IntPtr> _monitors = new();
    private readonly List<NativeMethods.RECT> _monitorRects = new();
    private readonly List<AppWindow> _windowsScratch = new();
    private readonly HashSet<IntPtr> _seenHandles = new();
    private readonly HashSet<uint> _seenProcessIds = new();

    // Stable delegate instances — Win32 callbacks pin these via the lifetime of the service.
    private readonly NativeMethods.MonitorEnumProc _monitorEnumProc;
    private readonly NativeMethods.EnumWindowsProc _enumWindowsProc;

    // Per-enumeration state passed to the Win32 callbacks. Set inside GetWindows() before EnumWindows().
    private IntPtr _shellWindow;
    private uint _currentProcessId;

    public WindowService()
    {
        _monitorEnumProc = MonitorEnumCallback;
        _enumWindowsProc = EnumWindowsCallback;
    }

    /// <summary>
    /// Returns true when a layered window is invisible to the user. See AppWindow rev1 for the
    /// full case discussion (transparent tray-only helper anchors, alpha=0, color-key only).
    /// </summary>
    private static bool IsFullyTransparentLayered(IntPtr hWnd, long exStyle)
    {
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
            return false;

        if (!NativeMethods.GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags))
            return false;

        if (flags == 0)
            return true;
        if ((flags & NativeMethods.LWA_ALPHA) != 0 && alpha == 0)
            return true;
        if ((flags & NativeMethods.LWA_ALPHA) == 0 && (flags & NativeMethods.LWA_COLORKEY) != 0)
            return true;

        return false;
    }

    private static bool RectanglesIntersect(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private bool WindowIntersectsAnyMonitor(NativeMethods.RECT windowRect)
    {
        for (int i = 0; i < _monitorRects.Count; i++)
        {
            if (RectanglesIntersect(windowRect, _monitorRects[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cheap, no-allocation predicate run before any string read or process query.
    /// </summary>
    private bool PassesCheapFilters(IntPtr hWnd)
    {
        if (hWnd == _shellWindow || !NativeMethods.IsWindowVisible(hWnd))
            return false;
        if (IsCloaked(hWnd))
            return false;

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

        if (IsFullyTransparentLayered(hWnd, exStyle))
            return false;

        bool isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;

        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && !isAppWindow)
            return false;

        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0 && !isAppWindow)
            return false;

        bool isIconic = NativeMethods.IsIconic(hWnd);

        if (!isIconic && !HasValidWindowSize(hWnd))
            return false;

        if (!isIconic && _monitorRects.Count > 0 &&
            NativeMethods.GetWindowRect(hWnd, out var windowRect) &&
            !WindowIntersectsAnyMonitor(windowRect))
            return false;

        // Ownership chain.
        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && !isAppWindow)
        {
            var current = owner;
            while (current != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(current))
                    return false;
                current = NativeMethods.GetWindow(current, NativeMethods.GW_OWNER);
            }
        }

        return true;
    }

    private WindowInfo? TryGetWindowInfo(IntPtr hWnd)
    {
        if (!PassesCheapFilters(hWnd))
            return null;

        // Title pulled before process query — windows with no title are noise (e.g. invisible
        // tooltip helpers with no caption) and we can drop them without paying for OpenProcess.
        var titleLength = NativeMethods.GetWindowTextLength(hWnd);
        if (titleLength == 0)
            return null;

        // Class name short-circuits known UI shells before process query.
        _classBuilder.Clear();
        _classBuilder.EnsureCapacity(MaxClassNameLength);
        NativeMethods.GetClassName(hWnd, _classBuilder, _classBuilder.Capacity);
        var className = _classBuilder.ToString();
        if (ExcludedClassNames.Contains(className))
            return null;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == _currentProcessId)
            return null;

        if (!_processNameCache.TryGetValue(processId, out var processName))
        {
            processName = GetProcessName(processId);
            _processNameCache[processId] = processName;
        }
        if (ExcludedProcessNames.Contains(processName))
            return null;

        // Now read the title (cheapest done after we know we'll keep the window).
        _titleBuilder.Clear();
        _titleBuilder.EnsureCapacity(titleLength + 1);
        NativeMethods.GetWindowText(hWnd, _titleBuilder, _titleBuilder.Capacity);
        var title = _titleBuilder.ToString();
        if (string.IsNullOrWhiteSpace(title))
            return null;

        return new WindowInfo(title, className, processId, processName);
    }

    private static (bool isMinimized, bool isMaximized) GetWindowState(IntPtr hWnd)
    {
        var isMinimized = NativeMethods.IsIconic(hWnd);
        var isMaximized = NativeMethods.IsZoomed(hWnd);

        if (isMinimized)
        {
            var placement = new NativeMethods.WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(placement);
            if (NativeMethods.GetWindowPlacement(hWnd, ref placement))
            {
                isMaximized = (placement.flags & WPF_RESTORETOMAXIMIZED) != 0;
            }
        }

        return (isMinimized, isMaximized);
    }

    private bool MonitorEnumCallback(IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
    {
        _monitors.Add(hMon);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            _monitorRects.Add(mi.rcMonitor);
        else
            _monitorRects.Add(rect);
        return true;
    }

    private bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var info = TryGetWindowInfo(hWnd);
            if (info is null)
                return true;

            _seenHandles.Add(hWnd);
            _seenProcessIds.Add(info.Value.ProcessId);

            // Reuse existing AppWindow instance if title/state hasn't changed.
            // This preserves the async-loaded Icon and avoids triggering UI rebinds.
            if (_windowInstanceCache.TryGetValue(hWnd, out var existing) &&
                existing.Title == info.Value.Title &&
                existing.ProcessName == info.Value.ProcessName)
            {
                var (existingMin, existingMax) = (existing.IsMinimized, existing.IsMaximized);
                var (curMin, curMax) = GetWindowState(hWnd);
                if (existingMin == curMin && existingMax == curMax)
                {
                    _windowsScratch.Add(existing);
                    return true;
                }
            }

            var (isMinimized, isMaximized) = GetWindowState(hWnd);
            var window = new AppWindow(hWnd, info.Value.Title, info.Value.ClassName,
                info.Value.ProcessId, info.Value.ProcessName, isMinimized, isMaximized,
                _monitors, _elevationCache);

            // Pre-populate icon synchronously if the global cache already knows it — avoids
            // the brief "icon flashes in" effect on subsequent activations.
            var exePath = IconCacheService.Instance.GetProcessPath(info.Value.ProcessId);
            if (!string.IsNullOrEmpty(exePath) &&
                IconCacheService.Instance.TryGetExeIcon(exePath, out var cachedIcon) &&
                cachedIcon != null)
            {
                window.TrySetCachedIcon(cachedIcon);
            }

            _windowInstanceCache[hWnd] = window;
            _windowsScratch.Add(window);
        }
        catch
        {
            // Skip windows that cause errors.
        }

        return true;
    }

    public List<AppWindow> GetWindows()
    {
        _shellWindow = NativeMethods.GetShellWindow();
        _currentProcessId = (uint)Environment.ProcessId;

        _monitors.Clear();
        _monitorRects.Clear();
        _windowsScratch.Clear();
        _seenHandles.Clear();
        _seenProcessIds.Clear();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _monitorEnumProc, IntPtr.Zero);
        NativeMethods.EnumWindows(_enumWindowsProc, IntPtr.Zero);

        // Drop instance-cache entries for windows that no longer exist (closed since last call).
        if (_windowInstanceCache.Count > _seenHandles.Count)
        {
            var stale = new List<IntPtr>(_windowInstanceCache.Count - _seenHandles.Count);
            foreach (var key in _windowInstanceCache.Keys)
            {
                if (!_seenHandles.Contains(key))
                    stale.Add(key);
            }
            foreach (var key in stale)
                _windowInstanceCache.Remove(key);
        }

        // Drop process-name / elevation entries for processes that no longer have any visible window.
        // Keeps the caches bounded across long sessions.
        if (_processNameCache.Count > _seenProcessIds.Count * 2)
        {
            var staleP = new List<uint>();
            foreach (var pid in _processNameCache.Keys)
            {
                if (!_seenProcessIds.Contains(pid))
                    staleP.Add(pid);
            }
            foreach (var pid in staleP)
            {
                _processNameCache.Remove(pid);
                _elevationCache.Remove(pid);
            }
            IconCacheService.Instance.TrimProcessCache(_seenProcessIds);
        }

        // Return a copy so callers can mutate freely without disturbing our scratch buffer.
        return new List<AppWindow>(_windowsScratch);
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            var result = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                out int cloakedState, sizeof(int));
            return result == 0 && cloakedState != 0;
        }
        catch
        {
            return false;
        }
    }

    private const int MinWindowSize = 50;
    /// <summary>
    /// When a window is collapsed to a title-bar strip (abnormal layout / user resize),
    /// <see cref="NativeMethods.GetWindowRect"/> height is often ~30–45px, below <see cref="MinWindowSize"/>.
    /// </summary>
    private const int MinCaptionStripHeight = 28;

    private static bool HasValidWindowSize(IntPtr hWnd)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(hWnd, out var rect))
                return false;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width >= MinWindowSize && height >= MinWindowSize)
                return true;

            var style = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
            var hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
            if (hasCaption && width >= MinWindowSize && height >= MinCaptionStripHeight)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string GetProcessName(uint processId)
    {
        var hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
            return "Unknown";
        try
        {
            var buffer = new StringBuilder(260);
            int size = buffer.Capacity;
            if (NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                return System.IO.Path.GetFileNameWithoutExtension(buffer.ToString());
            return "Unknown";
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }
}
