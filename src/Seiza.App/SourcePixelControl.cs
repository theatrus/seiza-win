using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Services;

namespace Seiza.App;

internal sealed class SourcePixelControl : StackPanel
{
    private readonly CheckBox _enabled = new() { Content = "Show source values under pointer" };
    private readonly TextBlock _reading = new() { Text = "Move over the image to read a source pixel.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private string? _path;
    private CancellationTokenSource? _request;
    private (int X, int Y)? _point;

    public SourcePixelControl()
    {
        Spacing = 8; Visibility = Visibility.Collapsed;
        Children.Add(new TextBlock { Text = "Source pixel", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        Children.Add(_enabled); Children.Add(_reading);
        Children.Add(new TextBlock { Text = "Before display processing. FITS scaling is applied; normalized XISF stays in its stored units. Compressed XISF samples are unavailable here.", TextWrapping = TextWrapping.Wrap });
        _enabled.Unchecked += (_, _) => ClearPoint();
    }

    public void SetSource(string? path)
    {
        if (_path == path) return;
        ClearPoint(); _path = path;
        Visibility = path is not null && ImageFileTypes.IsAstronomyImage(path) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ClearPoint()
    {
        _request?.Cancel(); _point = null;
        _reading.Text = "Move over the image to read a source pixel.";
    }

    public async void SetPoint(int x, int y)
    {
        if (!_enabled.IsChecked.GetValueOrDefault() || _path is not { } path || _point == (x, y)) return;
        _request?.Cancel(); _point = (x, y);
        var request = new CancellationTokenSource(); _request = request;
        _reading.Text = $"Pixel ({x}, {y}) · reading…";
        try
        {
            await Task.Delay(150, request.Token);
            var window = await ImageSourceService.ReadWindowAsync(path, x, y, 1, 1, token: request.Token);
            if (request.IsCancellationRequested || path != _path) return;
            string[] labels = window.Planes == 3 ? ["R", "G", "B"] : ["Value"];
            _reading.Text = $"Pixel ({x}, {y}) · " + string.Join(" · ", window.Values.Select((v, i) => $"{labels[i]} {v:G8}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!request.IsCancellationRequested && path == _path) _reading.Text = e.Message; }
        finally { if (ReferenceEquals(_request, request)) _request = null; request.Dispose(); }
    }
}
