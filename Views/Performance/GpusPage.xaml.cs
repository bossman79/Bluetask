 using Bluetask.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Bluetask.Views.Performance
{
    public sealed partial class GpusPage : Page
    {
        public PerformanceViewModel? ViewModel { get; set; }

        public GpusPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is PerformanceViewModel viewModel)
            {
                ViewModel = viewModel;
                this.DataContext = ViewModel;
            }
        }
    }
}
