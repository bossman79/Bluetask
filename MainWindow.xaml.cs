using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Bluetask
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private AppWindow? _appWindow;

        public MainWindow()
        {
            this.InitializeComponent();
            // Navigate to the first page after the window is shown to improve perceived startup
            this.Activated += MainWindow_Activated;

            // Extend content into the title bar and set custom drag region
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(TitleBarDragRegion);

            // Disable maximize using AppWindow presenter and cache AppWindow for later
            InitializeAppWindow();

            
        }

        private bool _initialNavigated;
        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (_initialNavigated) return;
            _initialNavigated = true;
            try { ContentFrame.Navigate(typeof(Bluetask.Views.DashboardPage)); } catch { }
            try { this.Activated -= MainWindow_Activated; } catch { }
        }

        private void InitializeAppWindow()
        {
            try
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                _appWindow = AppWindow.GetFromWindowId(windowId);
                if (_appWindow != null)
                {
                    _appWindow.Resize(new SizeInt32(1200, 710));
                    // Set window/taskbar icon to our app icon (prefer .ico, fallback to .png)
                    try
                    {
                        var baseDir = AppContext.BaseDirectory;
                        var ico = Path.Combine(baseDir, "Assets", "SpeedIcon.ico");
                        var png = Path.Combine(baseDir, "Assets", "SpeedIcon.png");
                        if (File.Exists(ico)) _appWindow.SetIcon(ico);
                        else if (File.Exists(png)) _appWindow.SetIcon(png);
                    }
                    catch { }
                }
                if (_appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = true;
                    // Hide system title bar and border to prevent overlayed caption buttons
                    presenter.SetBorderAndTitleBar(false, false);
                }

                if (_appWindow != null && AppWindowTitleBar.IsCustomizationSupported())
                {
                    _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    // Ensure system-drawn areas are transparent to avoid white/gray lines
                    _appWindow.TitleBar.BackgroundColor = Colors.Transparent;
                    _appWindow.TitleBar.InactiveBackgroundColor = Colors.Transparent;
                    _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                    _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    _appWindow.TitleBar.ButtonForegroundColor = Colors.Transparent;
                    _appWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Transparent;
                    _appWindow.TitleBar.ForegroundColor = Colors.Transparent;
                }

                // Remove the 1px DWM accent/border line at the top on Windows 11
                TryRemoveDwmBorder(hWnd);

                // As a fallback, strip WS_CAPTION if any caption remnants are present
                TryRemoveWindowCaption(hWnd);
            }
            catch
            {
                // Best-effort; ignore if not available
            }
        }

        private static void TryRemoveDwmBorder(IntPtr windowHandle)
        {
            try
            {
                // Set DWMWA_BORDER_COLOR to DWM_COLOR_NONE to hide the border
                uint colorNone = DWM_COLOR_NONE;
                _ = DwmSetWindowAttribute(windowHandle, DWMWA_BORDER_COLOR, ref colorNone, Marshal.SizeOf<uint>());

                // Optional: enable dark mode to avoid light accents
                int enable = 1;
                _ = DwmSetWindowAttribute(windowHandle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enable, Marshal.SizeOf<int>());

                // Explicitly clear the caption color if supported
                uint captionNone = DWM_COLOR_NONE;
                _ = DwmSetWindowAttribute(windowHandle, DWMWA_CAPTION_COLOR, ref captionNone, Marshal.SizeOf<uint>());
            }
            catch
            {
                // ignore
            }
        }

        private static void TryRemoveWindowCaption(IntPtr hWnd)
        {
            try
            {
                var style = GetWindowLong(hWnd, GWL_STYLE);
                if ((style & WS_CAPTION) != 0)
                {
                    style &= ~WS_CAPTION;
                    SetWindowLong(hWnd, GWL_STYLE, style);
                    SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                }
            }
            catch
            {
                // ignore
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hWnd, SW_MINIMIZE);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

        // DWM interop to control border/caption colors
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35; // Windows 11
        private const uint DWM_COLOR_NONE = 0xFFFFFFFF;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        // Win32 to strip caption as a fallback
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, int uFlags);

        private void Sidebar_NavigationRequested(object sender, Controls.NavigationEventArgs e)
        {
            switch ((e.NavigationTarget ?? string.Empty))
            {
                case "Dashboard":
                    ContentFrame.Navigate(typeof(Bluetask.Views.DashboardPage));
                    break;
                case "Processes":
                    ContentFrame.Navigate(typeof(Bluetask.Views.ProcessesPage));
                    break;
                case "Performance":
                    ContentFrame.Navigate(typeof(Bluetask.Views.PerformancePage));
                    break;
                case "Settings":
                    ContentFrame.Navigate(typeof(Bluetask.Views.SettingsPage));
                    break;
            }
        }
    }

    
}
