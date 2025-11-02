using Microsoft.UI.Xaml.Data;
using System;

namespace Bluetask.Services.Converters
{
    public class BoolToChevronConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isExpanded)
            {
                // Chevron down when expanded (E70D), chevron right when collapsed (E76C)
                return isExpanded ? "\uE70D" : "\uE76C";
            }
            return "\uE76C"; // Default to right chevron
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
