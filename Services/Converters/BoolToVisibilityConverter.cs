using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Bluetask.Services.Converters
{
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; } = false;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                bool isTrue = value is bool b && b;
                if (Invert)
                    isTrue = !isTrue;
                return isTrue ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                return Visibility.Collapsed;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is Visibility visibility)
                {
                    bool result = visibility == Visibility.Visible;
                    return Invert ? !result : result;
                }
            }
            catch { }
            return false;
        }
    }
}
