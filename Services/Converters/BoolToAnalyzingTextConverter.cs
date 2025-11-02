using Microsoft.UI.Xaml.Data;
using System;

namespace Bluetask.Services.Converters
{
	public sealed class BoolToAnalyzingTextConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value is bool isAnalyzing && isAnalyzing)
			{
				return "Please Wait...";
			}
			return "Analyze";
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotImplementedException();
		}
	}
}


