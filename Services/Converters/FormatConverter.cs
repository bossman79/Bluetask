using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace Bluetask.Services.Converters
{
    public sealed class FormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (parameter is string fmt)
            {
                try
                {
                    // If the parameter contains a composite format placeholder, use string.Format
                    if (fmt.IndexOf('{') >= 0 && fmt.IndexOf('}') >= 0)
                    {
                        var s = value?.ToString();
                        if (string.IsNullOrWhiteSpace(s))
                        {
                            // When the source is empty, suppress the formatted wrapper entirely
                            // so patterns like " × {0}" don't render stray separators.
                            return string.Empty;
                        }
                        return string.Format(CultureInfo.CurrentCulture, fmt, s);
                    }

                    // Otherwise, treat it as a standard numeric format string (optionally with suffix)
                    // e.g. "F2" or "F0%" would already be covered by composite; keeping fallback simple
                    if (value is IFormattable formattable)
                    {
                        return formattable.ToString(fmt, CultureInfo.CurrentCulture);
                    }
                }
                catch { }
            }

            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public sealed class BoolToYesNoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is bool b) return b ? "Yes" : "No";
                if (value is bool?) return ((bool?)value) == true ? "Yes" : "No";
            }
            catch { }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Compares an integer value against a threshold; returns true when value > threshold
    public sealed class IntGreaterThanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                int v = System.Convert.ToInt32(value);
                int threshold = parameter != null ? System.Convert.ToInt32(parameter) : 0;
                return v > threshold;
            }
            catch { return false; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Returns true when integer value <= threshold
    public sealed class IntLessThanOrEqualConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                int v = System.Convert.ToInt32(value);
                int threshold = parameter != null ? System.Convert.ToInt32(parameter) : 0;
                return v <= threshold;
            }
            catch { return false; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Strips any trailing parenthetical e.g. "Disk 0 (C:)" -> "Disk 0"
    public sealed class StripParenthesesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                var s = value?.ToString() ?? string.Empty;
                int idx = s.IndexOf('(');
                if (idx > 0) return s.Substring(0, idx).TrimEnd();
                return s;
            }
            catch { return value?.ToString() ?? string.Empty; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


