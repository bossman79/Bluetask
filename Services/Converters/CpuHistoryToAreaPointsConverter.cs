using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace Bluetask.Services.Converters
{
    // Produces a closed polygon for CPU area-under-curve chart
    public class CpuHistoryToAreaPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is not List<double> history || history.Count == 0) 
                    return new PointCollection();

                double width = 600;    // match chart area width used in XAML
                double height = 300;   // match chart area height used in XAML
                double padding = 10;
                width -= padding * 2;
                height -= padding * 2;
                
                if (history.Count < 2 || width <= 0 || height <= 0) 
                    return new PointCollection();

                double maxValue = 100.0; // CPU percentage max is always 100%
                double stepX = width / (history.Count - 1);

                var points = new PointCollection();
                // start at baseline left
                points.Add(new Point(padding, height + padding));

                for (int i = 0; i < history.Count; i++)
                {
                    var v = history[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) 
                        v = 0.0;
                    v = Math.Clamp(v, 0.0, maxValue);
                    
                    double x = i * stepX + padding;
                    double y = height - (v / maxValue * height) + padding;
                    points.Add(new Point(x, y));
                }

                // end at baseline right
                points.Add(new Point(width + padding, height + padding));

                return points;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CpuHistoryToAreaPointsConverter error: {ex.Message}");
                return new PointCollection();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
