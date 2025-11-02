 using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace Bluetask.Services.Converters
{
    // Maps PerformanceItemType to the corresponding accent brush.
    // Optional ConverterParameter variants:
    // - "Soft"        → returns accent color with ~19% alpha
    // - "SoftStrong"  → returns accent color with ~27% alpha
    public sealed class PerformanceTypeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                var resources = Application.Current?.Resources;
                if (resources == null)
                {
                    return new SolidColorBrush(Colors.Transparent);
                }

                string brushKey = value?.ToString() switch
                {
                    // value is typically Bluetask.ViewModels.PerformanceItemType
                    "Cpu" => "App.CpuAccent",
                    "Memory" => "App.RamAccent",
                    "Storage" => "App.StorageAccent",
                    "Network" => "App.NetworkAccent",
                    "Gpu" => "App.GpuAccent",
                    _ => "App.CpuAccent"
                };

                var baseBrush = resources[brushKey] as SolidColorBrush;
                if (baseBrush == null)
                {
                    return new SolidColorBrush(Colors.Transparent);
                }

                var color = baseBrush.Color;

                // Apply optional opacity variants for overlays
                string variant = parameter as string ?? string.Empty;
                if (string.Equals(variant, "Soft", StringComparison.OrdinalIgnoreCase))
                {
                    color = Color.FromArgb(0x30, color.R, color.G, color.B); // ~19%
                }
                else if (string.Equals(variant, "SoftStrong", StringComparison.OrdinalIgnoreCase))
                {
                    color = Color.FromArgb(0x44, color.R, color.G, color.B); // ~27%
                }

                return new SolidColorBrush(color);
            }
            catch
            {
                return new SolidColorBrush(Colors.Transparent);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


