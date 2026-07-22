using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Seiza.App.Models;
using Windows.Foundation;
using Windows.UI;

namespace Seiza.App;

public sealed partial class HistogramPlot : UserControl
{
    private ImageHistogram? _histogram;
    private bool _isMonochrome;

    public HistogramPlot()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    internal void ShowHistogram(ImageHistogram histogram, bool isMonochrome)
    {
        _histogram = histogram;
        _isMonochrome = isMonochrome;
        AutomationProperties.SetName(
            this,
            isMonochrome ? "Luminance histogram" : "RGB histogram");
        Rebuild();
    }

    internal void ClearHistogram()
    {
        _histogram = null;
        PlotCanvas.Children.Clear();
    }

    private void PlotCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        PlotCanvas.Children.Clear();
        if (_histogram is not { IsValid: true } histogram ||
            PlotCanvas.ActualWidth <= 0 || PlotCanvas.ActualHeight <= 0)
        {
            return;
        }

        double width = PlotCanvas.ActualWidth;
        double height = PlotCanvas.ActualHeight;
        var gridBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
        foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
        {
            double x = width * fraction;
            PlotCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                Stroke = gridBrush,
                StrokeThickness = 1,
            });
        }

        (IReadOnlyList<ulong> Bins, Color Color)[] channels = _isMonochrome
            ? [(histogram.Red, Color.FromArgb(255, 245, 245, 245))]
            :
            [
                (histogram.Red, Color.FromArgb(255, 255, 92, 92)),
                (histogram.Green, Color.FromArgb(255, 89, 220, 130)),
                (histogram.Blue, Color.FromArgb(255, 90, 154, 255)),
            ];
        double ceiling = HistogramCeiling(channels.Select(channel => channel.Bins));
        if (ceiling <= 0)
        {
            return;
        }

        foreach ((IReadOnlyList<ulong> bins, Color color) in channels)
        {
            var points = new List<Point>(bins.Count);
            for (int index = 0; index < bins.Count; index++)
            {
                double x = index * width / (bins.Count - 1);
                double normalized = Math.Min(bins[index] / ceiling, 1);
                points.Add(new Point(x, height - (normalized * height)));
            }

            var area = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(
                    _isMonochrome ? (byte)72 : (byte)45,
                    color.R,
                    color.G,
                    color.B)),
            };
            area.Points.Add(new Point(0, height));
            foreach (Point point in points)
            {
                area.Points.Add(point);
            }
            area.Points.Add(new Point(width, height));
            PlotCanvas.Children.Add(area);

            var curve = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B)),
                StrokeThickness = 1.2,
            };
            foreach (Point point in points)
            {
                curve.Points.Add(point);
            }
            PlotCanvas.Children.Add(curve);
        }
    }

    private static double HistogramCeiling(IEnumerable<IReadOnlyList<ulong>> channels)
    {
        IReadOnlyList<ulong>[] materialized = channels.ToArray();
        ulong[] populatedInterior = materialized
            .SelectMany(bins => bins.Count > 2 ? bins.Skip(1).Take(bins.Count - 2) : bins)
            .Where(count => count > 0)
            .Order()
            .ToArray();
        ulong[] candidates = populatedInterior.Length > 0
            ? populatedInterior
            : materialized.SelectMany(bins => bins).Where(count => count > 0).Order().ToArray();
        if (candidates.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Floor((candidates.Length - 1) * 0.98);
        return candidates[index];
    }
}
