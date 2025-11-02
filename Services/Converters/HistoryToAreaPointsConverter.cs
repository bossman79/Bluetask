 using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Bluetask.Services.Converters
{
    public class HistoryToAreaPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var points = new PointCollection();
            if (value is not List<double> history || history.Count < 2)
            {
                // Return a flat line at the bottom if no data
                points.Add(new Point(0, 100));
                points.Add(new Point(600, 100));
                return points;
            }

            double width = 600;
            double height = 100;
            double max = 100; // Use a fixed max of 100 for percentage-based values
            double stepX = width / (history.Count - 1);

            // Start from bottom-left
            points.Add(new Point(0, height));

            for (int i = 0; i < history.Count; i++)
            {
                double x = i * stepX;
                double y = height - (history[i] / max * height);
                points.Add(new Point(x, y));
            }

            // End at bottom-right to close the shape
            points.Add(new Point(width, height));

            return points;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
