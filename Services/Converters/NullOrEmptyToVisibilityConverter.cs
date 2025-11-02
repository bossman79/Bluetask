using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Bluetask.Services.Converters
{
	public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			try
			{
				var s = value as string;
				return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
			}
			catch { return Visibility.Collapsed; }
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotSupportedException();
		}
	}
}




