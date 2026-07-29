using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

internal enum ImageExportFormat
{
    Png,
    Jpeg,
    Tiff,
}

internal enum ImageExportBitDepth
{
    Eight = 8,
    Sixteen = 16,
}

internal sealed record ImageExportRequest(
    ImageExportFormat Format,
    ImageExportBitDepth BitDepth,
    bool IncludeVisibleOverlays);

internal sealed record ImageExportDestination(
    StorageFile File,
    ImageExportRequest Request);

internal static class ImageExportService
{
    public static async Task<ImageExportRequest?> PickOptionsAsync(
        XamlRoot xamlRoot,
        bool sixteenBitAvailable,
        bool overlaysAvailable,
        bool includeOverlays)
    {
        ComboBox formatPicker = new()
        {
            Header = "Format",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AddOption(formatPicker, "PNG", ImageExportFormat.Png);
        AddOption(formatPicker, "JPEG", ImageExportFormat.Jpeg);
        AddOption(formatPicker, "TIFF", ImageExportFormat.Tiff);

        ComboBox depthPicker = new()
        {
            Header = "Bit depth",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        CheckBox overlayPicker = new()
        {
            Content = "Include visible overlays",
            IsChecked = overlaysAvailable && includeOverlays,
            IsEnabled = overlaysAvailable,
        };

        void PopulateDepths()
        {
            ImageExportFormat format = SelectedValue<ImageExportFormat>(formatPicker);
            depthPicker.Items.Clear();
            if (sixteenBitAvailable &&
                format is ImageExportFormat.Png or ImageExportFormat.Tiff)
            {
                AddOption(depthPicker, "16 bits per channel", ImageExportBitDepth.Sixteen);
            }
            AddOption(depthPicker, "8 bits per channel", ImageExportBitDepth.Eight);
            depthPicker.SelectedIndex = 0;
            depthPicker.IsEnabled = depthPicker.Items.Count > 1;
        }

        formatPicker.SelectedIndex = 0;
        PopulateDepths();
        formatPicker.SelectionChanged += (_, _) => PopulateDepths();

        StackPanel content = new()
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = sixteenBitAvailable
                        ? "PNG and TIFF default to a true 16-bit render. JPEG is always 8-bit."
                        : "This source can be exported at 8 bits per channel. True 16-bit export is available for FITS and XISF images.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                },
                formatPicker,
                depthPicker,
                overlayPicker,
            },
        };
        ContentDialog dialog = new()
        {
            XamlRoot = xamlRoot,
            Title = "Export image",
            Content = content,
            PrimaryButtonText = "Choose location",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return new ImageExportRequest(
            SelectedValue<ImageExportFormat>(formatPicker),
            SelectedValue<ImageExportBitDepth>(depthPicker),
            overlaysAvailable && overlayPicker.IsChecked == true);
    }

    public static async Task<ImageExportDestination?> PickDestinationAsync(
        nint ownerWindow,
        string sourcePath,
        ImageExportRequest request)
    {
        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) +
                (request.IncludeVisibleOverlays ? "-overlays" : "-stretched"),
        };
        picker.FileTypeChoices.Add(FormatTitle(request.Format), [FormatExtension(request.Format)]);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        StorageFile? file = await picker.PickSaveFileAsync();
        return file is null ? null : new ImageExportDestination(file, request);
    }

    public static async Task Save8Async(
        CanvasBitmap image,
        ImageExportDestination destination)
    {
        CanvasBitmapFileFormat format = destination.Request.Format switch
        {
            ImageExportFormat.Jpeg => CanvasBitmapFileFormat.Jpeg,
            ImageExportFormat.Tiff => CanvasBitmapFileFormat.Tiff,
            _ => CanvasBitmapFileFormat.Png,
        };
        float quality = format == CanvasBitmapFileFormat.Jpeg ? 0.92f : 1.0f;
        using IRandomAccessStream stream = await destination.File.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        await image.SaveAsync(stream, format, quality);
    }

    public static async Task Save16Async(
        RenderedImage16Data image,
        ImageExportDestination destination)
    {
        if (destination.Request.Format == ImageExportFormat.Jpeg ||
            destination.Request.BitDepth != ImageExportBitDepth.Sixteen)
        {
            throw new ArgumentException("A 16-bit render requires a 16-bit PNG or TIFF destination.");
        }
        int expectedLength = checked(image.Width * image.Height * 4 * sizeof(ushort));
        if (image.RgbaBytes.Length != expectedLength)
        {
            throw new ArgumentException("The 16-bit render has an invalid pixel buffer.");
        }

        Guid encoderId = destination.Request.Format == ImageExportFormat.Tiff
            ? BitmapEncoder.TiffEncoderId
            : BitmapEncoder.PngEncoderId;
        using IRandomAccessStream stream = await destination.File.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Rgba16,
            BitmapAlphaMode.Straight,
            (uint)image.Width,
            (uint)image.Height,
            96,
            96,
            image.RgbaBytes);
        await encoder.FlushAsync();
    }

    private static void AddOption<T>(ComboBox picker, string title, T value) where T : struct =>
        picker.Items.Add(new ComboBoxItem { Content = title, Tag = value });

    private static T SelectedValue<T>(ComboBox picker) where T : struct =>
        picker.SelectedItem is ComboBoxItem { Tag: T value }
            ? value
            : throw new InvalidOperationException("No export option is selected.");

    private static string FormatTitle(ImageExportFormat format) => format switch
    {
        ImageExportFormat.Jpeg => "JPEG image",
        ImageExportFormat.Tiff => "TIFF image",
        _ => "PNG image",
    };

    private static string FormatExtension(ImageExportFormat format) => format switch
    {
        ImageExportFormat.Jpeg => ".jpg",
        ImageExportFormat.Tiff => ".tiff",
        _ => ".png",
    };
}
