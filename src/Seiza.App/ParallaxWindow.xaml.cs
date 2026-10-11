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
    private readonly ParallaxWindowSizing _windowSizing;

    internal ParallaxSession Session { get; }
    internal event EventHandler? OpenCatalogSettingsRequested;

    internal ParallaxWindow(ParallaxSession session)
    {
        Session = session;
        InitializeComponent();
        _windowSizing = new(WindowHandle, 980, 680);
        Title = $"Parallax Video — {session.SourceName}";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        SourceNameText.Text = session.SourceName;
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
            PickingText.Text = Session.PickingDistance ? "Click the scene’s distance reference" :
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
            StatusText.Text = Composition.ValidationMessage ??
                (active && !Session.Activity.CancellationRequested ? Session.Activity.NetworkPhase : null) ?? Session.Status;
            CancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            CancelButton.IsEnabled = Session.SourceReady && !Session.Activity.CancellationRequested;
            ShowVideoButton.Visibility = Session.ExportedPath is not null ? Visibility.Visible : Visibility.Collapsed;
            UpdateActivity();

            string structure = $"{InspectorPicker.SelectedIndex}:{Composition.Motion}:{Session.SelectedStop}:" +
                $"{Session.SelectedIndex}:" + string.Join(',', Composition.Labels.Select(label => label.Id));
            if (_inspectorStructure != structure)
            {
                _inspectorStructure = structure;
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

    private void Number(StackPanel panel, string title, Func<double> get, Action<double> set,
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

    private void Choice<T>(StackPanel panel, string title, Func<T> get, Action<T> set,
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
    }

    private void Slider(StackPanel panel, string title, Func<double> get, Action<double> set,
        double minimum = 0, double maximum = 1)
    {
        var label = new TextBlock { FontSize = 12 };
        panel.Children.Add(label);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = get(), StepFrequency = 0.01 };
        AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, args) =>
        {
            if (!_refreshing && !_closed) { set(args.NewValue); Refresh(); }
        };
        panel.Children.Add(slider);
        _refreshControls.Add(() => { slider.Value = get(); label.Text = $"{title}: {get():0.##}"; });
    }

    private void OptionalDistance(StackPanel panel, string title, string field, Func<double?> get, Action<double?> set,
        double fallback)
    {
        Toggle(panel, title, () => get() is not null, value => set(value ? fallback : null));
        Number(panel, field, () => get() ?? fallback, value => set(value), 0, enabled: () => get() is not null);
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
        Note(source, value: () => Session.SourceReady ? $"{Session.Width:N0} × {Session.Height:N0} pixels" : "Preparing full-resolution snapshot…");
        Note(source, value: () => Session.HasWcs ? "Sky coordinates available" : "The image will be plate solved when preparing or generating a tour.");
        Note(source, "Uses the current image adjustments. Imported layers must use the same stretch and alignment.");

        StackPanel separation = Section("Star separation");
        Toggle(separation, "Use StarXTerminator", () => Session.AutomaticSeparation, value => Session.AutomaticSeparation = value);
        var automatic = new StackPanel { Spacing = 8 };
        separation.Children.Add(automatic);
        InputPicker(automatic, ParallaxInputFile.Separator, "RC-Astro CLI (rc-astro.exe)");
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
            () => Session.SourceReady && !Session.CatalogsRunning);
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
            Slider(fly, "Dolly", () => Composition.Dolly, value => Session.Edit(c => c.Dolly = value), 0, 0.95);
            Slider(fly, "Pan", () => Composition.Pan, value => Session.Edit(c => c.Pan = value));
            Number(fly, "Start roll (°)", () => Composition.RotationStart, value => Session.Edit(c => c.RotationStart = value));
            Number(fly, "End roll (°)", () => Composition.Rotation, value => Session.Edit(c => c.Rotation = value));
            RollExplanation(fly);
            Number(fly, "Opening zoom", () => Composition.Zoom, value => Session.Edit(c => c.Zoom = value), 1, step: 0.1);
            Number(fly, "End lens multiplier", () => Composition.ZoomEnd, value => Session.Edit(c => c.ZoomEnd = value), 1, step: 0.1);
            Number(fly, "Sideways swing", () => Composition.Truck, value => Session.Edit(c => c.Truck = value), step: 0.01);
            Number(fly, "Swing direction (°)", () => Composition.TruckAngleDegrees,
                value => Session.Edit(c => c.TruckAngleDegrees = value));
            Choice(fly, "Easing", () => Composition.Easing, value => Session.Edit(c => c.Easing = value),
                ("inOut", "Smooth"), ("linear", "Linear"));
        }
        else if (Session.SelectedIndex is int index)
        {
            StackPanel stop = Section($"Stop {index + 1}");
            Text(stop, "Name", () => SelectedStop()?.Name ?? string.Empty, value => Session.EditStop(s => s.Name = value));
            Text(stop, "On-screen title", () => SelectedStop()?.Title ?? string.Empty,
                value => Session.EditStop(s => s.Title = string.IsNullOrWhiteSpace(value) ? null : value));
            Note(stop, "Shown during this stop when Show stop titles is enabled. Leave blank for no title.");
            Button(stop, "Pick Destination", BeginPickDestination);
            Button(stop, "Use Whole Image", () => Session.EditStop(s =>
            { s.Focus = null; s.Name = "Whole Image"; s.Dolly = 0; s.Zoom = 1; s.Pan = 0; }));
            DestinationCoordinates(stop);
            Number(stop, "Travel to stop (s)", () => SelectedStop()?.Travel ?? 0,
                value => Session.EditStop(s => s.Travel = value), 0, 600, 0.1, () => Session.SelectedIndex > 0);
            Number(stop, "Hold (s)", () => SelectedStop()?.Hold ?? 0,
                value => Session.EditStop(s => s.Hold = value), 0, 600, 0.1);
            Note(stop, "Set hold to 0 to pass straight through. Glide, spin and push can keep a held stop moving.");
            Slider(stop, "Dolly", () => SelectedStop()?.Dolly ?? 0, value => Session.EditStop(s => s.Dolly = value), 0, 0.95);
            Number(stop, "Zoom", () => SelectedStop()?.Zoom ?? 1, value => Session.EditStop(s => s.Zoom = value), 0, step: 0.1);
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
            Number(tour, "Overall zoom", () => Composition.Zoom, value => Session.Edit(c => c.Zoom = value), 1, step: 0.1);
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
    }

    private void BuildOutputInspector()
    {
        StackPanel output = Section("Video");
        StackPanel sizes = ButtonRow(output);
        foreach ((string name, int width, int height) in new[]
        {
            ("720p", 1280, 720), ("1080p", 1920, 1080), ("1440p", 2560, 1440), ("4K", 3840, 2160),
        })
            Button(sizes, name, () => Session.Edit(c => { c.Width = width; c.Height = height; }));
        Button(output, "Swap Orientation", () => Session.Edit(c => (c.Width, c.Height) = (c.Height, c.Width)));
        Number(output, "Width", () => Composition.Width, value => Session.Edit(c => c.Width = Integer(value)), 16, 3840, 2);
        Number(output, "Height", () => Composition.Height, value => Session.Edit(c => c.Height = Integer(value)), 16, 3840, 2);
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
        Note(output, "MP4 · SDR · silent\nPreview uses a smaller frame. Export renders every frame at the selected resolution. Hardware encoding is allowed when available; HEVC support depends on installed Windows codecs.");
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

    private double[] Center() => [Math.Max(0, Session.Width - 1) / 2.0, Math.Max(0, Session.Height - 1) / 2.0];
    private ParallaxStop? SelectedStop() => Session.SelectedIndex is int index && index >= 0 && index < Composition.Stops.Count ? Composition.Stops[index] : null;
    private double[]? CurrentFocus() => Session.PickingDistance ? Composition.Scene.DistanceFocus :
        Composition.Motion == ParallaxMotion.Tour ? SelectedStop()?.Focus : Composition.Focus;
    private double FocusCoordinate(int axis) => (Composition.Motion == ParallaxMotion.Tour ? SelectedStop()?.Focus : Composition.Focus)?[axis] ?? Center()[axis];
    private double DistanceCoordinate(int axis) => Composition.Scene.DistanceFocus?[axis] ?? Center()[axis];

    private void SetFocusCoordinate(int axis, double value)
    {
        double[] point = (Composition.Motion == ParallaxMotion.Tour ? SelectedStop()?.Focus : Composition.Focus)?.ToArray() ?? Center();
        point[axis] = value;
        if (Composition.Motion == ParallaxMotion.Tour) Session.EditStop(stop => { stop.Focus = point; stop.Name = "Custom Point"; });
        else Session.Edit(c => { c.Focus = point; c.FocusName = "Custom Point"; });
    }

    private void SetDistanceCoordinate(int axis, double value) => Session.Edit(c =>
    {
        double[] point = c.Scene.DistanceFocus?.ToArray() ?? Center();
        point[axis] = value; c.Scene.DistanceFocus = point; c.DistanceName = "Custom point";
    });

    private void BeginPickDestination()
    {
        Session.PickingDistance = false; Session.ShowsSource = true; Session.PickingDestination = true;
    }

    private void UpdateFocusMarker()
    {
        double[]? point = CurrentFocus();
        bool show = Session.ShowsSource && point is { Length: 2 } && point.All(double.IsFinite);
        FocusMarker.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        Rect rect = SourceRect();
        Canvas.SetLeft(FocusMarker, rect.X + point![0] / Math.Max(1, Session.Width) * rect.Width - 15);
        Canvas.SetTop(FocusMarker, rect.Y + point[1] / Math.Max(1, Session.Height) * rect.Height - 20);
    }

    private void PreviewSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Session.IsBusy || !Session.ShowsSource || (!Session.PickingDestination && !Session.PickingDistance)) return;
        Point position = e.GetCurrentPoint(PreviewSurface).Position;
        Rect rect = SourceRect();
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(position)) return;
        Session.Pick(Math.Clamp((position.X - rect.X) / rect.Width * Session.Width, 0, Math.Max(0, Session.Width - 1)),
            Math.Clamp((position.Y - rect.Y) / rect.Height * Session.Height, 0, Math.Max(0, Session.Height - 1)));
        e.Handled = true;
        Refresh();
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFocusMarker();
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

    private async Task<string?> PickSaveAsync(string name, string description, string extension)
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
        var file = await picker.PickSaveFileAsync();
        return _closed ? null : file?.Path;
    }

    private async Task ChooseLayerAsync(bool starless)
    {
        if (await PickFileAsync(".png", ".tif", ".tiff") is { } path) await Session.ImportLayerAsync(path, starless);
    }

    private async Task ChooseInputAsync(ParallaxInputFile kind)
    {
        if (await PickFileAsync(kind == ParallaxInputFile.Separator ? ".exe" : ".bin") is { } path)
            await Session.ChooseInputAsync(kind, path);
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
            if (await PickSaveAsync(name, "MP4 video", ".mp4") is { } path) await Session.ExportVideoAsync(path);
        });
    }

    private async void ShowVideo_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Session.ExportedPath is not { } path) return;
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
