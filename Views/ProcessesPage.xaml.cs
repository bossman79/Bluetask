using Microsoft.UI.Xaml.Controls;
using System;
using WinRT;
using Microsoft.UI.Xaml.Navigation;
using Bluetask.ViewModels;
using Microsoft.UI.Xaml;
using Bluetask.Models;
using Microsoft.UI.Xaml.Media;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Collections.Generic;

namespace Bluetask.Views
{
    public sealed partial class ProcessesPage : Page
    {
        public DashboardViewModel ViewModel { get; private set; }
        private readonly HashSet<ProcessModel> _subscribedParents = new();

        public ProcessesPage()
        {
            this.InitializeComponent();
            ViewModel = new DashboardViewModel();
            this.DataContext = ViewModel;
            this.Loaded += ProcessesPage_Loaded;
            this.Unloaded += ProcessesPage_Unloaded;
        }

        private void ProcessesPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Processes != null)
            {
                ViewModel.Processes.CollectionChanged += Processes_CollectionChanged;
                RefreshChildrenSubscriptions();
            }
            try
            {
                var sv = FindScrollViewer(processesList);
                if (sv != null)
                {
                    sv.ViewChanging += ScrollViewer_ViewChanging;
                    sv.ViewChanged += ScrollViewer_ViewChanged;
                }
            }
            catch { }
            
        }

        private void ProcessesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Processes != null)
            {
                ViewModel.Processes.CollectionChanged -= Processes_CollectionChanged;
            }
            try
            {
                var sv = FindScrollViewer(processesList);
                if (sv != null)
                {
                    sv.ViewChanging -= ScrollViewer_ViewChanging;
                    sv.ViewChanged -= ScrollViewer_ViewChanged;
                }
            }
            catch { }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            try { ViewModel?.Dispose(); } catch { }
            ViewModel = null!;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (ViewModel == null)
            {
                ViewModel = new DashboardViewModel();
                this.DataContext = ViewModel;
            }
        }

        private void Processes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Re-hook child collections only; no scroll pin logic anymore
            if (e.Action == NotifyCollectionChangedAction.Reset ||
                e.Action == NotifyCollectionChangedAction.Add ||
                e.Action == NotifyCollectionChangedAction.Remove ||
                e.Action == NotifyCollectionChangedAction.Replace ||
                e.Action == NotifyCollectionChangedAction.Move)
            {
                RefreshChildrenSubscriptions();
            }
        }

        private void ChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset ||
                e.Action == NotifyCollectionChangedAction.Add ||
                e.Action == NotifyCollectionChangedAction.Remove ||
                e.Action == NotifyCollectionChangedAction.Replace ||
                e.Action == NotifyCollectionChangedAction.Move)
            {
                // No scroll pin logic
            }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            try
            {
                if (root is ScrollViewer sv) return sv;
                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    var r = FindScrollViewer(child);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        private void ScrollViewer_ViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
        {
            try { if (ViewModel != null) ViewModel.SuppressSortDueToInteraction = true; } catch { }
        }

        private async void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            try
            {
                // Release suppression shortly after interaction ends
                await System.Threading.Tasks.Task.Delay(300);
                if (ViewModel != null) ViewModel.SuppressSortDueToInteraction = false;
            }
            catch { }
        }

        private void RefreshChildrenSubscriptions()
        {
            try
            {
                // Unsubscribe previous
                foreach (var parent in _subscribedParents)
                {
                    try { parent.Children.CollectionChanged -= ChildCollectionChanged; } catch { }
                }
                _subscribedParents.Clear();

                var vm = ViewModel;
                if (vm?.Processes == null) return;
                foreach (var parent in vm.Processes)
                {
                    try
                    {
                        parent.Children.CollectionChanged += ChildCollectionChanged;
                        _subscribedParents.Add(parent);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void PinProcessButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ProcessModel model)
            {
                ViewModel.TogglePin(model);
            }
        }

        private async void KillProcessButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ProcessModel model)
            {
                // If this is a parent with children, ask whether to kill the whole tree
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
            if (sender is Button btn && btn.CommandParameter is ProcessModel model)
            {
                if (ViewModel.OpenProcessLocationCommand.CanExecute(model))
                {
                    ViewModel.OpenProcessLocationCommand.Execute(model);
                }
            }
        }

        private void ToggleExpandButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ProcessModel model)
            {
                model.IsExpanded = !model.IsExpanded;
            }
        }

        private async void KillGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ProcessModel model)
            {
                // Confirm kill all with option to kill trees
                var dialog = new ContentDialog
                {
                    Title = "Kill all instances?",
                    Content = $"This will kill all {model.InstanceCount} instance(s) of '{model.Name}'.",
                    PrimaryButtonText = "Kill All",
                    SecondaryButtonText = "Kill Trees",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    ViewModel.KillGroup(model, false);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    ViewModel.KillGroup(model, true);
                }
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

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var query = (SearchBox?.Text ?? string.Empty).Trim();
                ViewModel.SearchQuery = query;
            }
            catch { }
        }

        // No auto-selection or scroll; view model filters VisibleProcesses
    }
}


