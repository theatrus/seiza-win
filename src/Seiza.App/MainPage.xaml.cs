using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Seiza.App.Models;
using Seiza.App.Services;
using Seiza.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage;

namespace Seiza.App;

public sealed partial class MainPage : Page, IDisposable
{
    private const float MinimumScale = 0.01f;
    private const float MaximumScale = 64.0f;

    private readonly List<string> _imagePaths = [];

    private CanvasBitmap? _bitmap;
    private CancellationTokenSource? _loadCancellation;
    private Vector2 _offset;
    private Vector2 _dragStart;
    private Vector2 _offsetAtDragStart;
    private float _scale = 1.0f;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _selectedIndex = -1;
    private int _loadGeneration;
    private bool _isDragging;
    private bool _isFitToWindow = true;

    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, _) => UpdateVisualState();
        Unloaded += MainPage_Unloaded;
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        string? path = await ImageFileService.PickImageAsync();
        if (path is not null)
        {
            await OpenImageAndDiscoverSiblingsAsync(path);
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = await ImageFileService.PickFolderAsync();
        if (path is not null)
        {
            await OpenFolderAsync(path);
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex > 0)
        {
            _selectedIndex--;
            UpdateNavigationState();
            await LoadImageAsync(_imagePaths[_selectedIndex]);
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex >= 0 && _selectedIndex < _imagePaths.Count - 1)
        {
            _selectedIndex++;
            UpdateNavigationState();
            await LoadImageAsync(_imagePaths[_selectedIndex]);
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitImageToWindow();

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomAt(new Vector2((float)ImageCanvas.ActualWidth / 2, (float)ImageCanvas.ActualHeight / 2), 1.25f);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomAt(new Vector2((float)ImageCanvas.ActualWidth / 2, (float)ImageCanvas.ActualHeight / 2), 0.8f);

    private void CatalogSettings_Click(object sender, RoutedEventArgs e) =>
        App.ShowCatalogSettings();

    private async Task OpenImageAndDiscoverSiblingsAsync(string path)
    {
        IReadOnlyList<string> siblings = [path];
        string? folder = Path.GetDirectoryName(path);
        if (folder is not null)
        {
            try
            {
                siblings = await ImageFileService.GetImagesAsync(folder);
            }
            catch (Exception) when (!string.IsNullOrWhiteSpace(folder))
            {
                // The selected image is still usable if sibling enumeration is unavailable.
            }
        }

        SetCollection(siblings.Count == 0 ? [path] : siblings, path);
        await LoadImageAsync(path);
    }

    private async Task OpenFolderAsync(string folderPath)
    {
        try
        {
            IReadOnlyList<string> paths = await ImageFileService.GetImagesAsync(folderPath);
            if (paths.Count == 0)
            {
                ViewModel.FailLoading("This folder does not contain a supported image.");
                return;
            }

            SetCollection(paths, paths[0]);
            await LoadImageAsync(paths[0]);
        }
        catch (Exception exception)
        {
            ViewModel.FailLoading(DescribeException(exception));
        }
    }

    private void SetCollection(IReadOnlyList<string> paths, string selectedPath)
    {
        _imagePaths.Clear();
        _imagePaths.AddRange(paths);
        _selectedIndex = _imagePaths.FindIndex(path =>
            string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (_selectedIndex < 0 && _imagePaths.Count > 0)
        {
            _selectedIndex = 0;
        }

        UpdateNavigationState();
    }

    private async Task LoadImageAsync(string path)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCancellation.Token;
        int generation = ++_loadGeneration;
        ViewModel.BeginLoading(path);

        try
        {
            RenderedImageData image = await ImageRenderService.RenderAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            CanvasBitmap nextBitmap = CanvasBitmap.CreateFromBytes(
                ImageCanvas,
                image.Bgra,
                image.Width,
                image.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                96.0f,
                CanvasAlphaMode.Ignore);

            _bitmap?.Dispose();
            _bitmap = nextBitmap;
            _sourceWidth = image.Width;
            _sourceHeight = image.Height;
            ViewModel.CompleteLoading(path, image.Metadata);
            if (App.Window is MainWindow window)
            {
                window.SetDocumentTitle(Path.GetFileName(path));
            }

            FitImageToWindow();
        }
        catch (OperationCanceledException)
        {
            // A newer load superseded this one.
        }
        catch (Exception exception) when (generation == _loadGeneration)
        {
            ViewModel.FailLoading(DescribeException(exception));
        }
    }

    private void ImageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_bitmap is null)
        {
            return;
        }

        Rect destination = new(
            _offset.X,
            _offset.Y,
            _sourceWidth * _scale,
            _sourceHeight * _scale);
        CanvasImageInterpolation interpolation = _scale < 1.0f
            ? CanvasImageInterpolation.HighQualityCubic
            : CanvasImageInterpolation.NearestNeighbor;
        args.DrawingSession.DrawImage(
            _bitmap,
            destination,
            _bitmap.Bounds,
            1.0f,
            interpolation);
    }

    private void ImageCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFitToWindow)
        {
            FitImageToWindow();
        }
    }

    private void ImageCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(ImageCanvas);
        float factor = MathF.Pow(1.0015f, point.Properties.MouseWheelDelta);
        ZoomAt(new Vector2((float)point.Position.X, (float)point.Position.Y), factor);
        e.Handled = true;
    }

    private void ImageCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageCanvas);
        if (_bitmap is null || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _dragStart = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _offsetAtDragStart = _offset;
        ImageCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ImageCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Point position = e.GetCurrentPoint(ImageCanvas).Position;
        Vector2 current = new((float)position.X, (float)position.Y);
        _offset = _offsetAtDragStart + current - _dragStart;
        _isFitToWindow = false;
        ImageCanvas.Invalidate();
        e.Handled = true;
    }

    private void ImageCanvas_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        EndDrag(e.Pointer);

    private void ImageCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
    }

    private void EndDrag(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        _isDragging = false;
        ImageCanvas.ReleasePointerCapture(pointer);
    }

    private void FitImageToWindow()
    {
        if (_bitmap is null || _sourceWidth <= 0 || _sourceHeight <= 0 ||
            ImageCanvas.ActualWidth <= 0 || ImageCanvas.ActualHeight <= 0)
        {
            return;
        }

        float horizontalScale = (float)ImageCanvas.ActualWidth / _sourceWidth;
        float verticalScale = (float)ImageCanvas.ActualHeight / _sourceHeight;
        _scale = Math.Clamp(Math.Min(horizontalScale, verticalScale), MinimumScale, MaximumScale);
        _offset = new Vector2(
            ((float)ImageCanvas.ActualWidth - (_sourceWidth * _scale)) / 2,
            ((float)ImageCanvas.ActualHeight - (_sourceHeight * _scale)) / 2);
        _isFitToWindow = true;
        ImageCanvas.Invalidate();
    }

    private void ZoomAt(Vector2 anchor, float factor)
    {
        if (_bitmap is null)
        {
            return;
        }

        float nextScale = Math.Clamp(_scale * factor, MinimumScale, MaximumScale);
        if (Math.Abs(nextScale - _scale) < float.Epsilon)
        {
            return;
        }

        Vector2 imagePoint = (anchor - _offset) / _scale;
        _scale = nextScale;
        _offset = anchor - (imagePoint * _scale);
        _isFitToWindow = false;
        ImageCanvas.Invalidate();
    }

    private void UpdateNavigationState()
    {
        ViewModel.CanNavigatePrevious = _selectedIndex > 0;
        ViewModel.CanNavigateNext = _selectedIndex >= 0 && _selectedIndex < _imagePaths.Count - 1;
        ViewModel.PositionText = _imagePaths.Count > 1 && _selectedIndex >= 0
            ? $"{_selectedIndex + 1:N0} of {_imagePaths.Count:N0}"
            : string.Empty;
    }

    private void UpdateVisualState()
    {
        WelcomePanel.Visibility = !ViewModel.HasImage && !ViewModel.IsLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoadingPanel.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    }

    private static string DescribeException(Exception exception)
    {
        string type = exception.GetType().Name;
        string code = $"0x{exception.HResult:X8}";
        return string.IsNullOrWhiteSpace(exception.Message)
            ? $"{type} ({code})"
            : $"{type} ({code}): {exception.Message}";
    }

    private void Viewport_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open in Seiza";
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void Viewport_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        IStorageItem? item = items.Count > 0 ? items[0] : null;
        switch (item)
        {
            case StorageFolder folder:
                await OpenFolderAsync(folder.Path);
                break;
            case StorageFile file when ImageFileService.IsSupportedImage(file.Path):
                await OpenImageAndDiscoverSiblingsAsync(file.Path);
                break;
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _bitmap?.Dispose();
        _bitmap = null;
        ImageCanvas.RemoveFromVisualTree();
        GC.SuppressFinalize(this);
    }
}
