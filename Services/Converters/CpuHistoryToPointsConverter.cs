using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Bluetask.Services.Converters
{
    public class CpuHistoryToPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is not List<double> history || history.Count == 0) 
                    return new PointCollection();

                var points = new PointCollection();
                
                // Use larger dimensions for the main CPU chart
                double width = 600; // Adjusted for larger chart area
                double height = 300; // Adjusted for larger chart area
                double maxValue = 100.0; // CPU percentage max is always 100%
                
                // Add padding
                double padding = 10;
                width -= padding * 2;
                height -= padding * 2;
                
                if (history.Count < 2 || width <= 0 || height <= 0) 
                    return new PointCollection();
                
                double stepX = width / (history.Count - 1);

                for (int i = 0; i < history.Count; i++)
                {
                    try
                    {
                        var value_i = history[i];
                        
                        // Clamp values to valid range
                        if (double.IsNaN(value_i) || double.IsInfinity(value_i))
                            value_i = 0.0;
                        else
                            value_i = Math.Clamp(value_i, 0.0, maxValue);
                        
                        double x = i * stepX + padding;
                        // Invert Y coordinate and scale to chart height
                        double y = height - (value_i / maxValue * height) + padding;
                        
                        // Ensure coordinates are valid
                        if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsInfinity(x) && !double.IsInfinity(y))
                        {
                            points.Add(new Point(x, y));
                        }
                    }
                    catch
                    {
                        // Skip problematic points but continue processing
                        continue;
                    }
                }
                
                return points;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CpuHistoryToPointsConverter error: {ex.Message}");
                return new PointCollection();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
