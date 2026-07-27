using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App;

public sealed partial class ImageStackWindow : Window, IDisposable
{
    private readonly IReadOnlyList<StackFrameChoice> _frames;
    private readonly Dictionary<string, string> _referencePaths = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<ImageStackBatchResult?> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ImageStackCalibration _calibration = new();
    private CancellationTokenSource? _stackCancellation;
    private IReadOnlyList<ImageStackGroup> _groups = [];
    private bool _allowClose;
    private bool _closed;
    private bool _closeWhenIdle;
    private bool _initializing = true;
    private bool _loaded;
    private bool _running;

    internal ImageStackWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        _frames = paths
            .Where(ImageFileService.IsStackableImage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new StackFrameChoice(path, Path.GetFileName(path)))
            .ToArray();
        FrameList.ItemsSource = _frames;
        foreach (StackFrameChoice frame in _frames)
        {
            FrameList.SelectedItems.Add(frame);
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(StackTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
        }
        ContentRoot.Loaded += ContentRoot_Loaded;
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        _initializing = false;
        RefreshGroupsAndValidation();
    }

    internal Task<ImageStackBatchResult?> Completion => _completion.Task;

    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private IReadOnlyList<string> SelectedPaths => FrameList.SelectedItems
        .OfType<StackFrameChoice>()
        .Select(frame => frame.Path)
        .ToArray();

    private bool SplitsOutput => SplitByFilterToggle.IsOn && _groups.Count > 1;

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
        int width = Math.Min((int)Math.Round(720 * scale), workArea.Width - (margin * 2));
        int height = Math.Min((int)Math.Round(860 * scale), workArea.Height - (margin * 2));
        int x = workArea.X + workArea.Width - width - margin;
        int y = workArea.Y + ((workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (StackFrameChoice frame in _frames)
        {
            if (!FrameList.SelectedItems.Contains(frame))
            {
                FrameList.SelectedItems.Add(frame);
            }
        }
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e) =>
        FrameList.SelectedItems.Clear();

    private void FrameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            RefreshGroupsAndValidation();
        }
    }

    private void SplitByFilterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            RefreshGroupsAndValidation();
        }
    }

    private void RefreshGroupsAndValidation()
    {
        IReadOnlyList<string> selected = SelectedPaths;
        bool hasMultipleFilters = ImageStackGrouping.HasMultipleDetectedFilters(selected);
        SplitByFilterToggle.IsEnabled = hasMultipleFilters;
        if (!hasMultipleFilters && SplitByFilterToggle.IsOn)
        {
            _initializing = true;
            SplitByFilterToggle.IsOn = false;
            _initializing = false;
        }
        FilterHintText.Text = hasMultipleFilters
            ? "Multiple filename filters were detected. Split mode creates one registered FITS stack for each filter."
            : "Include at least two detected filename filters to create separate filter stacks.";

        _groups = ImageStackGrouping.Groups(selected, SplitByFilterToggle.IsOn);
        SplitOutputPanel.Visibility = SplitsOutput ? Visibility.Visible : Visibility.Collapsed;
        BuildReferenceControls();
        UpdateOptionsAndValidation();
    }

    private void BuildReferenceControls()
    {
        ReferencePanel.Children.Clear();
        foreach (ImageStackGroup group in _groups)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = group.Title,
                VerticalAlignment = VerticalAlignment.Center,
            });

            StackFrameChoice[] choices = group.Inputs
                .Select(path => new StackFrameChoice(path, Path.GetFileName(path)))
                .ToArray();
            var picker = new ComboBox
            {
                ItemsSource = choices,
                DisplayMemberPath = nameof(StackFrameChoice.DisplayName),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = group.Id,
            };
            Grid.SetColumn(picker, 1);
            string preferred = _referencePaths.GetValueOrDefault(group.Id)
                ?? (group.Inputs.Count > 0 ? group.Inputs[0] : string.Empty);
            picker.SelectedItem = choices.FirstOrDefault(choice =>
                string.Equals(choice.Path, preferred, StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault();
            if (picker.SelectedItem is StackFrameChoice selected)
            {
                _referencePaths[group.Id] = selected.Path;
            }
            picker.SelectionChanged += ReferencePicker_SelectionChanged;
            row.Children.Add(picker);
            ReferencePanel.Children.Add(row);
        }
    }

    private void ReferencePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: string groupId, SelectedItem: StackFrameChoice frame })
        {
            _referencePaths[groupId] = frame.Path;
        }
    }

    private void OptionsSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateOptionsAndValidation();
        }
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateOptionsAndValidation();
        }
    }

    private void Options_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initializing)
        {
            UpdateOptionsAndValidation();
        }
    }

    private void OutputBaseNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateOptionsAndValidation();
        }
    }

    private void UpdateOptionsAndValidation()
    {
        StackNormalizationMode normalization = SelectedTag(NormalizationPicker) == "Local"
            ? StackNormalizationMode.Local
            : SelectedTag(NormalizationPicker) == "None"
                ? StackNormalizationMode.None
                : StackNormalizationMode.Global;
        StackRejectionMode rejection = SelectedTag(RejectionPicker) == "None"
            ? StackRejectionMode.None
            : StackRejectionMode.DeltaSigma;
        LocalTileRow.Visibility = normalization == StackNormalizationMode.Local
            ? Visibility.Visible
            : Visibility.Collapsed;
        RejectionOptionsPanel.Visibility = rejection == StackRejectionMode.DeltaSigma
            ? Visibility.Visible
            : Visibility.Collapsed;
        DarkExposureBox.IsEnabled = DarkExposureToggle.IsOn && _calibration.DarkPath is not null;
        _calibration.OverridesDarkExposure = DarkExposureToggle.IsOn;
        _calibration.DarkExposureSeconds = DarkExposureBox.Value;

        string? message = ValidationMessage(CreateOptions());
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.Message = message ?? string.Empty;
        ValidationInfoBar.IsOpen = message is not null;
        StartButton.IsEnabled = !_running && message is null;
    }

    private string? ValidationMessage(ImageStackOptions options)
    {
        IReadOnlyList<string> selected = SelectedPaths;
        if (selected.Count < 2)
        {
            return "Choose at least two light frames.";
        }
        if (_groups.Any(group => group.Inputs.Count < 2))
        {
            return "Every filter stack needs at least two selected frames.";
        }
        if (SplitsOutput &&
            string.IsNullOrWhiteSpace(ImageStackOutputNaming.SafeBaseName(OutputBaseNameBox.Text)))
        {
            return "Enter an output base name.";
        }
        return options.ValidationMessage ?? _calibration.ValidationMessage(selected);
    }

    private ImageStackOptions CreateOptions() => new()
    {
        Normalization = SelectedTag(NormalizationPicker) switch
        {
            "None" => StackNormalizationMode.None,
            "Local" => StackNormalizationMode.Local,
            _ => StackNormalizationMode.Global,
        },
        LocalTileSize = CheckedInt(LocalTileSizeBox.Value),
        Rejection = SelectedTag(RejectionPicker) == "None"
            ? StackRejectionMode.None
            : StackRejectionMode.DeltaSigma,
        SigmaLow = SigmaLowBox.Value,
        SigmaHigh = SigmaHighBox.Value,
        RejectionWarmup = CheckedInt(WarmupBox.Value),
        MaximumRegistrationRms = RegistrationRmsBox.Value,
        MaximumDriftPixels = DriftPixelsBox.Value,
        MaximumDriftFraction = DriftFractionBox.Value,
        MinimumOverlap = MinimumOverlapBox.Value,
    };

    private static string? SelectedTag(ComboBox picker) =>
        (picker.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static int CheckedInt(double value) => double.IsFinite(value)
        ? checked((int)Math.Round(value))
        : 0;

    private async void ChooseBias_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationAsync(path => _calibration.BiasPath = path);

    private async void ChooseDark_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationAsync(path => _calibration.DarkPath = path);

    private async void ChooseFlat_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationAsync(path => _calibration.FlatPath = path);

    private void ClearBias_Click(object sender, RoutedEventArgs e)
    {
        _calibration.BiasPath = null;
        RefreshCalibration();
    }

    private void ClearDark_Click(object sender, RoutedEventArgs e)
    {
        _calibration.DarkPath = null;
        RefreshCalibration();
    }

    private void ClearFlat_Click(object sender, RoutedEventArgs e)
    {
        _calibration.FlatPath = null;
        RefreshCalibration();
    }

    private async Task ChooseCalibrationAsync(Action<string> setPath)
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
        if (file is not null)
        {
            setPath(file.Path);
            RefreshCalibration();
        }
    }

    private void RefreshCalibration()
    {
        BiasPathText.Text = DisplayCalibration(_calibration.BiasPath);
        DarkPathText.Text = DisplayCalibration(_calibration.DarkPath);
        FlatPathText.Text = DisplayCalibration(_calibration.FlatPath);
        UpdateOptionsAndValidation();
    }

    private static string DisplayCalibration(string? path) =>
        path is null ? "None" : Path.GetFileName(path);

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _closed)
        {
            return;
        }
        ImageStackOptions options = CreateOptions();
        string? validation = ValidationMessage(options);
        if (validation is not null)
        {
            ValidationInfoBar.Message = validation;
            ValidationInfoBar.IsOpen = true;
            return;
        }

        ImageStackOutputSelection? outputSelection = await PickOutputsAsync();
        if (outputSelection is null || _closed)
        {
            if (outputSelection is not null)
            {
                CleanupPlaceholderOutputs(outputSelection.PlaceholderPaths);
            }
            return;
        }
        IReadOnlyDictionary<string, string> outputs = outputSelection.Outputs;
        string[] existing = outputs.Values.Where(File.Exists).ToArray();
        if (SplitsOutput && existing.Length > 0 && !await ConfirmOverwriteAsync(existing))
        {
            return;
        }
        if (_closed)
        {
            CleanupPlaceholderOutputs(outputSelection.PlaceholderPaths);
            return;
        }

        var jobs = new List<ImageStackJob>(_groups.Count);
        foreach (ImageStackGroup group in _groups)
        {
            string reference = _referencePaths.GetValueOrDefault(group.Id) ?? group.Inputs[0];
            string[] orderedInputs = new[] { reference }
                .Concat(group.Inputs.Where(path =>
                    !string.Equals(path, reference, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            jobs.Add(new ImageStackJob(
                group,
                new ImageStackRequest(
                    orderedInputs,
                    outputs[group.Id],
                    options,
                    _calibration)));
        }

        _running = true;
        _allowClose = false;
        _closeWhenIdle = false;
        ConfigurationPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed;
        FooterHintText.Text = "You can cancel safely between frames.";
        var cancellation = new CancellationTokenSource();
        _stackCancellation = cancellation;
        bool wroteOutputs = false;
        var progress = new Progress<ImageStackProgress>(update =>
        {
            if (_closed)
            {
                return;
            }
            StackProgressBar.Value = update.FractionCompleted;
            ProgressMessageText.Text = update.Message;
            ProgressCountsText.Text =
                $"{update.CompletedFrames} of {update.TotalFrames} processed  •  " +
                $"{update.AcceptedFrames} accepted  •  {update.RejectedFrames} rejected";
        });

        try
        {
            ImageStackBatchResult result = await ImageStackService.StackBatchAsync(
                jobs,
                progress,
                cancellation.Token);
            wroteOutputs = true;
            _running = false;
            _completion.TrySetResult(result);
            _allowClose = true;
            Close();
        }
        catch (ImageStackBatchCanceledException exception)
        {
            if (!_closeWhenIdle)
            {
                RestoreAfterFailure(exception.Message, InfoBarSeverity.Informational);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closeWhenIdle)
            {
                RestoreAfterFailure(
                    "Stacking was cancelled. No output was written.",
                    InfoBarSeverity.Informational);
            }
        }
        catch (Exception exception)
        {
            if (!_closeWhenIdle)
            {
                RestoreAfterFailure(exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (!wroteOutputs)
            {
                CleanupPlaceholderOutputs(outputSelection.PlaceholderPaths);
            }
            if (ReferenceEquals(_stackCancellation, cancellation))
            {
                _stackCancellation = null;
            }
            cancellation.Dispose();
            if (_closeWhenIdle && !_closed)
            {
                _running = false;
                _allowClose = true;
                Close();
            }
        }
    }

    private async Task<ImageStackOutputSelection?> PickOutputsAsync()
    {
        if (!SplitsOutput)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = "stacked",
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
                // Failure cleanup is best-effort; never reject a valid picker result.
            }
            return new ImageStackOutputSelection(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [_groups[0].Id] = file.Path,
                },
                isNewPlaceholder ? [file.Path] : []);
        }

        var folderPicker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
        };
        folderPicker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, WindowHandle);
        StorageFolder? folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null)
        {
            return null;
        }

        string baseName = ImageStackOutputNaming.SafeBaseName(OutputBaseNameBox.Text);
        return new ImageStackOutputSelection(
            ImageStackOutputNaming.SplitOutputPaths(folder.Path, baseName, _groups),
            []);
    }

    private async Task<bool> ConfirmOverwriteAsync(string[] paths)
    {
        string names = string.Join(Environment.NewLine, paths.Select(Path.GetFileName));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = paths.Length == 1 ? "Replace existing file?" : "Replace existing files?",
            Content = names,
            PrimaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void RestoreAfterFailure(string message, InfoBarSeverity severity)
    {
        if (_closed)
        {
            return;
        }
        _closeWhenIdle = false;
        _running = false;
        ConfigurationPanel.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        StartButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        FooterHintText.Text = "The reference frame is included in the stack.";
        ValidationInfoBar.Severity = severity;
        ValidationInfoBar.Message = message;
        ValidationInfoBar.IsOpen = true;
        StartButton.IsEnabled = ValidationMessage(CreateOptions()) is null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            CancelButton.IsEnabled = false;
            FooterHintText.Text = "Cancelling after the current frame…";
            _stackCancellation?.Cancel();
            return;
        }
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_running || _allowClose)
        {
            return;
        }

        args.Cancel = true;
        _closeWhenIdle = true;
        CancelButton.IsEnabled = false;
        FooterHintText.Text = "Closing after the current frame…";
        _stackCancellation?.Cancel();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        AppWindow.Closing -= AppWindow_Closing;
        Dispose();
        _completion.TrySetResult(null);
    }

    private static void CleanupPlaceholderOutputs(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length == 0)
                {
                    file.Delete();
                }
            }
            catch
            {
                // A failed stack must not be masked by best-effort picker cleanup.
            }
        }
    }

    public void Dispose()
    {
        _stackCancellation?.Cancel();
        _stackCancellation?.Dispose();
        _stackCancellation = null;
        GC.SuppressFinalize(this);
    }

    private sealed record StackFrameChoice(string Path, string DisplayName);

    private sealed record ImageStackOutputSelection(
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> PlaceholderPaths);
}
