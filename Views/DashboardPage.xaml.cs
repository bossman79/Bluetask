using Microsoft.UI.Xaml.Controls;
using System;
using WinRT;
using Microsoft.UI.Xaml.Navigation;
using Bluetask.ViewModels;
using Microsoft.UI.Xaml;
using Bluetask.Models;

namespace Bluetask.Views
{
    public sealed partial class DashboardPage : Page
    {
        public DashboardViewModel ViewModel { get; private set; }

        public DashboardPage()
        {
            this.InitializeComponent();
            // Enable navigation caching for instant page switches
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            // Create ViewModel but don't start updates yet - let UI render first
            ViewModel = new DashboardViewModel(deferStart: true);
            this.DataContext = ViewModel;
            this.Loaded += DashboardPage_Loaded;
        }

        private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            this.Loaded -= DashboardPage_Loaded;
            // Start monitoring after minimal delay to let initial frame render
            await System.Threading.Tasks.Task.Delay(10);
            ViewModel?.StartMonitoring();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Don't dispose - page is cached and will be reused
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Ensure ViewModel is started when navigating back to cached page
            if (ViewModel != null && !ViewModel.IsMonitoring)
            {
                ViewModel.StartMonitoring();
            }
        }

        private async void KillProcessButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Bluetask.Models.ProcessModel model)
            {
                if (model.HasChildren)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Kill process tree?",
                        Content = $"'{model.Name}' has child processes. Do you want to kill the whole tree?",
                        PrimaryButtonText = "Kill Tree",
                        SecondaryButtonText = "Kill Parent Only",
                        CloseButtonText = "Cancel",
                        XamlRoot = this.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        ViewModel.KillProcessTree(model);
                    }
                    else if (result == ContentDialogResult.Secondary)
                    {
                        if (ViewModel.KillProcessCommand.CanExecute(model))
                        {
                            ViewModel.KillProcessCommand.Execute(model);
                        }
                    }
                }
                else
                {
                    if (ViewModel.KillProcessCommand.CanExecute(model))
                    {
                        ViewModel.KillProcessCommand.Execute(model);
                    }
                }
            }
        }

        private void OpenProcessLocationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Bluetask.Models.ProcessModel model)
            {
                if (ViewModel.OpenProcessLocationCommand.CanExecute(model))
                {
                    ViewModel.OpenProcessLocationCommand.Execute(model);
                }
            }
        }

        private void ToggleExpandButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Bluetask.Models.ProcessModel model)
            {
                model.IsExpanded = !model.IsExpanded;
            }
        }

        private void SortByName_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetSort(DashboardViewModel.ProcessSortColumn.Name);
        }

        private void SortByCpu_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetSort(DashboardViewModel.ProcessSortColumn.Cpu);
        }

        private void SortByRam_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetSort(DashboardViewModel.ProcessSortColumn.Ram);
        }

        private void SortByGpu_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetSort(DashboardViewModel.ProcessSortColumn.Gpu);
        }
    }

    public class ProcessTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? LeafTemplate { get; set; }
        public DataTemplate? ParentTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is Bluetask.Models.ProcessModel proc)
            {
                return proc.HasChildren
                    ? (ParentTemplate ?? LeafTemplate ?? base.SelectTemplateCore(item))
                    : (LeafTemplate ?? ParentTemplate ?? base.SelectTemplateCore(item));
            }
            var fallback = LeafTemplate ?? ParentTemplate;
            if (fallback != null)
            {
                return fallback;
            }
            return base.SelectTemplateCore(item);
        }
    }

}



