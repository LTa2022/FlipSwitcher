using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlipSwitcher
{
    public class DwmThumbnailViewer : FrameworkElement
    {
        private IntPtr _thumbnailHandle = IntPtr.Zero;
        private Window _parentWindow;

        // --- Dependency Property for Window Handle (HWND) ---
        public static readonly DependencyProperty TargetHwndProperty =
            DependencyProperty.Register(
                nameof(TargetHwnd),
                typeof(IntPtr),
                typeof(DwmThumbnailViewer),
                new PropertyMetadata(IntPtr.Zero, OnTargetHwndChanged));

        /// <summary>
        /// Gets or sets the Win32 Window Handle (HWND) of the target process window to view.
        /// </summary>
        public IntPtr TargetHwnd
        {
            get => (IntPtr)GetValue(TargetHwndProperty);
            set => SetValue(TargetHwndProperty, value);
        }

        public DwmThumbnailViewer()
        {
            // Hook layout changes to reposition the thumbnail frame dynamically
            this.SizeChanged += DwmThumbnailViewer_SizeChanged;
            this.Loaded += DwmThumbnailViewer_Loaded;
            this.Unloaded += DwmThumbnailViewer_Unloaded;
        }

        private void DwmThumbnailViewer_Loaded(object sender, RoutedEventArgs e)
        {
            _parentWindow = Window.GetWindow(this);
            if (_parentWindow != null)
            {
                // Must monitor window movement to update OS-level thumbnail bounds
                _parentWindow.LocationChanged += ParentWindow_LocationChanged;
            }
            InitializeThumbnail();
        }

        private void DwmThumbnailViewer_Unloaded(object sender, RoutedEventArgs e)
        {
            ReleaseThumbnail();
            if (_parentWindow != null)
            {
                _parentWindow.LocationChanged -= ParentWindow_LocationChanged;
            }
        }

        private static void OnTargetHwndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DwmThumbnailViewer viewer)
            {
                viewer.InitializeThumbnail();
            }
        }

        private void DwmThumbnailViewer_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumbnailPosition();
        private void ParentWindow_LocationChanged(object sender, EventArgs e) => UpdateThumbnailPosition();

        private void InitializeThumbnail()
        {
            ReleaseThumbnail();

            // Guard rails: Check if handle is assigned and control is in a valid visual window
            if (TargetHwnd == IntPtr.Zero || _parentWindow == null) return;

            // Get host WPF window HWND
            IntPtr destHwnd = new WindowInteropHelper(_parentWindow).Handle;
            if (destHwnd == IntPtr.Zero) return;

            // Register link directly with Windows Desktop Window Manager using the unique HWND
            if (DwmRegisterThumbnail(destHwnd, TargetHwnd, out _thumbnailHandle) == 0)
            {
                UpdateThumbnailPosition();
            }
        }

        private void UpdateThumbnailPosition()
        {
            if (_thumbnailHandle == IntPtr.Zero || _parentWindow == null) return;

            var presentationSource = PresentationSource.FromVisual(this);
            if (presentationSource == null) return;

            // Convert WPF Layout DIPs (Device Independent Pixels) to exact Windows Physical Pixels
            var transform = presentationSource.CompositionTarget.TransformToDevice;
            Point topLeft = this.TranslatePoint(new Point(0, 0), _parentWindow);
            Point bottomRight = this.TranslatePoint(new Point(this.ActualWidth, this.ActualHeight), _parentWindow);

            Point physicalTopLeft = transform.Transform(topLeft);
            Point physicalBottomRight = transform.Transform(bottomRight);

            DWM_THUMB_PROPERTIES props = new DWM_THUMB_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                opacity = 255, // 0 to 255 opacity
                fVisible = true,
                rcDestination = new RECT(
                    (int)Math.Round(physicalTopLeft.X),
                    (int)Math.Round(physicalTopLeft.Y),
                    (int)Math.Round(physicalBottomRight.X),
                    (int)Math.Round(physicalBottomRight.Y)
                )
            };

            DwmUpdateThumbnailProperties(_thumbnailHandle, ref props);
        }

        private void ReleaseThumbnail()
        {
            if (_thumbnailHandle != IntPtr.Zero)
            {
                DwmUnregisterThumbnail(_thumbnailHandle);
                _thumbnailHandle = IntPtr.Zero;
            }
        }

        // --- Layout System Hook ---
        protected override void OnRender(DrawingContext drawingContext)
        {
            // Draws a completely transparent bounds block so the element has a 
            // layout identity and won't compress to 0x0 size inside grids.
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, RenderSize.Width, RenderSize.Height));
            base.OnRender(drawingContext);
        }

        #region Win32 P/Invoke Interop
        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);
        [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMB_PROPERTIES props);

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMB_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
        }

        private const int DWM_TNP_RECTDESTINATION = 0x00000001;
        private const int DWM_TNP_OPACITY = 0x00000004;
        private const int DWM_TNP_VISIBLE = 0x00000008;
        #endregion
    }
}
