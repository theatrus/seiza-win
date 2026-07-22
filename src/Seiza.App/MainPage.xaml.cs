using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Seiza.App.Models;
using Seiza.App.Rendering;
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
    private readonly OverlayOptions _overlayOptions = new();
    private readonly FitsImageProcessingHistory _processingHistory = new();

    private CanvasBitmap? _bitmap;
    private CanvasBitmap? _committedBitmap;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _solveCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private ImageMetadata? _currentMetadata;
    private SolveResult? _solveResult;
    private SolveOverlayRenderer? _overlayRenderer;
    private string? _currentPath;
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
    private bool _isInspectorOpen;
    private bool _isBrowserOpen;
    private bool _syncingBrowserSelection;
    private bool _isPickingSymmetryPoint;
    private FitsStretchWindow? _stretchWindow;

    public MainPageViewModel ViewModel { get; } = new();

    public ObservableCollection<ImageBrowserItemViewModel> BrowserItems { get; } = [];

    public MainPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, _) => UpdateVisualState();
        InspectorControl.SolveRequested += InspectorControl_SolveRequested;
        InspectorControl.ExportWcsRequested += InspectorControl_ExportWcsRequested;
        Unloaded += MainPage_Unloaded;
    }

    internal Task OpenPathAsync(string path) => OpenImageAndDiscoverSiblingsAsync(path);

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

    private void ImageBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserItems.Count <= 1)
        {
            return;
        }

        _isBrowserOpen = !_isBrowserOpen;
        UpdateVisualState();
        if (_isBrowserOpen)
        {
            SyncBrowserSelection(scrollIntoView: true);
            _ = PrefetchThumbnailsAroundSelectionAsync();
        }
    }

    private async void ImageBrowserList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingBrowserSelection ||
            ImageBrowserList.SelectedIndex < 0 ||
            ImageBrowserList.SelectedIndex == _selectedIndex)
        {
            return;
        }

        _selectedIndex = ImageBrowserList.SelectedIndex;
        UpdateNavigationState();
        await LoadImageAsync(_imagePaths[_selectedIndex]);
    }

    private async void ImageBrowserList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ImageBrowserItemViewModel item)
        {
            CancellationToken cancellationToken = _thumbnailCancellation?.Token ?? default;
            await item.EnsureLoadedAsync(cancellationToken);
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

    private async void Stretch_Click(object sender, RoutedEventArgs e)
    {
        if (_stretchWindow is not null)
        {
            if (_isPickingSymmetryPoint)
            {
                EndSymmetryPointPicker(showEditor: true);
            }
            else
            {
                _stretchWindow.Activate();
            }
            return;
        }
        if (_currentPath is null || !SupportsAstronomyProcessing(_currentMetadata))
        {
            return;
        }

        string path = _currentPath;
        FitsImageProcessingConfiguration currentProcessing = _processingHistory.Current;
        var window = new FitsStretchWindow(
            Path.GetFileName(path),
            currentProcessing.StretchStack,
            currentProcessing.ExtractsBackground,
            currentProcessing.Deconvolution,
            SupportsColorStretch(_currentMetadata));
        _stretchWindow = window;
        window.PreviewRequested = request => PreviewStretchAsync(path, request);
        window.PickSymmetryPointRequested = BeginSymmetryPointPicker;

        try
        {
            window.Activate();
            bool saveChanges = await window.Completion;
            window.PreviewRequested = null;
            RestoreCommittedBitmap();
            if (!saveChanges ||
                !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            FitsStretchStack requestedStack = window.ResultStack;
            bool requestedBackground = window.ResultExtractsBackground;
            FitsDeconvolutionConfiguration? requestedDeconvolution =
                window.ResultDeconvolution;
            var processing = new FitsImageProcessingConfiguration(
                requestedStack,
                requestedBackground,
                requestedDeconvolution);
            bool changed = !_processingHistory.Current.Equals(processing);
            if (!changed)
            {
                return;
            }

            if (await LoadImageAsync(
                path,
                preserveSolution: true,
                processingOverride: processing))
            {
                _processingHistory.Replace(processing);
                if (_currentMetadata is not null)
                {
                    InspectorControl.ShowMetadata(_currentMetadata, CurrentProcessing());
                }
            }
        }
        finally
        {
            window.PreviewRequested = null;
            window.PickSymmetryPointRequested = null;
            EndSymmetryPointPicker(showEditor: false);
            RestoreCommittedBitmap();
            if (ReferenceEquals(_stretchWindow, window))
            {
                _stretchWindow = null;
            }
            UpdateVisualState();
        }
    }

    private async void UndoStretch_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || !_processingHistory.Undo())
        {
            return;
        }
        if (!await LoadImageAsync(
            _currentPath,
            preserveSolution: true,
            processingOverride: _processingHistory.Current))
        {
            _processingHistory.Redo();
        }
        UpdateVisualState();
    }

    private async void RedoStretch_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || !_processingHistory.Redo())
        {
            return;
        }
        if (!await LoadImageAsync(
            _currentPath,
            preserveSolution: true,
            processingOverride: _processingHistory.Current))
        {
            _processingHistory.Undo();
        }
        UpdateVisualState();
    }

    private void Inspector_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = !_isInspectorOpen;
        UpdateVisualState();
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = false;
        UpdateVisualState();
    }

    private void CatalogSettings_Click(object sender, RoutedEventArgs e) =>
        App.ShowCatalogSettings();

    private async void Solve_Click(object sender, RoutedEventArgs e) =>
        await SolveCurrentAsync();

    private async void InspectorControl_SolveRequested(object? sender, EventArgs e) =>
        await SolveCurrentAsync();

    private async void InspectorControl_ExportWcsRequested(object? sender, EventArgs e) =>
        await ExportWcsAsync();

    private async Task SolveCurrentAsync()
    {
        if (_bitmap is null || _currentPath is null || ViewModel.IsSolving)
        {
            return;
        }

        _solveCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _solveCancellation = cancellation;
        int generation = _loadGeneration;
        string path = _currentPath;
        _solveResult = null;
        _overlayRenderer = null;
        _isInspectorOpen = true;
        ViewModel.BeginSolving();
        InspectorControl.BeginSolve();
        ImageCanvas.Invalidate();

        try
        {
            SolveResult result = await PlateSolveService.SolveAsync(
                path,
                CatalogSettingsStore.LoadCatalogDirectory(),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _loadGeneration ||
                !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _solveResult = result;
            _overlayRenderer = new SolveOverlayRenderer(result, _sourceWidth, _sourceHeight);
            ViewModel.CompleteSolve(result);
            InspectorControl.ShowSolveResult(result);
            ApplyOverlayAvailability(result);
            ImageCanvas.Invalidate();
        }
        catch (CatalogNotReadyException exception)
        {
            if (!cancellation.IsCancellationRequested &&
                generation == _loadGeneration &&
                string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.FailSolve(exception.Message, true);
                InspectorControl.ShowSolveFailure(exception.Message, true);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer image or solve superseded this one.
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested &&
                generation == _loadGeneration &&
                string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                string message = DescribeException(exception);
                ViewModel.FailSolve(message, false);
                InspectorControl.ShowSolveFailure(message, false);
            }
        }
        finally
        {
            if (ReferenceEquals(_solveCancellation, cancellation))
            {
                _solveCancellation = null;
            }
            cancellation.Dispose();
            UpdateVisualState();
        }
    }

    private async Task ExportWcsAsync()
    {
        if (_solveResult is null || _currentPath is null)
        {
            return;
        }

        try
        {
            ViewModel.ErrorMessage = null;
            await WcsExportService.PickAndSaveAsync(_currentPath, _solveResult.Wcs);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Couldn’t export WCS: {DescribeException(exception)}";
        }
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, DeepSkyOverlayItem))
        {
            _overlayOptions.ShowDeepSky = DeepSkyOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, NamedStarsOverlayItem))
        {
            _overlayOptions.ShowNamedStars = NamedStarsOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, TransientsOverlayItem))
        {
            _overlayOptions.ShowTransients = TransientsOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, HistoricalTransientsOverlayItem))
        {
            _overlayOptions.ShowHistoricalTransients = HistoricalTransientsOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, MinorBodiesOverlayItem))
        {
            _overlayOptions.ShowMinorBodies = MinorBodiesOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, CatalogOutlinesOverlayItem))
        {
            _overlayOptions.ShowCatalogOutlines = CatalogOutlinesOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, ObjectLabelsOverlayItem))
        {
            _overlayOptions.ShowObjectLabels = ObjectLabelsOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, FieldStarsOverlayItem))
        {
            _overlayOptions.ShowFieldStars = FieldStarsOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, CoordinateGridOverlayItem))
        {
            _overlayOptions.ShowCoordinateGrid = CoordinateGridOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, FieldCenterOverlayItem))
        {
            _overlayOptions.ShowFieldCenter = FieldCenterOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, DetectedStarsOverlayItem))
        {
            _overlayOptions.ShowDetectedStars = DetectedStarsOverlayItem.IsChecked;
        }

        ImageCanvas.Invalidate();
        UpdateVisualState();
    }

    private void CatalogToggle_Click(object sender, RoutedEventArgs e)
    {
        DeepSkyCatalog? catalog = sender switch
        {
            var item when ReferenceEquals(item, MessierCatalogItem) => DeepSkyCatalog.Messier,
            var item when ReferenceEquals(item, NgcCatalogItem) => DeepSkyCatalog.Ngc,
            var item when ReferenceEquals(item, IcCatalogItem) => DeepSkyCatalog.Ic,
            var item when ReferenceEquals(item, SharplessVdbCatalogItem) => DeepSkyCatalog.SharplessVdb,
            var item when ReferenceEquals(item, LbnCatalogItem) => DeepSkyCatalog.Lbn,
            var item when ReferenceEquals(item, CederbladCatalogItem) => DeepSkyCatalog.Cederblad,
            var item when ReferenceEquals(item, DarkNebulaeCatalogItem) => DeepSkyCatalog.DarkNebulae,
            var item when ReferenceEquals(item, SupernovaRemnantsCatalogItem) => DeepSkyCatalog.SupernovaRemnants,
            var item when ReferenceEquals(item, UgcCatalogItem) => DeepSkyCatalog.Ugc,
            var item when ReferenceEquals(item, PgcCatalogItem) => DeepSkyCatalog.Pgc,
            var item when ReferenceEquals(item, OtherCatalogItem) => DeepSkyCatalog.Other,
            _ => null,
        };
        if (catalog is null || sender is not ToggleMenuFlyoutItem toggle)
        {
            return;
        }

        if (toggle.IsChecked)
        {
            _overlayOptions.HiddenDeepSkyCatalogs.Remove(catalog.Value);
        }
        else
        {
            _overlayOptions.HiddenDeepSkyCatalogs.Add(catalog.Value);
        }
        ImageCanvas.Invalidate();
    }

    private void HideAllOverlays_Click(object sender, RoutedEventArgs e)
    {
        _overlayOptions.HideAll();
        SyncOverlayControls();
        ImageCanvas.Invalidate();
        UpdateVisualState();
    }

    private async void ExportImage_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(false);

    private async void ExportOverlays_Click(object sender, RoutedEventArgs e) =>
        await ExportAsync(true);

    private async void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        try
        {
            await ClipboardService.CopyImageAsync(_bitmap);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Couldn’t copy the image: {exception.Message}";
        }
    }

    private async void PasteImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardImageSource? source = await ClipboardService.GetImageAsync();
            if (source is null)
            {
                ViewModel.ErrorMessage = "The clipboard doesn’t contain a supported image.";
                return;
            }

            if (source.DiscoverSiblings)
            {
                await OpenImageAndDiscoverSiblingsAsync(source.Path);
            }
            else
            {
                SetCollection([source.Path], source.Path);
                await LoadImageAsync(source.Path);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Couldn’t paste the image: {exception.Message}";
        }
    }

    private void CopyAdjustments_Click(object sender, RoutedEventArgs e)
    {
        if (!SupportsAstronomyProcessing(_currentMetadata))
        {
            return;
        }

        try
        {
            ClipboardService.CopyProcessing(CurrentProcessing().ToClipboardJson());
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Couldn’t copy the processing settings: {exception.Message}";
        }
    }

    private async void PasteAdjustments_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || !SupportsAstronomyProcessing(_currentMetadata))
        {
            return;
        }

        try
        {
            string? json = await ClipboardService.GetProcessingJsonAsync();
            if (json is null)
            {
                ViewModel.ErrorMessage = "The clipboard doesn’t contain Seiza processing settings.";
                return;
            }

            FitsImageProcessingConfiguration processing =
                FitsImageProcessingConfiguration.FromClipboardJson(json);
            if (processing.Equals(CurrentProcessing()))
            {
                return;
            }

            if (await LoadImageAsync(_currentPath, preserveSolution: true, processingOverride: processing))
            {
                _processingHistory.Replace(processing);
                _isInspectorOpen = true;
            }
            UpdateVisualState();
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Couldn’t paste the processing settings: {exception.Message}";
        }
    }

    private async Task ExportAsync(bool includeOverlays)
    {
        if (_bitmap is null || _currentPath is null || ViewModel.IsExporting ||
            (includeOverlays && (_overlayRenderer is null || !_overlayOptions.HasVisibleOverlays)))
        {
            return;
        }

        ImageExportDestination? destination = await ImageExportService.PickDestinationAsync(
            _currentPath,
            includeOverlays);
        if (destination is null)
        {
            return;
        }

        ViewModel.IsExporting = true;
        ViewModel.ErrorMessage = null;
        try
        {
            if (!includeOverlays)
            {
                await ImageExportService.SaveAsync(_bitmap, destination);
                return;
            }

            using CanvasRenderTarget renderTarget = new(
                ImageCanvas,
                _sourceWidth,
                _sourceHeight,
                96);
            using (CanvasDrawingSession drawingSession = renderTarget.CreateDrawingSession())
            {
                drawingSession.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                drawingSession.DrawImage(_bitmap);
                _overlayRenderer!.Draw(
                    drawingSession,
                    _overlayOptions,
                    1,
                    1,
                    Vector2.Zero);
            }
            await ImageExportService.SaveAsync(renderTarget, destination);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = $"Export failed: {DescribeException(exception)}";
        }
        finally
        {
            ViewModel.IsExporting = false;
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

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
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = null;
        _imagePaths.Clear();
        _imagePaths.AddRange(paths);
        _selectedIndex = _imagePaths.FindIndex(path =>
            string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (_selectedIndex < 0 && _imagePaths.Count > 0)
        {
            _selectedIndex = 0;
        }

        _syncingBrowserSelection = true;
        try
        {
            BrowserItems.Clear();
            foreach (string path in _imagePaths)
            {
                BrowserItems.Add(new ImageBrowserItemViewModel(path));
            }
            ImageBrowserList.SelectedIndex = _selectedIndex;
        }
        finally
        {
            _syncingBrowserSelection = false;
        }
        _isBrowserOpen = BrowserItems.Count > 1;

        UpdateNavigationState();
        UpdateVisualState();
    }

    private async Task<bool> LoadImageAsync(
        string path,
        bool preserveSolution = false,
        FitsImageProcessingConfiguration? processingOverride = null)
    {
        bool isNewImage = !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);
        if (isNewImage)
        {
            EndSymmetryPointPicker(showEditor: false);
            _stretchWindow?.Close();
        }
        FitsImageProcessingConfiguration processing = processingOverride ??
            (isNewImage
                ? FitsImageProcessingConfiguration.Default
                : CurrentProcessing());
        if (!preserveSolution || ViewModel.IsSolving)
        {
            ResetSolveForImageChange();
        }
        if (!preserveSolution)
        {
            _currentMetadata = null;
            InspectorControl.ClearMetadata();
        }
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCancellation.Token;
        int generation = ++_loadGeneration;
        ViewModel.BeginLoading(path);

        try
        {
            RenderedImageData image = await ImageRenderService.RenderAsync(
                path,
                processing,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return false;
            }

            CanvasBitmap nextBitmap = CanvasBitmap.CreateFromBytes(
                ImageCanvas,
                image.Bgra,
                image.Width,
                image.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                96.0f,
                CanvasAlphaMode.Ignore);

            SetCommittedBitmap(nextBitmap);
            _sourceWidth = image.Width;
            _sourceHeight = image.Height;
            _currentPath = path;
            _currentMetadata = image.Metadata;
            if (isNewImage && processingOverride is null)
            {
                _processingHistory.Reset();
            }
            InspectorControl.ShowMetadata(image.Metadata, processing);
            ViewModel.CompleteLoading(path, image.Metadata);
            if (App.Window is MainWindow window)
            {
                window.SetDocumentTitle(Path.GetFileName(path));
            }

            FitImageToWindow();
            _ = PrefetchThumbnailsAroundSelectionAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            // A newer load superseded this one.
            return false;
        }
        catch (Exception exception)
        {
            if (generation == _loadGeneration)
            {
                ViewModel.FailLoading(DescribeException(exception));
            }
            return false;
        }
    }

    private async Task PreviewStretchAsync(
        string path,
        FitsStretchPreviewRequest request)
    {
        if (_stretchWindow is null ||
            !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RenderedImageData image = await ImageRenderService.RenderAsync(
            path,
            request.Processing,
            maxDimension: 2_048,
            cancellationToken: request.CancellationToken);
        request.CancellationToken.ThrowIfCancellationRequested();
        if (_stretchWindow is null ||
            !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
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
        if (!ReferenceEquals(_bitmap, _committedBitmap))
        {
            _bitmap?.Dispose();
        }
        _bitmap = nextBitmap;
        ImageCanvas.Invalidate();
    }

    private FitsImageProcessingConfiguration CurrentProcessing(bool interactivePreview = false) =>
        _processingHistory.Current.Clone(interactivePreview);

    private void SetCommittedBitmap(CanvasBitmap bitmap)
    {
        if (!ReferenceEquals(_bitmap, _committedBitmap))
        {
            _bitmap?.Dispose();
        }
        _committedBitmap?.Dispose();
        _committedBitmap = bitmap;
        _bitmap = bitmap;
    }

    private void RestoreCommittedBitmap()
    {
        if (!ReferenceEquals(_bitmap, _committedBitmap))
        {
            _bitmap?.Dispose();
        }
        _bitmap = _committedBitmap;
        ImageCanvas.Invalidate();
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
        _overlayRenderer?.Draw(args.DrawingSession, _overlayOptions, _scale, _scale, _offset);
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
        if (_bitmap is null || !point.Properties.IsLeftButtonPressed ||
            e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        if (_isPickingSymmetryPoint)
        {
            var position = new Vector2((float)point.Position.X, (float)point.Position.Y);
            if (TrySampleCommittedLuminance(position, out double luminance))
            {
                CompleteSymmetryPointPicker(luminance);
            }
            e.Handled = true;
            return;
        }

        _isDragging = true;
        _dragStart = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _offsetAtDragStart = _offset;
        ImageCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void BeginSymmetryPointPicker()
    {
        if (_stretchWindow is null || _committedBitmap is null)
        {
            return;
        }

        _isPickingSymmetryPoint = true;
        RestoreCommittedBitmap();
        SymmetryPickerBanner.Visibility = Visibility.Visible;
        ImageCanvas.Focus(FocusState.Programmatic);
    }

    private void CancelSymmetryPointPicker_Click(object sender, RoutedEventArgs e) =>
        EndSymmetryPointPicker(showEditor: true);

    private void CompleteSymmetryPointPicker(double luminance)
    {
        _isPickingSymmetryPoint = false;
        SymmetryPickerBanner.Visibility = Visibility.Collapsed;
        _stretchWindow?.ApplySymmetryPoint(luminance);
    }

    private void EndSymmetryPointPicker(bool showEditor)
    {
        bool wasPicking = _isPickingSymmetryPoint;
        _isPickingSymmetryPoint = false;
        SymmetryPickerBanner.Visibility = Visibility.Collapsed;
        if (showEditor && wasPicking)
        {
            _stretchWindow?.CancelSymmetryPointPicker();
        }
    }

    private bool TrySampleCommittedLuminance(Vector2 viewportPoint, out double luminance)
    {
        luminance = 0;
        if (_committedBitmap is null || _scale <= 0 || _sourceWidth <= 0 || _sourceHeight <= 0)
        {
            return false;
        }

        Vector2 imagePoint = (viewportPoint - _offset) / _scale;
        if (imagePoint.X < 0 || imagePoint.Y < 0 ||
            imagePoint.X >= _sourceWidth || imagePoint.Y >= _sourceHeight)
        {
            return false;
        }

        int bitmapWidth = checked((int)_committedBitmap.SizeInPixels.Width);
        int bitmapHeight = checked((int)_committedBitmap.SizeInPixels.Height);
        int centerX = Math.Min(
            (int)Math.Floor(imagePoint.X * bitmapWidth / _sourceWidth),
            bitmapWidth - 1);
        int centerY = Math.Min(
            (int)Math.Floor(imagePoint.Y * bitmapHeight / _sourceHeight),
            bitmapHeight - 1);
        int left = Math.Max(centerX - 1, 0);
        int top = Math.Max(centerY - 1, 0);
        int width = Math.Min(centerX + 1, bitmapWidth - 1) - left + 1;
        int height = Math.Min(centerY + 1, bitmapHeight - 1) - top + 1;
        byte[] bgra = _committedBitmap.GetPixelBytes(left, top, width, height);
        var samples = new List<double>(width * height);
        for (int index = 0; index + 3 < bgra.Length; index += 4)
        {
            double blue = bgra[index] / 255.0;
            double green = bgra[index + 1] / 255.0;
            double red = bgra[index + 2] / 255.0;
            samples.Add((0.2126 * red) + (0.7152 * green) + (0.0722 * blue));
        }
        if (samples.Count == 0)
        {
            return false;
        }

        samples.Sort();
        luminance = samples[samples.Count / 2];
        return true;
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

    private void ImageCanvas_ManipulationStarted(
        object sender,
        ManipulationStartedRoutedEventArgs e)
    {
        if (_bitmap is null || _isPickingSymmetryPoint)
        {
            return;
        }

        _isDragging = false;
        e.Handled = true;
    }

    private void ImageCanvas_ManipulationDelta(
        object sender,
        ManipulationDeltaRoutedEventArgs e)
    {
        if (_bitmap is null || _isPickingSymmetryPoint)
        {
            return;
        }

        Vector2 anchor = new((float)e.Position.X, (float)e.Position.Y);
        Vector2 imagePoint = (anchor - _offset) / _scale;
        float nextScale = Math.Clamp(
            _scale * e.Delta.Scale,
            MinimumScale,
            MaximumScale);
        Vector2 translation = new(
            (float)e.Delta.Translation.X,
            (float)e.Delta.Translation.Y);

        _scale = nextScale;
        _offset = anchor + translation - (imagePoint * _scale);
        _isFitToWindow = false;
        ImageCanvas.Invalidate();
        e.Handled = true;
    }

    private void ImageCanvas_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        if (_bitmap is not null && !_isPickingSymmetryPoint)
        {
            e.Handled = true;
        }
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
        SyncBrowserSelection(scrollIntoView: _isBrowserOpen);
    }

    private void SyncBrowserSelection(bool scrollIntoView)
    {
        if (_selectedIndex < 0 || _selectedIndex >= BrowserItems.Count)
        {
            return;
        }

        _syncingBrowserSelection = true;
        try
        {
            ImageBrowserList.SelectedIndex = _selectedIndex;
            if (scrollIntoView)
            {
                ImageBrowserList.ScrollIntoView(BrowserItems[_selectedIndex]);
            }
        }
        finally
        {
            _syncingBrowserSelection = false;
        }
    }

    private async Task PrefetchThumbnailsAroundSelectionAsync()
    {
        _thumbnailCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _thumbnailCancellation = cancellation;

        try
        {
            var indexes = new List<int>();
            if (_selectedIndex >= 0)
            {
                indexes.Add(_selectedIndex);
                for (int distance = 1; distance <= 4; distance++)
                {
                    if (_selectedIndex - distance >= 0)
                    {
                        indexes.Add(_selectedIndex - distance);
                    }
                    if (_selectedIndex + distance < BrowserItems.Count)
                    {
                        indexes.Add(_selectedIndex + distance);
                    }
                }
            }

            foreach (int index in indexes)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await BrowserItems[index].EnsureLoadedAsync(cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Selection changes intentionally supersede adjacent prefetching.
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCancellation, cancellation))
            {
                _thumbnailCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void UpdateVisualState()
    {
        WelcomePanel.Visibility = !ViewModel.HasImage && !ViewModel.IsLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoadingPanel.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
        SolveErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.SolveErrorMessage);
        SolveCatalogSettingsButton.Visibility = ViewModel.NeedsCatalogSetup
            ? Visibility.Visible
            : Visibility.Collapsed;
        SolveSummaryCard.Visibility = ViewModel.HasSolution
            ? Visibility.Visible
            : Visibility.Collapsed;
        SolveButton.Label = ViewModel.IsSolving ? "Solving..." : "Solve";
        SolveButton.IsEnabled =
            _bitmap is not null &&
            _currentPath is not null &&
            !ViewModel.IsLoading &&
            !ViewModel.IsSolving;
        OverlayButton.IsEnabled = ViewModel.HasSolution;
        ExportButton.Label = ViewModel.IsExporting ? "Exporting..." : "Export";
        ExportButton.IsEnabled =
            _bitmap is not null &&
            _currentPath is not null &&
            !ViewModel.IsLoading &&
            !ViewModel.IsExporting;
        CopyImageItem.IsEnabled = _bitmap is not null && !ViewModel.IsLoading;
        ExportImageItem.IsEnabled = !ViewModel.IsExporting;
        ExportOverlaysItem.IsEnabled =
            ViewModel.HasSolution &&
            _overlayOptions.HasVisibleOverlays &&
            !ViewModel.IsExporting;
        bool supportsAstronomyProcessing = SupportsAstronomyProcessing(_currentMetadata);
        CopyAdjustmentsItem.IsEnabled = supportsAstronomyProcessing && !ViewModel.IsLoading;
        PasteAdjustmentsItem.IsEnabled = supportsAstronomyProcessing && !ViewModel.IsLoading;
        ImageBrowserButton.IsEnabled = BrowserItems.Count > 1;
        ImageBrowserButton.Label = _isBrowserOpen ? "Hide images" : "Images";
        ImageBrowserPane.Visibility = _isBrowserOpen && BrowserItems.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        FitsStretchStack stretchStack = _processingHistory.Current.StretchStack;
        FitsStretchConfiguration currentStretch = stretchStack.Stages[^1];
        UndoStretchButton.IsEnabled =
            supportsAstronomyProcessing && _processingHistory.CanUndo && !ViewModel.IsLoading;
        RedoStretchButton.IsEnabled =
            supportsAstronomyProcessing && _processingHistory.CanRedo && !ViewModel.IsLoading;
        StretchButton.IsEnabled =
            supportsAstronomyProcessing && !ViewModel.IsLoading;
        StretchButton.Label = supportsAstronomyProcessing
            ? stretchStack.Stages.Count == 1
                ? currentStretch.Type.Title()
                : $"{stretchStack.Stages.Count} stages"
            : "Stretch";
        ToolTipService.SetToolTip(
            StretchButton,
            supportsAstronomyProcessing
                ? $"Stretch: {currentStretch.Type.Title()}. {currentStretch.Type.Help()}"
                : "Processing controls are available for FITS and XISF images");
        WorkspaceSplitView.IsPaneOpen = _isInspectorOpen && ViewModel.HasImage;
        InspectorButton.Label = WorkspaceSplitView.IsPaneOpen
            ? "Hide inspector"
            : "Inspector";
    }

    private void ResetSolveForImageChange()
    {
        _solveCancellation?.Cancel();
        _solveResult = null;
        _overlayRenderer = null;
        ViewModel.ResetSolve();
        InspectorControl.ResetSolve();
        ImageCanvas.Invalidate();
    }

    private void SyncOverlayControls()
    {
        DeepSkyOverlayItem.IsChecked = _overlayOptions.ShowDeepSky;
        NamedStarsOverlayItem.IsChecked = _overlayOptions.ShowNamedStars;
        TransientsOverlayItem.IsChecked = _overlayOptions.ShowTransients;
        HistoricalTransientsOverlayItem.IsChecked = _overlayOptions.ShowHistoricalTransients;
        MinorBodiesOverlayItem.IsChecked = _overlayOptions.ShowMinorBodies;
        CatalogOutlinesOverlayItem.IsChecked = _overlayOptions.ShowCatalogOutlines;
        ObjectLabelsOverlayItem.IsChecked = _overlayOptions.ShowObjectLabels;
        FieldStarsOverlayItem.IsChecked = _overlayOptions.ShowFieldStars;
        CoordinateGridOverlayItem.IsChecked = _overlayOptions.ShowCoordinateGrid;
        FieldCenterOverlayItem.IsChecked = _overlayOptions.ShowFieldCenter;
        DetectedStarsOverlayItem.IsChecked = _overlayOptions.ShowDetectedStars;

        MessierCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Messier);
        NgcCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Ngc);
        IcCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Ic);
        SharplessVdbCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.SharplessVdb);
        LbnCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Lbn);
        CederbladCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Cederblad);
        DarkNebulaeCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.DarkNebulae);
        SupernovaRemnantsCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.SupernovaRemnants);
        UgcCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Ugc);
        PgcCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Pgc);
        OtherCatalogItem.IsChecked = !_overlayOptions.HiddenDeepSkyCatalogs.Contains(DeepSkyCatalog.Other);
    }

    private void ApplyOverlayAvailability(SolveResult result)
    {
        IReadOnlyDictionary<string, bool>? availability = result.OverlayAvailability;
        IReadOnlyDictionary<string, int>? counts = result.OverlayCounts;

        UpdateLayerMenuItem(DeepSkyOverlayItem, "Deep-sky objects", "deep_sky");
        UpdateLayerMenuItem(NamedStarsOverlayItem, "Named stars", "named_stars");
        UpdateLayerMenuItem(TransientsOverlayItem, "Current transients", "transients");
        UpdateLayerMenuItem(
            HistoricalTransientsOverlayItem,
            "Historical transients",
            "historical_transients");
        UpdateLayerMenuItem(MinorBodiesOverlayItem, "Solar-system bodies", "minor_bodies");

        void UpdateLayerMenuItem(
            ToggleMenuFlyoutItem item,
            string label,
            string layer)
        {
            bool isAvailable = availability is null ||
                !availability.TryGetValue(layer, out bool value) ||
                value;
            item.IsEnabled = isAvailable;
            int count = 0;
            counts?.TryGetValue(layer, out count);
            if (layer == "transients" &&
                counts?.TryGetValue("historical_transients", out int historicalCount) == true)
            {
                count = Math.Max(0, count - historicalCount);
            }
            item.Text = isAvailable
                ? $"{label} ({count:N0})"
                : $"{label} (unavailable)";
        }
    }

    private static string DescribeException(Exception exception)
    {
        string type = exception.GetType().Name;
        string code = $"0x{exception.HResult:X8}";
        return string.IsNullOrWhiteSpace(exception.Message)
            ? $"{type} ({code})"
            : $"{type} ({code}): {exception.Message}";
    }

    private static bool SupportsColorStretch(ImageMetadata? metadata) =>
        metadata?.ColorKind is "planar-rgb" or "bayer";

    private static bool SupportsAstronomyProcessing(ImageMetadata? metadata) =>
        string.Equals(metadata?.Format, "FITS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(metadata?.Format, "XISF", StringComparison.OrdinalIgnoreCase);

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
        _solveCancellation?.Cancel();
        _solveCancellation = null;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = null;
        EndSymmetryPointPicker(showEditor: false);
        _stretchWindow?.Close();
        _stretchWindow = null;
        if (!ReferenceEquals(_bitmap, _committedBitmap))
        {
            _bitmap?.Dispose();
        }
        _bitmap = null;
        _committedBitmap?.Dispose();
        _committedBitmap = null;
        ImageCanvas.RemoveFromVisualTree();
        GC.SuppressFinalize(this);
    }
}
