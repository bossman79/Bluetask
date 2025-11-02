using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Bluetask.Views.Controls
{
	public sealed partial class PerformanceChart : UserControl
	{
		// Cached visuals to minimize UI element churn per frame
		private Polyline _linePolyline;
		private Polygon _fillPolygon;
		private double _lastGridWidth;
		private double _lastGridHeight;
		private int _lastGridVLines;
		private int _lastGridHLines;
		private Brush _lastGridLineBrush;
		private Thickness _lastPadding;
		private bool _gridInitialized;

		public PerformanceChart()
		{
			this.InitializeComponent();
			SizeChanged += OnSizeChanged;
		}

		public IList<double> Values
		{
			get { return (IList<double>)GetValue(ValuesProperty); }
			set { SetValue(ValuesProperty, value); }
		}

		public static readonly DependencyProperty ValuesProperty =
			DependencyProperty.Register(nameof(Values), typeof(IList<double>), typeof(PerformanceChart), new PropertyMetadata(null, OnValuesChanged));

		public Brush LineBrush
		{
			get { return (Brush)GetValue(LineBrushProperty); }
			set { SetValue(LineBrushProperty, value); }
		}

		public static readonly DependencyProperty LineBrushProperty =
			DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(PerformanceChart), new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x90, 0xF0)), OnStyleChanged));

		public double LineThickness
		{
			get { return (double)GetValue(LineThicknessProperty); }
			set { SetValue(LineThicknessProperty, value); }
		}

		public static readonly DependencyProperty LineThicknessProperty =
			DependencyProperty.Register(nameof(LineThickness), typeof(double), typeof(PerformanceChart), new PropertyMetadata(2.0, OnStyleChanged));

		public Brush GridLineBrush
		{
			get { return (Brush)GetValue(GridLineBrushProperty); }
			set { SetValue(GridLineBrushProperty, value); }
		}

		public static readonly DependencyProperty GridLineBrushProperty =
			DependencyProperty.Register(nameof(GridLineBrush), typeof(Brush), typeof(PerformanceChart), new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), OnStyleChanged));

		public int GridVerticalLines
		{
			get { return (int)GetValue(GridVerticalLinesProperty); }
			set { SetValue(GridVerticalLinesProperty, value); }
		}

		public static readonly DependencyProperty GridVerticalLinesProperty =
			DependencyProperty.Register(nameof(GridVerticalLines), typeof(int), typeof(PerformanceChart), new PropertyMetadata(6, OnStyleChanged));

		public int GridHorizontalLines
		{
			get { return (int)GetValue(GridHorizontalLinesProperty); }
			set { SetValue(GridHorizontalLinesProperty, value); }
		}

		public static readonly DependencyProperty GridHorizontalLinesProperty =
			DependencyProperty.Register(nameof(GridHorizontalLines), typeof(int), typeof(PerformanceChart), new PropertyMetadata(3, OnStyleChanged));

		public Brush FillBrush
		{
			get { return (Brush)GetValue(FillBrushProperty); }
			set { SetValue(FillBrushProperty, value); }
		}

		public static readonly DependencyProperty FillBrushProperty =
			DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(PerformanceChart), new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(0x19, 0x30, 0x90, 0xF0)), OnStyleChanged));

		public double FillOpacity
		{
			get { return (double)GetValue(FillOpacityProperty); }
			set { SetValue(FillOpacityProperty, value); }
		}

		public static readonly DependencyProperty FillOpacityProperty =
			DependencyProperty.Register(nameof(FillOpacity), typeof(double), typeof(PerformanceChart), new PropertyMetadata(0.18, OnStyleChanged));

		public double MaxValue
		{
			get { return (double)GetValue(MaxValueProperty); }
			set { SetValue(MaxValueProperty, value); }
		}

		public static readonly DependencyProperty MaxValueProperty =
			DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(PerformanceChart), new PropertyMetadata(100.0, OnStyleChanged));

		public Thickness ChartPadding
		{
			get { return (Thickness)GetValue(ChartPaddingProperty); }
			set { SetValue(ChartPaddingProperty, value); }
		}

		public static readonly DependencyProperty ChartPaddingProperty =
			DependencyProperty.Register(nameof(ChartPadding), typeof(Thickness), typeof(PerformanceChart), new PropertyMetadata(new Thickness(8), OnStyleChanged));

		private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (PerformanceChart)d;
			self.Redraw();
		}

		private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (PerformanceChart)d;
			self.Redraw();
		}

		private void OnSizeChanged(object sender, SizeChangedEventArgs e)
		{
			Redraw();
		}

		private void Redraw()
		{
			try
			{
				// Current size
				double width = ActualWidth;
				double height = ActualHeight;
				if (width <= 0 || height <= 0) return;

				// Ensure chart primitives exist and are attached once
				EnsureChartPrimitives();

				var pad = ChartPadding;
				double left = pad.Left, top = pad.Top, right = pad.Right, bottom = pad.Bottom;
				double plotWidth = Math.Max(0, width - left - right);
				double plotHeight = Math.Max(0, height - top - bottom);

				// Gridlines (Task Manager style) — rebuild only when size/style changes
				int vLines = Math.Max(0, GridVerticalLines);
				int hLines = Math.Max(0, GridHorizontalLines);
				bool gridChanged = !_gridInitialized
					|| !ReferenceEquals(_lastGridLineBrush, GridLineBrush)
					|| _lastGridWidth != width
					|| _lastGridHeight != height
					|| _lastGridVLines != vLines
					|| _lastGridHLines != hLines
					|| !_lastPadding.Equals(pad);
				if (gridChanged)
				{
					GridCanvas.Children.Clear();
					for (int i = 1; i <= vLines; i++)
					{
						double x = left + (plotWidth * i / (vLines + 1));
						var line = new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotHeight, Stroke = GridLineBrush, StrokeThickness = 1 };
						GridCanvas.Children.Add(line);
					}
					for (int i = 1; i <= hLines; i++)
					{
						double y = top + (plotHeight * i / (hLines + 1));
						var line = new Line { X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y, Stroke = GridLineBrush, StrokeThickness = 1 };
						GridCanvas.Children.Add(line);
					}
					_lastGridLineBrush = GridLineBrush;
					_lastGridWidth = width;
					_lastGridHeight = height;
					_lastGridVLines = vLines;
					_lastGridHLines = hLines;
					_lastPadding = pad;
					_gridInitialized = true;
				}

				var values = Values ?? Array.Empty<double>();
				int n = values.Count;
				if (n < 2) return;

				// Determine scale: if MaxValue <= 0, auto-scale to data max*1.1; else clamp to [0..MaxValue]
				double max = MaxValue;
				if (double.IsNaN(max) || max <= 0.0)
				{
					double vmax = 0.0;
					for (int i = 0; i < n; i++)
					{
						double v = values[i];
						if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
						if (v > vmax) vmax = v;
					}
					max = vmax <= 0 ? 1.0 : (vmax * 1.1);
				}

				var clamped = new double[n];
				for (int i = 0; i < n; i++)
				{
					double v = values[i];
					if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
					if (MaxValue > 0) v = Math.Clamp(v, 0.0, MaxValue);
					clamped[i] = v;
				}

				// X spacing fits full width of plot area
				double stepX = n > 1 ? (plotWidth / (n - 1)) : plotWidth;
				var pts = new PointCollection();
				var fillPts = new PointCollection();
				for (int i = 0; i < n; i++)
				{
					double x = left + i * stepX;
					double y = top + (plotHeight * (1.0 - (clamped[i] / max)));
					pts.Add(new Point(x, y));
					fillPts.Add(new Point(x, y));
				}
				// Close fill polygon down to bottom
				fillPts.Add(new Point(left + plotWidth, top + plotHeight));
				fillPts.Add(new Point(left, top + plotHeight));

				// Update visuals
				_linePolyline.Stroke = LineBrush;
				_linePolyline.StrokeThickness = LineThickness;
				_linePolyline.Points = pts;
				_fillPolygon.Fill = FillBrush;
				_fillPolygon.Opacity = Math.Max(0.0, Math.Min(1.0, FillOpacity));
				_fillPolygon.Points = fillPts;
			}
			catch { }
		}

		private void EnsureChartPrimitives()
		{
			if (_fillPolygon == null)
			{
				_fillPolygon = new Polygon { StrokeThickness = 0 };
				ChartCanvas.Children.Add(_fillPolygon);
			}
			if (_linePolyline == null)
			{
				_linePolyline = new Polyline { Fill = null };
				ChartCanvas.Children.Add(_linePolyline);
			}
		}
	}
}


