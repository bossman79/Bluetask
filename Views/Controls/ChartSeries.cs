using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;

namespace Bluetask.Views.Controls
{
	public sealed class ChartSeries
	{
		public IList<double> Values { get; set; } = new List<double>();
		public Brush? LineBrush { get; set; }
		public double LineThickness { get; set; } = 1.5;
		public Brush? FillBrush { get; set; }
		public double FillOpacity { get; set; } = 0.0;
	}
}


