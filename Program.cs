using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Microsoft.UI.Dispatching;

namespace Bluetask
{
    internal static class Program
    {
        [DllImport("Microsoft.ui.xaml.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern void XamlCheckProcessRequirements();

        [STAThread]
        private static void Main(string[] args)
        {
            // Ensure WinAppSDK runtime is initialized for unpackaged runs
            try
            {
                Bootstrap.Initialize(0x00010005); // matches Microsoft.WindowsAppSDK version major/minor (1.5)
            }
            catch
            {
                // If Initialize fails, continue; dynamic dependency may already be loaded via framework
            }

            // Required for XAML hosting
            XamlCheckProcessRequirements();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}


