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

    internal LiveStackWindow(string initialFolder)
    {
        InitializeComponent();
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
        foreach (string extension in new[] { ".fits", ".fit", ".fts", ".xisf" })
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
        LocalTileRow.Visibility = SelectedTag(NormalizationPicker) == "Local"
            ? Visibility.Visible
            : Visibility.Collapsed;
        RejectionOptionsPanel.Visibility = SelectedTag(RejectionPicker) == "DeltaSigma"
            ? Visibility.Visible
            : Visibility.Collapsed;

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
        Normalization = SelectedTag(NormalizationPicker) switch
        {
            "None" => StackNormalizationMode.None,
            "Local" => StackNormalizationMode.Local,
            _ => StackNormalizationMode.Global,
        },
        LocalTileSize = IntegerValue(LocalTileSizeBox, 256),
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
        return options.ValidationMessage ?? calibration.ValidationMessage([]);
    }

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
            if (calibrationMode == "Automatic")
            {
                (CalibrationPreparationResult prepared, string referencePath) =
                    await PrepareAutomaticCalibrationAsync(preparationCancellation.Token);
                pendingCalibration = prepared;
                _initialReferencePath = referencePath;
                if (prepared.Warnings.Length > 0 &&
                    !await ConfirmCalibrationWarningsAsync(prepared.Warnings))
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
                ApplyCalibrationOnResume = calibrationMode != "None",
                InitialReferencePath = _initialReferencePath,
                Options = options,
                Calibration = calibration,
            };

            await ReplaceCoordinatorAsync(configuration);
            ReplacePreparedCalibration(pendingCalibration);
            pendingCalibration = null;
            _runOperationError = null;
            ConfigurationScrollViewer.Visibility = Visibility.Collapsed;
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
            },
            progress,
            cancellationToken);
        CalibrationSummaryText.Text = DescribeCalibration(result);
        return (result, reference.Path);
    }

    private async Task<bool> ConfirmCalibrationWarningsAsync(
        string[] warnings)
    {
        string content = CalibrationPreparationWarningText.Format(warnings);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Calibration needs attention",
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = content,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            PrimaryButtonText = "Start live stacking",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        _activeDialog = dialog;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            if (ReferenceEquals(_activeDialog, dialog))
            {
                _activeDialog = null;
            }
        }
    }

    private async Task<CalibrationFrameProbe?> FindReferenceLightAsync(
        CancellationToken cancellationToken)
    {
        string watchFolder = Path.GetFullPath(WatchFolderTextBox.Text);
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
        var coordinator = new LiveStackCoordinator(configuration);
        coordinator.Changed += Coordinator_Changed;
        _coordinator = coordinator;
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        _runTask = ObserveRunAsync(coordinator, cancellation.Token);
        await Task.Yield();
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

        if (snapshot.Preview is not null &&
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
        PrimaryButton.Content = "Finish and save…";
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
            new Windows.Foundation.Rect(x, y, width, height));
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _coordinator is null)
        {
            return;
        }
        _runOperationError = null;
        CloseWithoutCheckpointButton.Visibility = Visibility.Collapsed;
        SetBusy(true);
        try
        {
            if (_coordinator.CurrentSnapshot.State == LiveStackRunState.Paused)
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
            LiveStackExportResult result = await _coordinator.FinishAsync(output.Path);
            wroteOutput = true;
            await StopCoordinatorAsync(save: false);
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
        if (IsConfigured)
        {
            UpdateConfigurationState();
        }
        else if (_coordinator is not null)
        {
            ApplySnapshot(_coordinator.CurrentSnapshot);
        }
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
                _coordinator?.CurrentSnapshot.RequiresReopenToResume != true;
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
        _checkpointAgeTimer.Stop();
        _checkpointAgeTimer.Tick -= CheckpointAgeTimer_Tick;
        PreviewCanvas.RemoveFromVisualTree();
        GC.SuppressFinalize(this);
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
