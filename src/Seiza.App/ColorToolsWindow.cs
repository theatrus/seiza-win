using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics;

namespace Seiza.App;

/// <summary>Native controls for the published display and linear color operations.</summary>
public sealed class ColorToolsWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly ComboBox _tool = new() { Header = "Color workflow", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _inputs = new() { Spacing = 14 };
    private readonly ContentControl _inputHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _bodyScroll = new() { HorizontalScrollMode = ScrollMode.Disabled };
    private readonly Expander _processingSettings = new() { Header = "Processing settings", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Expander _fitSettings = new() { Header = "Fit settings / saved plans", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 560 };
    private readonly TextBlock _previewHint = Note("Choose your source images, then build a preview.");
    private readonly Grid _workspace = new() { ColumnSpacing = 20, RowSpacing = 12 };
    private readonly TextBlock _starNote = Note("");
    private readonly Expander _balanceSettings = new() { Header = "Balance settings", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Expander _reportDetails = new() { Header = "Processing report / continuum plan (JSON)", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _narrowband = new() { Spacing = 12 };
    private readonly StackPanel _narrowbandProcessing = new() { Spacing = 12 };
    private readonly StackPanel _continuum = new() { Spacing = 12 };
    private readonly StackPanel _stars = new() { Spacing = 12 };
    private readonly TextBox _ha = PathField("Aligned H-alpha mono stack");
    private readonly TextBox _oiii = PathField("Aligned OIII mono stack");
    private readonly TextBox _sii = PathField("Aligned SII mono stack (SHO)");
    private readonly ComboBox _palette = new() { Header = "Palette" };
    private readonly CheckBox _displayChannels = new() { Content = "The narrowband channels are already stretched display values in [0, 1]" };
    private readonly CheckBox _balance = new() { Content = "Balance OIII / SII midtones against H-alpha", IsChecked = true };
    private readonly NumberBox _shadow = Number("Shadow protection (0–1)", 1, 0, 1, .1);
    private readonly NumberBox _oiiiBoost = Number("OIII boost", 1, .01, 100, .1);
    private readonly NumberBox _siiBoost = Number("SII boost", 1, .01, 100, .1);
    private readonly CheckBox _excludeStars = new() { Content = "Exclude detected stars from balance statistics (5 px radius)" };
    private readonly TextBox _structure = PathField("Optional aligned display RGB broadband structure");
    private readonly NumberBox _structureAmount = Number("Broadband structure mix (0 keeps narrowband)", 0, 0, 1, .1);
    private readonly TextBox _line = PathField("Narrowband stack (mono, linear)");
    private readonly NumberBox _lineSaturation = Number("Narrowband physical saturation (0 = unknown)", 0, 0, double.MaxValue, 1000);
    private readonly TextBox _lineMask = PathField("Optional producer clipping mask: mono 0 / 1");
    private readonly ReferenceRow[] _references = [new(1), new(2), new(3)];
    private readonly ComboBox _fitMode = new() { Header = "Continuum coefficients" };
    private readonly CheckBox _psf = new() { Content = "Broaden copies to a common Gaussian PSF before fitting / subtraction" };
    private readonly TextBox _original = PathField("Original RGB (linear)");
    private readonly TextBox _starless = PathField("Starless RGB (same linear scale)");
    private readonly TextBox _preparedStars = PathField("Stars-only RGB (display)");
    private readonly TextBox _nebula = PathField("Aligned RGB nebula image for restoration");
    private readonly CheckBox _nebulaDisplay = new() { Content = "The nebula is already stretched display data in [0, 1]", IsChecked = true };
    private readonly NumberBox _starAmount = Number("Star stretch (0–8)", 5, 0, 8, .25);
    private readonly NumberBox _starReduction = Number("Star reduction (0–1)", 0, 0, 1, .1);
    private readonly NumberBox _median = Number("Linked Auto-MTF target median for linear display inputs", .2, .01, .99, .01);
    private readonly NumberBox _green = Number("Excess-green reduction (0–1)", 0, 0, 1, .1);
    private readonly CheckBox _aligned = new() { Content = "All inputs share one registered pixel grid, crop and orientation" };
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false, Message = "Choose prepared FITS / XISF images. Sources are never modified." };
    private readonly TextBox _report = new() { IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 320 };
    private readonly Button _analyze = new() { Content = "Analyze stars and validate fit" };
    private readonly Button _loadPlan = new() { Content = "Load continuum plan / report…" };
    private readonly Button _save = new() { Content = "Process and save image…" };
    private readonly Button _export = new() { Content = "Save image…", IsEnabled = false };
    private readonly Button _saveReport = new() { Content = "Save report / plan…", IsEnabled = false };
    private readonly Button _open = new() { Content = "Open result…", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel operation", IsEnabled = false, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _progress = new() { Width = 24, Height = 24, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _operation;
    private JsonElement? _plan;
    private string? _planPath;
    private string? _planInputSnapshot;
    private ColorToolsResult? _result;
    private ColorToolsRequest? _completedRequest;
    private bool _busy, _closed, _previewCurrent, _initializing = true;
    private string? _savedPath;
    private readonly string _previewDirectory = Path.Combine(Path.GetTempPath(), "Seiza-color-preview-" + Guid.NewGuid().ToString("N"));

    public ColorToolsWindow()
    {
        Title = "Color tools — Seiza";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        RectInt32 workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(1280, workArea.Width), Math.Min(1000, workArea.Height)));
        foreach (string title in new[] { "Narrowband HOO / SHO display", "Linear continuum subtraction",
            "Extract and stretch RGB stars", "Restore RGB stars over a nebula", "Restore prepared RGB stars" }) _tool.Items.Add(title);
        _palette.Items.Add("HOO — H-alpha / OIII / OIII"); _palette.Items.Add("SHO — SII / H-alpha / OIII"); _palette.SelectedIndex = 0;
        _fitMode.Items.Add("Automatic stellar fit — analyze and review first");
        _fitMode.Items.Add("Manual coefficients — no automatic fit / validation");
        _fitMode.Items.Add("Use reviewed / saved exact-source plan"); _fitMode.SelectedIndex = 0;
        AddPaths(_narrowband, _ha, _oiii, _sii); _narrowband.Children.Add(_palette);
        var narrowbandProcessing = _narrowbandProcessing;
        narrowbandProcessing.Children.Add(_displayChannels); narrowbandProcessing.Children.Add(_balance);
        var balanceSettings = new StackPanel { Spacing = 12 };
        balanceSettings.Children.Add(Row(_shadow, _oiiiBoost, _siiBoost)); balanceSettings.Children.Add(_excludeStars);
        _balanceSettings.Content = balanceSettings; narrowbandProcessing.Children.Add(_balanceSettings);
        var structureSettings = new StackPanel { Spacing = 12 };
        structureSettings.Children.Add(Picker(_structure)); structureSettings.Children.Add(_structureAmount);
        narrowbandProcessing.Children.Add(new Expander { Header = "Broadband structure (optional)", Content = structureSettings, HorizontalAlignment = HorizontalAlignment.Stretch });
        narrowbandProcessing.Children.Add(Note("Uses a linked display stretch and midtone balance by default. The result is a display image, not photometric line flux."));
        _continuum.Children.Add(Picker(_line));
        var fitSettings = new StackPanel { Spacing = 12 };
        fitSettings.Children.Add(_fitMode); fitSettings.Children.Add(_psf);
        fitSettings.Children.Add(_lineSaturation); fitSettings.Children.Add(Picker(_lineMask));
        var additionalReferences = new StackPanel { Spacing = 12 };
        for (int index = 0; index < _references.Length; index++)
        {
            ReferenceRow reference = _references[index];
            var row = new StackPanel { Spacing = 8 };
            if (index == 0) _continuum.Children.Add(Picker(reference.Path)); else row.Children.Add(Picker(reference.Path));
            row.Children.Add(Row(reference.Saturation, reference.Coefficient));
            row.Children.Add(new Expander { Header = "Producer clipping mask (optional)", Content = Picker(reference.Mask), HorizontalAlignment = HorizontalAlignment.Stretch });
            if (index == 0) fitSettings.Children.Add(row); else additionalReferences.Children.Add(row);
        }
        fitSettings.Children.Add(new Expander { Header = "Additional continuum references (optional)", Content = additionalReferences, HorizontalAlignment = HorizontalAlignment.Stretch });
        fitSettings.Children.Add(Note("Automatic fitting needs a producer saturation ceiling or clipping mask for each source. No ceiling is guessed from image maxima. Manual coefficients and saved exact-source plans are optional alternatives."));
        fitSettings.Children.Add(_loadPlan); fitSettings.Children.Add(_analyze);
        _fitSettings.Content = fitSettings; _continuum.Children.Add(_fitSettings);
        AddPaths(_stars, _original, _starless, _preparedStars, _nebula);
        _stars.Children.Add(Row(_starAmount, _starReduction));
        _inputs.Children.Add(_narrowband); _inputs.Children.Add(_continuum); _inputs.Children.Add(_stars);
        var displaySettings = new StackPanel { Spacing = 12 }; displaySettings.Children.Add(narrowbandProcessing);
        displaySettings.Children.Add(_nebulaDisplay); displaySettings.Children.Add(_starNote); displaySettings.Children.Add(_median); displaySettings.Children.Add(_green);
        _processingSettings.Content = displaySettings;
        _inputs.Children.Add(_aligned); _inputs.Children.Add(_processingSettings);
        var reportBody = new StackPanel { Spacing = 12 }; reportBody.Children.Add(_report); reportBody.Children.Add(_saveReport);
        _reportDetails.Content = reportBody;
        var body = new StackPanel { Spacing = 16, Padding = new Thickness(24, 8, 4, 16) };
        _inputHost.Content = _inputs;
        body.Children.Add(_inputHost);
        var header = new StackPanel { Spacing = 12, Padding = new Thickness(24, 16, 24, 12) };
        header.Children.Add(new TextBlock { Text = "Color tools", FontSize = 24 }); header.Children.Add(_tool);
        foreach (Button button in new[] { _analyze, _loadPlan, _save, _export, _open, _saveReport, _cancel })
        {
            button.Content = new TextBlock { Text = (string)button.Content, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var footer = new StackPanel { Spacing = 8, Padding = new Thickness(24, 12, 24, 16) };
        footer.Children.Add(_status); footer.Children.Add(_progress);
        footer.Children.Add(Row(_save, _cancel)); footer.Children.Add(Row(_export, _open));
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _bodyScroll.Content = body;
        var previewPanel = new StackPanel { Spacing = 12, Padding = new Thickness(4, 8, 24, 16) };
        previewPanel.Children.Add(new TextBlock { Text = "Preview", FontSize = 20 }); previewPanel.Children.Add(_previewHint); previewPanel.Children.Add(_preview); previewPanel.Children.Add(_reportDetails);
        var previewScroll = new ScrollViewer { Content = previewPanel, HorizontalScrollMode = ScrollMode.Disabled };
        _workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) }); _workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        Grid.SetColumn(previewScroll, 1); _workspace.Children.Add(_bodyScroll); _workspace.Children.Add(previewScroll);
        layout.SizeChanged += (_, _) => {
            bool narrow = layout.ActualWidth < 850;
            _workspace.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(420);
            _workspace.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            _workspace.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(previewScroll, narrow ? 0 : 1); Grid.SetRow(previewScroll, narrow ? 1 : 0);
        };
        _workspace.SizeChanged += (_, _) => _preview.MaxHeight = Math.Max(80, _workspace.ActualHeight / (layout.ActualWidth < 850 ? 2 : 1) - 150);
        Grid.SetRow(_workspace, 1); Grid.SetRow(footer, 2); layout.Children.Add(header); layout.Children.Add(_workspace); layout.Children.Add(footer);
        Content = layout;
        layout.Loaded += (_, _) => UpdateMode();
        _tool.SelectionChanged += (_, _) =>
        {
            InvalidatePlan(); ClearResult();
            _aligned.IsChecked = false;
            _bodyScroll.ChangeView(null, 0, null, disableAnimation: true);
            Show("Choose prepared FITS / XISF inputs for this workflow. Sources are never modified.", InfoBarSeverity.Informational);
            UpdateMode();
        };
        _palette.SelectionChanged += (_, _) => UpdateMode();
        _displayChannels.Checked += (_, _) => UpdateMode(); _displayChannels.Unchecked += (_, _) => UpdateMode();
        _nebulaDisplay.Checked += (_, _) => UpdateMode(); _nebulaDisplay.Unchecked += (_, _) => UpdateMode();
        _balance.Checked += (_, _) => UpdateMode(); _balance.Unchecked += (_, _) => UpdateMode();
        _fitMode.SelectionChanged += (_, _) => { InvalidatePlan(); UpdateMode(); };
        foreach (TextBox field in new[] { _line, _lineMask }.Concat(_references.SelectMany(reference => new[] { reference.Path, reference.Mask })))
            field.TextChanged += (_, _) => InvalidatePlan();
        foreach (NumberBox box in new[] { _lineSaturation }.Concat(_references.Select(reference => reference.Saturation)))
            box.ValueChanged += (_, _) => InvalidatePlan();
        _psf.Checked += (_, _) => InvalidatePlan(); _psf.Unchecked += (_, _) => InvalidatePlan();
        foreach (TextBox field in new[] { _ha, _oiii, _sii, _structure, _line, _lineMask, _original, _starless, _preparedStars, _nebula }.Concat(_references.SelectMany(reference => new[] { reference.Path, reference.Mask })))
            field.TextChanged += (_, _) => InvalidatePreview();
        foreach (NumberBox box in new[] { _shadow, _oiiiBoost, _siiBoost, _structureAmount, _lineSaturation, _starAmount, _starReduction, _median, _green }.Concat(_references.SelectMany(reference => new[] { reference.Saturation, reference.Coefficient })))
            box.ValueChanged += (_, _) => InvalidatePreview();
        foreach (CheckBox box in new[] { _aligned, _displayChannels, _balance, _excludeStars, _nebulaDisplay, _psf })
        { box.Checked += (_, _) => InvalidatePreview(); box.Unchecked += (_, _) => InvalidatePreview(); }
        _palette.SelectionChanged += (_, _) => InvalidatePreview();
        _analyze.Click += async (_, _) => await RunAsync(analyze: true);
        _save.Click += async (_, _) => await RunAsync(analyze: (ColorTool)_tool.SelectedIndex == ColorTool.Continuum && _fitMode.SelectedIndex == 0 && _plan is null);
        _export.Click += SaveImage_Click;
        _loadPlan.Click += LoadPlan_Click; _saveReport.Click += SaveReport_Click;
        _open.Click += async (_, _) => { if (_savedPath is string path) await App.OpenDocumentPathsAsync([path]); };
        _cancel.Click += (_, _) => _operation?.Cancel();
        Closed += (_, _) => Dispose();
        _tool.SelectedIndex = 0; _initializing = false; UpdateMode();
    }

    private sealed class ReferenceRow(int number)
    {
        public TextBox Path { get; } = PathField($"Continuum reference {number} (mono, linear){(number > 1 ? " · optional" : "")}");
        public NumberBox Saturation { get; } = Number("Physical saturation (0 = unknown)", 0, 0, double.MaxValue, 1000);
        public NumberBox Coefficient { get; } = Number("Manual coefficient", 1, 0, 1000, .05);
        public TextBox Mask { get; } = PathField("Optional producer clipping mask: mono 0 / 1");
    }
    private static TextBox PathField(string header) => new() { Header = header };
    private static NumberBox Number(string header, double value, double min, double max, double step) => new()
    { Header = header, Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static Grid Row(params FrameworkElement[] controls)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < controls.Length; i++)
        { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); Grid.SetColumn(controls[i], i); grid.Children.Add(controls[i]); }
        return grid;
    }
    private void AddPaths(StackPanel panel, params TextBox[] fields) { foreach (TextBox field in fields) panel.Children.Add(Picker(field)); }
    private Grid Picker(TextBox field)
    {
        field.PlaceholderText = "Choose a FITS or XISF image";
        field.TextChanged += (_, _) => ToolTipService.SetToolTip(field, field.Text);
        field.LostFocus += (_, _) => field.Select(field.Text.Length, 0);
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(field); Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                foreach (string extension in ImageFileTypes.AstronomyExtensions) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync(); if (!_closed && file is not null) { field.Text = file.Path; field.Select(field.Text.Length, 0); }
            }
            catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        };
        return grid;
    }

    private void InvalidatePlan()
    {
        if (_initializing) return;
        // WinUI can deliver programmatic TextChanged events after report import
        // returns. Preserve the plan when those events describe the exact fields
        // it was loaded against; actual source/metadata changes still discard it.
        if (_plan is not null && _planInputSnapshot == PlanInputSnapshot()) return;
        _plan = null; _planPath = null; _planInputSnapshot = null; InvalidatePreview();
        UpdateMode();
    }
    private string PlanInputSnapshot() => JsonSerializer.Serialize(new {
        Tool = _tool.SelectedIndex, Mode = _fitMode.SelectedIndex, Line = _line.Text, Mask = _lineMask.Text,
        Saturation = _lineSaturation.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), Psf = _psf.IsChecked == true,
        References = _references.Select(reference => new { Path = reference.Path.Text,
            Mask = reference.Mask.Text, Saturation = reference.Saturation.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) }).ToArray(),
    });
    private void ClearResult()
    {
        _result = null; _completedRequest = null; _report.Text = ""; _previewCurrent = false;
        _savedPath = null; _preview.Source = null; _previewHint.Text = "Choose your source images, then build a preview.";
        _reportDetails.Visibility = Visibility.Collapsed; _reportDetails.IsExpanded = false;
        _open.IsEnabled = false; _saveReport.IsEnabled = false; _export.IsEnabled = false;
    }
    private void InvalidatePreview()
    {
        if (_initializing) return;
        _previewCurrent = false; _export.IsEnabled = false;
        if (_preview.Source is not null) _previewHint.Text = "Inputs or settings changed. Build a new preview before saving.";
    }
    private void UpdateMode()
    {
        if (_initializing) return;
        var mode = (ColorTool)_tool.SelectedIndex;
        bool sho = _palette.SelectedIndex == 1;
        bool prepared = mode == ColorTool.RestorePreparedStars;
        bool restore = mode is ColorTool.RestoreStars or ColorTool.RestorePreparedStars;
        bool needsStretch = mode == ColorTool.Narrowband ? _displayChannels.IsChecked != true : restore && _nebulaDisplay.IsChecked != true;
        string channelScale = _displayChannels.IsChecked == true ? "display" : "linear";
        _ha.Header = $"H-alpha stack (mono, {channelScale})";
        _oiii.Header = $"OIII stack (mono, {channelScale})";
        _sii.Header = $"SII stack (mono, {channelScale})";
        _nebula.Header = _nebulaDisplay.IsChecked == true ? "Nebula RGB (display)" : "Nebula RGB (linear)";
        foreach (TextBox field in new[] { _ha, _oiii, _sii, _nebula }) AutomationProperties.SetName(field, (string)field.Header);
        _balance.Content = sho ? "Balance OIII and SII midtones against H-alpha" : "Balance OIII midtones against H-alpha";
        _balanceSettings.IsEnabled = _balance.IsChecked == true;
        _siiBoost.Visibility = sho ? Visibility.Visible : Visibility.Collapsed;
        if (_siiBoost.Parent is Grid balanceRow) balanceRow.ColumnDefinitions[2].Width = new GridLength(sho ? 1 : 0, GridUnitType.Star);
        _narrowband.Visibility = mode == ColorTool.Narrowband ? Visibility.Visible : Visibility.Collapsed;
        _continuum.Visibility = mode == ColorTool.Continuum ? Visibility.Visible : Visibility.Collapsed;
        _stars.Visibility = mode is ColorTool.StretchStars or ColorTool.RestoreStars or ColorTool.RestorePreparedStars ? Visibility.Visible : Visibility.Collapsed;
        if (_sii.Parent is UIElement sulfur) sulfur.Visibility = sho ? Visibility.Visible : Visibility.Collapsed;
        foreach (TextBox field in new[] { _original, _starless })
            if (field.Parent is UIElement parent) parent.Visibility = mode == ColorTool.RestorePreparedStars ? Visibility.Collapsed : Visibility.Visible;
        if (_preparedStars.Parent is UIElement preparedParent) preparedParent.Visibility = prepared ? Visibility.Visible : Visibility.Collapsed;
        if (_nebula.Parent is UIElement nebula) nebula.Visibility = mode == ColorTool.StretchStars ? Visibility.Collapsed : Visibility.Visible;
        _nebulaDisplay.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        _narrowbandProcessing.Visibility = mode == ColorTool.Narrowband ? Visibility.Visible : Visibility.Collapsed;
        _starNote.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        _starAmount.Visibility = prepared ? Visibility.Collapsed : Visibility.Visible;
        _starReduction.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        if (_starAmount.Parent is Grid starRow)
        {
            starRow.ColumnDefinitions[0].Width = new GridLength(prepared ? 0 : 1, GridUnitType.Star);
            starRow.ColumnDefinitions[1].Width = new GridLength(restore ? 1 : 0, GridUnitType.Star);
        }
        _starNote.Text = prepared ? "Use an aligned display stars-only layer in [0, 1]. It is screened directly over the nebula, without another stretch. Star reduction is linked across RGB." :
            mode == ColorTool.StretchStars ? "Choose the linear original and its aligned starless RGB image on the same physical scale. Star separation must already be done. The original minus starless layer is stretched independently and saved as a stars-only display image." :
            "Choose the linear original and its aligned starless RGB image on the same physical scale. Star separation must already be done. The original minus starless layer is stretched independently; any green reduction affects the nebula before stars are restored.";
        _processingSettings.Visibility = mode is ColorTool.Continuum or ColorTool.StretchStars ? Visibility.Collapsed : Visibility.Visible;
        _processingSettings.Header = mode == ColorTool.Narrowband ? "Processing · linked stretch and balance" : "Processing settings";
        AutomationProperties.SetName(_processingSettings, (string)_processingSettings.Header);
        _median.Visibility = needsStretch ? Visibility.Visible : Visibility.Collapsed;
        _aligned.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = mode switch {
            ColorTool.Continuum => "These are aligned, linear mono stacks on their original common scale",
            ColorTool.RestorePreparedStars => _nebulaDisplay.IsChecked == true ? "These are aligned display RGB images in [0, 1]" : "The stars are display RGB; the linear nebula shares the same grid",
            ColorTool.Narrowband => _displayChannels.IsChecked == true ? "These are aligned display mono channels in [0, 1]" : "These are aligned, linear mono stacks on their original common scale",
            ColorTool.StretchStars => "Original and starless are aligned, linear RGB on the same scale",
            _ => "Original and starless are aligned linear RGB; the nebula shares their grid",
        } };
        _green.Visibility = mode is ColorTool.Continuum or ColorTool.StretchStars ? Visibility.Collapsed : Visibility.Visible;
        _analyze.Visibility = _loadPlan.Visibility = mode == ColorTool.Continuum ? Visibility.Visible : Visibility.Collapsed;
        _analyze.IsEnabled = !_busy && _fitMode.SelectedIndex == 0;
        foreach (ReferenceRow reference in _references) reference.Coefficient.Visibility = _fitMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _psf.IsEnabled = _fitMode.SelectedIndex == 0;
        _psf.Content = _fitMode.SelectedIndex == 2 ? "PSF preparation is taken from the loaded plan" :
            _fitMode.SelectedIndex == 1 ? "PSF matching is unavailable with manual coefficients" :
            "Broaden copies to a common Gaussian PSF before fitting / subtraction";
        _save.IsEnabled = !_busy && (mode != ColorTool.Continuum || _fitMode.SelectedIndex is 0 or 1 || _plan is not null);
        if (_save.Content is TextBlock saveLabel) saveLabel.Text = mode switch {
            ColorTool.Continuum => _fitMode.SelectedIndex == 0 && _plan is null ? "Analyze stars" : "Build subtraction preview",
            _ => "Build preview",
        };
        if (_save.Content is TextBlock actionLabel) AutomationProperties.SetName(_save, actionLabel.Text);
        if (_save.Parent is Grid saveRow) saveRow.ColumnDefinitions[1].Width = new GridLength(_busy ? 1 : 0, GridUnitType.Star);
    }

    private ColorToolsRequest CreateRequest(string? output)
    {
        ColorTool mode = (ColorTool)_tool.SelectedIndex;
        var references = _references.Where(reference => !string.IsNullOrWhiteSpace(reference.Path.Text)).ToArray();
        static double? Saturation(NumberBox box) => box.Value == 0 ? null : box.Value;
        return new ColorToolsRequest
        {
            Tool = mode, Output = output, Aligned = _aligned.IsChecked == true, LinearConfirmed = _aligned.IsChecked == true,
            Inputs = mode switch
            {
                ColorTool.Narrowband => _palette.SelectedIndex == 0 ? [_ha.Text, _oiii.Text] : [_ha.Text, _oiii.Text, _sii.Text],
                ColorTool.Continuum => new[] { _line.Text }.Concat(references.Select(reference => reference.Path.Text)).ToArray(),
                ColorTool.StretchStars => [_original.Text, _starless.Text],
                ColorTool.RestoreStars => [_original.Text, _starless.Text, _nebula.Text],
                _ => [_preparedStars.Text, _nebula.Text],
            },
            InputDisplay = _displayChannels.IsChecked == true, NebulaDisplay = _nebulaDisplay.IsChecked == true,
            Palette = _palette.SelectedIndex == 0 ? "hoo" : "sho", TargetMedian = _median.Value,
            Balance = _balance.IsChecked == true, ShadowProtection = _shadow.Value, OiiiBoost = _oiiiBoost.Value, SiiBoost = _siiBoost.Value,
            ExcludeStars = _excludeStars.IsChecked == true, GreenReduction = _green.Value,
            Structure = mode == ColorTool.Narrowband && !string.IsNullOrWhiteSpace(_structure.Text) ? _structure.Text : null,
            StructureAmount = _structureAmount.Value,
            Saturations = mode == ColorTool.Continuum ? new[] { Saturation(_lineSaturation) }.Concat(references.Select(reference => Saturation(reference.Saturation))).ToArray() : [],
            ClippingMasks = mode == ColorTool.Continuum ? new[] { _lineMask.Text }.Concat(references.Select(reference => reference.Mask.Text)).ToArray() : [],
            PsfMatching = _psf.IsChecked == true,
            ManualCoefficients = mode == ColorTool.Continuum && _fitMode.SelectedIndex == 1 ? references.Select(reference => reference.Coefficient.Value).ToArray() : null,
            Plan = mode == ColorTool.Continuum && _fitMode.SelectedIndex != 1 ? _plan : null, PlanPath = _planPath,
            StarAmount = _starAmount.Value, StarReduction = _starReduction.Value,
        };
    }

    private async Task RunAsync(bool analyze)
    {
        if (_busy) return;
        SetBusy(true);
        _operation = new CancellationTokenSource();
        try
        {
            CreateRequest(null).Validate(analyze, requireOutput: false);
            Directory.CreateDirectory(_previewDirectory);
            string? output = analyze ? null : Path.Combine(_previewDirectory, Guid.NewGuid().ToString("N") + ".fits");
            var request = CreateRequest(output);
            Show(analyze ? "Measuring and validating stars…" : "Building preview…", InfoBarSeverity.Informational);
            var result = await ColorToolsService.RunAsync(request, analyze, _operation.Token);
            if (_closed) return;
            _result = result; _completedRequest = request;
            _report.Text = JsonSerializer.Serialize(result.Report, Pretty);
            _reportDetails.Visibility = Visibility.Visible;
            if (analyze)
            {
                _plan = result.Status == "fitted" ? result.Report.GetProperty("plan").Clone() : null;
                _planInputSnapshot = _plan is null ? null : PlanInputSnapshot();
            }
            _savedPath = null; _preview.Source = null; _previewCurrent = false;
            if (result.Output is string previewPath)
            {
                var rendered = await ImageRenderService.RenderAsync(previewPath, maxDimension: 1000, cancellationToken: _operation.Token);
                if (_closed) return;
                var bitmap = new WriteableBitmap(rendered.Width, rendered.Height);
                using (Stream pixels = bitmap.PixelBuffer.AsStream()) pixels.Write(rendered.Bgra);
                _preview.Source = bitmap; _previewCurrent = true;
                _previewHint.Text = result.Report.GetProperty("transfer").GetString() == "linear"
                    ? "Autostretched preview · the saved residual stays signed and linear."
                    : "Display preview · no additional stretch.";
                Show("Preview ready. Review it, then save the image.", InfoBarSeverity.Success);
            }
            else
            {
                _previewHint.Text = result.Message;
                if (result.Status == "skipped") _fitSettings.IsExpanded = true;
                Show(result.Message, result.Status == "skipped" ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException) { Show("Cancelled. No output was replaced.", InfoBarSeverity.Informational); }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); if (_closed) CleanupPreviews(); }
    }

    private async void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_previewCurrent || _result?.Output is null || _completedRequest is null) return;
        SetBusy(true); _operation = new CancellationTokenSource();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            { SuggestedFileName = _completedRequest.Tool == ColorTool.Continuum ? "continuum-residual" : "color-display", DefaultFileExtension = ".fits", ShowOverwritePrompt = true };
            picker.FileTypeChoices.Add("Float FITS", [".fits"]); picker.FileTypeChoices.Add("Float XISF", [".xisf"]);
            var file = await picker.PickSaveFileAsync(); if (_closed || file is null) return;
            await ColorToolsService.SaveImageAsync(_result, _completedRequest, file.Path, _operation.Token);
            if (_closed) return;
            _savedPath = file.Path; ToolTipService.SetToolTip(_open, file.Path);
            Show($"Saved {Path.GetFileName(file.Path)}. Original sources are unchanged.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { Show("Cancelled. No output was replaced.", InfoBarSeverity.Informational); }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); if (_closed) CleanupPreviews(); }
    }

    private async void LoadPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync(); if (_closed || file is null) return;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            JsonElement plan = document.RootElement.TryGetProperty("plan", out JsonElement nested) ? nested : document.RootElement;
            if (!plan.TryGetProperty("sources", out _) || !plan.TryGetProperty("coefficients", out _))
                throw new InvalidOperationException("Choose a continuum plan or a fitted/subtraction report containing one.");
            ClearResult(); _plan = null; _planPath = null;
            _fitMode.SelectedIndex = 2;
            JsonElement identities = plan.GetProperty("sources");
            JsonElement[] references = identities.GetProperty("references").EnumerateArray().ToArray();
            if (references.Length is < 1 or > 3) throw new InvalidOperationException("The plan must contain one to three continuum references.");
            for (int index = references.Length; index < _references.Length; index++) _references[index].Path.Text = "";
            JsonElement[] sources = [identities.GetProperty("narrowband"), .. references];
            if (document.RootElement.TryGetProperty("sources", out JsonElement paths) && paths.ValueKind == JsonValueKind.Array && paths.GetArrayLength() == sources.Length)
            {
                _line.Text = paths[0].GetProperty("path").GetString() ?? "";
                for (int index = 0; index < _references.Length; index++) _references[index].Path.Text = index < references.Length ? paths[index + 1].GetProperty("path").GetString() ?? "" : "";
            }
            for (int index = 0; index < sources.Length; index++)
            {
                double saturation = sources[index].TryGetProperty("saturation", out JsonElement ceiling) && ceiling.ValueKind == JsonValueKind.Number ? ceiling.GetDouble() : 0;
                (index == 0 ? _lineSaturation : _references[index - 1].Saturation).Value = saturation;
                string mask = "";
                if (sources[index].TryGetProperty("clippingRevision", out JsonElement revision) && revision.ValueKind == JsonValueKind.String &&
                    document.RootElement.TryGetProperty("auxiliarySources", out JsonElement auxiliary))
                    foreach (JsonElement entry in auxiliary.EnumerateArray())
                        if (entry.GetProperty("sha256").GetString() == revision.GetString()) { mask = entry.GetProperty("path").GetString() ?? ""; break; }
                (index == 0 ? _lineMask : _references[index - 1].Mask).Text = mask;
            }
            _aligned.IsChecked = false;
            _plan = plan.Clone(); _planPath = file.Path; _planInputSnapshot = PlanInputSnapshot();
            _report.Text = JsonSerializer.Serialize(document.RootElement, Pretty); UpdateMode();
            _reportDetails.Visibility = Visibility.Visible;
            _fitSettings.IsExpanded = false; _bodyScroll.ChangeView(null, 0, null, disableAnimation: true);
            _previewHint.Text = "Loaded coefficients: " + string.Join(", ", plan.GetProperty("coefficients").EnumerateArray().Select(value => value.ToString())) + ". Confirm the inputs, then build the subtraction preview.";
            Show("Plan loaded with its producer metadata. A full report also restores source paths; building validates the exact source hashes and grid.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }
    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _completedRequest is null || _busy) return;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            { SuggestedFileName = "color-report", DefaultFileExtension = ".json", ShowOverwritePrompt = true };
            picker.FileTypeChoices.Add("Processing report / continuum plan", [".json"]);
            var file = await picker.PickSaveFileAsync(); if (!_closed && file is not null)
            {
                await ColorToolsService.SaveReportAsync(_result with { Output = _savedPath ?? _result.Output }, _completedRequest, file.Path);
                Show($"Report saved: {Path.GetFileName(file.Path)}.", InfoBarSeverity.Success);
            }
        }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; if (_closed) return;
        _inputHost.IsEnabled = !busy; _tool.IsEnabled = !busy; _loadPlan.IsEnabled = !busy;
        _progress.IsActive = busy; _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = busy; _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _open.IsEnabled = !busy && _savedPath is not null; _saveReport.IsEnabled = !busy && _result is not null;
        _export.IsEnabled = !busy && _previewCurrent && _result?.Output is not null;
        UpdateMode();
    }
    private void Show(string message, InfoBarSeverity severity)
    { if (!_closed) { _status.Message = message; _status.Severity = severity; } }
    public void Dispose()
    {
        if (_closed) return; _closed = true; _operation?.Cancel(); if (_operation is null) CleanupPreviews(); GC.SuppressFinalize(this);
    }
    private void CleanupPreviews()
    {
        try { if (Directory.Exists(_previewDirectory)) Directory.Delete(_previewDirectory, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
