using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using Microsoft.UI.Text;

namespace Bluetask.Services.Converters
{
    public sealed partial class ResponsivePerformanceGraph : UserControl
    {
        private List<double> _history = new();
        private string _accentColorKey = "App.CpuAccent";
        private double _maxValue = 100.0;
        private bool _showGrid = true;
        private bool _showFill = true;
        private readonly ModernPerformanceGraphConverter _converter = new();

        public List<double> History
        {
            get => _history;
            set
            {
                _history = value ?? new List<double>();
                UpdateGraph();
            }
        }

        public string AccentColorKey
        {
            get => _accentColorKey;
            set
            {
                _accentColorKey = value ?? "App.CpuAccent";
                UpdateGraph();
            }
        }

        public double MaxValue
        {
            get => _maxValue;
            set
            {
                _maxValue = Math.Max(1, value);
                UpdateGraph();
            }
        }

        public bool ShowGrid
        {
            get => _showGrid;
            set
            {
                _showGrid = value;
                UpdateGraph();
            }
        }

        public bool ShowFill
        {
            get => _showFill;
            set
            {
                _showFill = value;
                UpdateGraph();
            }
        }

        public ResponsivePerformanceGraph()
        {
            this.InitializeComponent();
            this.DefaultStyleKey = typeof(ResponsivePerformanceGraph);
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateGraph();
            UpdateLabels();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                // Update clip geometry to match new size
                if (ClipGeometry != null)
                {
                    ClipGeometry.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
                }
                
                UpdateGraph();
                UpdateLabels();
            }
        }

        private void UpdateGraph()
        {
            if (!IsLoaded || GraphPresenter == null) return;

            try
            {
                var actualWidth = ActualWidth;
                var actualHeight = ActualHeight;

                // Use minimum size if ActualSize is not available yet
                if (actualWidth <= 0) actualWidth = Width > 0 ? Width : 400;
                if (actualHeight <= 0) actualHeight = Height > 0 ? Height : 200;

                // Ensure minimum dimensions for readability
                actualWidth = Math.Max(100, actualWidth);
                actualHeight = Math.Max(50, actualHeight);

                var graphData = new ModernPerformanceGraphData
                {
                    History = History,
                    Width = actualWidth,
                    Height = actualHeight,
                    AccentColorKey = AccentColorKey,
                    MaxValue = MaxValue,
                    ShowGrid = ShowGrid,
                    ShowFill = ShowFill
                };

                var graphElement = _converter.Convert(graphData, typeof(UIElement), null, string.Empty);
                
                if (graphElement is UIElement element)
                {
                    GraphPresenter.Content = element;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResponsivePerformanceGraph.UpdateGraph error: {ex.Message}");
            }
        }

        private void UpdateLabels()
        {
            if (!IsLoaded || LabelsCanvas == null) return;

            try
            {
                LabelsCanvas.Children.Clear();

                var actualHeight = ActualHeight;
                if (actualHeight <= 0) actualHeight = Height > 0 ? Height : 200;

                // Add Y-axis labels for 0%, 50%, 100%
                var labelPositions = new[] 
                { 
                    (1.0, "0%"),      // Bottom
                    (0.5, "50%"),     // Middle  
                    (0.05, "100%")    // Top (with small offset)
                };

                foreach (var (position, text) in labelPositions)
                {
                    var label = new TextBlock
                    {
                        Text = text,
                        FontSize = GetResponsiveFontSize(actualHeight),
                        Foreground = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
                        FontWeight = FontWeights.Normal,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top
                    };

                    var y = actualHeight * position - (position == 1.0 ? label.FontSize : label.FontSize / 2);
                    
                    Canvas.SetLeft(label, 4);
                    Canvas.SetTop(label, Math.Max(0, y));
                    
                    LabelsCanvas.Children.Add(label);
                }

                // Add time label (optional, for context)
                if (actualHeight > 80) // Only show on larger graphs
                {
                    var timeLabel = new TextBlock
                    {
                        Text = "60s",
                        FontSize = GetResponsiveFontSize(actualHeight) * 0.9,
                        Foreground = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom
                    };

                    // WinUI 3 Canvas doesn't have SetRight/SetBottom, calculate position manually
                    var actualWidth = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 400);
                    Canvas.SetLeft(timeLabel, actualWidth - 30); // Approximate width for "60s"
                    Canvas.SetTop(timeLabel, actualHeight - timeLabel.FontSize - 4);
                    
                    LabelsCanvas.Children.Add(timeLabel);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResponsivePerformanceGraph.UpdateLabels error: {ex.Message}");
            }
        }

        private double GetResponsiveFontSize(double containerHeight)
        {
            // Scale font size based on container height
            return containerHeight switch
            {
                <= 80 => 8,    // Very small
                <= 120 => 9,   // Small
                <= 180 => 10,  // Medium
                <= 250 => 11,  // Large
                _ => 12        // Extra large
            };
        }

        // Dependency properties for XAML binding
        public static readonly DependencyProperty HistoryProperty =
            DependencyProperty.Register(nameof(History), typeof(List<double>), typeof(ResponsivePerformanceGraph),
                new PropertyMetadata(new List<double>(), OnHistoryChanged));

        public static readonly DependencyProperty AccentColorKeyProperty =
            DependencyProperty.Register(nameof(AccentColorKey), typeof(string), typeof(ResponsivePerformanceGraph),
                new PropertyMetadata("App.CpuAccent", OnAccentColorKeyChanged));

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(ResponsivePerformanceGraph),
                new PropertyMetadata(100.0, OnMaxValueChanged));

        public static readonly DependencyProperty ShowGridProperty =
            DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(ResponsivePerformanceGraph),
                new PropertyMetadata(true, OnShowGridChanged));

        public static readonly DependencyProperty ShowFillProperty =
            DependencyProperty.Register(nameof(ShowFill), typeof(bool), typeof(ResponsivePerformanceGraph),
                new PropertyMetadata(true, OnShowFillChanged));

        private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResponsivePerformanceGraph graph && e.NewValue is List<double> history)
            {
                graph.History = history;
            }
        }

        private static void OnAccentColorKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResponsivePerformanceGraph graph && e.NewValue is string colorKey)
            {
                graph.AccentColorKey = colorKey;
            }
        }

        private static void OnMaxValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResponsivePerformanceGraph graph && e.NewValue is double maxValue)
            {
                graph.MaxValue = maxValue;
            }
        }

        private static void OnShowGridChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResponsivePerformanceGraph graph && e.NewValue is bool showGrid)
            {
                graph.ShowGrid = showGrid;
            }
        }

        private static void OnShowFillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ResponsivePerformanceGraph graph && e.NewValue is bool showFill)
            {
                graph.ShowFill = showFill;
            }
        }
    }
}
