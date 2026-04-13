using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlipSwitcher.Core;
using FlipSwitcher.Models;

namespace FlipSwitcher.Services;

/// <summary>
/// Service for enumerating and managing windows
/// </summary>
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
        "Windows.UI.Core.CoreWindow" // Core window inside ApplicationFrameWindow
    };

    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchUI",
        "LockApp",
        "TextInputHost",
        // Bluetooth / tray helper: keeps a visible HWND for message loop; user only interacts via tray icon
        "BlueTray"
    };

    private const int WPF_RESTORETOMAXIMIZED = 0x2;
    private const int MaxClassNameLength = 256;

    private record struct WindowInfo(string Title, string ClassName, uint ProcessId, string ProcessName);

    private static bool IsFullyTransparentLayered(IntPtr hWnd, long exStyle)
    {
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
            return false;
        if (!NativeMethods.GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags))
            return false;
        return (flags & NativeMethods.LWA_ALPHA) != 0 && alpha == 0;
    }

    private static bool RectanglesIntersect(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool WindowIntersectsAnyMonitor(NativeMethods.RECT windowRect, IReadOnlyList<NativeMethods.RECT> monitorRects)
    {
        foreach (var m in monitorRects)
        {
            if (RectanglesIntersect(windowRect, m))
                return true;
        }
        return false;
    }

    private static WindowInfo? TryGetWindowInfo(IntPtr hWnd, IntPtr shellWindow, uint currentProcessId,
        Dictionary<uint, string> processNameCache, IReadOnlyList<NativeMethods.RECT> monitorRects)
    {
        if (hWnd == shellWindow || !NativeMethods.IsWindowVisible(hWnd))
            return null;

        if (IsCloaked(hWnd))
            return null;

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

        // Tray / ghost hosts: still "visible" to the API but alpha is fully transparent
        if (IsFullyTransparentLayered(hWnd, exStyle))
            return null;

        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return null;

        // Filter windows with WS_EX_NOACTIVATE (non-activatable windows should not appear in task switcher)
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0 &&
            (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            return null;

        // Filter windows that are too small (skip check for minimized windows)
        if (!NativeMethods.IsIconic(hWnd) && !HasValidWindowSize(hWnd))
            return null;

        // Drop top-level windows parked entirely outside physical monitors (common tray app pattern)
        if (!NativeMethods.IsIconic(hWnd) && monitorRects.Count > 0 &&
            NativeMethods.GetWindowRect(hWnd, out var windowRect) &&
            !WindowIntersectsAnyMonitor(windowRect, monitorRects))
            return null;

        NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == currentProcessId)
            return null;

        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            // Walk the owner chain: if any owner in the chain is visible, this window is a
            // subordinate dialog (e.g. Environment Variables owned by System Properties) and
            // should be hidden. If all owners are invisible (e.g. a hidden launcher window),
            // the window itself is the "top" of the visible chain and should be shown.
            var current = owner;
            while (current != IntPtr.Zero)
            {
                if (NativeMethods.IsWindowVisible(current))
                    return null;
                current = NativeMethods.GetWindow(current, NativeMethods.GW_OWNER);
            }
        }

        var titleLength = NativeMethods.GetWindowTextLength(hWnd);
        if (titleLength == 0)
            return null;

        var titleBuilder = new StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString();
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var classBuilder = new StringBuilder(MaxClassNameLength);
        NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
        var className = classBuilder.ToString();
        if (ExcludedClassNames.Contains(className))
            return null;

        if (!processNameCache.TryGetValue(processId, out var processName))
        {
            processName = GetProcessName(processId);
            processNameCache[processId] = processName;
        }
        if (ExcludedProcessNames.Contains(processName))
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

    public List<AppWindow> GetWindows()
    {
        var windows = new List<AppWindow>();
        var shellWindow = NativeMethods.GetShellWindow();
        var currentProcessId = (uint)Environment.ProcessId;
        var processNameCache = new Dictionary<uint, string>();
        var elevationCache = new Dictionary<uint, bool>();

        var monitors = new List<IntPtr>();
        var monitorRects = new List<NativeMethods.RECT>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                monitors.Add(hMon);
                var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                    monitorRects.Add(mi.rcMonitor);
                else
                    monitorRects.Add(rect);
                return true;
            }, IntPtr.Zero);

        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            try
            {
                var info = TryGetWindowInfo(hWnd, shellWindow, currentProcessId, processNameCache, monitorRects);
                if (info is null)
                    return true;

                var (isMinimized, isMaximized) = GetWindowState(hWnd);
                var window = new AppWindow(hWnd, info.Value.Title, info.Value.ClassName,
                    info.Value.ProcessId, info.Value.ProcessName, isMinimized, isMaximized, monitors, elevationCache);
                windows.Add(window);
            }
            catch
            {
                // Skip windows that cause errors
            }

            return true;
        }, IntPtr.Zero);

        return windows;
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

    private const int MinWindowSize = 50; // Minimum window size threshold (both dimensions)
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

