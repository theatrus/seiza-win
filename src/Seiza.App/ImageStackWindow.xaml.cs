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
    private IReadOnlyList<StackFrameChoice> _frames;
    private readonly string[] _sourcePaths;
    private IReadOnlyDictionary<string, string?> _filterNames = new Dictionary<string, string?>();
    private readonly List<string> _providedCalibration = [];
    private readonly CancellationTokenSource _routingCancellation = new();
    private bool _routing = true;
    private readonly GradedCalibrationControl _graded = new();
    private readonly string? _browserRoot;
    private readonly Func<bool> _useGpuSetting;
    private readonly Func<int?> _gpuMemorySetting;
    private readonly string _sharedAlignmentHint;
    private readonly Dictionary<string, string> _referencePaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _rankedReferencePaths = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<ImageStackBatchResult?> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ImageStackCalibration _calibration = new();
    private CancellationTokenSource? _stackCancellation;
    private ContentDialog? _activeDialog;
    private IReadOnlyList<ImageStackGroup> _groups = [];
    private bool _allowClose;
    private bool _closed;
    private bool _closeWhenIdle;
    private bool _initializing = true;
    private bool _loaded;
    private bool _running;
    private bool _hasCustomOverlap;
    private SkyPanelAnalysis? _skyAnalysis;
    private IReadOnlyList<string>? _skyInputs;
    private bool _solvingPanels;
    private bool _updatingPanelSelection;
    private readonly Dictionary<string, CheckBox> _skyFrameChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(CheckBox Check, IReadOnlyList<string> Paths)> _skyPanelChecks = [];
    private string? _calibrationLibraryPath;
    private string? _alignmentReferencePath;

    internal ImageStackWindow(IReadOnlyList<string> paths, string? browserRoot = null, Func<bool>? useGpuSetting = null,
        Func<int?>? gpuMemorySetting = null)
    {
        _useGpuSetting = useGpuSetting ?? (() => CatalogSettingsStore.LoadUseGpuForStacking());
        _gpuMemorySetting = gpuMemorySetting ?? (() => CatalogSettingsStore.LoadStackingGpuMemoryMib());
        InitializeComponent();
        BuildSetupTabs();
        GradedCalibrationHost.Children.Add(_graded);
        GradedCalibrationHost.Children.Add(_graded.SettingsView);
        _graded.StateChanged += (_, _) => { if (!_closed && !_initializing) UpdateOptionsAndValidation(); };
        _browserRoot = browserRoot;
        MinimumWeightBox.Value = new ImageStackOptions().MinimumWeight;
        SharedAlignmentToggle.IsEnabled = StackingCoreCapabilities.Current.SupportsReferenceImage;
        _sharedAlignmentHint = StackingCoreCapabilities.Current.SupportsReferenceImage
            ? StackingCoreCapabilities.Current.SupportsReferenceImageCandidates
                ? "A common grid keeps separate filter outputs aligned for color combination."
                : "This core supports a common grid, but not automatic alignment fallback. Only the chosen channel light is tried."
            : "Shared alignment needs published Seiza core 0.27.0 or later. Update Seiza; independent grids remain available.";
        SharedAlignmentHelpText.Text = "Use the first stack's channel reference for the common grid, or choose a FITS/XISF image of the same field. " +
            "This image is not calibrated or added to the stack unless it is also a selected light. Each filter still uses its own channel reference and calibration masters. " +
            (StackingCoreCapabilities.Current.SupportsReferenceImageCandidates
                ? "With automatic selection, up to five native-ranked lights are tried until one aligns."
                : "This core tries only the chosen channel light, including with automatic selection.");
        _frames = paths
            .Where(ImageFileService.IsStackableImage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new StackFrameChoice(path, ImageStackFrameLabel.DisplayName(path, _browserRoot)))
            .ToArray();
        _sourcePaths = _frames.Select(f => f.Path).ToArray();
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

    private bool SplitsOutput => _groups.Count > 1;

    private async void ContentRoot_Loaded(object sender, RoutedEventArgs e)
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
        int width = Math.Min((int)Math.Round(1040 * scale), workArea.Width - (margin * 2));
        int height = Math.Min((int)Math.Round(860 * scale), workArea.Height - (margin * 2));
        int x = workArea.X + workArea.Width - width - margin;
        int y = workArea.Y + ((workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        try
        {
            StackInputRoutingResult routed = await StackInputRouting.RouteAsync(_sourcePaths, _routingCancellation.Token);
            if (_closed) return;
            _initializing = true;
            _frames = routed.Lights.Select(p => new StackFrameChoice(p, ImageStackFrameLabel.DisplayName(p, _browserRoot))).ToArray();
            FrameList.ItemsSource = _frames;
            foreach (var frame in _frames) FrameList.SelectedItems.Add(frame);
            _providedCalibration.AddRange(routed.Calibration);
            _filterNames = routed.Filters;
            if (_providedCalibration.Count > 0) CalibrationSourcePicker.SelectedIndex = 1;
            RoutingNotes.Text = $"{routed.Lights.Length} lights · {routed.Calibration.Length} calibration files" +
                (routed.Notes.Length > 0 ? Environment.NewLine + string.Join(Environment.NewLine, routed.Notes) : "");
            RenderProvidedCalibration();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closed) RoutingNotes.Text = exception.Message; }
        finally
        {
            _routing = false; _initializing = false;
            if (!_closed) RefreshGroupsAndValidation();
        }
    }

    private readonly TextBlock RoutingNotes = new() { Text = "Reading FITS/XISF image roles…", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private void BuildSetupTabs()
    {
        UIElement[] sections = ConfigurationPanel.Children.ToArray();
        ConfigurationPanel.Children.Clear();
        ConfigurationPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ConfigurationPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tabs = new TabView { IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false };
        StackPanel Page(string title, IEnumerable<UIElement> children)
        {
            var panel = new StackPanel { Spacing = 18, Padding = new Thickness(0, 16, 12, 16) };
            foreach (UIElement child in children) panel.Children.Add(child);
            tabs.TabItems.Add(new TabViewItem { Header = title, IsClosable = false, Content = new ScrollViewer
                { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
            return panel;
        }
        StackPanel inputs = Page("Inputs", sections.Take(4).Append(sections[7]));
        inputs.Children.Insert(1, RoutingNotes);
        UIElement calibrationContent = (UIElement)CalibrationExpander.Content;
        CalibrationExpander.Content = null;
        Page("Calibration", [calibrationContent]);
        Page("Processing", [sections[4], sections[6]]);
        tabs.TabItems.Add(new TabViewItem { Header = "Review", IsClosable = false, Content = _graded.ReviewView });
        ConfigurationPanel.Children.Add(tabs);
        Grid.SetRow(ValidationInfoBar, 1); ConfigurationPanel.Children.Add(ValidationInfoBar);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_skyAnalysis is not null) { SelectSkyPaths(_skyAnalysis.Sources.Keys.ToArray(), true); return; }
        foreach (StackFrameChoice frame in _frames)
        {
            if (_skyAnalysis is not null && !_skyAnalysis.Sources.ContainsKey(frame.Path)) continue;
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
            SyncSkySelection();
        }
    }

    private void SplitByFilterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            RefreshGroupsAndValidation();
        }
    }

    private void SkyGrouping_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _skyAnalysis = null;
        _skyInputs = null;
        _skyFrameChecks.Clear();
        _skyPanelChecks.Clear();
        SkyPanelReview.Children.Clear();
        SkyPanelReview.Visibility = Visibility.Collapsed;
        FrameList.Visibility = Visibility.Visible;
        LightSelectionTitle.Text = "Light frames — select inputs";
        SkyGroupingControls.Visibility = SkyGroupingToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SkyGroupingStatus.Text = "Uses your installed plate-solving catalogs. No source files are changed.";
        SolvePanelsButton.Content = "Solve and group selected lights";
        RefreshGroupsAndValidation();
    }

    private async void SolvePanels_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _closed) return;
        string[] paths = (_skyInputs ?? SelectedPaths).ToArray();
        _solvingPanels = _running = true;
        ConfigurationHost.IsEnabled = false;
        StartButton.IsEnabled = false;
        var cancellation = new CancellationTokenSource();
        _stackCancellation = cancellation;
        try
        {
            var progress = new Progress<string>(message =>
            {
                if (!_closed && _solvingPanels) SkyGroupingStatus.Text = message;
            });
            SkyPanelAnalysis analysis = await SkyPanelService.AnalyzeAsync(paths,
                CatalogSettingsStore.LoadCatalogDirectory(), progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ApplySkyAnalysis(analysis, paths);
        }
        catch (OperationCanceledException)
        { SkyGroupingStatus.Text = "Solving cancelled. No partial grouping was applied; no files were changed."; }
        catch (Exception exception)
        { SkyGroupingStatus.Text = exception.Message; }
        finally
        {
            _solvingPanels = _running = false;
            ConfigurationHost.IsEnabled = true;
            CancelButton.IsEnabled = true;
            if (ReferenceEquals(_stackCancellation, cancellation)) _stackCancellation = null;
            cancellation.Dispose();
            RefreshGroupsAndValidation();
            if (_closeWhenIdle && !_closed) { _allowClose = true; Close(); }
        }
    }

    private void ApplySkyAnalysis(SkyPanelAnalysis analysis, IReadOnlyList<string> inputs)
    {
        _skyAnalysis = analysis;
        _skyInputs = inputs;
        SolvePanelsButton.Content = "Re-solve inputs and reset panel selection";
        // Applying a complete analysis is atomic: cancellation never leaves a
        // partially solved selection eligible for stacking.
        _initializing = true;
        try
        {
            FrameList.SelectedItems.Clear();
            foreach (StackFrameChoice frame in _frames)
                if (analysis.Sources.ContainsKey(frame.Path)) FrameList.SelectedItems.Add(frame);
        }
        finally { _initializing = false; }
        BuildSkyPanelReview();
        RefreshGroupsAndValidation();
    }

    private void BuildSkyPanelReview()
    {
        SkyPanelReview.Children.Clear();
        _skyFrameChecks.Clear();
        _skyPanelChecks.Clear();
        if (_skyAnalysis is null) return;
        FrameList.Visibility = Visibility.Collapsed;
        SkyPanelReview.Visibility = Visibility.Visible;
        LightSelectionTitle.Text = "Select panels to include";
        int fieldNumber = 0;
        foreach (SkyField field in _skyAnalysis.Fields)
        {
            SkyPanelReview.Children.Add(new TextBlock { Text = $"Sky field {++fieldNumber} · {field.Panels.Count} panel(s)",
                TextWrapping = TextWrapping.Wrap });
            int panelNumber = 0;
            foreach (SkyPanel panel in field.Panels)
            {
                var children = new StackPanel { Spacing = 4 };
                foreach (SkySolvedFrame frame in panel.Frames)
                {
                    var check = new CheckBox { Content = new TextBlock
                        {
                            Text = ImageStackFrameLabel.DisplayName(frame.Path, _browserRoot),
                            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        IsChecked = true, HorizontalAlignment = HorizontalAlignment.Stretch };
                    ToolTipService.SetToolTip(check, frame.Path);
                    check.Checked += (_, _) => SelectSkyPaths([frame.Path], true);
                    check.Unchecked += (_, _) => SelectSkyPaths([frame.Path], false);
                    _skyFrameChecks.Add(frame.Path, check);
                    children.Children.Add(check);
                }
                string[] paths = panel.Frames.Select(f => f.Path).ToArray();
                var selectPanel = new CheckBox { Content = $"Panel {++panelNumber} · {paths.Length} light(s) · {panel.Position}",
                    IsChecked = true, HorizontalAlignment = HorizontalAlignment.Stretch };
                selectPanel.Checked += (_, _) => SelectSkyPaths(paths, true);
                selectPanel.Unchecked += (_, _) => SelectSkyPaths(paths, false);
                _skyPanelChecks.Add((selectPanel, paths));
                SkyPanelReview.Children.Add(new Expander { Header = selectPanel, Content = children,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }
        if (_skyAnalysis.Failures.Count > 0)
        {
            var failures = new StackPanel { Spacing = 6 };
            foreach (SkySolveFailure failure in _skyAnalysis.Failures)
                failures.Children.Add(new TextBlock { Text = $"{ImageStackFrameLabel.DisplayName(failure.Path, _browserRoot)} — {failure.Reason}",
                    TextWrapping = TextWrapping.Wrap });
            SkyPanelReview.Children.Add(new Expander { Header = $"Excluded: {_skyAnalysis.Failures.Count} calibration / unsolved / unverified input(s)",
                Content = failures, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        SkyGroupingStatus.Text = $"Solved {_skyAnalysis.Sources.Count}/{_skyInputs?.Count ?? 0} lights; {_skyAnalysis.Fields.Count} sky field(s). " +
            "Check panels to include all their exposures; expand a panel to select individual lights. 85% overlap groups repeats, 5% connects panels. These are grouping estimates, not a guarantee of star registration.";
        SyncSkySelection();
    }

    private void SelectSkyPaths(IReadOnlyList<string> paths, bool selected)
    {
        if (_updatingPanelSelection || _running) return;
        _initializing = true;
        try
        {
            var choices = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (StackFrameChoice frame in _frames.Where(frame => choices.Contains(frame.Path)))
                if (selected) { if (!FrameList.SelectedItems.Contains(frame)) FrameList.SelectedItems.Add(frame); }
                else FrameList.SelectedItems.Remove(frame);
        }
        finally { _initializing = false; }
        SyncSkySelection();
        RefreshGroupsAndValidation();
    }

    private void SyncSkySelection()
    {
        _updatingPanelSelection = true;
        try
        {
            var selected = SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, check) in _skyFrameChecks) check.IsChecked = selected.Contains(path);
            foreach (var (check, paths) in _skyPanelChecks)
            {
                int count = paths.Count(selected.Contains);
                check.IsChecked = count == 0 ? false : count == paths.Count ? true : null;
            }
        }
        finally { _updatingPanelSelection = false; }
    }

    private void RefreshGroupsAndValidation()
    {
        IReadOnlyList<string> selected = SelectedPaths;
        bool hasMultipleFilters = ImageStackGrouping.HasMultipleDetectedFilters(selected, _filterNames);
        SplitByFilterToggle.IsEnabled = hasMultipleFilters;
        // Keep the user's split preference while temporarily selecting no
        // lights or one filter. All/None must not silently turn splitting off.
        FilterHintText.Text = hasMultipleFilters
            ? "Multiple filters were detected from headers or filenames. Split mode creates one registered FITS stack for each filter."
            : "Include at least two detected filters to create separate filter stacks.";

        _groups = SkyGroupingToggle.IsOn && _skyAnalysis is not null
            ? _skyAnalysis.SelectedGroups(selected, SplitByFilterToggle.IsOn, _filterNames)
            : ImageStackGrouping.Groups(selected, SplitByFilterToggle.IsOn, _filterNames);
        SkyOutputSummary.Visibility = SkyGroupingToggle.IsOn && _skyAnalysis is not null ? Visibility.Visible : Visibility.Collapsed;
        SkyOutputSummary.Text = $"{selected.Count} lights selected → {_groups.Count} output(s). " +
            string.Join("; ", _groups.Take(8).Select(group => $"{group.Title}: {group.Inputs.Count} lights")) +
            (_groups.Count > 8 ? "; …" : string.Empty) + ". Removing a connecting panel can split a sky field into separate outputs.";
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
                .Select(path => new StackFrameChoice(path, ImageStackFrameLabel.DisplayName(path, _browserRoot)))
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

    private void CanvasMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        bool expanded = SelectedTag(CanvasModePicker) == "Expanded";
        // Follow mode defaults only until the user explicitly edits overlap,
        // even when that edit happens to equal another mode's default.
        if (!_hasCustomOverlap)
        {
            _initializing = true;
            try { MinimumOverlapBox.Value = expanded ? 0.05 : 0.60; }
            finally { _initializing = false; }
        }
        UpdateOptionsAndValidation();
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
            if (ReferenceEquals(sender, MinimumOverlapBox)) _hasCustomOverlap = true;
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
        bool expanded = SelectedTag(CanvasModePicker) == "Expanded";
        MosaicOptionsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CanvasModeHelpText.Text = expanded
            ? "Register selected lights onto one expanding canvas per output, including images from different folders. Overlapping panels can connect through other panels. Sky-panel analysis is optional; no pre-solving catalogs are required. Star registration decides which lights join."
            : "Align and combine selected lights within a fixed reference frame. Choose Expanded mosaic to retain overlapping panels or dithered edges outside that frame.";
        SharedAlignmentToggle.IsEnabled = StackingCoreCapabilities.Current.SupportsReferenceImage &&
            (!expanded || SharedAlignmentToggle.IsOn);
        ChooseAlignmentReferenceButton.IsEnabled = !expanded;
        ClearAlignmentReferenceButton.Content = expanded ? "Clear shared alignment image" : "Use first stack's reference";
        SharedAlignmentAvailabilityText.Text = expanded
            ? SharedAlignmentToggle.IsOn
                ? "Shared fixed alignment is unavailable on an expanded canvas. Turn it off or clear the selected shared image."
                : "Each output's channel reference anchors its expanding canvas and normalization. Shared fixed alignment is unavailable."
            : _sharedAlignmentHint;
        ReferencePanel.Visibility = AutomaticReferenceToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        SharedAlignmentPanel.Visibility = SharedAlignmentToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        FooterHintText.Text = expanded
            ? "All selected lights contribute, including the anchor; disconnected panels may be rejected."
            : SharedAlignmentToggle.IsOn
            ? "Only selected light frames contribute to the stack; the alignment image supplies geometry only."
            : "The reference frame is included in the stack.";
        StackRejectionMode rejection = SelectedTag(RejectionPicker) == "None"
            ? StackRejectionMode.None
            : StackRejectionMode.DeltaSigma;
        LocalTileRow.Visibility = SelectedTag(NormalizationPicker) is "Local" or "LocalBackground"
            ? Visibility.Visible
            : Visibility.Collapsed;
        WeightingOptionsPanel.Visibility = SelectedTag(WeightingPicker) == "InverseNoiseVariance"
            ? Visibility.Visible
            : Visibility.Collapsed;
        CosmeticOptionsPanel.Visibility = SuppressHotPixelsToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
        RejectionOptionsPanel.Visibility = rejection == StackRejectionMode.DeltaSigma
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool automaticCalibration = SelectedTag(CalibrationSourcePicker) == "Automatic";
        RemoveCalibrationLibraryButton.IsEnabled = _calibrationLibraryPath is not null;
        _graded.SetInputs(_calibrationLibraryPath, SelectedPaths, _providedCalibration, automaticCalibration && !_routing);
        ManualCalibrationPanel.Visibility = automaticCalibration
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutomaticCalibrationPanel.Visibility = automaticCalibration
            ? Visibility.Visible
            : Visibility.Collapsed;
        DarkExposureBox.IsEnabled =
            !automaticCalibration &&
            DarkExposureToggle.IsOn &&
            _calibration.DarkPath is not null;
        _calibration.OverridesDarkExposure = DarkExposureToggle.IsOn;
        _calibration.DarkExposureSeconds = DarkExposureBox.Value;

        string? message = ValidationMessage(CreateOptions());
        if (message is not null) FooterHintText.Text = message;
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.Message = message ?? string.Empty;
        ValidationInfoBar.IsOpen = message is not null;
        StartButton.IsEnabled = !_running && !_solvingPanels && message is null;
    }

    private string? ValidationMessage(ImageStackOptions options)
    {
        if (_routing) return "Reading image roles before selecting lights…";
        if (SkyGroupingToggle.IsOn)
        {
            if (_skyAnalysis is null) return "Solve and review panels, or turn off optional sky-panel analysis to stack the selected lights directly.";
            if (SelectedPaths.Any(path => !_skyAnalysis.Sources.ContainsKey(path))) return "Only solved and verified lights can be included in automatic mode.";
            if (_groups.Count == 0) return "Select at least one solved panel.";
        }
        if (options.CanvasMode == StackCanvasMode.Expanded)
        {
            if (!StackingCoreCapabilities.Current.SupportsExpandedCanvas)
                return "Update Seiza to core 0.40.1 or later for expanded mosaic canvases.";
            if (SharedAlignmentToggle.IsOn)
                return "Turn off shared fixed alignment to plan an expanded mosaic canvas.";
        }
        IReadOnlyList<string> selected = SelectedPaths;
        if (selected.Count < 2)
        {
            return "Choose at least two light frames.";
        }
        if (_groups.Any(group => group.Inputs.Count < 2))
        {
            return "Every sky field / filter output needs at least two selected light frames. Select more panels or deselect that field.";
        }
        if (SharedAlignmentToggle.IsOn && !StackingCoreCapabilities.Current.SupportsReferenceImage)
        {
            return "Update Seiza to use published core 0.27.0 or later for shared alignment.";
        }
        if (SharedAlignmentToggle.IsOn && _alignmentReferencePath is not null &&
            !File.Exists(_alignmentReferencePath))
        {
            return "The alignment reference image is no longer available. Choose another image.";
        }
        if (SplitsOutput &&
            string.IsNullOrWhiteSpace(ImageStackOutputNaming.SafeBaseName(OutputBaseNameBox.Text)))
        {
            return "Enter an output base name.";
        }
        if (SelectedTag(CalibrationSourcePicker) == "Automatic")
        {
            if (_calibrationLibraryPath is null && _providedCalibration.Count == 0) return "Add calibration files or choose a library folder.";
            if (_calibrationLibraryPath is not null && !Directory.Exists(_calibrationLibraryPath)) return "The calibration folder is unavailable.";
            if (!_graded.IsCurrent) return "Calibration review needs analysis. See Calibration for progress or errors.";
            if (options.LearnDefects) return "Turn off learned sensor defects when using graded per-light calibration.";
            if (_graded.Preferences.Incomplete == "stop" && _graded.Review!.Missing.Any(m => m)) return "Some lights have incomplete calibration. Review the matches or choose Keep or Exclude in Calibration.";
        }
        return options.ValidationMessage ??
            (SelectedTag(CalibrationSourcePicker) == "Automatic"
                ? null
                : _calibration.ValidationMessage(selected));
    }

    private ImageStackOptions CreateOptions() => new()
    {
        UseGpu = _useGpuSetting(),
        GpuMemoryMib = _gpuMemorySetting(),
        CanvasMode = SelectedTag(CanvasModePicker) == "Expanded" ? StackCanvasMode.Expanded : StackCanvasMode.Reference,
        MaximumCanvasMegasamples = CheckedInt(CanvasLimitBox.Value),
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
        LocalTileSize = CheckedInt(LocalTileSizeBox.Value),
        Weighting = SelectedTag(WeightingPicker) == "InverseNoiseVariance"
            ? StackWeightingMode.InverseNoiseVariance
            : StackWeightingMode.Equal,
        MinimumWeight = MinimumWeightBox.Value,
        MaximumWeight = MaximumWeightBox.Value,
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
        CosmeticLowSigma = CosmeticLowSigmaBox.Value,
        CosmeticHighSigma = CosmeticHighSigmaBox.Value,
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
        RemoveTransients = RemoveTransientsToggle.IsOn,
    };

    private static string? SelectedTag(ComboBox picker) =>
        (picker.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static int CheckedInt(double value) => double.IsFinite(value)
        ? checked((int)Math.Round(value))
        : 0;

    private void CalibrationSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateOptionsAndValidation();
        }
    }

    private async void ChooseCalibrationLibrary_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null || _closed)
        {
            return;
        }
        _calibrationLibraryPath = folder.Path;
        CalibrationLibraryTextBox.Text = folder.Path;
        AutomaticCalibrationHintText.Text =
            "The folder is scanned recursively. Calibration files remain available in the browser.";
        UpdateOptionsAndValidation();
    }

    private async void AddCalibrationFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (string extension in ImageFileTypes.AstronomyExtensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (_closed) return;
        foreach (StorageFile file in files)
            if (!_providedCalibration.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) _providedCalibration.Add(file.Path);
        RenderProvidedCalibration(); UpdateOptionsAndValidation();
    }
    private void RemoveCalibrationLibrary_Click(object sender, RoutedEventArgs e)
    {
        _calibrationLibraryPath = null; CalibrationLibraryTextBox.Text = "";
        AutomaticCalibrationHintText.Text = ""; UpdateOptionsAndValidation();
    }
    private void RenderProvidedCalibration()
    {
        ProvidedCalibrationExpander.Header = $"Included calibration files ({_providedCalibration.Count})";
        ProvidedCalibrationPanel.Children.Clear();
        foreach (string path in _providedCalibration)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var remove = new Button { Content = "Remove" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Remove calibration file " + Path.GetFileName(path));
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            remove.Click += (_, _) => { _providedCalibration.Remove(path); RenderProvidedCalibration(); UpdateOptionsAndValidation(); };
            ProvidedCalibrationPanel.Children.Add(row);
        }
    }

    private async void ChooseBias_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationAsync(path => _calibration.BiasPath = path);

    private async void ChooseDark_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationAsync(path => _calibration.DarkPath = path);

    private async void ChooseFlat_Click(object sender, RoutedEventArgs e) =>
        await ChooseCalibrationAsync(path => _calibration.FlatPath = path);

    private async void ChooseAlignmentReference_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTag(CanvasModePicker) == "Expanded") return;
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
        if (file is not null && !_closed)
        {
            _alignmentReferencePath = file.Path;
            AlignmentReferenceText.Text = file.Path;
            UpdateOptionsAndValidation();
        }
    }

    private void ClearAlignmentReference_Click(object sender, RoutedEventArgs e)
    {
        _alignmentReferencePath = null;
        if (SelectedTag(CanvasModePicker) == "Expanded") SharedAlignmentToggle.IsOn = false;
        AlignmentReferenceText.Text = "First stack's channel reference";
        UpdateOptionsAndValidation();
    }

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
        foreach (string extension in ImageFileTypes.AstronomyExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null && !_closed)
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
        if (_running || _solvingPanels || _closed)
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
            if (outputs.Values.Any(output => _sourcePaths.Concat(_providedCalibration).Any(source => StackSourceIdentity.RefersToSameFile(output, source))))
                throw new InvalidOperationException("An output cannot replace any input or calibration file, including excluded or unselected sources.");
            await VerifySkySourcesAsync(cancellation.Token);
            _rankedReferencePaths.Clear();
            if (AutomaticReferenceToggle.IsOn)
            {
                foreach (ImageStackGroup group in _groups)
                {
                    ProgressMessageText.Text = $"{group.Title}: choosing the best reference…";
                    ProgressCountsText.Text = "Scoring sharpness and sky background";
                    StackProgressBar.IsIndeterminate = true;
                    (CalibrationFrameProbe[] probes, _) = await ProbeTargetLightsAsync(group.Inputs, cancellation.Token);
                    string[] candidates = CalibrationTargetSelection.Split(probes).Eligible
                        .Select(probe => probe.Path).ToArray();
                    StackReferenceSelection selected = await StackReferenceService.ChooseAsync(candidates, cancellation.Token);
                    _referencePaths[group.Id] = selected.ReferencePath;
                    _rankedReferencePaths[group.Id] = selected.GetAlignmentCandidates(candidates).ToArray();
                }
            }
            string? alignmentReference = SharedAlignmentToggle.IsOn
                ? _alignmentReferencePath ?? _referencePaths.GetValueOrDefault(_groups[0].Id)
                    ?? _groups[0].Inputs[0]
                : null;
            LibraryPreparedPlan? libraryPlan = null;
            if (SelectedTag(CalibrationSourcePicker) == "Automatic")
            {
                if (!_graded.IsCurrent || _graded.Review is not { } review) throw new InvalidOperationException("Analyze calibration before stacking.");
                var calibrationProgress = new Progress<LibraryProgress>(p =>
                {
                    if (!_closed) { ProgressMessageText.Text = "Preparing calibration: " + p.Phase.Replace('_', ' ');
                        ProgressCountsText.Text = $"{p.Completed} / {p.Total}"; }
                });
                LibraryRequest execute = _graded.Request("execute", review.ReviewKey);
                CalibrationLibraryPreferencesStore.Save(_graded.Preferences);
                LibraryReview built = await CalibrationLibraryService.PerformAsync(execute, calibrationProgress, cancellation.Token);
                libraryPlan = new LibraryPreparedPlan(built, execute.Lights);
            }
            using BatchCalibrationPreparation prepared = new(
                _groups.ToDictionary(g => g.Id, _ => libraryPlan is null ? _calibration.Copy() : new ImageStackCalibration()),
                libraryPlan?.Review.Warnings ?? [], []);
            FlatQualityReview[] reviews = prepared.Results.SelectMany(result => result.FlatQualityReviews).ToArray();
            if ((prepared.Warnings.Count > 0 || reviews.Length > 0) &&
                !await ConfirmCalibrationWarningsAsync(prepared.Warnings, reviews))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_closeWhenIdle)
                {
                    RestoreAfterFailure(
                        "Stacking was cancelled before any light frames were processed.",
                        InfoBarSeverity.Informational);
                }
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
                if (libraryPlan is not null) orderedInputs = libraryPlan.Inputs(orderedInputs, _graded.Preferences.Incomplete == "exclude");
                if (orderedInputs.Length < 2) throw new InvalidOperationException($"{group.Title} needs two usable lights after calibration exclusions.");
                jobs.Add(new ImageStackJob(
                    group,
                    new ImageStackRequest(
                        orderedInputs,
                        outputs[group.Id],
                        options,
                        prepared.ByGroup[group.Id])
                    {
                        LibraryPlan = libraryPlan,
                        AlignmentReferencePath = alignmentReference,
                        ChannelReferenceCandidates = alignmentReference is not null && AutomaticReferenceToggle.IsOn &&
                            StackingCoreCapabilities.Current.SupportsReferenceImageCandidates
                            ? (_rankedReferencePaths.GetValueOrDefault(group.Id) ?? [reference])
                                .Where(p => libraryPlan is null || _graded.Preferences.Incomplete != "exclude" || !libraryPlan.Review.Missing[libraryPlan.Index(p)])
                                .Select(p => libraryPlan is null ? p : libraryPlan.Review.Paths[libraryPlan.Index(p)]).ToArray()
                            : [],
                    }));
            }

            StackProgressBar.IsIndeterminate = false;
            await VerifySkySourcesAsync(cancellation.Token);
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

    private async Task VerifySkySourcesAsync(CancellationToken token)
    {
        if (!SkyGroupingToggle.IsOn || _skyAnalysis is null) return;
        SkyPanelAnalysis analysis = _skyAnalysis;
        string[] paths = _groups.SelectMany(group => group.Inputs).ToArray();
        if (!await Task.Run(() => analysis.SourcesUnchanged(paths), token))
            throw new IOException("A selected source changed after solving. Solve and group again before stacking.");
        token.ThrowIfCancellationRequested();
    }

    private static async Task<(CalibrationFrameProbe[] Probes, string[] Warnings)>
        ProbeTargetLightsAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(initialCount: 4, maxCount: 4);
        Task<(CalibrationFrameProbe? Probe, string? Warning)>[] tasks =
            paths.Select(async path =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(
                    path,
                    cancellationToken);
                return ((CalibrationFrameProbe?)(probe with { Path = Path.GetFullPath(path) }),
                    (string?)null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (null,
                    $"Could not inspect {Path.GetFileName(path)}: {exception.Message}");
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        (CalibrationFrameProbe? Probe, string? Warning)[] outcomes =
            await Task.WhenAll(tasks);
        return (
            outcomes.Select(outcome => outcome.Probe)
                .OfType<CalibrationFrameProbe>()
                .ToArray(),
            outcomes.Select(outcome => outcome.Warning).OfType<string>().ToArray());
    }

    private async Task<bool> ConfirmCalibrationWarningsAsync(IReadOnlyList<string> warnings, FlatQualityReview[] reviews)
    {
        var dialog = CalibrationReviewDialog.Create(Content.XamlRoot, warnings, reviews, "Continue stacking");
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
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
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _activeDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_activeDialog, dialog))
            {
                _activeDialog = null;
            }
        }
    }

    private void RestoreAfterFailure(string message, InfoBarSeverity severity)
    {
        if (_closed)
        {
            return;
        }
        _closeWhenIdle = false;
        _running = false;
        BuildReferenceControls();
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
        _activeDialog?.Hide();
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
        Closed -= Window_Closed;
        _activeDialog?.Hide();
        _activeDialog = null;
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
        _graded.Dispose();
        _routingCancellation.Cancel();
        _stackCancellation?.Cancel();
        _stackCancellation?.Dispose();
        _stackCancellation = null;
        GC.SuppressFinalize(this);
    }

    private sealed record StackFrameChoice(string Path, string DisplayName);

    private sealed record ImageStackOutputSelection(
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> PlaceholderPaths);

    private sealed record BatchCalibrationPreparation(
        IReadOnlyDictionary<string, ImageStackCalibration> ByGroup,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<CalibrationPreparationResult> Results) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            foreach (CalibrationPreparationResult result in Results)
            {
                result.Dispose();
            }
        }
    }
}
