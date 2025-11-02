 using Microsoft.UI.Xaml.Data;
using System;

namespace Bluetask.Services.Converters
{
    public sealed class BoolToExpandGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isExpanded)
            {
                return isExpanded ? "▾" : "▸";
            }
            return "▸";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is string s && s == "▾";
        }
    }
}