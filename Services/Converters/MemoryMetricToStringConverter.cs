using Microsoft.UI.Xaml.Data;
using System;
using Bluetask.Services;

namespace Bluetask.Services.Converters
{
    public class MemoryMetricToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is MemoryMetric metric)
            {
                switch (metric)
                {
                    case MemoryMetric.WorkingSet:
                        return "Working set (RAM in use now)";
                    case MemoryMetric.Private:
                        return "Private (RAM reserved just for the app)";
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
