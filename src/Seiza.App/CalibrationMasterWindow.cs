using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App;

internal sealed class CalibrationMasterWindow : Window
{
    private readonly ListView _masterInputs = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 220 };
    private readonly ComboBox _masterKind = new() { Header = "Master type", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _masterStatus = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _masterSelectionHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _masterBuildButton = new() { Content = "Build and save master…", IsEnabled = false };
    private readonly TextBlock _masterBiasText = new() { Text = "No master bias", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _masterDarkText = new() { Text = "No master dark-flat", TextWrapping = TextWrapping.Wrap };
    private string? _masterBias;
    private string? _masterDark;
    private readonly ContentControl _masterConfigurationHost = new() { IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl ConfigurationHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button CancelButton = new() { Content = "Close" };
    private string[] _sourcePaths;
    private bool _running, _closed, _closeWhenIdle, _allowClose, _loaded;
    private bool _routing = true;
    private CancellationTokenSource? _stackCancellation;
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal CalibrationMasterWindow(IReadOnlyList<string> paths)
    {
        Title = "Build calibration masters";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _sourcePaths = paths.Where(ImageFileService.IsStackableImage).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ConfigurationHost.Content = new ScrollViewer { Content = CreateMasterBuilder(), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(ConfigurationHost);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        CancelButton.Click += (_, _) =>
        {
            if (_running) { CancelButton.IsEnabled = false; _stackCancellation?.Cancel(); }
            else Close();
        };
        footer.Children.Add(CancelButton);
        footer.Children.Add(_masterBuildButton);
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        Content = root;
        AppWindow.Closing += (_, args) =>
        {
            if (!_running || _allowClose) return;
            args.Cancel = true; _closeWhenIdle = true; _stackCancellation?.Cancel();
        };
        Closed += (_, _) => { _closed = true; _stackCancellation?.Cancel(); };
        root.Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            double scale = root.XamlRoot.RasterizationScale;
            int width = Math.Min((int)(720 * scale), area.Width - 48);
            int height = Math.Min((int)(860 * scale), area.Height - 48);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
            await AddMasterPathsAsync(_sourcePaths);
            _routing = false;
            UpdateMasterValidation();
        };
        UpdateMasterValidation();
    }

    private StackPanel CreateMasterBuilder()
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 16, 12, 16) };
        panel.Children.Add(new TextBlock { Text = "Build calibration masters without light frames. Select one compatible set at a time (same camera settings, exposure for darks, and filter for flats). Frames are combined without star alignment.", TextWrapping = TextWrapping.Wrap });
        var _masterConfiguration = new StackPanel { Spacing = 10 };
        _masterConfigurationHost.Content = _masterConfiguration;
        panel.Children.Add(_masterConfigurationHost);
        foreach (string kind in new[] { "Bias", "Dark / dark-flat", "Flat" }) _masterKind.Items.Add(kind);
        _masterKind.SelectedIndex = 0;
        _masterConfiguration.Children.Add(_masterKind);
        _masterConfiguration.Children.Add(StackFramePicker.CreateButton("Add calibration frames", AddFramesAsync));
        _masterConfiguration.Children.Add(_masterInputs);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_masterInputs, "Calibration frames to combine");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(_masterStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _masterInputs.SelectionChanged += (_, _) => UpdateMasterValidation();
        _masterKind.SelectionChanged += (_, _) => UpdateMasterValidation();
        var selection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var all = new Button { Content = "Select all" };
        all.Click += (_, _) => _masterInputs.SelectAll();
        var none = new Button { Content = "Select none" };
        none.Click += (_, _) => _masterInputs.SelectedItems.Clear();
        var clearFrames = new Button { Content = "Clear frames" };
        clearFrames.Click += (_, _) => { SetMasterInputs([]); _masterStatus.Text = string.Empty; };
        selection.Children.Add(all); selection.Children.Add(none); selection.Children.Add(clearFrames);
        _masterConfiguration.Children.Add(selection);
        _masterConfiguration.Children.Add(_masterSelectionHint);
        var bias = new Button { Content = "Choose master bias…" };
        bias.Click += async (_, _) =>
        {
            StorageFile? file = await StackFramePicker.OpenPicker(WindowHandle).PickSingleFileAsync();
            if (!_closed && file is not null) { _masterBias = file.Path; _masterBiasText.Text = file.Path; UpdateMasterValidation(); }
        };
        var dark = new Button { Content = "Choose master dark-flat…" };
        dark.Click += async (_, _) =>
        {
            StorageFile? file = await StackFramePicker.OpenPicker(WindowHandle).PickSingleFileAsync();
            if (!_closed && file is not null) { _masterDark = file.Path; _masterDarkText.Text = file.Path; UpdateMasterValidation(); }
        };
        var clear = new Button { Content = "Clear calibration masters" };
        clear.Click += (_, _) => { _masterBias = _masterDark = null; _masterBiasText.Text = "No master bias"; _masterDarkText.Text = "No master dark-flat"; UpdateMasterValidation(); };
        _masterConfiguration.Children.Add(new TextBlock { Text = "Bias masters need no calibration. Darks may use a master bias. Flats require a master bias or an uncalibrated master dark-flat matching the flat exposure; the core checks compatibility.", TextWrapping = TextWrapping.Wrap });
        _masterConfiguration.Children.Add(bias);
        _masterConfiguration.Children.Add(_masterBiasText);
        _masterConfiguration.Children.Add(dark);
        _masterConfiguration.Children.Add(_masterDarkText);
        _masterConfiguration.Children.Add(clear);
        _masterBuildButton.Click += BuildMaster_Click;

        panel.Children.Add(_masterStatus);
        return panel;
    }

    private void SetMasterInputs(IEnumerable<string> paths)
    {
        _masterInputs.ItemsSource = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _masterInputs.SelectAll();
        UpdateMasterValidation();
    }

    private void UpdateMasterValidation()
    {
        _masterConfigurationHost.IsEnabled = !_routing && !_running;
        CancelButton.Content = _running ? "Cancel" : "Close";
        int count = _masterInputs.SelectedItems.Count;
        bool needsPedestal = _masterKind.SelectedIndex == 2 && _masterBias is null && _masterDark is null;
        _masterBuildButton.IsEnabled = !_routing && !_running && count >= 2 && !needsPedestal;
        _masterSelectionHint.Text = _masterInputs.Items.Count == 0
            ? "Add calibration frames from images, a folder, or a TXT/CSV file list to get started."
            : needsPedestal ? "Choose a master bias or matching master dark-flat to build a flat."
            : $"{count} of {_masterInputs.Items.Count} frames selected." + (count < 2 ? " Select at least two to build a master." : "");
    }

    private async void BuildMaster_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _routing || _closed) return;
        string[] inputs = _masterInputs.SelectedItems.OfType<string>().ToArray();
        string kind = _masterKind.SelectedIndex switch { 0 => "bias", 1 => "dark", _ => "flat" };
        if (inputs.Length < 2) { _masterStatus.Text = "Select at least two calibration frames."; return; }
        if (kind == "flat" && _masterBias is null && _masterDark is null)
        { _masterStatus.Text = "Choose a master bias or matching master dark-flat before building a flat."; return; }
        _running = true;
        ConfigurationHost.IsEnabled = false;
        UpdateMasterValidation();
        using var cancellation = new CancellationTokenSource();
        _stackCancellation = cancellation;
        string? placeholder = null;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"master-{kind}" };
            picker.FileTypeChoices.Add("FITS image", new List<string> { ".fits" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            StorageFile? output = await picker.PickSaveFileAsync();
            if (output is null || _closed) return;
            string[] protectedPaths = _sourcePaths
                .Concat(_masterInputs.Items.OfType<string>())
                .Concat(new[] { _masterBias, _masterDark }.OfType<string>()).ToArray();
            if (protectedPaths.Any(path => StackSourceIdentity.RefersToSameFile(output.Path, path)))
                throw new InvalidOperationException("The master output cannot replace an input image or calibration master.");
            if (new FileInfo(output.Path).Length == 0) placeholder = output.Path;
            cancellation.Token.ThrowIfCancellationRequested();
            _masterStatus.Text = $"Building master {kind} from {inputs.Length} frames…";
            var request = new CalibrationMasterBuildRequest
            {
                Kind = kind, Inputs = inputs, Output = output.Path,
                Bias = kind != "bias" ? _masterBias : null,
                Dark = kind == "flat" ? _masterDark : null,
                DefectSuppression = kind == "flat" ? new() : null,
            };
            CalibrationMasterBuildResult result = await CalibrationMasterExport.BuildAsync(request,
                protectedPaths, token: cancellation.Token);
            placeholder = null;
            _masterStatus.Text = $"Saved {result.Output}\n{result.InputFrames} frames combined; {result.SkippedInputs.Length} skipped." +
                string.Concat(result.SkippedInputs.Select(item => $"\n{Path.GetFileName(item.Path)}: {item.Reason}"));
        }
        catch (OperationCanceledException) { _masterStatus.Text = "Master build cancelled."; }
        catch (Exception exception) { _masterStatus.Text = exception.Message; }
        finally
        {
            if (placeholder is not null) CleanupPlaceholderOutputs([placeholder]);
            _running = false;
            if (ReferenceEquals(_stackCancellation, cancellation)) _stackCancellation = null;
            if (!_closed)
            {
                ConfigurationHost.IsEnabled = true;
                CancelButton.IsEnabled = true;
                UpdateMasterValidation();
                if (_closeWhenIdle) { _allowClose = true; Close(); }
            }
        }
    }
    private async Task AddFramesAsync(string source)
    {
        if (_running || _routing || _closed) return;
        await AddMasterPathsAsync(null, source);
    }

    private async Task AddMasterPathsAsync(string[]? initialPaths, string? source = null)
    {
        _running = true;
        ConfigurationHost.IsEnabled = false;
        UpdateMasterValidation();
        using var cancellation = new CancellationTokenSource();
        _stackCancellation = cancellation;
        try
        {
            StackFrameImportResult? imported = initialPaths is not null ? new(initialPaths, [])
                : await StackFramePicker.PickAsync(source!, WindowHandle, cancellation.Token);
            if (imported is null) return;
            _masterStatus.Text = "Reading image roles…";
            string[] existing = _masterInputs.Items.OfType<string>().ToArray();
            string[] added = imported.Paths.Except(existing, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            StackInputRoutingResult routed = await StackInputRouting.RouteAsync(added, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _sourcePaths = _sourcePaths.Concat(imported.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var selected = _masterInputs.SelectedItems.OfType<string>().Concat(routed.Calibration).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _masterInputs.ItemsSource = existing.Concat(routed.Calibration).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string path in _masterInputs.Items.OfType<string>())
                if (selected.Contains(path)) _masterInputs.SelectedItems.Add(path);
            var messages = new List<string>(imported.Notes.Concat(routed.Notes));
            if (imported.Paths.Length > added.Length) messages.Add($"{imported.Paths.Length - added.Length} duplicate frames ignored; selections kept.");
            if (routed.Lights.Length > 0) messages.Add($"Skipped {routed.Lights.Length} light or unclassified frames; choose calibration frames here.");
            if (imported.Paths.Length > 0) messages.Insert(0, $"Added {routed.Calibration.Length} calibration frames. {_masterInputs.Items.Count} available.");
            _masterStatus.Text = string.Join(Environment.NewLine, messages);
        }
        catch (OperationCanceledException) { if (!_closed) _masterStatus.Text = "Adding frames cancelled; existing inputs kept."; }
        catch (Exception exception) { if (!_closed) _masterStatus.Text = exception.Message; }
        finally
        {
            _running = false;
            if (ReferenceEquals(_stackCancellation, cancellation)) _stackCancellation = null;
            if (!_closed)
            {
                ConfigurationHost.IsEnabled = true;
                CancelButton.IsEnabled = true;
                UpdateMasterValidation();
                if (_closeWhenIdle) { _allowClose = true; Close(); }
            }
        }
    }

    private static void CleanupPlaceholderOutputs(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
