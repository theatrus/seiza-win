using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Seiza.App.Models;
using Windows.Foundation;

namespace Seiza.App;

public sealed partial class StackSnrChart : UserControl
{
    private IReadOnlyList<StackSnrPlotPoint> _points = [];

    public StackSnrChart()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    internal void SetPoints(IEnumerable<StackSnrPlotPoint> points)
    {
        _points = points.ToArray();
        Redraw();
    }

    private void Redraw()
    {
        PlotCanvas.Children.Clear();
        StackSnrPlotGeometry geometry = StackSnrPlotLayout.Create(
            _points,
            PlotCanvas.ActualWidth,
            PlotCanvas.ActualHeight);
        EmptyText.Visibility = geometry.Measured.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RangeText.Text = geometry.Measured.Count == 0
            ? string.Empty
            : $"{geometry.MinimumFrames:N0}–{geometry.MaximumFrames:N0} frames";
        if (geometry.Measured.Count == 0)
        {
            return;
        }

        var idealBrush = new SolidColorBrush(
            ActualTheme == ElementTheme.Dark
                ? Colors.LightGray
                : Colors.DimGray);
        AddPolyline(geometry.Ideal, idealBrush, 1.5, [5, 4]);

        Brush accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        AddPolyline(geometry.Measured, accent, 2.5, null);
        foreach (StackSnrChartPoint point in geometry.Measured)
        {
            var marker = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = accent,
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            PlotCanvas.Children.Add(marker);
        }
    }

    private void AddPolyline(
        IReadOnlyList<StackSnrChartPoint> points,
        Brush stroke,
        double thickness,
        DoubleCollection? dashArray)
    {
        var line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = dashArray,
        };
        foreach (StackSnrChartPoint point in points)
        {
            line.Points.Add(new Point(point.X, point.Y));
        }
        PlotCanvas.Children.Add(line);
    }
}
