using Microsoft.Graphics.Canvas;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

internal sealed record ImageExportDestination(
    StorageFile File,
    CanvasBitmapFileFormat Format,
    float Quality);

internal static class ImageExportService
{
    public static async Task<ImageExportDestination?> PickDestinationAsync(
        string sourcePath,
        bool includeOverlays)
    {
        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) +
                (includeOverlays ? "-overlays" : "-stretched"),
        };
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        picker.FileTypeChoices.Add("JPEG image", [".jpg"]);
        picker.FileTypeChoices.Add("TIFF image", [".tiff"]);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        CanvasBitmapFileFormat format = file.FileType.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => CanvasBitmapFileFormat.Jpeg,
            ".tif" or ".tiff" => CanvasBitmapFileFormat.Tiff,
            _ => CanvasBitmapFileFormat.Png,
        };
        float quality = format == CanvasBitmapFileFormat.Jpeg ? 0.92f : 1.0f;
        return new ImageExportDestination(file, format, quality);
    }

    public static async Task SaveAsync(
        CanvasBitmap image,
        ImageExportDestination destination)
    {
        using IRandomAccessStream stream = await destination.File.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        await image.SaveAsync(stream, destination.Format, destination.Quality);
    }
}
