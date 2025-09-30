using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Bluetask.ViewModels;
using Microsoft.UI.Xaml;
using Bluetask.Models;
using System;

namespace Bluetask.Views
{
    public sealed partial class PerformancePage : Page
    {
        public PerformanceViewModel ViewModel { get; private set; }

        public PerformancePage()
        {
            ViewModel = new PerformanceViewModel();
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }

        private void CpuCoreItems_Loaded(object sender, RoutedEventArgs e)
        {
            try { RecomputeCoreTileSizing(); } catch { }
        }

        private void CpuCoreContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try { RecomputeCoreTileSizing(); } catch { }
        }

        private void RecomputeCoreTileSizing()
        {
            try
            {
                if (CpuCoreItems == null) return;
                if (CpuCoreContainer == null) return;
                double availableW = Math.Max(0, CpuCoreContainer.ActualWidth - 4);
                double availableH = Math.Max(0, CpuCoreContainer.ActualHeight - 4);
                if (availableW <= 0 || availableH <= 0) return;
                int coreCount = ViewModel?.Cpu?.PhysicalCoreCount > 0 ? ViewModel.Cpu.PhysicalCoreCount : (ViewModel?.CpuCores?.Count ?? 0);
                if (coreCount <= 0) coreCount = 1;

                // Compute columns from width using a minimum tile width and horizontal gap
                double minTileW = 110.0;   // minimum comfortable width including bar and label
                double maxTileW = 180.0;
                double hGap = 20.0;        // matches item margin left+right (~10 each)
                double vGap = 18.0;        // matches item top+bottom (~6 + ~12)

                int columns = Math.Max(1, (int)Math.Floor((availableW + hGap) / (minTileW + hGap)));
                if (columns > coreCount) columns = coreCount;
                if (columns <= 0) columns = 1;

                int rows = Math.Max(1, (int)Math.Ceiling(coreCount / (double)columns));

                // Fit width exactly to columns, accounting for inter-item gaps
                double itemWidth = Math.Floor((availableW - (columns - 1) * hGap) / columns);
                if (double.IsNaN(itemWidth) || itemWidth <= 0) itemWidth = minTileW;
                itemWidth = Math.Max(88.0, Math.Min(maxTileW, itemWidth));

                // Fit height exactly to rows, accounting for inter-item gaps
                double itemHeight = Math.Floor((availableH - (rows - 1) * vGap) / rows);
                if (double.IsNaN(itemHeight) || itemHeight <= 0) itemHeight = 80.0;
                itemHeight = Math.Max(70.0, Math.Min(140.0, itemHeight));

                // Apply sizing and lock maximum columns so wrapping is stable
                if (CpuCoreItems.ItemsPanelRoot is Microsoft.UI.Xaml.Controls.WrapGrid wrap)
                {
                    wrap.ItemWidth = itemWidth;
                    wrap.ItemHeight = itemHeight;
                    wrap.MaximumRowsOrColumns = columns;
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
                ViewModel = new PerformanceViewModel();
                this.DataContext = ViewModel;
            }
        }

        private void DiskCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is StorageInfo disk)
                {
                    if (ViewModel != null)
                    {
                        ViewModel.SelectedDrive = disk;
                    }
                }
            }
            catch { }
        }

        private void GpuCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is GpuInfo gpu)
                {
                    if (ViewModel != null)
                    {
                        ViewModel.SelectedGpu = gpu;
                    }
                }
            }
            catch { }
        }
    }
}


