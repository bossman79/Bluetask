using Microsoft.UI.Xaml.Data;
using System;

namespace Bluetask.Services.Converters
{
    public sealed class PercentToScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                double v = 0.0;
                if (value is double d) v = d;
                else if (value is float f) v = f;
                else if (value != null) v = System.Convert.ToDouble(value);
                if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;
                if (v < 0) v = 0.0;
                if (v > 100.0) v = 100.0;
                return v / 100.0;
            }
            catch { return 0.0; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            try
            {
                double s = 0.0;
                if (value is double d) s = d;
                else if (value is float f) s = f;
                else if (value != null) s = System.Convert.ToDouble(value);
                return s * 100.0;
            }
            catch { return 0.0; }
        }
    }
}



