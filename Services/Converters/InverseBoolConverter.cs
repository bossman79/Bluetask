using Microsoft.UI.Xaml.Data;
using System;

namespace Bluetask.Services.Converters
{
	public sealed class InverseBoolConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value is bool boolValue)
			{
				return !boolValue;
			}
			return true;
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			if (value is bool boolValue)
			{
				return !boolValue;
			}
			return false;
		}
	}
}


