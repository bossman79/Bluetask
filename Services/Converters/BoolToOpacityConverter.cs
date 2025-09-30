using System;
using Microsoft.UI.Xaml.Data;

namespace Bluetask.Services.Converters
{
    public sealed class BoolToOpacityConverter : IValueConverter
    {
        public double TrueValue { get; set; } = 0.55;
        public double FalseValue { get; set; } = 0.0;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                bool isTrue = value is bool b && b;
                if (parameter is string s && double.TryParse(s, out var parsed))
                {
                    return isTrue ? parsed : 0.0;
                }
                return isTrue ? TrueValue : FalseValue;
            }
            catch { return FalseValue; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is double d)
                {
                    return d > ((TrueValue + FalseValue) / 2.0);
                }
            }
            catch { }
            return false;
        }
    }
}


