using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Bluetask.ViewModels;

namespace Bluetask.Views
{
    public sealed partial class DisplayRecoveryPage : Page
    {
        public DisplayRecoveryViewModel ViewModel { get; }

        public DisplayRecoveryPage()
        {
            this.InitializeComponent();
            ViewModel = new DisplayRecoveryViewModel();
            this.DataContext = ViewModel;
        }

        private void KillApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is int processId)
            {
                ViewModel.KillApp(processId);
            }
        }
    }
}
