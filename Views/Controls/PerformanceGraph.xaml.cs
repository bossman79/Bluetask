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
    public sealed partial class PerformanceGraph : UserControl
    {
        public PerformanceGraph()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.SizeChanged += OnSizeChanged;
        }

        // History of values (newest at end)
        public IList<double> History
        {
            get => (IList<double>)GetValue(HistoryProperty);
            set => SetValue(HistoryProperty, value);
        }
        public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
            nameof(History), typeof(IList<double>), typeof(PerformanceGraph),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        // Stroke (line color)
        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(PerformanceGraph),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        // Thickness of the stroke
        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }
        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(PerformanceGraph),
            new PropertyMetadata(2.0, OnVisualPropertyChanged));

        // Area fill under the line
        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(PerformanceGraph),
            new PropertyMetadata(null, OnVisualPropertyChanged));

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }
        public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
            nameof(FillOpacity), typeof(double), typeof(PerformanceGraph),
            new PropertyMetadata(0.18, OnVisualPropertyChanged));

        // Grid lines
        public Brush GridLineBrush
        {
            get => (Brush)GetValue(GridLineBrushProperty);
            set => SetValue(GridLineBrushProperty, value);
        }
        public static readonly DependencyProperty GridLineBrushProperty = DependencyProperty.Register(
            nameof(GridLineBrush), typeof(Brush), typeof(PerformanceGraph),
            new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)), OnVisualPropertyChanged));

        public double GridLineThickness
        {
            get => (double)GetValue(GridLineThicknessProperty);
            set => SetValue(GridLineThicknessProperty, value);
        }
        public static readonly DependencyProperty GridLineThicknessProperty = DependencyProperty.Register(
            nameof(GridLineThickness), typeof(double), typeof(PerformanceGraph),
            new PropertyMetadata(1.0, OnVisualPropertyChanged));

        public int HorizontalGridLines
        {
            get => (int)GetValue(HorizontalGridLinesProperty);
            set => SetValue(HorizontalGridLinesProperty, value);
        }
        public static readonly DependencyProperty HorizontalGridLinesProperty = DependencyProperty.Register(
            nameof(HorizontalGridLines), typeof(int), typeof(PerformanceGraph),
            new PropertyMetadata(4, OnVisualPropertyChanged));

        public int VerticalGridLines
        {
            get => (int)GetValue(VerticalGridLinesProperty);
            set => SetValue(VerticalGridLinesProperty, value);
        }
        public static readonly DependencyProperty VerticalGridLinesProperty = DependencyProperty.Register(
            nameof(VerticalGridLines), typeof(int), typeof(PerformanceGraph),
            new PropertyMetadata(6, OnVisualPropertyChanged));

        // Axis
        public double YMin
        {
            get => (double)GetValue(YMinProperty);
            set => SetValue(YMinProperty, value);
        }
        public static readonly DependencyProperty YMinProperty = DependencyProperty.Register(
            nameof(YMin), typeof(double), typeof(PerformanceGraph),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

        // If YMax <= 0, auto-scale to data (rounded to nice tick)
        public double YMax
        {
            get => (double)GetValue(YMaxProperty);
            set => SetValue(YMaxProperty, value);
        }
        public static readonly DependencyProperty YMaxProperty = DependencyProperty.Register(
            nameof(YMax), typeof(double), typeof(PerformanceGraph),
            new PropertyMetadata(100.0, OnVisualPropertyChanged));

        public bool ShowYAxisLabels
        {
            get => (bool)GetValue(ShowYAxisLabelsProperty);
            set => SetValue(ShowYAxisLabelsProperty, value);
        }
        public static readonly DependencyProperty ShowYAxisLabelsProperty = DependencyProperty.Register(
            nameof(ShowYAxisLabels), typeof(bool), typeof(PerformanceGraph),
            new PropertyMetadata(true, OnVisualPropertyChanged));

        public bool ShowXAxisGrid
        {
            get => (bool)GetValue(ShowXAxisGridProperty);
            set => SetValue(ShowXAxisGridProperty, value);
        }
        public static readonly DependencyProperty ShowXAxisGridProperty = DependencyProperty.Register(
            nameof(ShowXAxisGrid), typeof(bool), typeof(PerformanceGraph),
            new PropertyMetadata(true, OnVisualPropertyChanged));

        public bool ShowYAxisGrid
        {
            get => (bool)GetValue(ShowYAxisGridProperty);
            set => SetValue(ShowYAxisGridProperty, value);
        }
        public static readonly DependencyProperty ShowYAxisGridProperty = DependencyProperty.Register(
            nameof(ShowYAxisGrid), typeof(bool), typeof(PerformanceGraph),
            new PropertyMetadata(true, OnVisualPropertyChanged));

        // Unit suffix for labels (e.g., "%", "Mbps")
        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }
        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
            nameof(Unit), typeof(string), typeof(PerformanceGraph),
            new PropertyMetadata("%", OnVisualPropertyChanged));

        public Thickness GraphPadding
        {
            get => (Thickness)GetValue(GraphPaddingProperty);
            set => SetValue(GraphPaddingProperty, value);
        }
        public static readonly DependencyProperty GraphPaddingProperty = DependencyProperty.Register(
            nameof(GraphPadding), typeof(Thickness), typeof(PerformanceGraph),
            new PropertyMetadata(new Thickness(8, 8, 8, 8), OnVisualPropertyChanged));

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as PerformanceGraph;
            control?.ScheduleRender();
        }

        private bool _renderQueued;
        private void ScheduleRender()
        {
            if (_renderQueued) return;
            _renderQueued = true;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _renderQueued = false;
                try { Render(); } catch { }
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Render();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Render();
        }

        private void Render()
        {
            double width = Root.ActualWidth;
            double height = Root.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                // Fallback to desired size if not laid out yet
                width = double.IsNaN(this.Width) ? 320 : this.Width;
                height = double.IsNaN(this.Height) ? 180 : this.Height;
            }

            GridCanvas.Children.Clear();
            AxisLabelsCanvas.Children.Clear();

            // Compactness heuristics for small graphs
            bool compact = width < 360 || height < 160;

            var padding = GraphPadding;
            double leftPad = ShowYAxisLabels ? (compact ? 28 : 36) : 8;
            double rightPad = 8;
            double topPad = padding.Top;
            double bottomPad = ShowYAxisLabels ? (compact ? 12 : 16) : 8;

            double gx = leftPad;
            double gy = topPad;
            double gw = Math.Max(0, width - leftPad - rightPad);
            double gh = Math.Max(0, height - topPad - bottomPad);

            if (gw < 1 || gh < 1)
            {
                StrokePolyline.Points = new PointCollection();
                FillCanvas.Children.Clear();
                return;
            }

            // Determine Y range
            double ymin = YMin;
            double ymax = YMax;
            var hist = History;
            if ((hist == null || hist.Count == 0) && Stroke is SolidColorBrush sb)
            {
                StrokePolyline.Stroke = sb;
            }
            if (hist != null && hist.Count > 0 && (ymax <= 0 || double.IsNaN(ymax) || double.IsInfinity(ymax)))
            {
                double localMax = hist.Max();
                ymax = ChooseNiceMax(localMax);
            }
            if (ymax <= ymin) { ymax = ymin + 1.0; }

            // Draw grid
            if (ShowYAxisGrid && HorizontalGridLines > 0)
            {
                for (int i = 0; i <= HorizontalGridLines; i++)
                {
                    double t = (double)i / HorizontalGridLines;
                    double y = gy + (1.0 - t) * gh;
                    var line = new Line
                    {
                        X1 = gx,
                        X2 = gx + gw,
                        Y1 = y,
                        Y2 = y,
                        Stroke = GridLineBrush,
                        StrokeThickness = GridLineThickness
                    };
                    GridCanvas.Children.Add(line);

                    if (ShowYAxisLabels)
                    {
                        double v = ymin + (ymax - ymin) * t;
                        string label = FormatTick(v, Unit);
                        var tb = new TextBlock
                        {
                            Text = label,
                            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(compact ? (byte)0x88 : (byte)0xAA, 0xFF, 0xFF, 0xFF)),
                            FontSize = compact ? 10 : 12
                        };
                        AxisLabelsCanvas.Children.Add(tb);
                        // Position left of the grid area
                        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double tx = Math.Max(0, gx - (tb.DesiredSize.Width + 6));
                        Canvas.SetLeft(tb, tx);
                        Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
                    }
                }
            }

            if (ShowXAxisGrid && VerticalGridLines > 0)
            {
                for (int i = 1; i < VerticalGridLines; i++)
                {
                    double t = (double)i / VerticalGridLines;
                    double x = gx + (t * gw);
                    var line = new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = gy,
                        Y2 = gy + gh,
                        Stroke = GridLineBrush,
                        StrokeThickness = GridLineThickness
                    };
                    GridCanvas.Children.Add(line);
                }
            }

            // Plot data
            StrokePolyline.Stroke = Stroke ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            StrokePolyline.StrokeThickness = StrokeThickness;

            var points = new PointCollection();
            if (hist != null && hist.Count > 0)
            {
                int n = hist.Count;
                double stepX = n > 1 ? (gw / (n - 1)) : gw; // spread across width
                for (int i = 0; i < n; i++)
                {
                    double v = hist[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) v = ymin;
                    v = Math.Clamp(v, ymin, ymax);
                    double nx = gx + i * stepX;
                    double ny = gy + (1.0 - ((v - ymin) / (ymax - ymin))) * gh;
                    points.Add(new Point(nx, ny));
                }
            }
            StrokePolyline.Points = points;

            // Fill under the line
            FillCanvas.Children.Clear();
            if (points.Count >= 2 && Fill is Brush fillBrush)
            {
                var poly = new Polygon
                {
                    Fill = fillBrush,
                    Opacity = FillOpacity
                };
                var fillPts = new PointCollection();
                // Start at left-bottom
                fillPts.Add(new Point(gx, gy + gh));
                foreach (var p in points) fillPts.Add(p);
                // End at right-bottom
                fillPts.Add(new Point(gx + gw, gy + gh));
                poly.Points = fillPts;
                FillCanvas.Children.Add(poly);
            }
        }

        private static string FormatTick(double value, string unit)
        {
            try
            {
                if (string.Equals(unit, "%", StringComparison.Ordinal))
                {
                    return string.Format("{0:F0}%", value);
                }
                // For rates like Mbps, choose compact formatting
                if (string.Equals(unit, "Mbps", StringComparison.OrdinalIgnoreCase))
                {
                    if (value >= 1000.0) return string.Format("{0:F1} Gbps", value / 1000.0);
                    return string.Format("{0:F0} Mbps", value);
                }
                if (string.Equals(unit, "Kbps", StringComparison.OrdinalIgnoreCase))
                {
                    if (value >= 1000.0) return string.Format("{0:F1} Mbps", value / 1000.0);
                    return string.Format("{0:F0} Kbps", value);
                }
                // Default
                return string.Format("{0:F0} {1}", value, unit);
            }
            catch { return value.ToString("F0"); }
        }

        private static double ChooseNiceMax(double rawMax)
        {
            if (double.IsNaN(rawMax) || double.IsInfinity(rawMax) || rawMax <= 0) return 1.0;
            double[] steps = new[] { 1.0, 2.0, 5.0 };
            double pow10 = Math.Pow(10, Math.Floor(Math.Log10(rawMax)));
            foreach (var s in steps)
            {
                double cand = s * pow10;
                if (cand >= rawMax) return cand;
            }
            return 10.0 * pow10; // next decade
        }
    }
}


