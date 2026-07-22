using Microsoft.Graphics.Canvas;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

internal sealed record ClipboardImageSource(string Path, bool DiscoverSiblings);

internal static class ClipboardService
{
    private const string ProcessingFormat = "org.seiza.image-processing";
    private const int MaximumProcessingJsonLength = 1_048_576;
    private static InMemoryRandomAccessStream? _copiedImageStream;

    public static async Task CopyImageAsync(CanvasBitmap image)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            await image.SaveAsync(stream, CanvasBitmapFileFormat.Png, 1.0f);
            stream.Seek(0);

            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.Properties.Title = "Seiza image";
            package.Properties.Description = "Rendered astronomy image from Seiza";
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();

            InMemoryRandomAccessStream? previous = _copiedImageStream;
            _copiedImageStream = stream;
            previous?.Dispose();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static void CopyProcessing(string json)
    {
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.Properties.Title = "Seiza processing adjustments";
        package.Properties.Description = "Stretch, background, and deconvolution settings";
        package.SetData(ProcessingFormat, json);
        package.SetText(json);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _copiedImageStream?.Dispose();
        _copiedImageStream = null;
    }

    public static async Task<string?> GetProcessingJsonAsync()
    {
        DataPackageView content = Clipboard.GetContent();
        string? json = null;
        if (content.Contains(ProcessingFormat))
        {
            json = await content.GetDataAsync(ProcessingFormat) as string;
        }
        else if (content.Contains(StandardDataFormats.Text))
        {
            json = await content.GetTextAsync();
        }

        if (json is null)
        {
            return null;
        }
        if (json.Length > MaximumProcessingJsonLength)
        {
            throw new FormatException("The clipboard processing object is too large.");
        }
        return json;
    }

    public static async Task<ClipboardImageSource?> GetImageAsync()
    {
        DataPackageView content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> items = await content.GetStorageItemsAsync();
            StorageFile? file = items.OfType<StorageFile>().FirstOrDefault(candidate =>
                ImageFileService.IsSupportedImage(candidate.Path) ||
                ImageFileService.IsSupportedExtension(candidate.FileType));
            if (file is not null)
            {
                if (!string.IsNullOrWhiteSpace(file.Path) &&
                    ImageFileService.IsSupportedImage(file.Path))
                {
                    return new ClipboardImageSource(file.Path, true);
                }

                StorageFile copy = await file.CopyAsync(
                    await GetTemporaryFolderAsync(),
                    $"Seiza clipboard{file.FileType}",
                    NameCollisionOption.GenerateUniqueName);
                return new ClipboardImageSource(copy.Path, false);
            }
        }

        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            return null;
        }

        RandomAccessStreamReference reference = await content.GetBitmapAsync();
        using IRandomAccessStreamWithContentType input = await reference.OpenReadAsync();
        StorageFile temporary = await (await GetTemporaryFolderAsync()).CreateFileAsync(
            "Seiza clipboard.png",
            CreationCollisionOption.GenerateUniqueName);
        using IRandomAccessStream output = await temporary.OpenAsync(FileAccessMode.ReadWrite);
        output.Size = 0;
        input.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return new ClipboardImageSource(temporary.Path, false);
    }

    private static async Task<StorageFolder> GetTemporaryFolderAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "Seiza", "Clipboard");
        Directory.CreateDirectory(path);
        return await StorageFolder.GetFolderFromPathAsync(path);
    }
}
