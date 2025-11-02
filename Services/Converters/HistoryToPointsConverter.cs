using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Bluetask.Services.Converters
{
    public class HistoryToPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not List<double> history || history.Count == 0) return new PointCollection();

            var points = new PointCollection();
            double width = 60;
            double height = 20;
            double max = history.Max() * 1.1;
            if (max == 0) max = 1;
            double stepX = width / (history.Count - 1);

            for (int i = 0; i < history.Count; i++)
            {
                double x = i * stepX;
                double y = height - (history[i] / max * height);
                points.Add(new Point(x, y));
            }
            return points;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
