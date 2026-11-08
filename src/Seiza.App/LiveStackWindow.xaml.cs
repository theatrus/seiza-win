using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App;

public sealed partial class LiveStackWindow : Window, IDisposable
{
    private static readonly SemaphoreSlim CalibrationReviewGate = new(1, 1);
    private void ColorStack_Click(object sender, RoutedEventArgs e) => App.ShowColorStack();

    private readonly TaskCompletionSource<string?> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ImageStackCalibration _manualCalibration = new();
    private readonly DispatcherTimer _checkpointAgeTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30),
    };
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _preparationCancellation;
    private TaskCompletionSource<bool>? _preparationCompletion;
    private LiveStackCoordinator? _coordinator;
    private LiveStackSessionCoordinator? _session;
    private bool _updatingGroups;
    private LiveStackFilterGroup[] _displayedGroups = [];
    private int _groupRefreshQueued;
    private Task? _runTask;
    private CanvasBitmap? _previewBitmap;
    private RenderedImageData? _displayedPreview;
    private CalibrationPreparationResult? _preparedCalibration;
    private ContentDialog? _activeDialog;
    private string? _initialReferencePath;
    private string? _configurationNotice;
    private InfoBarSeverity _configurationNoticeSeverity = InfoBarSeverity.Informational;
    private string? _runOperationError;
    private bool _initializing = true;
    private bool _loaded;
    private bool _busy;
    private bool _closed;
    private bool _allowClose;
    private bool _closeInProgress;
    private readonly Func<bool> _useGpuSetting;
    private readonly Func<int?> _gpuMemorySetting;

    internal LiveStackWindow(string initialFolder, Func<bool>? useGpuSetting = null, Func<int?>? gpuMemorySetting = null)
    {
        _useGpuSetting = useGpuSetting ?? (() => CatalogSettingsStore.LoadUseGpuForStacking());
        _gpuMemorySetting = gpuMemorySetting ?? (() => CatalogSettingsStore.LoadStackingGpuMemoryMib());
        InitializeComponent();
        MinimumWeightBox.Value = new ImageStackOptions().MinimumWeight;
        WatchFolderTextBox.Text = Path.GetFullPath(initialFolder);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(LiveStackTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ContentRoot.Loaded += ContentRoot_Loaded;
        _checkpointAgeTimer.Tick += CheckpointAgeTimer_Tick;
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        _initializing = false;
        UpdateConfigurationState();
    }

    internal Task<string?> Completion => _completion.Task;

    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private bool IsConfigured => ConfigurationScrollViewer.Visibility == Visibility.Visible;

    private bool ShowingOverview => _session is not null && AllFiltersToggle.IsOn;

    private async void RunningStretch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _closed || _busy || IsConfigured || (_session is null && _coordinator is null)) return;
        SetBusy(true);
        try
        {
            var type = Enum.Parse<FitsStretchType>(SelectedTag(RunningStretchPicker));
            if (_session is not null) await _session.SetPreviewStretchAsync(type);
            else if (_coordinator is not null) await _coordinator.SetPreviewStretchAsync(type);
        }
        catch (Exception exception) { if (!_closed) ShowRunError(exception.Message); }
        finally { SetBusy(false); }
    }

    private void AllFilters_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _closed || _session is null) return;
        RefreshSessionGroups();
    }

    private void OpenOverviewFilter(string key)
    {
        if (_closed || _busy) return;
        LiveStackFilterGroup? group = _displayedGroups.FirstOrDefault(group => group.Filter.Key == key);
        if (group is null) return;
        SelectGroup(group);
        AllFiltersToggle.IsOn = false;
        RefreshSessionGroups();
    }

    private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        double scale = ContentRoot.XamlRoot.RasterizationScale;
        int margin = Math.Max(24, (int)Math.Round(24 * scale));
        int width = Math.Min((int)Math.Round(1240 * scale), workArea.Width - (margin * 2));
        int height = Math.Min((int)Math.Round(860 * scale), workArea.Height - (margin * 2));
        int x = workArea.X + ((workArea.Width - width) / 2);
        int y = workArea.Y + ((workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private async void ChooseWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync();
        if (folder is null || _closed || _closeInProgress)
        {
            return;
        }
        WatchFolderTextBox.Text = folder;
        _initialReferencePath = null;
        ReplacePreparedCalibration(null);
        CalibrationSummaryText.Text = string.Empty;
        UpdateConfigurationState();
    }

    private async void ChooseCalibrationFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync();
        if (folder is null || _closed || _closeInProgress)
        {
            return;
        }
        CalibrationFolderTextBox.Text = folder;
        ReplacePreparedCalibration(null);
        CalibrationSummaryText.Text = "Masters will be matched and built when the live stack starts.";
        UpdateConfigurationState();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void ChooseBias_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationMasterAsync(path => _manualCalibration.BiasPath = path);

    private async void ChooseDark_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationMasterAsync(path => _manualCalibration.DarkPath = path);

    private async void ChooseFlat_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationMasterAsync(path => _manualCalibration.FlatPath = path);

    private async void ChooseAlignmentReference_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
        };
        foreach (string extension in ImageFileTypes.AstronomyExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null && !_closed && !_closeInProgress)
        {
            AlignmentReferenceTextBox.Text = file.Path;
            _initialReferencePath = null;
            ReplacePreparedCalibration(null);
            ClearConfigurationNotice();
            UpdateConfigurationState();
        }
    }

    private void ClearAlignmentReference_Click(object sender, RoutedEventArgs e)
    {
        AlignmentReferenceTextBox.Text = string.Empty;
        _initialReferencePath = null;
        ReplacePreparedCalibration(null);
        ClearConfigurationNotice();
        UpdateConfigurationState();
    }

    private void ClearBias_Click(object sender, RoutedEventArgs e)
    {
        _manualCalibration.BiasPath = null;
        RefreshManualCalibration();
    }

    private void ClearDark_Click(object sender, RoutedEventArgs e)
    {
        _manualCalibration.DarkPath = null;
        RefreshManualCalibration();
    }

    private void ClearFlat_Click(object sender, RoutedEventArgs e)
    {
        _manualCalibration.FlatPath = null;
        RefreshManualCalibration();
    }

    private async Task ChooseCalibrationMasterAsync(Action<string> setPath)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
        };
        foreach (string extension in ImageFileTypes.AstronomyExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null && !_closed && !_closeInProgress)
        {
            setPath(file.Path);
            RefreshManualCalibration();
        }
    }

    private void RefreshManualCalibration()
    {
        BiasPathText.Text = DisplayCalibration(_manualCalibration.BiasPath);
        DarkPathText.Text = DisplayCalibration(_manualCalibration.DarkPath);
        FlatPathText.Text = DisplayCalibration(_manualCalibration.FlatPath);
        UpdateConfigurationState();
    }

    private static string DisplayCalibration(string? path) =>
        path is null ? "None" : Path.GetFileName(path);

    private void CalibrationMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            if (SelectedTag(CalibrationModePicker) != "Automatic")
            {
                _initialReferencePath = null;
                ReplacePreparedCalibration(null);
            }
            ClearConfigurationNotice();
            UpdateConfigurationState();
        }
    }

    private void ConfigurationSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            ClearConfigurationNotice();
            UpdateConfigurationState();
        }
    }

    private void Configuration_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            ClearConfigurationNotice();
            UpdateConfigurationState();
        }
    }

    private void ConfigurationNumber_Changed(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!_initializing)
        {
            ClearConfigurationNotice();
            UpdateConfigurationState();
        }
    }

    private void DarkExposureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        DarkExposureBox.IsEnabled = DarkExposureToggle.IsOn;
        ClearConfigurationNotice();
        UpdateConfigurationState();
    }

    private void UpdateConfigurationState()
    {
        string calibrationMode = SelectedTag(CalibrationModePicker);
        ManualCalibrationPanel.Visibility = calibrationMode == "Masters"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomaticCalibrationPanel.Visibility = calibrationMode == "Automatic"
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalTileRow.Visibility = SelectedTag(NormalizationPicker) is "Local" or "LocalBackground"
            ? Visibility.Visible
            : Visibility.Collapsed;
        WeightingOptionsPanel.Visibility = SelectedTag(WeightingPicker) == "InverseNoiseVariance"
            ? Visibility.Visible
            : Visibility.Collapsed;
        CosmeticOptionsPanel.Visibility = SuppressHotPixelsToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        RejectionOptionsPanel.Visibility = SelectedTag(RejectionPicker) == "DeltaSigma"
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool supportsSharedReference = StackingCoreCapabilities.Current.SupportsReferenceImage;
        ChooseAlignmentReferenceButton.IsEnabled = supportsSharedReference;
        ClearAlignmentReferenceButton.IsEnabled = AlignmentReferencePath is not null;
        AlignmentReferenceHelpText.Text = supportsSharedReference
            ? "Use the same FITS/XISF image in separate filter runs to share one output grid and WCS. It supplies geometry only: calibration, brightness and exposure come from this filter's lights. Match mono/RGB channel count and image scale. Automatic selection tries up to five ranked lights. Resume retains the saved alignment reference; selecting a different image starts a new accumulator."
            : "Shared alignment references need a newer Seiza core. This installed core does not export the required API; ordinary live stacking is unchanged.";

        ImageStackOptions options = CreateOptions();
        ImageStackCalibration calibration = CreateSelectedCalibration();
        string? validation = ValidateConfiguration(options, calibration, calibrationMode);
        if (validation is not null)
        {
            ConfigurationInfoBar.Severity = InfoBarSeverity.Error;
            ConfigurationInfoBar.Message = validation;
            ConfigurationInfoBar.IsOpen = true;
        }
        else if (_configurationNotice is not null)
        {
            ConfigurationInfoBar.Severity = _configurationNoticeSeverity;
            ConfigurationInfoBar.Message = _configurationNotice;
            ConfigurationInfoBar.IsOpen = true;
        }
        else
        {
            ConfigurationInfoBar.Message = string.Empty;
            ConfigurationInfoBar.IsOpen = false;
        }
        PrimaryButton.IsEnabled = !_busy && validation is null;
    }

    private ImageStackOptions CreateOptions() => new()
    {
        UseGpu = _useGpuSetting(),
        GpuMemoryMib = _gpuMemorySetting(),
        RegistrationModel = SelectedTag(RegistrationModelPicker) switch
        {
            "Affine" => StackRegistrationModel.Affine,
            "Quadratic" => StackRegistrationModel.Quadratic,
            _ => StackRegistrationModel.Similarity,
        },
        Normalization = SelectedTag(NormalizationPicker) switch
        {
            "None" => StackNormalizationMode.None,
            "Local" => StackNormalizationMode.Local,
            "LocalBackground" => StackNormalizationMode.LocalBackground,
            _ => StackNormalizationMode.Global,
        },
        LocalTileSize = IntegerValue(LocalTileSizeBox, 256),
        Weighting = SelectedTag(WeightingPicker) == "InverseNoiseVariance"
            ? StackWeightingMode.InverseNoiseVariance
            : StackWeightingMode.Equal,
        MinimumWeight = FiniteValue(MinimumWeightBox, 0.05),
        MaximumWeight = FiniteValue(MaximumWeightBox, 20),
        Interpolation = SelectedTag(InterpolationPicker) == "Lanczos3"
            ? StackInterpolation.Lanczos3
            : StackInterpolation.Bilinear,
        Demosaic = SelectedTag(DemosaicPicker) switch
        {
            "Mhc" => StackDemosaic.Mhc,
            "Bilinear" => StackDemosaic.Bilinear,
            _ => StackDemosaic.Vng,
        },
        CfaIntegration = SelectedTag(CfaIntegrationPicker) == "BayerDrizzle"
            ? StackCfaIntegration.BayerDrizzle
            : StackCfaIntegration.Demosaic,
        SuppressHotPixels = SuppressHotPixelsToggle.IsOn,
        LearnDefects = LearnDefectsToggle.IsOn,
        CosmeticLowSigma = FiniteValue(CosmeticLowSigmaBox, 16),
        CosmeticHighSigma = FiniteValue(CosmeticHighSigmaBox, 16),
        Rejection = SelectedTag(RejectionPicker) == "None"
            ? StackRejectionMode.None
            : StackRejectionMode.DeltaSigma,
        SigmaLow = FiniteValue(SigmaLowBox, 3),
        SigmaHigh = FiniteValue(SigmaHighBox, 3),
        RejectionWarmup = IntegerValue(WarmupBox, 5),
        MaximumRegistrationRms = FiniteValue(RegistrationRmsBox, 2),
        MaximumDriftPixels = FiniteValue(DriftPixelsBox, 256),
        MaximumDriftFraction = FiniteValue(DriftFractionBox, 0.15),
        MinimumOverlap = FiniteValue(MinimumOverlapBox, 0.60),
        RemoveTransients = RemoveTransientsToggle.IsOn,
    };

    private ImageStackCalibration CreateSelectedCalibration()
    {
        if (SelectedTag(CalibrationModePicker) == "Automatic" &&
            _preparedCalibration is not null)
        {
            return _preparedCalibration.Calibration.Copy();
        }
        if (SelectedTag(CalibrationModePicker) != "Masters")
        {
            return new ImageStackCalibration();
        }
        return new ImageStackCalibration
        {
            BiasPath = _manualCalibration.BiasPath,
            DarkPath = _manualCalibration.DarkPath,
            FlatPath = _manualCalibration.FlatPath,
            OverridesDarkExposure = DarkExposureToggle.IsOn,
            DarkExposureSeconds = FiniteValue(DarkExposureBox, 300),
        };
    }

    private string? ValidateConfiguration(
        ImageStackOptions options,
        ImageStackCalibration calibration,
        string calibrationMode)
    {
        if (string.IsNullOrWhiteSpace(WatchFolderTextBox.Text) ||
            !Directory.Exists(WatchFolderTextBox.Text))
        {
            return "Choose an existing capture folder.";
        }
        if (calibrationMode == "Automatic" &&
            (string.IsNullOrWhiteSpace(CalibrationFolderTextBox.Text) ||
             !Directory.Exists(CalibrationFolderTextBox.Text)))
        {
            return "Choose a calibration library folder.";
        }
        if (AlignmentReferencePath is string alignmentPath)
        {
            if (!StackingCoreCapabilities.Current.SupportsReferenceImage)
            {
                return "Shared alignment needs published Seiza core 0.27.0 or later. Update Seiza to use it.";
            }
            if (!File.Exists(alignmentPath))
            {
                return "Choose an existing shared alignment reference image.";
            }
            if (AutomaticReferenceToggle.IsOn && !StackingCoreCapabilities.Current.SupportsReferenceImageCandidates)
            {
                return "Automatic shared-reference fallback needs published Seiza core 0.27.0 or later. Choose a manual channel reference or update Seiza.";
            }
            if (new[] { calibration.BiasPath, calibration.DarkPath, calibration.FlatPath }
                .Any(path => LiveStackPath.Equals(path, alignmentPath)))
            {
                return "The shared alignment reference cannot also be a calibration master.";
            }
        }
        return options.ValidationMessage ?? calibration.ValidationMessage([]);
    }

    private string? AlignmentReferencePath => string.IsNullOrWhiteSpace(AlignmentReferenceTextBox.Text)
        ? null : AlignmentReferenceTextBox.Text;

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed)
        {
            return;
        }

        if (IsConfigured)
        {
            await StartLiveStackAsync();
        }
        else
        {
            await FinishAsync();
        }
    }

    private async Task StartLiveStackAsync()
    {
        ClearConfigurationNotice();
        string calibrationMode = SelectedTag(CalibrationModePicker);
        ImageStackOptions options = CreateOptions();
        ImageStackCalibration calibration = CreateSelectedCalibration();
        string? validation = ValidateConfiguration(options, calibration, calibrationMode);
        if (validation is not null)
        {
            ShowConfigurationError(validation);
            return;
        }

        SetBusy(true);
        var preparationCancellation = new CancellationTokenSource();
        var preparationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _preparationCancellation = preparationCancellation;
        _preparationCompletion = preparationCompletion;
        CalibrationPreparationResult? pendingCalibration = null;
        try
        {
            if (calibrationMode == "Automatic" && !SeparateFiltersToggle.IsOn)
            {
                (CalibrationPreparationResult prepared, string referencePath) =
                    await PrepareAutomaticCalibrationAsync(preparationCancellation.Token);
                pendingCalibration = prepared;
                _initialReferencePath = referencePath;
                if ((prepared.Warnings.Length > 0 || prepared.FlatQualityReviews.Length > 0) &&
                    !await ConfirmCalibrationWarningsAsync(prepared.Warnings, prepared.FlatQualityReviews, preparationCancellation.Token))
                {
                    preparationCancellation.Token.ThrowIfCancellationRequested();
                    ShowConfigurationError(
                        "Live stacking was cancelled before any light frames were processed.",
                        InfoBarSeverity.Informational);
                    return;
                }
                calibration = prepared.Calibration.Copy();
            }
            else
            {
                _initialReferencePath = null;
            }

            string watchFolder = Path.GetFullPath(WatchFolderTextBox.Text);
            var configuration = new LiveStackRunConfiguration
            {
                WatchFolder = watchFolder,
                SessionRootDirectory = LiveStackSessionPaths.ForWatchFolder(watchFolder),
                GroupId = "live",
                GroupTitle = $"Live stack — {Path.GetFileName(watchFolder)}",
                IncludeSubdirectories = IncludeSubdirectoriesToggle.IsOn,
                ResumeExisting = ResumeToggle.IsOn,
                ChooseReferenceAutomatically = AutomaticReferenceToggle.IsOn,
                ApplyCalibrationOnResume = calibrationMode != "None",
                InitialReferencePath = _initialReferencePath,
                AlignmentReferencePath = AlignmentReferencePath,
                Options = options,
                Calibration = calibration,
                PreviewProcessingJson = LiveStackRunConfiguration.PreviewProcessingJsonFor(
                    Enum.Parse<FitsStretchType>(SelectedTag(LiveStretchPicker))),
            };

            await ReplaceCoordinatorAsync(configuration);
            ReplacePreparedCalibration(pendingCalibration);
            pendingCalibration = null;
            _runOperationError = null;
            ConfigurationScrollViewer.Visibility = Visibility.Collapsed;
            RunningStretchPicker.SelectedIndex = LiveStretchPicker.SelectedIndex;
            RunningPanel.Visibility = Visibility.Visible;
            _checkpointAgeTimer.Start();
            PauseButton.Visibility = Visibility.Visible;
            SnapshotButton.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Finish and save…";
            FooterHintText.Text = "Closing pauses and checkpoints the session so it can resume later.";
        }
        catch (OperationCanceledException)
        {
            if (!_closeInProgress)
            {
                ShowConfigurationError(
                    "Calibration preparation was cancelled.",
                    InfoBarSeverity.Informational);
            }
        }
        catch (Exception exception)
        {
            if (!_closeInProgress)
            {
                ShowConfigurationError(exception.Message);
            }
        }
        finally
        {
            pendingCalibration?.Dispose();
            if (ReferenceEquals(_preparationCancellation, preparationCancellation))
            {
                _preparationCancellation = null;
            }
            if (ReferenceEquals(_preparationCompletion, preparationCompletion))
            {
                _preparationCompletion = null;
            }
            preparationCancellation.Dispose();
            if (!_closed)
            {
                CalibrationProgressBar.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
            preparationCompletion.TrySetResult(true);
        }
    }

    private async Task<(CalibrationPreparationResult Result, string ReferencePath)>
        PrepareAutomaticCalibrationAsync(CancellationToken cancellationToken)
    {
        string? alignmentReferencePath = AlignmentReferencePath;
        CalibrationProgressBar.Visibility = Visibility.Visible;
        CalibrationProgressBar.IsIndeterminate = true;
        CalibrationSummaryText.Text = "Finding a reference light…";

        CalibrationFrameProbe reference = await FindReferenceLightAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Automatic calibration needs one completed light frame in the capture folder. " +
                "Add a light first, or choose existing masters.");
        var progress = new Progress<CalibrationPreparationProgress>(update =>
        {
            if (_closed || _closeInProgress)
            {
                return;
            }
            CalibrationSummaryText.Text = update.Message;
            CalibrationProgressBar.IsIndeterminate = update.Total <= 0;
            CalibrationProgressBar.Value = update.Total <= 0
                ? 0
                : Math.Clamp((double)update.Completed / update.Total, 0, 1);
        });
        var service = new CalibrationPreparationService();
        string cacheDirectory = CalibrationCachePaths.ForLibrary(
            CalibrationFolderTextBox.Text);
        CalibrationPreparationResult result = await service.PrepareAsync(
            new CalibrationPreparationRequest
            {
                Reference = reference,
                SourcePaths = [Path.GetFullPath(CalibrationFolderTextBox.Text)],
                CacheDirectory = cacheDirectory,
                ProtectedMasterPaths = alignmentReferencePath is null ? [] : [alignmentReferencePath],
                Options = new() { FlatQuality = FlatQualityOptions.ReadOptions() },
            },
            progress,
            cancellationToken);
        CalibrationSummaryText.Text = DescribeCalibration(result);
        return (result, reference.Path);
    }

    private async Task<bool> ConfirmCalibrationWarningsAsync(
        string[] warnings, FlatQualityReview[] reviews, CancellationToken token)
    {
        var dialog = CalibrationReviewDialog.Create(Content.XamlRoot, warnings, reviews, "Start live stacking");
        return await ShowTrackedDialogAsync(dialog, token) == ContentDialogResult.Primary;
    }

    private async Task<ContentDialogResult> ShowTrackedDialogAsync(ContentDialog dialog, CancellationToken token = default)
    {
        await CalibrationReviewGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_closed || _closeInProgress) throw new OperationCanceledException(token);
            _activeDialog = dialog;
            using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            var result = await dialog.ShowAsync();
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (ReferenceEquals(_activeDialog, dialog))
            {
                _activeDialog = null;
            }
            CalibrationReviewGate.Release();
        }
    }

    private async Task<CalibrationFrameProbe?> FindReferenceLightAsync(
        CancellationToken cancellationToken)
    {
        string watchFolder = Path.GetFullPath(WatchFolderTextBox.Text);
        string? alignmentReferencePath = AlignmentReferencePath;
        SearchOption searchOption = IncludeSubdirectoriesToggle.IsOn
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.ReparsePoint,
        };
        string[] paths = await Task.Run(() => Directory
            .EnumerateFiles(watchFolder, "*", enumerationOptions)
            .Where(ImageFileService.IsStackableImage)
            .Where(path => !StackSourceIdentity.RefersToSameFile(path, alignmentReferencePath))
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.Length > 0)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName)
            .ToArray(), cancellationToken);

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(
                    path,
                    cancellationToken);
                // A master or preprocessed frame in the capture folder is not
                // a usable anchor; keep scanning for a raw light.
                if (CalibrationLightEligibility.IsEligible(probe))
                {
                    return probe;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A live writer or unrelated unreadable file is not a reference candidate.
            }
        }
        return null;
    }

    private static string DescribeCalibration(CalibrationPreparationResult result)
    {
        string masters = string.Join(
            ", ",
            result.Summaries
                .Where(summary => summary.MasterPath is not null)
                .Select(summary =>
                    $"{summary.Kind} ({(summary.CacheReused ? "reused" : "built")})"));
        string description = masters.Length == 0
            ? "No compatible masters could be prepared."
            : $"Prepared {masters}.";
        if (result.Warnings.Length > 0)
        {
            description += $" {string.Join(" ", result.Warnings)}";
        }
        return description;
    }

    private async Task ReplaceCoordinatorAsync(LiveStackRunConfiguration configuration)
    {
        await StopCoordinatorAsync(save: false);
        if (SeparateFiltersToggle.IsOn)
        {
            string library = CalibrationFolderTextBox.Text;
            string? alignment = configuration.AlignmentReferencePath;
            var quality = SelectedTag(CalibrationModePicker) == "Automatic" ? FlatQualityOptions.ReadOptions() : new FlatQualityOptions();
            quality.Validate();
            Func<CalibrationFrameProbe, CancellationToken, Task<CalibrationPreparationResult>>? prepare =
                SelectedTag(CalibrationModePicker) == "Automatic"
                ? (reference, token) => PrepareFilterCalibrationAsync(reference, library, alignment, quality, token)
                : null;
            var session = new LiveStackSessionCoordinator(configuration with
            {
                SessionRootDirectory = Path.Combine(configuration.SessionRootDirectory, "multi-filter"),
            }, prepare);
            _session = session;
            session.Changed += Session_Changed;
            SessionGroupsPanel.Visibility = Visibility.Visible;
            await session.StartAsync(_preparationCancellation?.Token ?? CancellationToken.None);
            RefreshSessionGroups();
            return;
        }
        var coordinator = new LiveStackCoordinator(configuration);
        coordinator.Changed += Coordinator_Changed;
        _coordinator = coordinator;
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        _runTask = ObserveRunAsync(coordinator, cancellation.Token);
        await Task.Yield();
    }

    private async Task<CalibrationPreparationResult> PrepareFilterCalibrationAsync(CalibrationFrameProbe reference,
        string library, string? alignment, FlatQualityOptions quality, CancellationToken token)
    {
        var result = await new CalibrationPreparationService().PrepareAsync(new CalibrationPreparationRequest
        {
            Reference = reference, SourcePaths = [Path.GetFullPath(library)],
            CacheDirectory = CalibrationCachePaths.ForLibrary(library),
            ProtectedMasterPaths = alignment is null ? [] : [alignment],
            Options = new() { FlatQuality = quality },
        }, null, token).ConfigureAwait(false);
        try
        {
            string group = LiveStackFilterIdentity.FromProbe(reference).DisplayName;
            for (int i = 0; i < result.FlatQualityReviews.Length; i++)
                result.FlatQualityReviews[i] = result.FlatQualityReviews[i] with { GroupTitle = group };
            if (result.Warnings.Length > 0 || result.FlatQualityReviews.Length > 0)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try { completion.TrySetResult(await ConfirmCalibrationWarningsAsync(
                        result.Warnings.Select(warning => $"{group}: {warning}").ToArray(), result.FlatQualityReviews, token)); }
                    catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                    catch (Exception error) { completion.TrySetException(error); }
                })) throw new OperationCanceledException("The live stack window is no longer available.", token);
                if (!await completion.Task.ConfigureAwait(false))
                    throw new OperationCanceledException($"Calibration review cancelled for {group}; no lights processed. Restart the session to review again.", token);
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void Session_Changed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _groupRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _groupRefreshQueued, 0);
            if (!_closed) RefreshSessionGroups();
        })) Interlocked.Exchange(ref _groupRefreshQueued, 0);
    }

    private void RefreshSessionGroups()
    {
        if (_session is null) return;
        _updatingGroups = true;
        try
        {
            LiveStackFilterGroup[] groups = _session.Groups.ToArray();
            _displayedGroups = groups;
            OverviewScroll.Visibility = ShowingOverview ? Visibility.Visible : Visibility.Collapsed;
            SinglePreviewPanel.Visibility = ShowingOverview ? Visibility.Collapsed : Visibility.Visible;
            AllFiltersToggle.IsEnabled = !_busy;
            if (ShowingOverview)
            {
                _previewBitmap?.Dispose();
                _previewBitmap = null;
                _displayedPreview = null;
                FilterOverview.OpenFilter = OpenOverviewFilter;
                FilterOverview.SetItems(groups.Select(group => LiveStackOverviewItem.Create(
                    group.Filter, group.Coordinator.CurrentSnapshot, group.Completed, group.Error)).ToArray(), !_busy);
            }
            else FilterOverview.Clear();
            LiveStackFilterGroup? selected = groups.FirstOrDefault(group => ReferenceEquals(group.Coordinator, _coordinator)) ?? groups.FirstOrDefault();
            FilterGroupPicker.ItemsSource = groups.Select(group => group.ToString()).ToArray();
            FilterGroupPicker.SelectedIndex = selected is null ? -1 : Array.IndexOf(groups, selected);
            SelectGroup(selected);
            SessionStatusText.Text = _session.Status;
            FilterGroupPicker.IsEnabled = !_busy;
            PauseButton.Content = _session.IsPaused ? "Resume all filters" : "Pause and save all";
            PauseButton.IsEnabled = !_busy;
        }
        finally { _updatingGroups = false; }
    }

    private void FilterGroup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGroups || _session is null) return;
        SelectGroup(_displayedGroups.ElementAtOrDefault(FilterGroupPicker.SelectedIndex));
        AllFiltersToggle.IsOn = false;
        RefreshSessionGroups();
    }

    private void SelectGroup(LiveStackFilterGroup? group)
    {
        if (!ReferenceEquals(_coordinator, group?.Coordinator))
        {
            _runOperationError = null;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
            _displayedPreview = null;
            EmptyPreviewPanel.Visibility = Visibility.Visible;
            PreviewCanvas.Invalidate();
        }
        _coordinator = group?.Coordinator;
        if (_coordinator is not null) ApplySnapshot(_coordinator.CurrentSnapshot);
        else
        {
            RunStateText.Text = "Waiting for lights";
            RunStatusText.Text = "Filter stacks will appear as complete light frames are discovered.";
            SnapshotButton.IsEnabled = PrimaryButton.IsEnabled = false;
        }
        if (group?.Error is string error) ShowRunError(error);
        if (group?.Completed == true)
        {
            RunStateText.Text = "Completed";
            RunStatusText.Text = "This filter was finished in a previous run.";
            SnapshotButton.IsEnabled = PrimaryButton.IsEnabled = false;
        }
    }

    private async Task ObserveRunAsync(
        LiveStackCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            await coordinator.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Window shutdown intentionally stops the coordinator loop.
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && ReferenceEquals(_coordinator, coordinator))
                {
                    RunInfoBar.Severity = InfoBarSeverity.Error;
                    RunInfoBar.Message = exception.Message;
                    RunInfoBar.IsOpen = true;
                }
            });
        }
    }

    private void Coordinator_Changed(object? sender, LiveStackRunChangedEventArgs e)
    {
        LiveStackCoordinator? coordinator = sender as LiveStackCoordinator;
        LiveStackRunSnapshot snapshot = e.Snapshot;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_coordinator, coordinator))
            {
                ApplySnapshot(snapshot);
            }
        });
    }

    private void ApplySnapshot(LiveStackRunSnapshot snapshot)
    {
        if (_closed)
        {
            return;
        }

        RunStateText.Text = StateTitle(snapshot.State);
        RunStatusText.Text = snapshot.StatusMessage;
        CurrentFileText.Text = snapshot.CurrentPath is null
            ? string.Empty
            : Path.GetFileName(snapshot.CurrentPath);
        AcceptedCountText.Text = snapshot.AcceptedFrames.ToString("N0", CultureInfo.CurrentCulture);
        RejectedCountText.Text = snapshot.RejectedFrames.ToString("N0", CultureInfo.CurrentCulture);
        SkippedCountText.Text = (snapshot.IgnoredFrames + snapshot.UnreadableFrames)
            .ToString("N0", CultureInfo.CurrentCulture);
        FilterText.Text = snapshot.LockedFilter is null
            ? "Filter: waiting for first light"
            : $"Filter: {snapshot.LockedFilter.DisplayName} ({snapshot.LockedFilter.Source.ToString().ToLowerInvariant()})";
        CalibrationText.Text = DescribeCalibrationHistory(snapshot.CalibrationHistory);
        CheckpointText.Text = DescribeCheckpoint(snapshot);
        MonitorText.Text = snapshot.FolderMonitorMessage is null
            ? $"Folder monitor: {snapshot.FolderMonitorStatus}"
            : $"Folder monitor: {snapshot.FolderMonitorStatus} — {snapshot.FolderMonitorMessage}";
        SnrChart.SetPoints(snapshot.SnrPlot);
        SnrSummaryText.Text = DescribeSnr(snapshot);

        string[] attention = LiveStackAttentionPresentation.RecentMessages(snapshot.Attention);
        bool hasAttention = attention.Length > 0;
        AttentionCard.Visibility = hasAttention ? Visibility.Visible : Visibility.Collapsed;
        AttentionList.ItemsSource = hasAttention ? attention : null;

        if (!ShowingOverview && snapshot.Preview is not null &&
            !ReferenceEquals(_displayedPreview, snapshot.Preview))
        {
            try
            {
                CanvasBitmap nextBitmap = CanvasBitmap.CreateFromBytes(
                    PreviewCanvas,
                    snapshot.Preview.Bgra,
                    snapshot.Preview.Width,
                    snapshot.Preview.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    96.0f,
                    CanvasAlphaMode.Premultiplied);
                _displayedPreview = snapshot.Preview;
                _previewBitmap?.Dispose();
                _previewBitmap = nextBitmap;
                EmptyPreviewPanel.Visibility = Visibility.Collapsed;
                PreviewCanvas.Invalidate();
            }
            catch (Exception exception)
            {
                _displayedPreview = snapshot.Preview;
                ShowRunError($"The live preview could not be displayed: {exception.Message}");
            }
        }

        bool paused = snapshot.State == LiveStackRunState.Paused;
        PauseButton.Content = snapshot.RequiresReopenToResume
            ? "Reopen required"
            : paused
            ? "Resume"
            : snapshot.State == LiveStackRunState.NeedsAttention
                ? "Retry checkpoint"
                : "Pause and save";
        PauseButton.IsEnabled = !snapshot.RequiresReopenToResume && !_busy &&
            (snapshot.IsRunning || paused || snapshot.State == LiveStackRunState.NeedsAttention);
        SnapshotButton.IsEnabled =
            !snapshot.RequiresReopenToResume && !_busy && snapshot.HasStack;
        PrimaryButton.IsEnabled =
            !snapshot.RequiresReopenToResume && !_busy && snapshot.HasStack;
        PrimaryButton.Content = _session is null ? "Finish and save…" : "Finish selected filter…";
        if (_session is not null)
        {
            bool completed = snapshot.State == LiveStackRunState.Completed;
            PrimaryButton.IsEnabled &= !completed && !ShowingOverview;
            SnapshotButton.IsEnabled &= !completed && !ShowingOverview;
            PauseButton.Content = _session.IsPaused ? "Resume all filters" : "Pause and save all";
            PauseButton.IsEnabled = !_busy;
        }
        WaitingProgressRing.IsActive = snapshot.IsRunning && !snapshot.HasStack;
        if (snapshot.State == LiveStackRunState.Faulted)
        {
            RunInfoBar.Severity = InfoBarSeverity.Error;
            RunInfoBar.Message = snapshot.StatusMessage;
            RunInfoBar.IsOpen = true;
        }
        else if (_runOperationError is not null)
        {
            RunInfoBar.Severity = InfoBarSeverity.Error;
            RunInfoBar.Message = _runOperationError;
            RunInfoBar.IsOpen = true;
        }
        else
        {
            RunInfoBar.IsOpen = false;
        }
    }

    private static string StateTitle(LiveStackRunState state) => state switch
    {
        LiveStackRunState.WaitingForLight => "Waiting for a light",
        LiveStackRunState.SavingSnapshot => "Saving snapshot",
        LiveStackRunState.NeedsAttention => "Needs attention",
        _ => string.Concat(state.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString())),
    };

    private void CheckpointAgeTimer_Tick(object? sender, object e)
    {
        if (!_closed && _coordinator is not null && !IsConfigured)
        {
            CheckpointText.Text = DescribeCheckpoint(_coordinator.CurrentSnapshot);
        }
    }

    private static string DescribeCheckpoint(LiveStackRunSnapshot snapshot)
    {
        if (snapshot.LastCheckpointGeneration is not long generation)
        {
            return "Checkpoint: not saved yet";
        }
        TimeSpan age = snapshot.CheckpointAge ?? TimeSpan.Zero;
        string ageText = age.TotalSeconds < 10
            ? "just now"
            : age.TotalMinutes < 1
                ? $"{Math.Floor(age.TotalSeconds):N0} seconds ago"
                : $"{Math.Floor(age.TotalMinutes):N0} minutes ago";
        return $"Checkpoint {generation:N0}: {ageText}";
    }

    private static string DescribeCalibrationHistory(
        IReadOnlyList<LiveStackCalibrationEpoch> history)
    {
        LiveStackCalibrationEpoch? current = history.Count == 0
            ? null
            : history[history.Count - 1];
        if (current is null)
        {
            return "Calibration: none";
        }
        string[] masters = new[]
        {
            current.BiasPath is null ? null : $"bias {Path.GetFileName(current.BiasPath)}",
            current.DarkPath is null ? null : $"dark {Path.GetFileName(current.DarkPath)}",
            current.FlatPath is null ? null : $"flat {Path.GetFileName(current.FlatPath)}",
        }.OfType<string>().ToArray();
        return masters.Length == 0
            ? "Calibration: none"
            : $"Calibration: {string.Join(", ", masters)}" +
              (history.Count > 1 ? $" · {history.Count:N0} epochs" : string.Empty);
    }

    private static string DescribeSnr(LiveStackRunSnapshot snapshot)
    {
        if (snapshot.SnrPlot.Count == 0)
        {
            return "Noise and signal are measured at 1, 2, 4, 8… accepted frames.";
        }
        StackSnrPlotPoint plotted = snapshot.SnrPlot[snapshot.SnrPlot.Count - 1];
        LiveStackPersistedSnrSample? sample = snapshot.SnrSamples
            .LastOrDefault(candidate => candidate.AcceptedFrames == plotted.Frames);
        if (sample is null)
        {
            return $"Relative SNR {plotted.Snr:N2} at {plotted.Frames:N0} accepted frame(s).";
        }
        string exposure = sample.CumulativeExposureSeconds is double seconds &&
            double.IsFinite(seconds) && seconds > 0
            ? $" · {FormatDuration(seconds)} exposure at measurement"
            : string.Empty;
        string background = double.IsFinite(sample.Background)
            ? sample.Background.ToString("N5", CultureInfo.CurrentCulture)
            : "unavailable";
        return $"Relative SNR {plotted.Snr:N2} at {plotted.Frames:N0} frame(s) · " +
            $"noise {sample.Noise:N5} · background {background}{exposure}";
    }

    private static string FormatDuration(double seconds)
    {
        TimeSpan duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{duration.TotalHours:N1} h"
            : duration.TotalMinutes >= 1
                ? $"{duration.TotalMinutes:N1} min"
                : $"{duration.TotalSeconds:N0} s";
    }

    private void PreviewCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_previewBitmap is null)
        {
            return;
        }
        float scale = Math.Min(
            (float)sender.ActualWidth / _previewBitmap.SizeInPixels.Width,
            (float)sender.ActualHeight / _previewBitmap.SizeInPixels.Height);
        if (!float.IsFinite(scale) || scale <= 0)
        {
            return;
        }
        float width = _previewBitmap.SizeInPixels.Width * scale;
        float height = _previewBitmap.SizeInPixels.Height * scale;
        float x = ((float)sender.ActualWidth - width) / 2;
        float y = ((float)sender.ActualHeight - height) / 2;
        args.DrawingSession.DrawImage(
            _previewBitmap,
            new Windows.Foundation.Rect(x, y, width, height),
            _previewBitmap.Bounds,
            1.0f,
            CanvasImageInterpolation.HighQualityCubic);
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (_coordinator is null && _session is null))
        {
            return;
        }
        _runOperationError = null;
        CloseWithoutCheckpointButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        try
        {
            if (_session is not null)
            {
                if (_session.IsPaused) await _session.StartAsync();
                else await _session.PauseAndSaveAsync();
                RefreshSessionGroups();
            }
            else if (_coordinator!.CurrentSnapshot.State == LiveStackRunState.Paused)
            {
                if (_runTask is not null)
                {
                    await _runTask;
                }
                var cancellation = new CancellationTokenSource();
                _runCancellation?.Dispose();
                _runCancellation = cancellation;
                _runTask = ObserveRunAsync(_coordinator, cancellation.Token);
            }
            else
            {
                await _coordinator.PauseAndSaveAsync();
            }
        }
        catch (Exception exception)
        {
            ShowRunError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _coordinator is null || !_coordinator.CurrentSnapshot.HasStack)
        {
            return;
        }
        LiveStackOutputSelection? output = await PickOutputPathAsync("live-stack-snapshot");
        if (output is null)
        {
            return;
        }
        if (_closed || _closeInProgress)
        {
            CleanupPlaceholderOutput(output);
            return;
        }
        _runOperationError = null;
        CloseWithoutCheckpointButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        bool wroteOutput = false;
        try
        {
            _session?.ValidateOutputPath(output.Path);
            LiveStackExportResult result = await _coordinator.SaveSnapshotAsync(output.Path);
            wroteOutput = true;
            PreviewInfoBar.Message =
                $"Saved {Path.GetFileName(result.OutputPath)} with " +
                $"{result.AcceptedFrames:N0} accepted frame(s).";
            PreviewInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ShowRunError(exception.Message);
        }
        finally
        {
            if (!wroteOutput)
            {
                CleanupPlaceholderOutput(output);
            }
            SetBusy(false);
        }
    }

    private async Task FinishAsync()
    {
        if (_coordinator is null || !_coordinator.CurrentSnapshot.HasStack)
        {
            return;
        }
        LiveStackOutputSelection? output = await PickOutputPathAsync("live-stack");
        if (output is null)
        {
            return;
        }
        if (_closed || _closeInProgress)
        {
            CleanupPlaceholderOutput(output);
            return;
        }
        _runOperationError = null;
        CloseWithoutCheckpointButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        bool wroteOutput = false;
        try
        {
            _session?.ValidateOutputPath(output.Path);
            LiveStackExportResult result = await _coordinator.FinishAsync(output.Path);
            wroteOutput = true;
            if (_session is not null)
            {
                PreviewInfoBar.Message = $"Saved {Path.GetFileName(result.OutputPath)}. Other filter stacks continue running.";
                PreviewInfoBar.IsOpen = true;
                if (result.TransientRemovalNote is not null) await ShowTransientRemovalNoteAsync(result.TransientRemovalNote);
                SetBusy(false);
                RefreshSessionGroups();
                return;
            }
            await StopCoordinatorAsync(save: false);
            if (result.TransientRemovalNote is not null && !_closed)
            {
                await ShowTransientRemovalNoteAsync(result.TransientRemovalNote);
            }
            _completion.TrySetResult(result.OutputPath);
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            ShowRunError(exception.Message);
            SetBusy(false);
        }
        finally
        {
            if (!wroteOutput)
            {
                CleanupPlaceholderOutput(output);
            }
        }
    }

    private async Task ShowTransientRemovalNoteAsync(string note)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Stack saved",
            Content = new TextBlock
            {
                Text = note,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowTrackedDialogAsync(dialog);
    }

    private async Task<LiveStackOutputSelection?> PickOutputPathAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".fits",
        };
        picker.FileTypeChoices.Add("FITS image", [".fits"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        DateTimeOffset pickerOpenedAt = DateTimeOffset.UtcNow;
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }
        bool isNewPlaceholder = false;
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            isNewPlaceholder = properties.Size == 0 &&
                file.DateCreated >= pickerOpenedAt.AddSeconds(-2);
        }
        catch
        {
            // Cleanup is best-effort and must never reject a valid picker result.
        }
        return new LiveStackOutputSelection(file.Path, isNewPlaceholder);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed)
        {
            return;
        }
        ConfigurationScrollViewer.IsEnabled = !busy;
        RunningStretchPicker.IsEnabled = !busy;
        if (IsConfigured)
        {
            UpdateConfigurationState();
        }
        else if (_coordinator is not null)
        {
            ApplySnapshot(_coordinator.CurrentSnapshot);
        }
        if (_session is not null) RefreshSessionGroups();
    }

    private void ShowConfigurationError(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        _configurationNotice = message;
        _configurationNoticeSeverity = severity;
        ConfigurationInfoBar.Severity = severity;
        ConfigurationInfoBar.Message = message;
        ConfigurationInfoBar.IsOpen = true;
    }

    private void ShowRunError(string message)
    {
        _runOperationError = message;
        RunInfoBar.Severity = InfoBarSeverity.Error;
        RunInfoBar.Message = message;
        RunInfoBar.IsOpen = true;
    }

    private void ClearConfigurationNotice()
    {
        _configurationNotice = null;
        _configurationNoticeSeverity = InfoBarSeverity.Informational;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _closed)
        {
            return;
        }
        args.Cancel = true;
        if (!_closeInProgress)
        {
            _closeInProgress = true;
            _ = PauseAndCloseAsync();
        }
    }

    private async Task PauseAndCloseAsync()
    {
        SetBusy(true);
        try
        {
            Task? preparationTask = _preparationCompletion?.Task;
            _preparationCancellation?.Cancel();
            _activeDialog?.Hide();
            if (preparationTask is not null)
            {
                await preparationTask;
            }
            bool checkpointBeforeClose =
                _session is not null || _coordinator?.CurrentSnapshot.RequiresReopenToResume != true;
            await StopCoordinatorAsync(save: checkpointBeforeClose);
        }
        catch (Exception exception)
        {
            ShowRunError($"The final checkpoint failed: {exception.Message}");
            CloseWithoutCheckpointButton.Visibility = Visibility.Visible;
            _closeInProgress = false;
            SetBusy(false);
            return;
        }

        _completion.TrySetResult(null);
        _allowClose = true;
        Close();
    }

    private void CloseWithoutCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        _completion.TrySetResult(null);
        _allowClose = true;
        Close();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= Window_Closed;
        _activeDialog?.Hide();
        _activeDialog = null;
        _checkpointAgeTimer.Stop();
        _completion.TrySetResult(null);
        Dispose();
    }

    private async Task StopCoordinatorAsync(bool save)
    {
        if (_session is { } session)
        {
            if (save) await session.PauseAndSaveAsync();
            session.Changed -= Session_Changed;
            await session.DisposeAsync();
            _session = null;
            _coordinator = null;
            return;
        }
        LiveStackCoordinator? coordinator = _coordinator;
        Task? runTask = _runTask;
        CancellationTokenSource? cancellation = _runCancellation;
        if (coordinator is null)
        {
            cancellation?.Dispose();
            return;
        }

        if (save)
        {
            // Do not detach or clear ownership until the data-safety boundary
            // succeeds. A failed checkpoint must remain retryable in this window.
            await coordinator.PauseAndSaveAsync();
        }

        coordinator.Changed -= Coordinator_Changed;
        if (!save)
        {
            cancellation?.Cancel();
        }
        try
        {
            if (runTask is not null)
            {
                try
                {
                    await runTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected during replacement or shutdown.
                }
            }
            await coordinator.DisposeAsync();
            if (ReferenceEquals(_coordinator, coordinator))
            {
                _coordinator = null;
                _runTask = null;
                _runCancellation = null;
            }
        }
        catch
        {
            if (ReferenceEquals(_coordinator, coordinator))
            {
                // Preserve the coordinator for a retry or the explicit
                // discard path, but do not retain completed task/token objects.
                _runTask = null;
                _runCancellation = null;
            }
            throw;
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_session is { } session)
        {
            session.Changed -= Session_Changed;
            _session = null;
            _coordinator = null;
            _ = DisposeSessionAfterCloseAsync(session);
        }
        _preparationCancellation?.Cancel();
        _preparationCancellation?.Dispose();
        _preparationCancellation = null;
        _preparationCompletion?.TrySetResult(true);
        _preparationCompletion = null;
        CalibrationPreparationResult? preparedCalibration = _preparedCalibration;
        _preparedCalibration = null;

        LiveStackCoordinator? coordinator = _coordinator;
        Task? runTask = _runTask;
        CancellationTokenSource? runCancellation = _runCancellation;
        _coordinator = null;
        _runTask = null;
        _runCancellation = null;
        if (coordinator is not null)
        {
            coordinator.Changed -= Coordinator_Changed;
            _ = DisposeCoordinatorAfterCloseAsync(
                coordinator,
                runTask,
                runCancellation,
                preparedCalibration);
        }
        else
        {
            runCancellation?.Dispose();
            preparedCalibration?.Dispose();
        }
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _displayedPreview = null;
        FilterOverview.Dispose();
        _checkpointAgeTimer.Stop();
        _checkpointAgeTimer.Tick -= CheckpointAgeTimer_Tick;
        PreviewCanvas.RemoveFromVisualTree();
        GC.SuppressFinalize(this);
    }

    private static async Task DisposeSessionAfterCloseAsync(LiveStackSessionCoordinator session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
    }

    private void ReplacePreparedCalibration(CalibrationPreparationResult? replacement)
    {
        if (ReferenceEquals(_preparedCalibration, replacement))
        {
            return;
        }
        CalibrationPreparationResult? previous = _preparedCalibration;
        _preparedCalibration = replacement;
        previous?.Dispose();
    }

    private static async Task DisposeCoordinatorAfterCloseAsync(
        LiveStackCoordinator coordinator,
        Task? runTask,
        CancellationTokenSource? cancellation,
        CalibrationPreparationResult? preparedCalibration)
    {
        try
        {
            cancellation?.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch
                {
                    // The coordinator's own disposal makes one last best-effort checkpoint.
                }
            }
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The window is already closed, so there is nowhere safe to surface
            // teardown failures. The explicit close path reports checkpoint errors.
        }
        finally
        {
            cancellation?.Dispose();
            preparedCalibration?.Dispose();
        }
    }

    private static string SelectedTag(ComboBox picker) =>
        (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private static int IntegerValue(NumberBox box, int fallback) =>
        double.IsFinite(box.Value) ? Math.Max(0, (int)Math.Round(box.Value)) : fallback;

    private static double FiniteValue(NumberBox box, double fallback) =>
        double.IsFinite(box.Value) ? box.Value : fallback;

    private static void CleanupPlaceholderOutput(LiveStackOutputSelection output)
    {
        if (!output.IsNewPlaceholder)
        {
            return;
        }
        try
        {
            var file = new FileInfo(output.Path);
            if (file.Exists && file.Length == 0)
            {
                file.Delete();
            }
        }
        catch
        {
            // Export failures must not be masked by best-effort picker cleanup.
        }
    }

    private sealed record LiveStackOutputSelection(string Path, bool IsNewPlaceholder);
}
