using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
 

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Bluetask
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            
            // Handle unhandled exceptions to prevent "Fault bucket, type 0" crashes
            this.UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // Log the exception (you can add logging here if needed)
            // For now, mark it as handled to prevent crash
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            // Log critical unhandled exceptions
            // This cannot be prevented from terminating the app if IsTerminating is true
            try
            {
                var exception = e.ExceptionObject as Exception;
                // Add logging here if needed
            }
            catch { }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            // Mark unobserved task exceptions as observed to prevent crash
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Closed += OnWindowClosed;
            m_window.Activate();

            // Kick off update check if enabled
            try
            {
                if (Services.SettingsService.UpdateAutoCheckOnLaunch)
                {
                    Services.UpdateService.Shared.Configure("bossman79", "Bluetask", false);
                    _ = Services.UpdateService.Shared.CheckForUpdatesAsync();
                }
            }
            catch { }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            try
            {
                // Shutdown SystemMonitorService to dispose hardware monitoring and performance counters
                Services.SystemMonitorService.Shared.Shutdown();
            }
            catch { }

            try
            {
                // Dispose DisplayRecoveryService if it was instantiated
                if (Services.DisplayRecoveryService.Shared != null)
                {
                    Services.DisplayRecoveryService.Shared.Dispose();
                }
            }
            catch { }

            try
            {
                // Give a moment for cleanup to complete before app fully exits
                System.Threading.Thread.Sleep(100);
            }
            catch { }
        }

        private Window? m_window;
    }
}
