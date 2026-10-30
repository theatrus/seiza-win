using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Seiza.App.Models;
using Seiza.App.Rendering;
using Seiza.App.Services;
using Seiza.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Seiza.App;

public sealed partial class MainPage : Page, IDisposable
{
    private const float MinimumScale = 0.000001f;
    private const float MaximumScale = 64.0f;

    private readonly List<string> _imagePaths = [];
    private readonly OverlayOptions _overlayOptions = new();
    private readonly OverlayScene _overlayScene = new();
    private readonly FitsImageProcessingHistory _processingHistory = new();

    private CanvasDisplayImage? _bitmap;
    private CanvasDisplayImage? _committedBitmap;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _solveCancellation;
    private CancellationTokenSource? _starAnalysisCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private ImageMetadata? _currentMetadata;
    private SolveResult? _solveResult;
    private string? _currentPath;
    private Vector2 _offset;
    private Vector2 _dragStart;
    private Vector2 _offsetAtDragStart;
    private float _scale = 1.0f;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _selectedIndex = -1;
    private int _loadGeneration;
    private int _documentGeneration;
    private bool _isDragging;
    private bool _isFitToWindow = true;
    private bool _isInspectorOpen;
    private bool _isBrowserOpen;
    private bool _syncingBrowserSelection;
    private bool _isPickingSymmetryPoint;
    private bool _isAnalyzingStars;
    private bool _didCheckForUpdates;
    private bool _disposed;
    private FitsStretchWindow? _stretchWindow;
    private ImageStackWindow? _stackWindow;
    private LiveStackWindow? _liveStackWindow;
    private MainWindow? _ownerWindow;

    public MainPageViewModel ViewModel { get; } = new();

    public ObservableCollection<ImageBrowserItemViewModel> BrowserItems { get; } = [];

    internal bool HasImage => _currentPath is not null;

    internal string? CurrentPath => _currentPath;

    internal void AttachWindow(MainWindow window) => _ownerWindow = window;

    private nint OwnerWindowHandle => _ownerWindow is null
        ? throw new InvalidOperationException("The page is not attached to a window.")
        : WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow);

    public MainPage()
    {
        InitializeComponent();
        InitializeImageBrowser();
        ViewModel.PropertyChanged += (_, _) => UpdateVisualState();
        InspectorControl.SolveRequested += InspectorControl_SolveRequested;
        InspectorControl.ExportWcsRequested += InspectorControl_ExportWcsRequested;
        InspectorControl.StarAnalysisRequested += InspectorControl_StarAnalysisRequested;
        InspectorControl.FlatInspection.AvailabilityChanged += (_, _) => InspectFlatButton.Visibility =
            InspectorControl.FlatInspection.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_didCheckForUpdates || !App.TryBeginAutomaticUpdateCheck())
        {
            return;
        }

        _didCheckForUpdates = true;
        await App.Updates.CheckForUpdatesAsync(this, userInitiated: false);
    }

    internal Task OpenPathAsync(string path) => ImagePathList.IsSupported(path)
        ? OpenImageListAsync(path) : OpenImageAndDiscoverSiblingsAsync(path);

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> paths = await ImageFileService.PickImagesAsync(OwnerWindowHandle);
        if (paths.Count > 0)
        {
            await App.OpenDocumentPathsAsync(paths, _ownerWindow);
        }
    }

    private async void OpenImageList_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await ImageFileService.PickImageListAsync(OwnerWindowHandle);
            if (path is not null && !_disposed)
            {
                await App.OpenImageListInWindowAsync(path, _ownerWindow);
            }
        }
        catch (Exception exception)
        {
            if (!_disposed) ViewModel.ErrorMessage = DescribeException(exception);
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = await ImageFileService.PickFolderAsync(OwnerWindowHandle);
        if (path is not null)
        {
            await App.OpenFolderInWindowAsync(path, _ownerWindow);
        }
    }

    private async void ParallaxVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath is null || ViewModel.IsLoading) return;
        string sourcePath = _currentPath;
        int generation = _loadGeneration;
        bool StillSameDocument() => generation == _loadGeneration &&
            string.Equals(sourcePath, _currentPath, StringComparison.OrdinalIgnoreCase);
        var session = new ParallaxSession(sourcePath, CurrentProcessing(), _solveResult?.Wcs, DispatcherQueue,
            () => StillSameDocument() ? _solveResult : null,
            result =>
            {
                if (!StillSameDocument() || ViewModel.IsSolving) return;
                _solveResult = result;
                _overlayScene.SolveOverlay = new SolveOverlayRenderer(result, _sourceWidth, _sourceHeight);
                ViewModel.CompleteSolve(result);
                InspectorControl.ShowSolveResult(result);
                ApplyOverlayAvailability(result);
                ImageCanvas.Invalidate();
            });
        var window = new ParallaxWindow(session);
        App.RegisterParallaxWindow(window);
        window.OpenCatalogSettingsRequested += (_, _) => App.ShowCatalogSettings();
        // Parallax owns a source snapshot, not the viewer's disposable CanvasBitmap.
        window.Activate();
        await session.InitializeAsync();
    }

    private async void StackImages_Click(object sender, RoutedEventArgs e)
    {
        if (_stackWindow is not null)
        {
            _stackWindow.Activate();
            return;
        }

        string[] paths = GetStackableBrowserPaths().ToArray();
        if (paths.Length < 2)
        {
            return;
        }

        var window = new ImageStackWindow(paths, _browserRoot);
        _stackWindow = window;
        UpdateVisualState();
        window.Activate();
        try
        {
            ImageStackBatchResult? result = await window.Completion;
            if (_disposed || result is null || result.OutputPaths.Count == 0)
            {
                return;
            }

            await OpenImageAndDiscoverSiblingsAsync(result.OutputPaths[0]);
            if (result.Results.Count > 1 ||
                result.RejectedFrames > 0 ||
                result.Results.Any(item =>
                    item.SnrAnalysis.Points.Count > 1 ||
                    item.SnrWarning is not null ||
                    item.TransientRemovalNote is not null ||
                    item.ReferenceSelection?.Skipped.Length > 0))
            {
                string outputs = string.Join(
                    Environment.NewLine,
                    result.Results.Select(item =>
                        $"{Path.GetFileName(item.OutputPath)}: " +
                        $"{item.AcceptedFrames} accepted, {item.RejectedFrames} rejected" +
                        DescribeStackSnrInline(item.SnrAnalysis)));
                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(new TextBlock
                {
                    Text = outputs,
                    TextWrapping = TextWrapping.Wrap,
                });
                ImageStackResult? chartResult = result.Results.Count == 1
                    ? result.Results[0]
                    : null;
                if (chartResult?.SnrAnalysis.Points.Count > 1)
                {
                    var chart = new StackSnrChart { Height = 220 };
                    chart.SetPoints(chartResult.SnrAnalysis.Points);
                    content.Children.Add(chart);
                    content.Children.Add(new TextBlock
                    {
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        Text = DescribeStackSnr(chartResult.SnrAnalysis),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                string[] analysisWarnings = result.Results
                    .Select(item => item.SnrWarning)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (analysisWarnings.Length > 0)
                {
                    content.Children.Add(new TextBlock
                    {
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                            "TextFillColorSecondaryBrush"],
                        Text = string.Join(Environment.NewLine, analysisWarnings),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                foreach (ImageStackResult item in result.Results.Where(item => item.CanvasNote is not null))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = $"{Path.GetFileName(item.OutputPath)}: {item.CanvasNote}",
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                string[] transientNotes = result.Results
                    .Where(item => item.TransientRemovalNote is not null)
                    .Select(item => result.Results.Count == 1
                        ? item.TransientRemovalNote!
                        : $"{Path.GetFileName(item.OutputPath)}: {item.TransientRemovalNote}")
                    .ToArray();
                if (transientNotes.Length > 0)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = string.Join(Environment.NewLine, transientNotes),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                foreach (ImageStackResult item in result.Results.Where(item => item.ReferenceSelection?.Skipped.Length > 0))
                {
                    StackAlignmentReferenceSelection selection = item.ReferenceSelection!;
                    content.Children.Add(new TextBlock
                    {
                        Text = $"{Path.GetFileName(item.OutputPath)}: using channel reference " +
                            $"{Path.GetFileName(selection.ChannelReferencePath)}." + Environment.NewLine +
                            string.Join(Environment.NewLine, selection.Skipped.Select(skipped =>
                                $"Could not align {Path.GetFileName(skipped.Path)} as reference: {skipped.Reason}")),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = result.Results.Count == 1 ? "Stack complete" : "Stacks complete",
                    Content = content,
                    CloseButtonText = "Done",
                    DefaultButton = ContentDialogButton.Close,
                };
                await dialog.ShowAsync();
            }
        }
        finally
        {
            if (ReferenceEquals(_stackWindow, window))
            {
                _stackWindow = null;
            }
            if (!_disposed)
            {
                UpdateVisualState();
            }
        }
    }

    private static string DescribeStackSnrInline(StackSnrAnalysis analysis) =>
        analysis.Points.Count > 1
            ? $" · noise {analysis.NoiseImprovement:N2}× lower"
            : string.Empty;

    private static string DescribeStackSnr(StackSnrAnalysis analysis) =>
        $"Measured noise improved {analysis.NoiseImprovement:N2}×; " +
        $"an ideal square-root stack at this depth improves {analysis.IdealImprovement:N2}× " +
        $"({analysis.Efficiency:P0} efficiency).";

    private void ColorStack_Click(object sender, RoutedEventArgs e) => App.ShowColorStack();

    private async void LiveStack_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        if (_liveStackWindow is not null)
        {
            _liveStackWindow.Activate();
            return;
        }

        string? initialFolder = _currentPath is null
            ? null
            : Path.GetDirectoryName(_currentPath);
        if (initialFolder is null || !Directory.Exists(initialFolder))
        {
            initialFolder = await ImageFileService.PickFolderAsync(OwnerWindowHandle);
        }
        if (initialFolder is null || _disposed)
        {
            return;
        }

        var window = new LiveStackWindow(initialFolder);
        _liveStackWindow = window;
        UpdateVisualState();
        window.Activate();
        try
        {
            string? outputPath = await window.Completion;
            if (!_disposed &&
                !string.IsNullOrWhiteSpace(outputPath) &&
                File.Exists(outputPath))
            {
                await OpenImageAndDiscoverSiblingsAsync(outputPath);
            }
        }
        finally
        {
            if (ReferenceEquals(_liveStackWindow, window))
            {
                _liveStackWindow = null;
            }
            if (!_disposed)
            {
                UpdateVisualState();
            }
        }
    }

    private void ImageBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (!CanBrowseImages)
        {
            return;
        }

        _isBrowserOpen = !_isBrowserOpen;
        UpdateVisualState();
        if (_isBrowserOpen)
        {
            RevealBrowserSelection();
            SyncBrowserSelection(scrollIntoView: true);
            _ = PrefetchThumbnailsAroundSelectionAsync();
        }
    }

    private async void ImageBrowserList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingBrowserSelection || _disposed || _isScanningFolder ||
            ImageBrowserList.SelectedItem is not ImageBrowserItemViewModel item)
        {
            return;
        }

        int index = BrowserItems.IndexOf(item);
        if (index < 0 || index == _selectedIndex) return;
        _selectedIndex = index;
        UpdateNavigationState();
        await LoadImageAsync(
            _imagePaths[_selectedIndex],
            processingOverride: _processingHistory.Current);
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
        int previous = FindBrowserNavigationIndex(-1);
        if (!_isScanningFolder && previous >= 0)
        {
            _selectedIndex = previous;
            RevealBrowserSelection();
            UpdateNavigationState();
            await LoadImageAsync(
                _imagePaths[_selectedIndex],
                processingOverride: _processingHistory.Current);
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        int next = FindBrowserNavigationIndex(1);
        if (!_isScanningFolder && next >= 0)
        {
            _selectedIndex = next;
            RevealBrowserSelection();
            UpdateNavigationState();
            await LoadImageAsync(
                _imagePaths[_selectedIndex],
                processingOverride: _processingHistory.Current);
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
            currentProcessing.BackgroundConfiguration,
            currentProcessing.Deconvolution,
            SupportsColorStretch(_currentMetadata),
            _currentMetadata!.Headers.TryGetValue("SEIZAPCC", out var marker) && marker.ToString() == "GAIA");
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
            FitsBackgroundConfiguration? requestedBackground =
                window.ResultBackgroundConfiguration;
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

    private void Inspector_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.HasImage) return;
        _isInspectorOpen = !_isInspectorOpen;
        UpdateVisualState();
        args.Handled = true;
    }

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = false;
        UpdateVisualState();
    }

    private void CatalogSettings_Click(object sender, RoutedEventArgs e) =>
        App.ShowCatalogSettings();

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await App.Updates.CheckForUpdatesAsync(this, userInitiated: true);

    private async void Solve_Click(object sender, RoutedEventArgs e) =>
        await SolveCurrentAsync();

    private async void AnalyzeStars_Click(object sender, RoutedEventArgs e) =>
        await AnalyzeCurrentStarsAsync();

    private async void InspectFlat_Click(object sender, RoutedEventArgs e)
    {
        _isInspectorOpen = true; UpdateVisualState();
        await InspectorControl.FlatInspection.InspectAsync();
    }

    private async void InspectorControl_SolveRequested(object? sender, EventArgs e) =>
        await SolveCurrentAsync();

    private async void InspectorControl_ExportWcsRequested(object? sender, EventArgs e) =>
        await ExportWcsAsync();

    private async void InspectorControl_StarAnalysisRequested(object? sender, EventArgs e) =>
        await AnalyzeCurrentStarsAsync();

    private async Task AnalyzeCurrentStarsAsync()
    {
        if (_bitmap is null ||
            _currentPath is null ||
            !ImageFileService.IsAstronomyImage(_currentPath) ||
            _isAnalyzingStars)
        {
            return;
        }

        _starAnalysisCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _starAnalysisCancellation = cancellation;
        int documentGeneration = _documentGeneration;
        string path = _currentPath;
        _overlayScene.StarAnalysisOverlay = null;
        _isAnalyzingStars = true;
        _isInspectorOpen = true;
        InspectorControl.BeginStarAnalysis();
        ImageCanvas.Invalidate();
        UpdateVisualState();

        try
        {
            StarAnalysisResult result = await StarAnalysisService.Shared.AnalyzeAsync(
                path,
                StarAnalysisOptions.InteractiveDefault,
                cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (documentGeneration != _documentGeneration ||
                !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (result.Width != _sourceWidth || result.Height != _sourceHeight)
            {
                throw new InvalidOperationException(
                    $"Star analysis returned {result.Width:N0} × {result.Height:N0} for a " +
                    $"{_sourceWidth:N0} × {_sourceHeight:N0} source image.");
            }

            _overlayScene.StarAnalysisOverlay = new StarAnalysisOverlayRenderer(
                result,
                _sourceWidth,
                _sourceHeight);
            _overlayOptions.ShowMeasuredStars = true;
            _overlayOptions.ShowSensorTilt = true;
            InspectorControl.ShowStarAnalysisResult(result);
            SyncOverlayControls();
            ImageCanvas.Invalidate();
        }
        catch (OperationCanceledException)
        {
            // Image navigation or a newer analysis intentionally superseded this work.
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested &&
                documentGeneration == _documentGeneration &&
                string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                InspectorControl.ShowStarAnalysisFailure(DescribeException(exception));
            }
        }
        finally
        {
            if (ReferenceEquals(_starAnalysisCancellation, cancellation))
            {
                _starAnalysisCancellation = null;
                _isAnalyzingStars = false;
            }
            cancellation.Dispose();
            UpdateVisualState();
        }
    }

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
        _overlayScene.SolveOverlay = null;
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
            _overlayScene.SolveOverlay = new SolveOverlayRenderer(
                result,
                _sourceWidth,
                _sourceHeight);
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
            await WcsExportService.PickAndSaveAsync(
                OwnerWindowHandle,
                _currentPath,
                _solveResult.Wcs);
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
        else if (ReferenceEquals(sender, MeasuredStarsOverlayItem))
        {
            _overlayOptions.ShowMeasuredStars = MeasuredStarsOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, SensorTiltOverlayItem))
        {
            _overlayOptions.ShowSensorTilt = SensorTiltOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, ParallelogramTiltOverlayItem))
        {
            _overlayOptions.ShowParallelogramTilt = ParallelogramTiltOverlayItem.IsChecked;
        }
        else if (ReferenceEquals(sender, TriangleTiltOverlayItem))
        {
            _overlayOptions.ShowTriangleTilt = TriangleTiltOverlayItem.IsChecked;
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

    private async void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        try
        {
            await ClipboardService.CopyImageAsync(_bitmap.Snapshot());
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
        if (_committedBitmap is null || _currentPath is null || ViewModel.IsExporting ||
            (includeOverlays && !_overlayScene.HasVisibleOverlays(_overlayOptions)))
        {
            return;
        }

        CanvasDisplayImage committedBitmap = _committedBitmap;
        string sourcePath = _currentPath;
        int sourceWidth = _sourceWidth;
        int sourceHeight = _sourceHeight;
        int documentGeneration = _documentGeneration;
        FitsImageProcessingConfiguration processing = CurrentProcessing();
        OverlayScene overlayScene = _overlayScene.Snapshot();
        OverlayOptions overlayOptions = _overlayOptions.Snapshot();
        bool overlaysAvailable = overlayScene.HasVisibleOverlays(overlayOptions);
        ImageExportRequest? request = await ImageExportService.PickOptionsAsync(
            XamlRoot,
            ImageFileService.IsAstronomyImage(sourcePath),
            overlaysAvailable,
            includeOverlays);
        if (request is null)
        {
            return;
        }

        ImageExportDestination? destination = await ImageExportService.PickDestinationAsync(
            OwnerWindowHandle,
            sourcePath,
            request);
        if (destination is null)
        {
            return;
        }
        if (documentGeneration != _documentGeneration ||
            !ReferenceEquals(committedBitmap, _committedBitmap))
        {
            return;
        }

        ViewModel.IsExporting = true;
        ViewModel.ErrorMessage = null;
        try
        {
            if (request.BitDepth == ImageExportBitDepth.Sixteen)
            {
                RenderedImage16Data rendered = await ImageRenderService.Render16Async(
                    sourcePath,
                    processing);
                if (request.IncludeVisibleOverlays)
                {
                    TiledOverlayRasterizer.Draw(ImageCanvas, sourceWidth, sourceHeight,
                        drawing => overlayScene.Draw(drawing, overlayOptions, 1, 1, Vector2.Zero),
                        (region, overlay) => {
                            for (int row = 0; row < region.Height; row++)
                                Rgba16Compositor.CompositePremultipliedBgra8(
                                    MemoryMarshal.Cast<byte, ushort>(rendered.RgbaBytes.AsSpan()).Slice(
                                        ((region.Y + row) * sourceWidth + region.X) * 4, region.Width * 4),
                                    overlay.AsSpan(row * region.Width * 4, region.Width * 4));
                        });
                }
                await ImageExportService.Save16Async(rendered, destination);
                return;
            }

            // The CPU snapshot survives navigation without another full-image
            // GPU allocation. Only overlay exports need a mutable pixel copy.
            RenderedImageData display = committedBitmap.Snapshot();
            if (request.IncludeVisibleOverlays)
            {
                display = display with { Bgra = (byte[])display.Bgra.Clone() };
                TiledOverlayRasterizer.Draw(ImageCanvas, sourceWidth, sourceHeight,
                    drawing => overlayScene.Draw(drawing, overlayOptions, 1, 1, Vector2.Zero),
                    (region, overlay) => {
                        for (int row = 0; row < region.Height; row++)
                            Bgra8Compositor.CompositePremultiplied(
                                display.Bgra.AsSpan(((region.Y + row) * sourceWidth + region.X) * 4, region.Width * 4),
                                overlay.AsSpan(row * region.Width * 4, region.Width * 4));
                    });
            }
            await ImageExportService.Save8Async(display, destination);
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

    private void NewWindow_Click(object sender, RoutedEventArgs e) => CreateNewWindow();

    private void NewWindow_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        CreateNewWindow();
        args.Handled = true;
    }

    private void CreateNewWindow()
    {
        if (_ownerWindow is not null)
        {
            App.CreateDocumentWindow();
        }
    }

    private async Task OpenImageAndDiscoverSiblingsAsync(string path)
    {
        if (_disposed) return;
        var cancellation = BeginBrowserDiscovery();
        _isScanningFolder = false;
        try
        {
            IReadOnlyList<string> siblings = [path];
            string? folder = Path.GetDirectoryName(path);
            if (folder is not null)
            {
                try
                {
                    siblings = await ImageFileService.GetImagesAsync(folder, cancellation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The selected image is still usable if sibling enumeration is unavailable.
                }
            }
            if (!IsCurrentBrowserDiscovery(cancellation)) return;
            SetCollection(siblings.Count == 0 ? [path] : siblings, path, cancelDiscovery: false);
            await LoadImageAsync(path);
        }
        catch (OperationCanceledException)
        {
            // A newer document or folder superseded this discovery.
        }
        finally
        {
            FinishBrowserDiscovery(cancellation);
        }
    }

    private async Task<bool> LoadImageAsync(
        string path,
        bool preserveSolution = false,
        FitsImageProcessingConfiguration? processingOverride = null)
    {
        if (_disposed) return false;
        bool isNewImage = !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);
        bool resetStarAnalysis = StarAnalysisOverlayLifecycle.ShouldResetForLoad(
            isNewImage,
            preserveSolution);
        if (resetStarAnalysis)
        {
            _documentGeneration++;
            EndSymmetryPointPicker(showEditor: false);
            _stretchWindow?.Close();
            ResetStarAnalysisForImageChange();
        }
        FitsImageProcessingConfiguration processing = processingOverride ??
            (isNewImage
                ? FitsImageProcessingConfiguration.Default.Clone()
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
            ImageSourceHeader? sourceHeader = await Task.Run(() => ImageSourceService.TryReadHeader(path), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (isNewImage && processingOverride is null)
                processing = ColorWorkflowService.InitialProcessingFromHeader(sourceHeader);
            RenderedImageData image = await ImageRenderService.RenderAsync(
                path,
                processing,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || generation != _loadGeneration)
            {
                return false;
            }

            // A command-line/list activation can finish rendering before the
            // CanvasControl's first resource event. It uses this shared device
            // too; creating the bitmap directly avoids that startup race.
            CanvasDisplayImage nextBitmap = await CanvasDisplayImage.CreateAsync(CanvasDevice.GetSharedDevice(), image, cancellationToken: cancellationToken);
            if (_disposed || generation != _loadGeneration) { nextBitmap.Dispose(); return false; }

            SetCommittedBitmap(nextBitmap);
            _sourceWidth = image.Width;
            _sourceHeight = image.Height;
            _currentPath = path;
            _currentMetadata = image.Metadata;
            if (isNewImage && processingOverride is null)
            {
                _processingHistory.Reset(processing);
            }
            InspectorControl.ShowMetadata(image.Metadata, processing);
            InspectorControl.FlatInspection.SetSource(path);
            InspectorControl.SourcePixels.SetSource(path);
            InspectorControl.ShowSourceCommentary(sourceHeader);
            ViewModel.CompleteLoading(path, image.Metadata);
            _ownerWindow?.SetDocumentTitle(Path.GetFileName(path));

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
            if (!_disposed && generation == _loadGeneration)
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

        CanvasDisplayImage nextBitmap = await CanvasDisplayImage.CreateAsync(CanvasDevice.GetSharedDevice(), image, cancellationToken: request.CancellationToken);
        if (_stretchWindow is null || !string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
        { nextBitmap.Dispose(); return; }
        if (!ReferenceEquals(_bitmap, _committedBitmap))
        {
            _bitmap?.Dispose();
        }
        _bitmap = nextBitmap;
        ImageCanvas.Invalidate();
    }

    private FitsImageProcessingConfiguration CurrentProcessing(bool interactivePreview = false)
    {
        var processing = _processingHistory.Current.Clone(interactivePreview);
        processing.PreserveDisplaySamples = _currentMetadata?.Headers.TryGetValue("SEIZATRF", out JsonElement transfer) == true &&
            transfer.ValueKind == JsonValueKind.String && transfer.GetString() == "DISPLAY";
        return processing;
    }

    private void SetCommittedBitmap(CanvasDisplayImage bitmap)
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
        CanvasImageInterpolation interpolation = ImageSampling.NeedsFiltering(
            _bitmap.Width, _bitmap.Height,
            _sourceWidth, _sourceHeight,
            destination.Width, destination.Height, sender.Dpi / 96.0)
            ? CanvasImageInterpolation.HighQualityCubic
            : CanvasImageInterpolation.NearestNeighbor;
        _bitmap.Draw(args.DrawingSession, destination, interpolation);
        _overlayScene.Draw(args.DrawingSession, _overlayOptions, _scale, _scale, _offset);
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

        int bitmapWidth = _committedBitmap.Width;
        int bitmapHeight = _committedBitmap.Height;
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
        if (_isInspectorOpen && _currentPath is not null && _scale > 0)
        {
            var pointer = e.GetCurrentPoint(ImageCanvas).Position;
            Vector2 source = (new Vector2((float)pointer.X, (float)pointer.Y) - _offset) / _scale;
            if (source.X >= 0 && source.Y >= 0 && source.X < _sourceWidth && source.Y < _sourceHeight)
                InspectorControl.SourcePixels.SetPoint((int)Math.Floor(source.X), (int)Math.Floor(source.Y));
            else InspectorControl.SourcePixels.ClearPoint();
        }
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
        ViewModel.CanNavigatePrevious = !_isScanningFolder && FindBrowserNavigationIndex(-1) >= 0;
        ViewModel.CanNavigateNext = !_isScanningFolder && FindBrowserNavigationIndex(1) >= 0;
        ViewModel.PositionText = _imagePaths.Count > 1 && _selectedIndex >= 0
            ? $"{_selectedIndex + 1:N0} of {_imagePaths.Count:N0}"
            : string.Empty;
        SyncBrowserSelection(scrollIntoView: _isBrowserOpen);
    }

    private void SyncBrowserSelection(bool scrollIntoView)
    {
        _syncingBrowserSelection = true;
        try
        {
            ImageBrowserItemViewModel? selected = _selectedIndex >= 0 && _selectedIndex < BrowserItems.Count
                && _visibleBrowserPaths.Contains(_imagePaths[_selectedIndex])
                ? BrowserItems[_selectedIndex] : null;
            ImageBrowserList.SelectedItem = selected;
            if (scrollIntoView && selected is not null)
            {
                ImageBrowserList.ScrollIntoView(selected);
            }
        }
        finally
        {
            _syncingBrowserSelection = false;
        }
    }

    private async Task PrefetchThumbnailsAroundSelectionAsync()
    {
        if (_disposed || _isScanningFolder) return;
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

            // Snapshot instances before awaiting: regrouping and rescanning may
            // replace/reorder the flat collection while a thumbnail is decoded.
            ImageBrowserItemViewModel[] items = indexes.Select(index => BrowserItems[index]).ToArray();
            foreach (ImageBrowserItemViewModel item in items)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await item.EnsureLoadedAsync(cancellation.Token);
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
        if (_disposed)
        {
            return;
        }
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
        StarAnalysisButton.Label = _isAnalyzingStars ? "Analyzing…" : "Analyze stars";
        StarAnalysisButton.IsEnabled =
            _bitmap is not null &&
            _currentPath is not null &&
            ImageFileService.IsAstronomyImage(_currentPath) &&
            !ViewModel.IsLoading &&
            !_isAnalyzingStars;
        UpdateOverlayMenuAvailability();
        OverlayButton.IsEnabled = _overlayScene.HasAnyOverlay;
        ExportButton.Label = ViewModel.IsExporting ? "Exporting..." : "Export";
        ExportButton.IsEnabled =
            _bitmap is not null &&
            _currentPath is not null &&
            !ViewModel.IsLoading &&
            !ViewModel.IsExporting;
        CopyImageItem.IsEnabled = _bitmap is not null && !ViewModel.IsLoading;
        bool supportsAstronomyProcessing = SupportsAstronomyProcessing(_currentMetadata);
        CopyAdjustmentsItem.IsEnabled = supportsAstronomyProcessing && !ViewModel.IsLoading;
        PasteAdjustmentsItem.IsEnabled = supportsAstronomyProcessing && !ViewModel.IsLoading;
        ImageBrowserButton.IsEnabled = CanBrowseImages;
        StackImagesButton.IsEnabled =
            _stackWindow is null &&
            !_isScanningFolder &&
            !ViewModel.IsLoading &&
            GetStackableBrowserPaths().Take(2).Count() >= 2;
        ToolTipService.SetToolTip(StackImagesButton, _browserRoot is not null && _includeSubfolders
            ? "Choose FITS/XISF frames from this directory tree to stack (including collapsed groups)"
            : "Choose FITS/XISF frames from the current folder to stack");
        LiveStackButton.IsEnabled = _liveStackWindow is null && !ViewModel.IsLoading;
        ParallaxVideoButton.IsEnabled = _currentPath is not null && _bitmap is not null && !ViewModel.IsLoading;
        ImageBrowserButton.Label = _isBrowserOpen ? "Hide images" : "Images";
        ImageBrowserPane.Visibility = _isBrowserOpen && CanBrowseImages
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateBrowserControls();
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
        _overlayScene.SolveOverlay = null;
        ViewModel.ResetSolve();
        InspectorControl.ResetSolve();
        ImageCanvas.Invalidate();
    }

    private void ResetStarAnalysisForImageChange()
    {
        _starAnalysisCancellation?.Cancel();
        _starAnalysisCancellation = null;
        _overlayScene.StarAnalysisOverlay = null;
        _isAnalyzingStars = false;
        InspectorControl.ResetStarAnalysis();
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
        MeasuredStarsOverlayItem.IsChecked = _overlayOptions.ShowMeasuredStars;
        SensorTiltOverlayItem.IsChecked = _overlayOptions.ShowSensorTilt;
        ParallelogramTiltOverlayItem.IsChecked = _overlayOptions.ShowParallelogramTilt;
        TriangleTiltOverlayItem.IsChecked = _overlayOptions.ShowTriangleTilt;

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

    private void UpdateOverlayMenuAvailability()
    {
        MeasuredStarsOverlayItem.IsEnabled = _overlayScene.HasStarAnalysisOverlay;
        SensorTiltOverlayItem.IsEnabled = _overlayScene.HasStarAnalysisOverlay;
        ParallelogramTiltOverlayItem.IsEnabled =
            _overlayScene.HasParallelogramTiltOverlay;
        TriangleTiltOverlayItem.IsEnabled = _overlayScene.HasTriangleTiltOverlay;

        bool hasSolveOverlay = _overlayScene.HasSolveOverlay && _solveResult is not null;
        if (hasSolveOverlay)
        {
            ApplyOverlayAvailability(_solveResult!);
        }
        else
        {
            DeepSkyOverlayItem.IsEnabled = false;
            DeepSkyOverlayItem.Text = "Deep-sky objects";
            NamedStarsOverlayItem.IsEnabled = false;
            NamedStarsOverlayItem.Text = "Named stars";
            TransientsOverlayItem.IsEnabled = false;
            TransientsOverlayItem.Text = "Current transients";
            HistoricalTransientsOverlayItem.IsEnabled = false;
            HistoricalTransientsOverlayItem.Text = "Historical transients";
            MinorBodiesOverlayItem.IsEnabled = false;
            MinorBodiesOverlayItem.Text = "Solar-system bodies";
        }

        CatalogOutlinesOverlayItem.IsEnabled = hasSolveOverlay;
        ObjectLabelsOverlayItem.IsEnabled = hasSolveOverlay;
        FieldStarsOverlayItem.IsEnabled = hasSolveOverlay;
        CoordinateGridOverlayItem.IsEnabled = hasSolveOverlay;
        FieldCenterOverlayItem.IsEnabled = hasSolveOverlay;
        DetectedStarsOverlayItem.IsEnabled = hasSolveOverlay;

        bool hasDeepSkyOverlay = hasSolveOverlay && DeepSkyOverlayItem.IsEnabled;
        MessierCatalogItem.IsEnabled = hasDeepSkyOverlay;
        NgcCatalogItem.IsEnabled = hasDeepSkyOverlay;
        IcCatalogItem.IsEnabled = hasDeepSkyOverlay;
        SharplessVdbCatalogItem.IsEnabled = hasDeepSkyOverlay;
        LbnCatalogItem.IsEnabled = hasDeepSkyOverlay;
        CederbladCatalogItem.IsEnabled = hasDeepSkyOverlay;
        DarkNebulaeCatalogItem.IsEnabled = hasDeepSkyOverlay;
        SupernovaRemnantsCatalogItem.IsEnabled = hasDeepSkyOverlay;
        UgcCatalogItem.IsEnabled = hasDeepSkyOverlay;
        PgcCatalogItem.IsEnabled = hasDeepSkyOverlay;
        OtherCatalogItem.IsEnabled = hasDeepSkyOverlay;
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
            case StorageFile file when ImageFileTypes.IsOpenableFile(file.Path):
                await OpenPathAsync(file.Path);
                break;
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CancelBrowserDiscovery();
        _loadGeneration++;
        _solveCancellation?.Cancel();
        _solveCancellation = null;
        _starAnalysisCancellation?.Cancel();
        _starAnalysisCancellation = null;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = null;
        EndSymmetryPointPicker(showEditor: false);
        _stretchWindow?.Close();
        _stretchWindow = null;
        _stackWindow?.Close();
        _stackWindow = null;
        _liveStackWindow?.Close();
        _liveStackWindow = null;
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
