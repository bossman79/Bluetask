using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;

namespace Bluetask.Services.Converters
{
    public class ModernPerformanceGraphData
    {
        public List<double> History { get; set; } = new();
        public double Width { get; set; } = 400;
        public double Height { get; set; } = 200;
        public string AccentColorKey { get; set; } = "App.CpuAccent";
        public double MaxValue { get; set; } = 100.0;
        public bool ShowGrid { get; set; } = true;
        public bool ShowFill { get; set; } = true;
    }

    public class ModernPerformanceGraphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                ModernPerformanceGraphData graphData;
                
                // Handle both direct List<double> and ModernPerformanceGraphData
                if (value is List<double> history)
                {
                    graphData = new ModernPerformanceGraphData { History = history };
                }
                else if (value is ModernPerformanceGraphData data)
                {
                    graphData = data;
                }
                else
                {
                    return CreateEmptyCanvas();
                }

                if (graphData.History?.Count == 0)
                    return CreateEmptyCanvas();

                return CreateModernGraph(graphData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ModernPerformanceGraphConverter error: {ex.Message}");
                return CreateEmptyCanvas();
            }
        }

        private Canvas CreateEmptyCanvas()
        {
            return new Canvas { Width = 400, Height = 200 };
        }

        private Canvas CreateModernGraph(ModernPerformanceGraphData data)
        {
            var canvas = new Canvas 
            { 
                Width = data.Width, 
                Height = data.Height
            };
            
            // Set up proper clipping for WinUI 3
            var clipGeometry = new RectangleGeometry
            {
                Rect = new Rect(0, 0, data.Width, data.Height)
            };
            canvas.Clip = clipGeometry;

            var history = data.History;
            if (history.Count < 2) return canvas;

            // Add grid lines first (so they appear behind the graph)
            if (data.ShowGrid)
            {
                AddGridLines(canvas, data);
            }

            // Calculate points for the line
            var points = CalculatePoints(history, data);
            if (points.Count < 2) return canvas;

            // Create gradient fill area
            if (data.ShowFill)
            {
                var fillPath = CreateFillPath(points, data);
                canvas.Children.Add(fillPath);
            }

            // Create the main line
            var mainLine = CreateMainLine(points, data);
            canvas.Children.Add(mainLine);

            return canvas;
        }

        private List<Point> CalculatePoints(List<double> history, ModernPerformanceGraphData data)
        {
            var points = new List<Point>();
            var padding = 2.0;
            var usableWidth = data.Width - (padding * 2);
            var usableHeight = data.Height - (padding * 2);

            if (usableWidth <= 0 || usableHeight <= 0) return points;

            var stepX = usableWidth / Math.Max(1, history.Count - 1);

            for (int i = 0; i < history.Count; i++)
            {
                var value = Math.Clamp(history[i], 0, data.MaxValue);
                if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;

                var x = (i * stepX) + padding;
                var y = usableHeight - (value / data.MaxValue * usableHeight) + padding;

                points.Add(new Point(x, y));
            }

            return points;
        }

        private void AddGridLines(Canvas canvas, ModernPerformanceGraphData data)
        {
            var gridBrush = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)); // Very subtle white lines
            var gridThickness = 0.5;

            // Horizontal grid lines (25%, 50%, 75%)
            var gridPositions = new[] { 0.25, 0.5, 0.75 };
            
            foreach (var position in gridPositions)
            {
                var y = data.Height * position;
                var line = new Line
                {
                    X1 = 0,
                    X2 = data.Width,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = gridThickness,
                    Opacity = 0.4
                };
                canvas.Children.Add(line);
            }
        }

        private Path CreateFillPath(List<Point> points, ModernPerformanceGraphData data)
        {
            var pathGeometry = new PathGeometry();
            var pathFigure = new PathFigure();

            if (points.Count == 0) return new Path();

            // Start from bottom-left
            pathFigure.StartPoint = new Point(points[0].X, data.Height - 2);

            // Add line to first point
            var lineSegment1 = new LineSegment { Point = points[0] };
            pathFigure.Segments.Add(lineSegment1);

            // Add smooth curve through all points
            if (points.Count > 2)
            {
                // Create smooth curve using cubic bezier segments
                for (int i = 0; i < points.Count - 1; i++)
                {
                    var current = points[i];
                    var next = points[i + 1];
                    
                    // Simple smoothing - could be enhanced with more sophisticated curve fitting
                    var controlPoint1 = new Point(current.X + (next.X - current.X) * 0.5, current.Y);
                    var controlPoint2 = new Point(current.X + (next.X - current.X) * 0.5, next.Y);
                    
                    var bezier = new BezierSegment
                    {
                        Point1 = controlPoint1,
                        Point2 = controlPoint2,
                        Point3 = next
                    };
                    pathFigure.Segments.Add(bezier);
                }
            }
            else
            {
                // Fallback to line segments for simple cases
                foreach (var point in points.Skip(1))
                {
                    var lineSegment = new LineSegment { Point = point };
                    pathFigure.Segments.Add(lineSegment);
                }
            }

            // Close the path at bottom-right
            var lineSegment2 = new LineSegment { Point = new Point(points[^1].X, data.Height - 2) };
            pathFigure.Segments.Add(lineSegment2);

            var lineSegment3 = new LineSegment { Point = new Point(points[0].X, data.Height - 2) };
            pathFigure.Segments.Add(lineSegment3);

            pathFigure.IsClosed = true;
            pathGeometry.Figures.Add(pathFigure);

            // Create gradient fill
            var accentBrush = GetAccentBrush(data.AccentColorKey);
            var fillBrush = CreateGradientFill(accentBrush, data.Height);

            return new Path
            {
                Data = pathGeometry,
                Fill = fillBrush,
                Opacity = 0.7
            };
        }

        private Polyline CreateMainLine(List<Point> points, ModernPerformanceGraphData data)
        {
            var pointCollection = new PointCollection();
            foreach (var point in points)
            {
                pointCollection.Add(point);
            }

            var accentBrush = GetAccentBrush(data.AccentColorKey);

            return new Polyline
            {
                Points = pointCollection,
                Stroke = accentBrush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
        }

        private Brush GetAccentBrush(string colorKey)
        {
            try
            {
                var resource = Application.Current?.Resources[colorKey];
                if (resource is Brush brush)
                    return brush;
            }
            catch { }
            
            // Fallback to a nice blue color
            return new SolidColorBrush(Color.FromArgb(255, 30, 144, 255));
        }

        private LinearGradientBrush CreateGradientFill(Brush accentBrush, double height)
        {
            var color = Microsoft.UI.Colors.DodgerBlue; // Default fallback

            if (accentBrush is SolidColorBrush solidBrush)
            {
                color = solidBrush.Color;
            }

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };

            gradient.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(80, color.R, color.G, color.B),
                Offset = 0
            });

            gradient.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(20, color.R, color.G, color.B),
                Offset = 0.7
            });

            gradient.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(5, color.R, color.G, color.B),
                Offset = 1
            });

            return gradient;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // Responsive converter that adapts to container size
    public class ResponsiveGraphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is not List<double> history || history.Count == 0)
                    return new UserControl();

                return new ResponsivePerformanceGraph
                {
                    History = history,
                    AccentColorKey = parameter?.ToString() ?? "App.CpuAccent"
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResponsiveGraphConverter error: {ex.Message}");
                return new UserControl();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
