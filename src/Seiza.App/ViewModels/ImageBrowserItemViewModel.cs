using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Seiza.App.Services;
using Windows.Storage.Streams;

namespace Seiza.App.ViewModels;

public partial class ImageBrowserItemViewModel(string path) : ObservableObject
{
    private int _loadState;

    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool Failed { get; private set; }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _loadState, 1, 0) != 0)
        {
            return;
        }

        IsLoading = true;
        try
        {
            byte[]? png = await ThumbnailCacheService.GetAsync(Path, cancellationToken);
            if (png is null)
            {
                Failed = true;
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            using (Stream destination = stream.AsStreamForWrite())
            {
                await destination.WriteAsync(png, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            Thumbnail = image;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _loadState, 0);
        }
        catch
        {
            Failed = true;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
