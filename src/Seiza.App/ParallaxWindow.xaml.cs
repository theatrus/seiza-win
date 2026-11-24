using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Seiza.App.Controls;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Foundation;
using Windows.Globalization.NumberFormatting;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Seiza.App;

/// <summary>
/// Native editor chrome only. Scene construction, camera fitting, labels and
/// rendering stay in the shared Rust core; the session owns asynchronous work.
/// </summary>
public sealed partial class ParallaxWindow : Window, IDisposable
{
    private readonly List<Action> _refreshControls = [];
    private readonly Dictionary<TextBox, Func<bool>> _textCommitters = [];
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private RenderedImageData? _shownImage;
    private WriteableBitmap? _displayBitmap;
    private ParallaxComposition? _stripComposition;
    private Guid? _stripSelection;
    private string? _inspectorStructure;
    private int _inspectorIndex;
    private bool _refreshing;
    private bool _loaded;
    private bool _closed;
    private ContentDialog? _activityDialog;
    private ContentDialog? _editDialog;
    private double[]? _cropAnchor;
    private int[]? _cropDraft;
    private int[]? _cropNumericDraft;
    private ParallaxComposition? _cropDraftComposition;
    private bool _lockCropAspect;
    private IReadOnlyList<ParallaxPlane>? _inspectorPlanes;
    private string _sequenceFolderName = "Parallax frames";
    private readonly ParallaxWindowSizing _windowSizing;

    internal ParallaxSession Session { get; }
    internal event EventHandler? OpenCatalogSettingsRequested;

    internal ParallaxWindow(ParallaxSession session)
    {
        Session = session;
        InitializeComponent();
        _windowSizing = new(WindowHandle, 980, 680);
        Title = $"Parallax Video — {session.SourceName}";
        WindowIcon.Apply(AppWindow);
        SourceNameText.Text = session.SourceName;
        _sequenceFolderName = Path.GetFileNameWithoutExtension(session.SourceName) + "-frames";
        ContentRoot.Loaded += ContentRoot_Loaded;
        Closed += Window_Closed;
        Session.PropertyChanged += Session_PropertyChanged;
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control, Session.Undo);
        AddShortcut(VirtualKey.Y, VirtualKeyModifiers.Control, Session.Redo);
        AddShortcut(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Session.Redo);
        _activityTimer.Tick += ActivityTimer_Tick;
        _activityTimer.Start();
        Refresh();
    }

    private ParallaxComposition Composition => Session.Composition;
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Text controls keep their own editing undo stack while focused.
            if (ContentRoot.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox) return;
            if (!Session.IsBusy) { action(); args.Handled = true; }
        };
        ContentRoot.KeyboardAccelerators.Add(accelerator);
    }

    private void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 area = display.WorkArea;
        double scale = ContentRoot.XamlRoot.RasterizationScale;
        int margin = Math.Max(24, (int)Math.Round(24 * scale));
        int width = Math.Min((int)Math.Round(1200 * scale), area.Width - margin * 2);
        int height = Math.Min((int)Math.Round(840 * scale), area.Height - margin * 2);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        Refresh();
    }

    private void Window_Closed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _activityTimer.Stop();
        _activityTimer.Tick -= ActivityTimer_Tick;
        _windowSizing.Dispose();
        _activityDialog?.Hide();
        _editDialog?.Hide();
        ContentRoot.Loaded -= ContentRoot_Loaded;
        Closed -= Window_Closed;
        Session.PropertyChanged -= Session_PropertyChanged;
        Session.Close();
        PreviewImage.Source = null;
        _shownImage = null;
        _displayBitmap = null;
        GC.SuppressFinalize(this);
    }

    private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (DispatcherQueue.HasThreadAccess) Refresh();
        else DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        if (_closed || _refreshing || InspectorPanel is null) return;
        _refreshing = true;
        try
        {
            if (!ReferenceEquals(_cropDraftComposition, Composition))
            { _cropDraftComposition = Composition; _cropNumericDraft = null; }
            if (!Session.PickingCrop) { _cropAnchor = null; _cropDraft = null; }
            bool busy = Session.IsBusy;
            UndoButton.IsEnabled = Session.CanUndo && !busy;
            RedoButton.IsEnabled = Session.CanRedo && !busy;
            PrepareButton.Content = Session.HasScene ? "Update Preview" : "Prepare Preview";
            PrepareButton.IsEnabled = Session.CanPrepare;
            ExportButton.IsEnabled = Session.CanPrepare;
            InspectorHost.IsEnabled = !busy;
            TourHost.IsEnabled = !busy;
            TourPanel.Visibility = Composition.Motion == ParallaxMotion.Tour ? Visibility.Visible : Visibility.Collapsed;
            ViewPicker.SelectedIndex = Session.ShowsSource ? 0 : 1;
            PickingText.Text = Session.PickingCrop ? "Drag the filmed crop on the source image" : Session.PickingDistance ? "Click the scene’s distance reference" :
                Session.PickingDestination ? "Click a destination in the source" : string.Empty;
            PlayButton.IsEnabled = Session.HasPreview && !busy;
            PlayButton.Content = Session.IsPlaying ? "\uE769" : "\uE768";
            AutomationProperties.SetName(PlayButton, Session.IsPlaying ? "Pause preview" : "Play preview");
            Timeline.IsEnabled = Session.HasPreview && !busy;
            double duration = Composition.Duration;
            Timeline.Maximum = double.IsFinite(duration) && duration > 0 ? duration : 0.01;
            Timeline.Value = Math.Clamp(double.IsFinite(Session.Playhead) ? Session.Playhead : 0, 0, Timeline.Maximum);
            TimeText.Text = $"{Session.Playhead:0.0} / {duration:0.0} s";
            string? edgeWarning = Session.Summary?.Fit?.EdgeWarning;
            FitWarning.Message = edgeWarning ?? string.Empty;
            FitWarning.IsOpen = edgeWarning is not null;
            ErrorBar.Message = Session.Error ?? string.Empty;
            ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Session.Error);
            bool active = busy || Session.IsReconfiguring;
            OperationProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            OperationProgress.IsIndeterminate = Session.Progress is null;
            OperationProgress.Value = Math.Clamp(Session.Progress ?? 0, 0, 1);
            StatusText.Text = Session.ValidationMessage ??
                (active && !Session.Activity.CancellationRequested ? Session.Activity.NetworkPhase : null) ??
                (!busy && !Session.CanPrepare ? Session.PreparationIssue : null) ?? Session.Status;
            CancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = Session.SourceReady && !Session.Activity.CancellationRequested;
            ShowVideoButton.Visibility = Session.ExportedPath is not null ? Visibility.Visible : Visibility.Collapsed;
            UpdateActivity();

            string structure = $"{InspectorPicker.SelectedIndex}:{Composition.Motion}:{Session.SelectedStop}:{Session.SourceReady}:{Session.Width}:{Session.Height}:" +
                $"{Session.SelectedIndex}:" + string.Join(',', Composition.Labels.Select(label => label.Id)) + ":" + PlaneRowsKey() + ":" +
                string.Join(',', (Composition.Scene.Planes ?? []).Select(plane => plane.Id)) + ":" + Session.ExportFormat;
            if (_inspectorStructure != structure || !ReferenceEquals(_inspectorPlanes, Session.ScenePlanes))
            {
                _inspectorStructure = structure;
                _inspectorPlanes = Session.ScenePlanes;
                BuildInspector();
            }
            foreach (Action refresh in _refreshControls) refresh();
            if (!ReferenceEquals(_stripComposition, Composition) || _stripSelection != Session.SelectedStop)
                BuildStopStrip();
            UpdateImage();
            UpdateFocusMarker();
        }
        finally { _refreshing = false; }
    }

    private void BuildInspector()
    {
        InspectorPanel.Children.Clear();
        _refreshControls.Clear();
        _textCommitters.Clear();
        _inspectorIndex = InspectorPicker.SelectedIndex;
        switch (InspectorPicker.SelectedIndex)
        {
            case 1: BuildCameraInspector(); break;
            case 2: BuildLabelsInspector(); break;
            case 3: BuildOutputInspector(); break;
            default: BuildSourceInspector(); break;
        }
    }

    private StackPanel Section(string title)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        InspectorPanel.Children.Add(panel);
        return panel;
    }

    private TextBlock Note(StackPanel panel, string text = "", Func<string>? value = null, bool warning = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Foreground = warning ? new SolidColorBrush(Colors.Orange) :
                (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        panel.Children.Add(block);
        if (value is not null) _refreshControls.Add(() => block.Text = value());
        return block;
    }

    private Button Button(StackPanel panel, string text, Action action, Func<bool>? enabled = null)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetName(button, text.TrimEnd('…'));
        button.Click += (_, _) => { if (!_refreshing && !_closed && CommitFocusedEdit()) { action(); Refresh(); } };
        panel.Children.Add(button);
        if (enabled is not null) _refreshControls.Add(() => button.IsEnabled = enabled());
        return button;
    }

    private Button AsyncButton(StackPanel panel, string text, Func<Task> action, Func<bool>? enabled = null) =>
        Button(panel, text, () => _ = RunAsync(action), enabled);

    private static StackPanel ButtonRow(StackPanel panel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(row);
        return row;
    }

    private void Toggle(StackPanel panel, string title, Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
    {
        var toggle = new ToggleSwitch { Header = title, IsOn = get() };
        AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => { if (!_refreshing && !_closed) { set(toggle.IsOn); Refresh(); } };
        panel.Children.Add(toggle);
        _refreshControls.Add(() => { toggle.IsOn = get(); toggle.IsEnabled = enabled?.Invoke() ?? true; });
    }

    private NumberBox Number(StackPanel panel, string title, Func<double> get, Action<double> set,
        double minimum = double.MinValue, double maximum = double.MaxValue, double step = 1,
        Func<bool>? enabled = null)
    {
        var number = new NumberBox
        {
            Header = title, Value = get(), Minimum = minimum, Maximum = maximum, SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(number, title);
        // LostFocus validation in NumberBox is asynchronous. Commit while focus
        // is still changing, before a clicked command can snapshot the draft.
        number.LosingFocus += (_, args) =>
        {
            if (args.OldFocusedElement is TextBox input && !CommitNumber(number, input)) args.TryCancel();
        };
        number.ValueChanged += (_, args) =>
        {
            if (_refreshing || _closed) return;
            if (!double.IsFinite(args.NewValue))
            {
                // Empty NumberBox input is NaN. Do not round it to zero for
                // integer fields or leave the displayed clamped value out of
                // sync with the draft's validation state.
                Session.Error = $"Enter a finite number for {title.ToLowerInvariant()}.";
                _refreshing = true;
                try { number.Value = get(); }
                finally { _refreshing = false; }
                Refresh();
                return;
            }
            set(args.NewValue); Refresh();
        };
        panel.Children.Add(number);
        _refreshControls.Add(() => { number.Value = get(); number.IsEnabled = enabled?.Invoke() ?? true; });
        return number;
    }

    private void Text(StackPanel panel, string title, Func<string> get, Action<string> set,
        Func<bool>? enabled = null)
    {
        var text = new TextBox { Header = title, Text = get(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(text, title);
        // Commit text as one edit, preserving useful undo steps and the caret
        // while asynchronous progress notifications refresh other controls.
        bool Commit()
        {
            if (!_refreshing && !_closed && text.Text != get()) { set(text.Text); Refresh(); }
            return true;
        }
        _textCommitters.Add(text, Commit);
        text.LosingFocus += (_, _) => Commit();
        text.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) Commit(); };
        panel.Children.Add(text);
        _refreshControls.Add(() =>
        {
            if (text.FocusState == FocusState.Unfocused) text.Text = get();
            text.IsEnabled = enabled?.Invoke() ?? true;
        });
    }

    private ComboBox Choice<T>(StackPanel panel, string title, Func<T> get, Action<T> set,
        params (T Value, string Name)[] choices)
    {
        var picker = new ComboBox { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(picker, title);
        foreach ((T _, string name) in choices) picker.Items.Add(name);
        void Select() => picker.SelectedIndex = Array.FindIndex(choices,
            choice => EqualityComparer<T>.Default.Equals(choice.Value, get()));
        Select();
        picker.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && !_closed && picker.SelectedIndex >= 0)
            { set(choices[picker.SelectedIndex].Value); Refresh(); }
        };
        panel.Children.Add(picker);
        _refreshControls.Add(Select);
        return picker;
    }

    private void Slider(StackPanel panel, string title, Func<double> get, Action<double> set,
        double minimum = 0, double maximum = 1, Func<double, string>? format = null,
        string? tooltip = null, double step = 0.01, bool retainRange = false)
    {
        var label = new TextBlock { FontSize = 12 };
        panel.Children.Add(label);
        var slider = new Slider { Minimum = retainRange ? Math.Min(minimum, get()) : minimum,
            Maximum = retainRange ? Math.Max(maximum, get()) : maximum, Value = get(), StepFrequency = step };
        AutomationProperties.SetName(slider, title);
        if (tooltip is not null) ToolTipService.SetToolTip(slider, tooltip);
        slider.ValueChanged += (_, args) =>
        {
            if (!_refreshing && !_closed) { set(args.NewValue); Refresh(); }
        };
        panel.Children.Add(slider);
        _refreshControls.Add(() =>
        {
            if (retainRange) { slider.Minimum = Math.Min(minimum, get()); slider.Maximum = Math.Max(maximum, get()); }
            slider.Value = get(); label.Text = $"{title}: {format?.Invoke(get()) ?? get().ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)}";
        });
    }

    private void ZoomControl(StackPanel panel, string title, Func<double> get, Action<double> set, double minimum = 1)
    {
        Slider(panel, title, get, set, minimum, 8, value => $"{value:0.##}×",
            "Lens magnification: 0.5× gives a wider view, 1× is the base view, and 2× magnifies it. Dolly moves through depth instead. Overall zoom and camera fitting also apply.",
            step: 0.05, retainRange: true);
        var reset = Button(panel, "Reset to 1×", () => set(1));
        AutomationProperties.SetName(reset, $"Reset {title} to 1×");
    }

    private void DollyControl(StackPanel panel, Func<double> get, Action<double> set)
    {
        Slider(panel, "Dolly", get, set, 0, 0.99, value => $"{value:P0}",
            "Moves toward the background through depth. Reducing Dolly pulls back and changes how near stars move against the nebula; a high Dolly stays close even at Zoom 1×.", retainRange: true);
        var reset = Button(panel, "Reset Dolly to 0%", () => set(0));
        AutomationProperties.SetName(reset, "Reset Dolly to 0%");
    }

    private void OptionalDistance(StackPanel panel, string title, string field, Func<double?> get, Action<double?> set,
        double fallback)
    {
        Toggle(panel, title, () => get() is not null, value => set(value ? fallback : null));
        Number(panel, field, () => get() ?? fallback, value => set(value), 0, enabled: () => get() is not null);
    }

    // An image laid over the frames, its transparency kept: a logo or a title
    // card. Its path is part of the draft, so choosing one can be undone.
    private void ImagePicker(StackPanel panel, string title, Func<string?> get, Action<string?> set, params string[] extensions)
    {
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        var row = ButtonRow(panel);
        Button choose = AsyncButton(row, "Choose…", async () =>
        {
            if (await PickFileAsync(extensions.Length == 0 ? [".png", ".tif", ".tiff"] : extensions) is { } path) set(path);
        });
        AutomationProperties.SetName(choose, $"Choose {title.ToLowerInvariant()}");
        Button clear = Button(row, "Clear", () => set(null), () => get() is not null);
        AutomationProperties.SetName(clear, $"Clear {title.ToLowerInvariant()}");
        Note(panel, value: () => get() is { } path ? Path.GetFileName(path) : "None selected");
    }

    private void InputPicker(StackPanel panel, ParallaxInputFile kind, string title)
    {
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        var row = ButtonRow(panel);
        Button choose = AsyncButton(row, "Choose…", () => ChooseInputAsync(kind));
        AutomationProperties.SetName(choose, $"Choose {title}");
        if (kind == ParallaxInputFile.Objects)
        {
            Button folder = AsyncButton(row, "Folder…", ChooseObjectFolderAsync);
            AutomationProperties.SetName(folder, "Choose object catalogue folder");
        }
        Button(row, "Reset", () => Session.ClearInput(kind), () => Session.InputNames.ContainsKey(kind));
        Note(panel, value: () => Session.InputNames.GetValueOrDefault(kind) ?? "None selected");
    }

    private void BuildSourceInspector()
    {
        StackPanel source = Section("Source snapshot");
        Note(source, value: () => Session.SourceReady ? $"Source: {Session.Width:N0} × {Session.Height:N0} pixels · Filmed: {Session.FilmedRectangle[2]:N0} × {Session.FilmedRectangle[3]:N0}" : "Preparing full-resolution snapshot…");
        Note(source, value: () => Session.SolvingDescription);
        Note(source, value: () => Session.PreparationIssue ?? string.Empty, warning: true);
        Note(source, "Uses the current image adjustments. Imported layers must use the same stretch and alignment.");
        BuildCropInspector();

        StackPanel separation = Section("Star separation");
        Toggle(separation, "Use StarXTerminator", () => Session.AutomaticSeparation, value => Session.AutomaticSeparation = value);
        var automatic = new StackPanel { Spacing = 8 };
        separation.Children.Add(automatic);
        Note(automatic, CatalogSettingsStore.LoadRcAstroExecutable() is string executable
            ? $"RC-Astro: {executable}" : "Choose rc-astro.exe once in Settings; it is shared by all Parallax windows.");
        Button(automatic, "RC-Astro Settings…", () => OpenCatalogSettingsRequested?.Invoke(this, EventArgs.Empty));
        Note(automatic, "Requires a local RC-Astro CLI with a valid StarXTerminator license.");
        var manual = new StackPanel { Spacing = 8 };
        separation.Children.Add(manual);
        AsyncButton(manual, "Choose Starless…", () => ChooseLayerAsync(true), () => Session.SourceReady);
        Note(manual, value: () => Session.StarlessName ?? "No starless image selected");
        AsyncButton(manual, "Choose Stars…", () => ChooseLayerAsync(false), () => Session.SourceReady);
        Note(manual, value: () => Session.StarsName ?? "No stars image selected");
        Note(manual, "PNG or TIFF, full size. Stars must be unscreened.");
        Toggle(manual, "Layers match source alignment and stretch", () => Session.LayersAligned,
            value => Session.LayersAligned = value, () => Session.StarlessName is not null && Session.StarsName is not null);
        _refreshControls.Add(() =>
        {
            automatic.Visibility = Session.AutomaticSeparation ? Visibility.Visible : Visibility.Collapsed;
            manual.Visibility = Session.AutomaticSeparation ? Visibility.Collapsed : Visibility.Visible;
        });

        StackPanel depth = Section("Scene depth");
        Note(depth, value: () => Session.CatalogStatus);
        AsyncButton(depth, "Download Offline Distances", Session.DownloadCatalogsAsync,
            () => !Session.IsBusy && !Session.CatalogsRunning);
        Button(depth, "Catalogue Settings…", () => OpenCatalogSettingsRequested?.Invoke(this, EventArgs.Empty));
        Toggle(depth, "Look up stellar distances online", () => Composition.Scene.Online,
            value => Session.Edit(c => c.Scene.Online = value));
        Note(depth, "Uses the installed database when it covers the selected Gaia limit. Online lookup sends sky coordinates to the catalogues when needed. Unmatched stars use the fallback distance.");
        Note(depth, value: () => $"Distance reference: {Composition.DistanceName}");
        Button(depth, "Pick Distance Reference", Session.PickDistanceReference);
        Number(depth, "Distance reference X", () => DistanceCoordinate(0), value => SetDistanceCoordinate(0, value));
        Number(depth, "Distance reference Y", () => DistanceCoordinate(1), value => SetDistanceCoordinate(1, value));
        Note(depth, "Moving the camera does not change this reference.");
        OptionalDistance(depth, "Set background distance", "Background (pc)", () => Composition.Scene.DistanceParsecs,
            value => Session.Edit(c => c.Scene.DistanceParsecs = value), 400);
        OptionalDistance(depth, "Set unmatched star distance", "Unmatched (pc)", () => Composition.Scene.UnmatchedDistanceParsecs,
            value => Session.Edit(c => c.Scene.UnmatchedDistanceParsecs = value), 1000);
        Number(depth, "Gaia magnitude limit", () => Composition.Scene.GaiaMaxMagnitude,
            value => Session.Edit(c => c.Scene.GaiaMaxMagnitude = value), 1, 25, 0.5);

        StackPanel stars = Section("Stars and dust");
        Toggle(stars, "Limit moving stars", () => Composition.Scene.MaxStars is not null,
            value => Session.Edit(c => c.Scene.MaxStars = value ? 2000 : null));
        Number(stars, "Brightest stars", () => Composition.Scene.MaxStars ?? 2000,
            value => Session.Edit(c => c.Scene.MaxStars = Integer(value)), 0, int.MaxValue, 100,
            () => Composition.Scene.MaxStars is not null);
        Choice(stars, "Remaining stars", () => Composition.Scene.SmallStars,
            value => Session.Edit(c => c.Scene.SmallStars = value), ("field", "Keep on field"), ("drop", "Drop"));
        Toggle(stars, "Keep galaxies on background", () => Composition.Scene.KeepGalaxies,
            value => Session.Edit(c => c.Scene.KeepGalaxies = value));
        Toggle(stars, "Dust dimming", () => Composition.Scene.Dust, value => Session.Edit(c => c.Scene.Dust = value));
        Number(stars, "Dust strength", () => Composition.Scene.DustOpacity, value => Session.Edit(c => c.Scene.DustOpacity = value),
            0, step: 0.1, enabled: () => Composition.Scene.Dust);
        // A map painted white where the dust is opaque replaces the counted dust;
        // stars seen through its opaque places fly in front of the nebula.
        InputPicker(stars, ParallaxInputFile.DustMap, "dust map (white where opaque)");
        var dust = ButtonRow(stars);
        AsyncButton(dust, "Reload", Session.ReloadDustMapAsync, () => Session.CanReloadDustMap);
        AsyncButton(dust, "Export Dust Map…", ExportDustMapAsync, () => Session.CanExportDustMap);
        AsyncButton(stars, "Paint Dust Map…", EditDustMapAsync, () => Session.CanEditDustMap);
        Choice(stars, "Dust map to export", () => Session.ExportCountedDustMap, value => Session.ExportCountedDustMap = value,
            (false, "Current map"), (true, "Counted from stars"));
        Note(stars, value: () => Session.DustDescription);

        StackPanel depthPlanes = Section("Depth planes");
        Toggle(depthPlanes, "Automatic depth planes", () => Composition.Scene.AutoPlanes,
            value => Session.Edit(c => c.Scene.AutoPlanes = value));
        Note(depthPlanes, "Catalogued nebulae at distances of their own, and dust clouds in front of the nebula, move on planes of their own. A dark nebula moves with the dust it lies in.");
        Note(depthPlanes, value: () => Session.PlanesDescription);
        AsyncButton(depthPlanes, "Add Object Distance…", AddObjectDistanceAsync);
        var (planes, others) = PlaneRows();
        foreach (ParallaxPlane plane in planes) PlaneRow(depthPlanes, plane);
        if (others.Count > 0)
        {
            depthPlanes.Children.Add(new TextBlock { Text = "Other distances you gave", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (string name in others)
            {
                StackPanel card = Card(depthPlanes);
                card.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                GivenDistance(card, name, null);
            }
        }
        if (others.Count > 0 || planes.Any(plane => plane.ObjectName is not null))
            Note(depthPlanes, "A distance you give replaces the catalogue's, for the nebula's distance and its planes, when the scene is prepared.");
        Note(depthPlanes, "Name any catalogued object to override its distance, including large objects that automatic planes leave on the background.");

        StackPanel manualPlanes = Section("Custom depth planes");
        AsyncButton(manualPlanes, "Add Plane Mask…", AddDepthPlaneAsync);
        Note(manualPlanes, "A white mask lifts that part of the starless image to its own distance. Black stays on the background. Masks use the source image's shape and may be a different resolution.");
        foreach (ParallaxDepthPlane original in Composition.Scene.Planes ?? [])
        {
            Guid id = original.Id;
            ParallaxDepthPlane? Plane() => Composition.Scene.Planes?.FirstOrDefault(plane => plane.Id == id);
            void EditPlane(Action<ParallaxDepthPlane> edit) => Session.Edit(c =>
            {
                if (c.Scene.Planes?.FirstOrDefault(plane => plane.Id == id) is { } current) edit(current);
            });
            StackPanel card = Card(manualPlanes);
            Note(card, value: () => Plane()?.DisplayName ?? string.Empty);
            Text(card, "Plane name", () => Plane()?.Name ?? string.Empty,
                value => EditPlane(plane => plane.Name = string.IsNullOrWhiteSpace(value) ? null : value.Trim()));
            ImagePicker(card, "Plane mask", () => Plane()?.Mask, path => EditPlane(plane => plane.Mask = path ?? string.Empty),
                ".png", ".tif", ".tiff", ".jpg", ".jpeg", ".fits", ".fit", ".fts", ".xisf");
            Number(card, "Plane distance (pc)", () => Plane()?.DistanceParsecs ?? 1,
                value => EditPlane(plane => plane.DistanceParsecs = value), 1, step: 10);
            Button(card, "Remove Plane", () => Session.Edit(c =>
            {
                c.Scene.Planes?.RemoveAll(plane => plane.Id == id);
                if (c.Scene.Planes is { Count: 0 }) c.Scene.Planes = null;
            }));
        }

        StackPanel catalogues = Section("Catalogues and solving");
        Note(catalogues, value: () => string.IsNullOrWhiteSpace(Session.CatalogDirectoryPath)
            ? "Choose a catalogue directory in Catalogue Settings to enable solving and tour generation."
            : Session.CatalogDirectoryPath);
        InputPicker(catalogues, ParallaxInputFile.Objects, "object catalogue");
        InputPicker(catalogues, ParallaxInputFile.ObjectDistances, "object distances");
        InputPicker(catalogues, ParallaxInputFile.StarDistances, "star distances");
        Number(catalogues, "Minimum scale (arcsec/px)", () => Composition.Scene.MinimumScaleArcsecPerPixel,
            value => Session.Edit(c => c.Scene.MinimumScaleArcsecPerPixel = value), 0, step: 0.1);
        Number(catalogues, "Maximum scale (arcsec/px)", () => Composition.Scene.MaximumScaleArcsecPerPixel,
            value => Session.Edit(c => c.Scene.MaximumScaleArcsecPerPixel = value), 0, step: 0.1);

        StackPanel summary = Section("Prepared scene");
        Note(summary, value: () => Session.Summary is { } scene ?
            $"{scene.WithDistance:N0} / {scene.DetectedStars:N0} stars with distances\n" +
            $"Background: {scene.BackgroundDistanceParsecs:N0} pc ({scene.BackgroundBasis})" : "No scene prepared yet.");
        Note(summary, value: () => string.Join("\n", Session.Warnings), warning: true);
        var details = new Expander { Header = "Preparation details", HorizontalAlignment = HorizontalAlignment.Stretch };
        var notes = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        details.Content = notes;
        summary.Children.Add(details);
        _refreshControls.Add(() => notes.Text = string.Join("\n", Session.PreparationNotes));
    }

    // The depth plane list: the planes found when the scene was last prepared,
    // then any other objects given a distance. The inspector is rebuilt when
    // these change, which happens only after a preparation or an edit here.
    private (IReadOnlyList<ParallaxPlane> Planes, IReadOnlyList<string> Others) PlaneRows()
    {
        IReadOnlyList<ParallaxPlane> planes = Composition.Scene.AutoPlanes || Composition.Scene.Planes is { Count: > 0 } ? Session.ScenePlanes ?? [] : [];
        List<string> others = (Composition.Scene.DistanceOverrides ?? []).Select(given => given.ObjectName)
            .Where(name => !planes.Any(plane => ParallaxSceneSettings.SameObject(plane.ObjectName, name))).ToList();
        return (planes, others);
    }

    private string PlaneRowsKey()
    {
        var (planes, others) = PlaneRows();
        return string.Join('|', planes.Select(plane => plane.Name)) + "/" + string.Join('|', others);
    }

    private void PlaneRow(StackPanel panel, ParallaxPlane plane)
    {
        StackPanel card = Card(panel);
        card.Children.Add(new TextBlock { Text = plane.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Note(card, $"{plane.DistanceParsecs:N0} pc, {plane.Basis} · {plane.Coverage:P1} of the image" + (plane.Absorbs ? " · dark cloud" : string.Empty));
        if (plane.ObjectName is string name) GivenDistance(card, name, plane.DistanceParsecs);
        else Note(card, "No catalogued object names it, so its distance cannot be given here.");
    }

    // A distance for a catalogued object in place of the one found, and a way
    // back to the one found.
    private void GivenDistance(StackPanel card, string name, double? found)
    {
        NumberBox distance = Number(card, "Distance (pc)", () => Composition.Scene.DistanceOverride(name) ?? found ?? 1,
            value => Session.SetDistanceOverride(name, value), 1, step: 10);
        AutomationProperties.SetName(distance, $"Distance of {name} in parsecs");
        Button reset = Button(card, "Reset", () => Session.SetDistanceOverride(name, null),
            () => Composition.Scene.DistanceOverride(name) is not null);
        AutomationProperties.SetName(reset, $"Reset the distance of {name}");
    }

    private static StackPanel Card(StackPanel panel)
    {
        var card = new StackPanel { Spacing = 8 };
        panel.Children.Add(new Border
        {
            Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = card,
        });
        return card;
    }

    private void BuildCameraInspector()
    {
        StackPanel motion = Section("Camera");
        Choice(motion, "Motion", () => Composition.Motion, value => Session.Edit(c => c.Motion = value),
            (ParallaxMotion.FlyIn, "Fly In"), (ParallaxMotion.Tour, "Tour"));
        StackPanel generation = Section("Generate tour");
        Toggle(generation, "Automatic target count", () => Session.TourOptions.Targets is null,
            value => Session.TourOptions.Targets = value ? null : 5);
        Number(generation, "Targets", () => Session.TourOptions.Targets ?? 5,
            value => Session.TourOptions.Targets = Integer(value), 1, 20,
            enabled: () => Session.TourOptions.Targets is not null);
        Number(generation, "Hold per stop (s)", () => Session.TourOptions.Hold, value => Session.TourOptions.Hold = value, 0, 600, 0.1);
        Slider(generation, "Generated motion", () => Session.TourOptions.Motion, value => Session.TourOptions.Motion = value, 0, 2);
        Note(generation, "0 removes generated roll and pan. Options affect the next candidate, not the current draft.");
        Button generate = AsyncButton(generation, "Generate Tour", Session.GenerateTourAsync,
            () => Session.SourceReady && !Session.CatalogsRunning && Session.TourSolveIssue is null);
        Note(generation, value: () => Session.TourSolveIssue ?? string.Empty, warning: true);
        _refreshControls.Add(() => generate.Content = Session.HasWcs ? "Generate Tour" : "Solve & Generate Tour");
        var candidate = new StackPanel { Spacing = 8 };
        generation.Children.Add(candidate);
        Note(candidate, value: () => Session.Candidate is { } plan ? $"{plan.Tour.Count} stops · {plan.Seconds:0.0} seconds" : string.Empty);
        StackPanel candidates = ButtonRow(candidate);
        Button(candidates, "Apply Tour", Session.ApplyCandidate);
        Button(candidates, "Keep Draft", Session.DiscardCandidate);
        _refreshControls.Add(() => candidate.Visibility = Session.Candidate is not null ? Visibility.Visible : Visibility.Collapsed);

        if (Composition.Motion == ParallaxMotion.FlyIn)
        {
            StackPanel fly = Section("Fly in");
            Number(fly, "Duration (s)", () => Composition.Seconds, value => Session.Edit(c => c.Seconds = value), 0, 600, 0.1);
            Choice(fly, "Start", () => Composition.Start, value => Session.Edit(c => c.Start = value),
                ("whole", "Whole Image"), ("focus", "At Focus"));
            Button(fly, "Pick Destination", BeginPickDestination);
            Button(fly, "Use Image Center", () => Session.Edit(c => { c.Focus = Center(); c.FocusName = "Image center"; }));
            DestinationCoordinates(fly);
            DollyControl(fly, () => Composition.Dolly, value => Session.Edit(c => c.Dolly = value));
            Slider(fly, "Pan", () => Composition.Pan, value => Session.Edit(c => c.Pan = value));
            Number(fly, "Start roll (°)", () => Composition.RotationStart, value => Session.Edit(c => c.RotationStart = value));
            Number(fly, "End roll (°)", () => Composition.Rotation, value => Session.Edit(c => c.Rotation = value));
            RollExplanation(fly);
            ZoomControl(fly, "Opening zoom", () => Composition.Zoom, value => Session.Edit(c => c.Zoom = value));
            ZoomControl(fly, "End lens multiplier", () => Composition.ZoomEnd, value => Session.Edit(c => c.ZoomEnd = value));
            Number(fly, "Sideways swing", () => Composition.Truck, value => Session.Edit(c => c.Truck = value), step: 0.01);
            Number(fly, "Swing direction (°)", () => Composition.TruckAngleDegrees,
                value => Session.Edit(c => c.TruckAngleDegrees = value));
            Choice(fly, "Easing", () => Composition.Easing, value => Session.Edit(c => c.Easing = value),
                ("inOut", "Smooth"), ("linear", "Linear"));
        }
        else if (Session.SelectedIndex is int index)
        {
            StackPanel stop = Section($"Stop {index + 1}");
            StopText(stop, "Name", index, s => s.Name, (s, value) => s.Name = value);
            StopText(stop, "On-screen title", index, s => s.Title,
                (s, value) => s.Title = string.IsNullOrWhiteSpace(value) ? null : value);
            Note(stop, "Shown during this stop when Show stop titles is enabled. Leave blank for no title.");
            ImagePicker(stop, "Title card", () => SelectedStop()?.Card, path => Session.EditStop(s => s.Card = path));
            Note(stop, "An image with transparency shown during this stop in place of its title, whether or not titles are shown. Make it the video's size for sharp lettering.");
            Button(stop, "Pick Destination", BeginPickDestination);
            Button(stop, "Use Whole Image", () => Session.EditStop(s =>
            { s.Focus = null; s.Name = "Whole Image"; s.Dolly = 0; s.Zoom = 1; s.Pan = 0; s.DepthParsecs = null; s.PullBackToWholeImage = false; }));
            DestinationCoordinates(stop);
            OptionalDistance(stop, "Set target plane distance", "Target depth (pc)", () => SelectedStop()?.DepthParsecs,
                value => Session.EditStop(s => s.DepthParsecs = value),
                Session.Summary?.BackgroundDistanceParsecs ?? Composition.Scene.DistanceParsecs ?? 400);
            Note(stop, "Leave off to use the depth plane under this point. A given depth centres this stop on that physical distance; it does not move the scene's planes.");
            Number(stop, "Travel to stop (s)", () => SelectedStop()?.Travel ?? 0,
                value => Session.EditStop(s => s.Travel = value), 0, 600, 0.1, () => Session.SelectedIndex > 0);
            Number(stop, "Hold (s)", () => SelectedStop()?.Hold ?? 0,
                value => Session.EditStop(s => s.Hold = value), 0, 600, 0.1);
            Note(stop, "Set hold to 0 to pass straight through. Glide, spin and push can keep a held stop moving.");
            DollyControl(stop, () => SelectedStop()?.Dolly ?? 0, value => Session.EditStopCamera(s => s.Dolly = value));
            ZoomControl(stop, "Zoom", () => SelectedStop()?.Zoom ?? 1, value => Session.EditStopCamera(s => s.Zoom = value), 0.1);
            Button(stop, "Pull Back", Session.PullBackStop, () => ParallaxCore.SupportsPullBackToWholeImage);
            Note(stop, ParallaxCore.SupportsPullBackToWholeImage ?
                "Pull Back widens toward the whole image, keeping this target, its depth and timing. Tighter Dolly/Zoom views return toward the target." :
                "Whole-image pull-back needs a native core containing Seiza #283; this core keeps authored point-centred framing.");
            var wide = new StackPanel { Spacing = 8 }; stop.Children.Add(wide);
            Note(wide, "Fill Frame may increase fitted zoom to hide image edges. Show Whole Image preserves a wider stop and permits black space; this changes framing for the entire video.");
            Button(wide, "Show Whole Image for Entire Video", () => Session.Edit(c => c.Framing = "fit"), () => ParallaxCore.SupportsFraming);
            _refreshControls.Add(() => wide.Visibility = SelectedStop()?.Zoom < 1 && Composition.Framing == "fill" ? Visibility.Visible : Visibility.Collapsed);
            Slider(stop, "Pan", () => SelectedStop()?.Pan ?? 0, value => Session.EditStop(s => s.Pan = value));
            Number(stop, "Roll (°)", () => SelectedStop()?.RotateDegrees ?? 0, value => Session.EditStop(s => s.RotateDegrees = value));
            StackPanel turns = ButtonRow(stop);
            Button(turns, "Travel Turn CW", () => Session.Edit(c => c.SetTravelTurn(index, true)), () => index > 0);
            Button(turns, "Travel Turn CCW", () => Session.Edit(c => c.SetTravelTurn(index, false)), () => index > 0);
            Number(stop, "Spin around stop (°)", () => SelectedStop()?.SpinDegrees ?? 0, value => Session.EditStop(s => s.SpinDegrees = value));
            StackPanel spins = ButtonRow(stop);
            Button(spins, "Spin CW", () => Session.Edit(c => c.SetSpin(index, true)));
            Button(spins, "Spin CCW", () => Session.Edit(c => c.SetSpin(index, false)));
            Slider(stop, "Push during hold", () => SelectedStop()?.Push ?? 0, value => Session.EditStop(s => s.Push = value), 0, 0.95);
            Note(stop, value: () => $"Push {(SelectedStop()?.Push ?? 0) * 100:0}% of the remaining distance");
            Note(stop, "Spin blends into arrival and departure and carries through later stops. It needs a positive hold; combine it with push for a spiral.");
            RollExplanation(stop);
            StackPanel reorder = ButtonRow(stop);
            Button(reorder, "Earlier", () => Session.MoveStop(-1), () => Session.SelectedIndex > 0);
            Button(reorder, "Later", () => Session.MoveStop(1), () => Session.SelectedIndex < Composition.Stops.Count - 1);
            StackPanel edit = ButtonRow(stop);
            Button(edit, "Duplicate", Session.DuplicateStop);
            Button(edit, "Remove", Session.RemoveStop, () => Composition.Stops.Count > 2);
        }
        else
        {
            Note(Section("Tour stop"), "Select a stop below the image to edit its camera and timing.");
        }

        if (Composition.Motion == ParallaxMotion.Tour)
        {
            StackPanel tour = Section("Tour motion");
            ZoomControl(tour, "Overall zoom", () => Composition.Zoom, value => Session.Edit(c => c.Zoom = value));
            Note(tour, "Multiplies every stop’s zoom, including the opening stop.");
            Number(tour, "Glide through holds", () => Composition.TourGlide, value => Session.Edit(c => c.TourGlide = value), 0, step: 0.1);
            Note(tour, "A fraction of travel speed: 0 pauses, 0.2 glides gently. Spinning stops turn around their own destination.");
            Toggle(tour, "Show stop titles", () => Composition.TourTitles, value => Session.Edit(c => c.TourTitles = value));
            Toggle(tour, "Loop tour", () => Composition.TourLoop, value => Session.Edit(c => c.TourLoop = value));
            Note(tour, value: () => Composition.TourLoop ?
                "Returns to the opening view with a gentle zoom through the join, even at zero glide. Push at the opening and closing stops is unused." :
                "The final hold drifts gently to rest.");
        }
        StackPanel preview = Section("Preview");
        Toggle(preview, "Update camera preview automatically", () => Session.AutomaticPreview, value => Session.AutomaticPreview = value);
        Note(preview, value: () => Session.Summary?.Fit?.EdgeWarning ?? string.Empty, warning: true);
        Note(preview, value: () => Session.Summary?.Fit is { } fit ?
            (fit.Adjustments.Count == 0 ? (fit.Inside ? "Requested framing fits the image." : string.Empty) :
                (fit.Inside ? "Adjusted to keep image edges outside the frame:\n" : "Framing adjustments applied:\n") +
                string.Join("\n", fit.Adjustments)) : "Prepare a preview to check framing.");
    }

    private void RollExplanation(StackPanel panel) => Note(panel,
        "Positive roll and spin turn counterclockwise. Roll defines travel between stops; spin adds a turn around a held stop. Pan turns toward the destination; 0 uses translation only.");

    private void DestinationCoordinates(StackPanel panel)
    {
        Number(panel, "Destination X", () => FocusCoordinate(0), value => SetFocusCoordinate(0, value));
        Number(panel, "Destination Y", () => FocusCoordinate(1), value => SetFocusCoordinate(1, value));
        Note(panel, "Destination in source pixels, measured from the top-left corner.");
    }

    private void BuildLabelsInspector()
    {
        StackPanel catalogue = Section("Catalogue labels");
        Toggle(catalogue, "Object labels", () => Composition.Overlay, value => Session.Edit(c => c.Overlay = value));
        Slider(catalogue, "Density", () => Composition.OverlayDensity, value => Session.Edit(c => c.OverlayDensity = value));
        Note(catalogue, "Labels follow the depth and camera projection of their objects.");
        StackPanel labels = Section("Custom labels");
        Text(labels, "Color (#RRGGBB)", () => Composition.LabelColor, value => Session.Edit(c => c.LabelColor = value));
        foreach (ParallaxLabel original in Composition.Labels)
        {
            Guid id = original.Id;
            ParallaxLabel? Label() => Composition.Labels.FirstOrDefault(label => label.Id == id);
            void EditLabel(Action<ParallaxLabel> edit) => Session.Edit(c =>
            {
                if (c.Labels.FirstOrDefault(label => label.Id == id) is { } current) edit(current);
            });
            var panel = new StackPanel { Spacing = 8 };
            labels.Children.Add(new Border
            {
                Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = panel,
            });
            Text(panel, "Text", () => Label()?.Text ?? string.Empty, value => EditLabel(label => label.Text = value));
            Number(panel, "Label X", () => Label()?.X ?? 0, value => EditLabel(label => label.X = value));
            Number(panel, "Label Y", () => Label()?.Y ?? 0, value => EditLabel(label => label.Y = value));
            Number(panel, "Radius (px)", () => Label()?.Radius ?? 0, value => EditLabel(label => label.Radius = value), 0);
            Button(panel, "Remove Label", () => Session.Edit(c => c.Labels.RemoveAll(label => label.Id == id)));
        }
        Button(labels, "Add Label", () => Session.Edit(c => c.Labels.Add(new ParallaxLabel { X = Center()[0], Y = Center()[1] })));
        Note(labels, "Positions and radius use source pixels.");
        StackPanel credit = Section("Credit");
        Toggle(credit, "Show credit", () => Composition.CreditEnabled, value => Session.Edit(c => c.CreditEnabled = value));
        Text(credit, "Credit text", () => Composition.Credit, value => Session.Edit(c => c.Credit = value), () => Composition.CreditEnabled);
        ImagePicker(credit, "Logo", () => Composition.WatermarkImage, path => Session.Edit(c => c.WatermarkImage = path));
        Number(credit, "Logo size", () => Composition.WatermarkSize, value => Session.Edit(c => c.WatermarkSize = value),
            0.01, 1, 0.01, () => Composition.WatermarkImage is not null);
        Note(credit, "The logo's height as a share of the frame's shorter side; it is at most a third of the frame's width. With credit text, it sits just inside the text.");
        Choice(credit, "Corner", () => Composition.WatermarkCorner, value => Session.Edit(c => c.WatermarkCorner = value),
            ("bottomRight", "Bottom right"), ("bottomLeft", "Bottom left"), ("topRight", "Top right"), ("topLeft", "Top left"));
        Number(credit, "Opacity", () => Composition.WatermarkOpacity, value => Session.Edit(c => c.WatermarkOpacity = value),
            0, 1, 0.05);
        Note(credit, "The corner and opacity apply to the credit text and the logo alike.");

        StackPanel cards = Section("Title cards");
        ImagePicker(cards, "Opening card", () => Composition.IntroCard, path => Session.Edit(c => c.IntroCard = path));
        ImagePicker(cards, "Closing card", () => Composition.OutroCard, path => Session.Edit(c => c.OutroCard = path));
        Number(cards, "Card seconds", () => Composition.CardSeconds, value => Session.Edit(c => c.CardSeconds = value),
            0.1, 600, 0.5);
        Note(cards, "Images with transparency, fitted inside the frame and centred: make them the video's size for sharp lettering. The opening card shows from the first frame and fades out; the closing card fades in for the end. A looping tour shows both across its join, so it still loops. A tour stop can carry a card of its own.");
    }

    private void BuildCropInspector()
    {
        StackPanel crop = Section("Filmed crop");
        Note(crop, "The source, layers, dust maps and WCS stay untouched. Cropping rebuilds the scene; camera points and labels retain their source positions.");
        if (!Session.SourceReady || Session.Width < 16 || Session.Height < 16)
        { Note(crop, "Cropping needs a ready source at least 16 pixels each way."); return; }
        bool Available() => ParallaxCore.SupportsFraming && Session.SourceReady;
        Button(crop, "Draw Crop on Source", () =>
        {
            Session.ShowsSource = true; Session.PickingCrop = true;
            Session.PickingDestination = Session.PickingDistance = false;
            _cropAnchor = null; _cropDraft = null;
        }, Available);
        Toggle(crop, "Lock crop to frame shape", () => _lockCropAspect, value => _lockCropAspect = value, Available);
        int[] Draft() => _cropNumericDraft ?? Session.FilmedRectangle;
        void Change(int axis, double value)
        {
            _cropNumericDraft ??= Session.FilmedRectangle;
            _cropNumericDraft[axis] = Integer(value);
            if (_lockCropAspect && axis is 2 or 3)
            {
                var size = Composition.OutputDimensions;
                double aspect = (double)size.Width / size.Height;
                if (axis == 2) _cropNumericDraft[3] = (int)Math.Round(value / aspect);
                else _cropNumericDraft[2] = (int)Math.Round(value * aspect);
            }
        }
        Number(crop, "Crop X (source px)", () => Draft()[0], value => Change(0, value), 0, Session.Width - 16, enabled: Available);
        Number(crop, "Crop Y (source px)", () => Draft()[1], value => Change(1, value), 0, Session.Height - 16, enabled: Available);
        Number(crop, "Crop width", () => Draft()[2], value => Change(2, value), 16, Session.Width, enabled: Available);
        Number(crop, "Crop height", () => Draft()[3], value => Change(3, value), 16, Session.Height, enabled: Available);
        Button(crop, "Apply Crop Values", () =>
        {
            Session.SetCrop(Draft());
            if (Session.Error is null) _cropNumericDraft = null;
        }, Available);
        Button(crop, "Use Entire Source", () => { Session.SetCrop(null); _cropNumericDraft = null; });
        if (!ParallaxCore.SupportsFraming) Note(crop, "Cropping requires Seiza 0.32.0 or newer.");
    }

    private void BuildOutputInspector()
    {
        StackPanel output = Section("Video");
        var framing = Choice(output, "Framing", () => Composition.Framing, value => Session.Edit(c => c.Framing = value),
            ("fill", "Fill frame"), ("fit", "Show whole image (letterbox)"));
        _refreshControls.Add(() => framing.IsEnabled = ParallaxCore.SupportsFraming);
        Note(output, ParallaxCore.SupportsFraming ? "Fill may trim the image. Show whole image keeps its edges visible, with black beside it." :
            "Whole-image framing and cropping become available with Seiza 0.32.0.");
        Toggle(output, "Match image shape", () => Composition.MatchImageShape,
            value => Session.Edit(c => { c.MatchImageShape = value; c.ImageLongSide = Math.Clamp(Math.Max(c.Width, c.Height) / 2 * 2, 16, 3840); }), () => Session.SourceReady);
        Choice(output, "Output format", () => Session.ExportFormat, value => Session.ExportFormat = value,
            (ParallaxExportFormat.Mp4, "MP4 (native Windows)"), (ParallaxExportFormat.Mov, "MOV (FFmpeg)"),
            (ParallaxExportFormat.Matroska, "Matroska / MKV (FFmpeg)"), (ParallaxExportFormat.PngSequence, "PNG sequence"));
        if (Session.ExportFormat is ParallaxExportFormat.Mov or ParallaxExportFormat.Matroska)
        {
            ImagePicker(output, "FFmpeg executable", () => Session.FfmpegPath, path => Session.FfmpegPath = path, ".exe");
            Note(output, "Optional FFmpeg is required for MOV/MKV. Select an installed ffmpeg.exe, or leave unset to search PATH. Seiza does not download or bundle it.");
        }
        if (Session.ExportFormat == ParallaxExportFormat.PngSequence)
        {
            Text(output, "New sequence folder name", () => _sequenceFolderName, value => _sequenceFolderName = value);
            Note(output, "Choose a parent folder when exporting. A new sequence folder is created only after every frame succeeds; existing folders are never replaced.");
        }
        StackPanel sizes = ButtonRow(output);
        foreach ((string name, int width, int height) in new[]
        {
            ("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160),
        })
            Button(sizes, name, () => Session.Edit(c => { c.Width = width; c.Height = height; c.ImageLongSide = width; }));
        Button(output, "Swap Orientation", () => Session.Edit(c => (c.Width, c.Height) = (c.Height, c.Width)), () => !Composition.MatchImageShape);
        Number(output, "Width", () => Composition.OutputDimensions.Width, value => Session.Edit(c => c.Width = Integer(value)), 16, 3840, 2, () => !Composition.MatchImageShape);
        Number(output, "Height", () => Composition.OutputDimensions.Height, value => Session.Edit(c => c.Height = Integer(value)), 16, 3840, 2, () => !Composition.MatchImageShape);
        Note(output, value: () => $"Export frame: {Composition.OutputDimensions.Width} × {Composition.OutputDimensions.Height} pixels. Match title-card artwork to this size.");
        Note(output, value: () => Session.Summary is { } summary ? $"Prepared frame: {summary.Width} × {summary.Height} pixels (preview is scaled down)." : "Prepare Preview to check the framing.");
        Choice(output, "Frames per second", () => Composition.Fps, value => Session.Edit(c => c.Fps = value),
            (24, "24"), (30, "30"), (60, "60"));
        Choice(output, "Codec", () => Session.Codec, value => Session.Codec = value,
            (ParallaxVideoCodec.H264, "H.264"), (ParallaxVideoCodec.Hevc, "HEVC"));
        Choice(output, "Quality", () => Composition.Quality, value => Session.Edit(c => c.Quality = value),
            ("standard", "Standard"), ("high", "High"));
        Number(output, "Star growth limit", () => Composition.GrowthLimit, value => Session.Edit(c => c.GrowthLimit = value), 1, step: 0.1);
        Note(output, "At least 1. Stars swell by the square root of the capped growth: 4 allows twice their original size.");
        Number(output, "Fade stars from growth", () => Composition.FadeFrom, value => Session.Edit(c => c.FadeFrom = value), 0, step: 0.1);
        Note(output, "Stars start fading at this growth and disappear at twice it.");
        Note(output, "SDR · silent\nPreview uses a smaller frame. Export renders every frame at the selected resolution. MP4 uses Windows encoding; HEVC support depends on installed Windows codecs. PNG frames are lossless and ignore the video codec.");
    }

    private void StopText(StackPanel panel, string header, int index,
        Func<ParallaxStop, string?> read, Action<ParallaxStop, string> write)
    {
        Guid id = Composition.Stops[index].Id;
        var box = new TextBox { Header = header, Text = read(Composition.Stops[index]) ?? string.Empty };
        AutomationProperties.SetName(box, header);
        ParallaxComposition? lastEdit = null;
        // Store the draft before a focus change or preview refresh can replace
        // its text. Consecutive keystrokes in this field form one undo step.
        box.TextChanging += (_, _) =>
        {
            if (_refreshing || _closed || Session.SelectedStop != id ||
                index >= Composition.Stops.Count || Composition.Stops[index].Id != id) return;
            var previous = Composition;
            Session.Edit(c => write(c.Stops[index], box.Text), lastEdit);
            if (!ReferenceEquals(previous, Composition)) lastEdit = Composition;
        };
        box.LostFocus += (_, _) => lastEdit = null;
        panel.Children.Add(box);
        _refreshControls.Add(() =>
        {
            string value = read(Composition.Stops[index]) ?? string.Empty;
            if (box.Text != value && !(box.FocusState != FocusState.Unfocused &&
                string.IsNullOrWhiteSpace(box.Text) && value.Length == 0)) box.Text = value;
        });
    }

    private void BuildStopStrip()
    {
        _stripComposition = Composition;
        _stripSelection = Session.SelectedStop;
        StopStrip.Children.Clear();
        if (Composition.Motion != ParallaxMotion.Tour) return;
        for (int index = 0; index < Composition.Stops.Count; index++)
        {
            ParallaxStop stop = Composition.Stops[index];
            var card = new StackPanel { Spacing = 4, Width = 175 };
            card.Children.Add(new TextBlock { Text = $"{index + 1}. {stop.DisplayName}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            card.Children.Add(new TextBlock { Text = $"{(index == 0 ? 0 : stop.Travel):0.0} s travel · {stop.Hold:0.0} s hold", FontSize = 12 });
            card.Children.Add(new TextBlock { Text = $"{stop.RotateDegrees:0}° roll · {stop.Pan * 100:0}% pan", FontSize = 12 });
            card.Children.Add(new TextBlock { Text = $"{stop.SpinDegrees:0}° spin · {stop.Push * 100:0}% push", FontSize = 12 });
            var button = new Button { Content = card, Padding = new Thickness(10),
                BorderThickness = new Thickness(Session.SelectedStop == stop.Id ? 2 : 1) };
            if (Session.SelectedStop == stop.Id)
                button.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            AutomationProperties.SetName(button, $"Tour stop {index + 1}: {stop.DisplayName}");
            button.Click += (_, _) => { Session.SelectStop(stop.Id); InspectorPicker.SelectedIndex = 1; Refresh(); };
            StopStrip.Children.Add(button);
        }
        if (Composition.LoopClosingDuration > 0)
        {
            var closing = new StackPanel { Width = 175, Spacing = 4, Margin = new Thickness(10) };
            closing.Children.Add(new TextBlock { Text = "Return to opening", FontSize = 12 });
            closing.Children.Add(new TextBlock { Text = $"{Composition.LoopClosingDuration:0.0} s travel and hold", FontSize = 12 });
            closing.Children.Add(new TextBlock { Text = "Added automatically for the loop", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            StopStrip.Children.Add(closing);
        }
    }

    private void UpdateImage()
    {
        RenderedImageData? data = Session.ShowsSource ? Session.SourceImage : Session.PreviewImage;
        EmptyPreview.Visibility = data is null ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(data, _shownImage)) return;
        _shownImage = data;
        if (data is null) { PreviewImage.Source = null; _displayBitmap = null; return; }
        if (_displayBitmap is null || _displayBitmap.PixelWidth != data.Width || _displayBitmap.PixelHeight != data.Height)
            _displayBitmap = new WriteableBitmap(data.Width, data.Height);
        WriteableBitmap bitmap = _displayBitmap;
        using (Stream pixels = bitmap.PixelBuffer.AsStream()) pixels.Write(data.Bgra);
        bitmap.Invalidate();
        if (!ReferenceEquals(PreviewImage.Source, bitmap)) PreviewImage.Source = bitmap;
    }

    private Rect SourceRect()
    {
        if (Session.Width <= 0 || Session.Height <= 0) return Rect.Empty;
        // Match Image.Stretch=Uniform using the actual bounded source raster.
        // Its integer resize can differ slightly from the full-resolution
        // aspect ratio. Coordinates still map back to the full source below.
        double imageWidth = Session.SourceImage?.Width ?? Session.Width;
        double imageHeight = Session.SourceImage?.Height ?? Session.Height;
        double scale = Math.Min(PreviewSurface.ActualWidth / imageWidth, PreviewSurface.ActualHeight / imageHeight);
        double width = imageWidth * scale, height = imageHeight * scale;
        return new Rect((PreviewSurface.ActualWidth - width) / 2, (PreviewSurface.ActualHeight - height) / 2, width, height);
    }

    private double[] Center() => Session.ImageCenter;
    private ParallaxStop? SelectedStop() => Session.SelectedIndex is int index && index >= 0 && index < Composition.Stops.Count ? Composition.Stops[index] : null;
    private double[]? CurrentFocus() => Session.PickingDistance ? Composition.Scene.DistanceFocus :
        Composition.Motion == ParallaxMotion.Tour ? SelectedStop()?.Focus : Composition.Focus;
    private double FocusCoordinate(int axis) => (Composition.Motion == ParallaxMotion.Tour ? SelectedStop()?.Focus : Composition.Focus)?[axis] ?? Center()[axis];
    private double DistanceCoordinate(int axis) => Composition.Scene.DistanceFocus?[axis] ?? Center()[axis];

    private void SetFocusCoordinate(int axis, double value)
    {
        double[] point = (Composition.Motion == ParallaxMotion.Tour ? SelectedStop()?.Focus : Composition.Focus)?.ToArray() ?? Center();
        point[axis] = value;
        if (Composition.Motion == ParallaxMotion.Tour) Session.EditStop(stop => { stop.Focus = point; stop.Name = "Custom Point"; stop.DepthParsecs = null; });
        else Session.Edit(c => { c.Focus = point; c.FocusName = "Custom Point"; });
    }

    private void SetDistanceCoordinate(int axis, double value) => Session.Edit(c =>
    {
        double[] point = c.Scene.DistanceFocus?.ToArray() ?? Center();
        point[axis] = value; c.Scene.DistanceFocus = point; c.DistanceName = "Custom point";
    });

    private void BeginPickDestination()
    {
        Session.PickingCrop = false;
        Session.PickingDistance = false; Session.ShowsSource = true; Session.PickingDestination = true;
    }

    private void UpdateFocusMarker()
    {
        UpdateCropOutline();
        double[]? point = CurrentFocus();
        bool show = Session.ShowsSource && point is { Length: 2 } && point.All(double.IsFinite);
        FocusMarker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        Rect rect = SourceRect();
        Canvas.SetLeft(FocusMarker, rect.X + (point![0] + 0.5) / Math.Max(1, Session.Width) * rect.Width - 15);
        Canvas.SetTop(FocusMarker, rect.Y + (point[1] + 0.5) / Math.Max(1, Session.Height) * rect.Height - 20);
    }

    private void PreviewSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Session.PickingCrop && !Session.IsBusy && Session.ShowsSource && SourceRect().Contains(e.GetCurrentPoint(PreviewSurface).Position))
        {
            _cropAnchor = SourcePoint(e.GetCurrentPoint(PreviewSurface).Position);
            PreviewSurface.CapturePointer(e.Pointer); e.Handled = true; return;
        }
        if (Session.IsBusy || !Session.ShowsSource || (!Session.PickingDestination && !Session.PickingDistance)) return;
        Point position = e.GetCurrentPoint(PreviewSurface).Position;
        Rect rect = SourceRect();
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(position)) return;
        Session.Pick(Math.Clamp((position.X - rect.X) / rect.Width * Session.Width - 0.5, 0, Math.Max(0, Session.Width - 1)),
            Math.Clamp((position.Y - rect.Y) / rect.Height * Session.Height - 0.5, 0, Math.Max(0, Session.Height - 1)));
        e.Handled = true;
        Refresh();
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFocusMarker();
    private double[] SourcePoint(Point position)
    {
        Rect rect = SourceRect();
        return [Math.Clamp((position.X - rect.X) / Math.Max(1, rect.Width) * Session.Width, 0, Session.Width),
            Math.Clamp((position.Y - rect.Y) / Math.Max(1, rect.Height) * Session.Height, 0, Session.Height)];
    }
    private void PreviewSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropAnchor is null || !Session.PickingCrop || Session.IsBusy) return;
        double[] end = SourcePoint(e.GetCurrentPoint(PreviewSurface).Position);
        var size = Composition.OutputDimensions;
        _cropDraft = ParallaxGeometry.CropFromDrag(_cropAnchor[0], _cropAnchor[1], end[0], end[1],
            Session.Width, Session.Height, _lockCropAspect ? (double)size.Width / size.Height : null);
        UpdateCropOutline(); e.Handled = true;
    }
    private void PreviewSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_cropAnchor is null) return;
        PreviewSurface_PointerMoved(sender, e);
        int[]? crop = _cropDraft;
        _cropAnchor = null; _cropDraft = null; _cropNumericDraft = null;
        if (crop is not null) Session.SetCrop(crop);
        PreviewSurface.ReleasePointerCaptures(); e.Handled = true; Refresh();
    }
    private void PreviewSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _cropAnchor = null; _cropDraft = null; Session.PickingCrop = false;
        UpdateCropOutline();
    }
    private void UpdateCropOutline()
    {
        int[]? crop = _cropDraft ?? Composition.Scene.Crop;
        bool show = Session.ShowsSource && crop is { Length: 4 } && Session.Width > 0 && Session.Height > 0;
        CropOutline.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        Rect rect = SourceRect();
        Canvas.SetLeft(CropOutline, rect.X + crop![0] * rect.Width / Session.Width);
        Canvas.SetTop(CropOutline, rect.Y + crop[1] * rect.Height / Session.Height);
        CropOutline.Width = crop[2] * rect.Width / Session.Width;
        CropOutline.Height = crop[3] * rect.Height / Session.Height;
    }
    private void InspectorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || InspectorPanel is null) return;
        if (!CommitFocusedEdit())
        {
            _refreshing = true;
            try { InspectorPicker.SelectedIndex = _inspectorIndex; }
            finally { _refreshing = false; }
            return;
        }
        Refresh();
        InspectorScroller?.ChangeView(null, 0, null, true);
    }
    private void ViewPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || Session is null) return;
        Session.ShowsSource = ViewPicker.SelectedIndex == 0;
        Refresh();
    }
    private void Timeline_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_refreshing || Session is null) return;
        Session.StopPlayback(); Session.Playhead = e.NewValue; Session.RequestFrame();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Session.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Session.Redo();
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (CommitFocusedEdit()) await RunAsync(Session.PreparePreviewAsync);
    }
    private void Play_Click(object sender, RoutedEventArgs e) => Session.TogglePlayback();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Session.Cancel();
    private void AddPoint_Click(object sender, RoutedEventArgs e) { Session.AddStop(); InspectorPicker.SelectedIndex = 1; Refresh(); }
    private void AddWhole_Click(object sender, RoutedEventArgs e) { Session.AddStop(true); InspectorPicker.SelectedIndex = 1; Refresh(); }
    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) { if (!_refreshing) Session.Error = null; }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) Session.Error = error.Message; }
        if (!_closed) Refresh();
    }

    private bool CommitFocusedEdit()
    {
        if (_closed) return false;
        if (ContentRoot.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not TextBox input) return true;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(input); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is NumberBox number) return CommitNumber(number, input);
        }
        return !_textCommitters.TryGetValue(input, out Func<bool>? commit) || commit();
    }

    private static bool IsDescendant(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (ReferenceEquals(parent, ancestor)) return true;
        return false;
    }

    private bool CommitNumber(NumberBox number, TextBox input)
    {
        if (_refreshing || _closed || Session.IsBusy) return true;
        // Use the same locale-aware parser as NumberBox itself, then its public
        // Text property triggers native validation and the existing ValueChanged
        // history path. Do not silently run a command with the previous value.
        double? value = (number.NumberFormatter as INumberParser)?.ParseDouble(input.Text.Trim());
        if (value is not double parsed || !double.IsFinite(parsed))
        {
            Session.Error = $"Enter a finite number for {number.Header?.ToString()?.ToLowerInvariant() ?? "this field"}.";
            // Do not rebuild an inspector that is in the middle of attempting
            // a tab change; keep the invalid editor available for correction.
            ErrorBar.Message = Session.Error;
            ErrorBar.IsOpen = true;
            return false;
        }
        number.Text = input.Text;
        return double.IsFinite(number.Value);
    }

    private async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.List };
        foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync();
        return _closed ? null : file?.Path;
    }

    private async Task<string?> PickSaveAsync(string name, string description, string extension,
        params (string Description, string Extension)[] more)
    {
        // The desktop SDK picker returns a path only. The legacy StorageFile
        // picker may create a file before rendering, undermining atomic export.
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
            SuggestedFileName = name,
            DefaultFileExtension = extension,
            ShowOverwritePrompt = true,
        };
        picker.FileTypeChoices.Add(description, [extension]);
        foreach (var choice in more) picker.FileTypeChoices.Add(choice.Description, [choice.Extension]);
        var file = await picker.PickSaveFileAsync();
        return _closed ? null : file?.Path;
    }

    private async Task ChooseLayerAsync(bool starless)
    {
        if (await PickFileAsync(".png", ".tif", ".tiff") is { } path) await Session.ImportLayerAsync(path, starless);
    }

    private async Task ChooseInputAsync(ParallaxInputFile kind)
    {
        string[] extensions = kind switch
        {
            ParallaxInputFile.Separator => [".exe"],
            ParallaxInputFile.DustMap => [".png", ".tif", ".tiff", ".jpg", ".jpeg", ".fits", ".fit", ".fts", ".xisf"],
            _ => [".bin"],
        };
        if (await PickFileAsync(extensions) is { } path)
            await Session.ChooseInputAsync(kind, path);
    }

    private async Task ExportDustMapAsync()
    {
        string name = Path.GetFileNameWithoutExtension(Session.SourceName) + "-dust";
        if (await PickSaveAsync(name, "16-bit PNG", ".png", ("16-bit TIFF", ".tif"), ("32-bit FITS", ".fits"), ("32-bit XISF", ".xisf")) is { } path)
            await Session.ExportDustMapAsync(path, Session.ExportCountedDustMap);
    }

    private async Task EditDustMapAsync()
    {
        if (ContentRoot.XamlRoot is not { } root || Session.SourceImage is not { } source) return;
        ParallaxDustEditResult? map = await Session.ReadDustMapForEditingAsync();
        if (_closed || map is null) return;
        ParallaxDustEditResult? edited = await ParallaxDustEditorDialog.EditAsync(root, source, map.Opacity, map.Width, map.Height);
        if (!_closed && edited is not null) await Session.ApplyPaintedDustMapAsync(edited.Opacity, edited.Width, edited.Height);
    }

    private async Task AddDepthPlaneAsync()
    {
        if (await PickFileAsync(".png", ".tif", ".tiff", ".jpg", ".jpeg", ".fits", ".fit", ".fts", ".xisf") is not { } path) return;
        Session.Edit(c => (c.Scene.Planes ??= []).Add(new ParallaxDepthPlane
        {
            Mask = path, DistanceParsecs = c.Scene.DistanceParsecs ?? Session.Summary?.BackgroundDistanceParsecs ?? 400,
        }));
    }

    private async Task AddObjectDistanceAsync()
    {
        if (_editDialog is not null || ContentRoot.XamlRoot is not { } root) return;
        var name = new TextBox { Header = "Catalog object name", PlaceholderText = "For example, Sh2-170" };
        var distance = new NumberBox { Header = "Distance (pc)", Value = 400, Minimum = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Orange) };
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(name); panel.Children.Add(distance); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = root, Title = "Object distance", Content = panel,
            PrimaryButtonText = "Add or Replace", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // NumberBox's own focus-loss parse can lag the dialog button.
            if (FocusManager.GetFocusedElement(root) is TextBox input && IsDescendant(input, distance) && !CommitNumber(distance, input))
            { args.Cancel = true; return; }
            if (string.IsNullOrWhiteSpace(name.Text) || !double.IsFinite(distance.Value) || distance.Value <= 0)
            { error.Text = "Enter a catalog object name and a positive, finite distance."; args.Cancel = true; }
        };
        _editDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_closed)
                Session.SetDistanceOverride(name.Text.Trim(), distance.Value);
        }
        finally { _editDialog = null; }
    }

    private async Task ChooseObjectFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (!_closed && folder is not null) await Session.ChooseInputAsync(ParallaxInputFile.Objects, folder.Path);
    }

    private async void LoadTour_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (await PickFileAsync(".json") is { } path) await Session.LoadTourAsync(path);
    });

    private async void SaveTour_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitFocusedEdit()) return;
        await RunAsync(async () =>
        {
            if (await PickSaveAsync("Tour", "Tour itinerary", ".json") is { } path) await Session.SaveTourAsync(path);
        });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitFocusedEdit()) return;
        await RunAsync(async () =>
        {
            string name = Path.GetFileNameWithoutExtension(Session.SourceName) + "-parallax";
            if (Session.ExportFormat == ParallaxExportFormat.PngSequence)
            {
                string folderName = _sequenceFolderName.Trim();
                if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or ".." ||
                    folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName.EndsWith('.') || folderName.EndsWith(' '))
                    throw new InvalidDataException("Choose a valid new sequence folder name, without a path.");
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
                StorageFolder? parent = await picker.PickSingleFolderAsync();
                if (!_closed && parent is not null) await Session.ExportVideoAsync(Path.Combine(parent.Path, folderName));
            }
            else
            {
                (string description, string extension) = Session.ExportFormat switch
                {
                    ParallaxExportFormat.Mov => ("MOV video", ".mov"),
                    ParallaxExportFormat.Matroska => ("Matroska video", ".mkv"),
                    _ => ("MP4 video", ".mp4"),
                };
                if (await PickSaveAsync(name, description, extension) is { } path) await Session.ExportVideoAsync(path);
            }
        });
    }

    private async void ShowVideo_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Session.ExportedPath is not { } path) return;
        if (Directory.Exists(path))
        {
            await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path));
            return;
        }
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        StorageFolder folder = await file.GetParentAsync();
        var options = new FolderLauncherOptions();
        options.ItemsToSelect.Add(file);
        await Launcher.LaunchFolderAsync(folder, options);
    });

    private void ActivityTimer_Tick(object? sender, object e)
    {
        if (!_closed) UpdateActivity();
    }

    private void UpdateActivity()
    {
        bool busy = Session.IsBusy || Session.IsReconfiguring;
        DateTimeOffset now = DateTimeOffset.Now;
        ActivityDetailText.Text = busy ? Session.Activity.Detail(now) +
            (Session.Activity.WaitingMessage(now) is { } waiting ? "\n" + waiting : string.Empty) : string.Empty;
    }

    private async void Activity_Click(object sender, RoutedEventArgs e)
    {
        if (_activityDialog is not null) return;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 };
        void Update(object? _, PropertyChangedEventArgs __) => text.Text = Session.Status + "\n\n" +
            string.Join("\n\n", Session.Activity.Entries.Select(entry =>
                $"{(int)entry.Elapsed.TotalMinutes}:{entry.Elapsed.Seconds:00}  {(entry.Warning ? "Warning: " : string.Empty)}{entry.Message}"));
        Update(null, new PropertyChangedEventArgs(null));
        Session.PropertyChanged += Update;
        try
        {
            _activityDialog = new ContentDialog
            {
                XamlRoot = ContentRoot.XamlRoot, Title = "Preparation activity", CloseButtonText = "Close",
                Content = new ScrollViewer { Content = text, MaxHeight = 360, MinWidth = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            };
            await _activityDialog.ShowAsync();
        }
        finally { Session.PropertyChanged -= Update; _activityDialog = null; }
    }

    private static int Integer(double value) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue
        ? (int)Math.Round(value) : 0;
}
