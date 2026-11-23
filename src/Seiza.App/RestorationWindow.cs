using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Seiza.App.Interop;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics;

namespace Seiza.App;

internal sealed class RestorationWindow : Window
{
    private readonly TextBlock _sourceLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _browse = new() { Content = "Choose image…" };
    private readonly ComboBox _model = new() { Header = "Noise model", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _runtimeStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new() { Content = "Check for model updates" };
    private readonly Button _import = new() { Content = "Import model folder…" };
    private readonly Button _verify = new() { Content = "Verify model" };
    private readonly Button _remove = new() { Content = "Remove model…" };
    private readonly Slider _grain = new() { Header = "Grain reduction · 70%", Minimum = 0, Maximum = 100, Value = 70, StepFrequency = 1 };
    private readonly Slider _mottle = new() { Header = "Mottle reduction · 90%", Minimum = 0, Maximum = 100, Value = 90, StepFrequency = 1 };
    private readonly ComboBox _backend = new() { Header = "Backend", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _precision = new() { Header = "Precision", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _device = new() { Header = "DirectML adapter index (-1 = automatic)", Value = -1, Minimum = -1, Maximum = int.MaxValue, Visibility = Visibility.Collapsed };
    private readonly NumberBox _tile = new() { Header = "Tile size (px, 0 = automatic)", Value = 0, Minimum = 0, Maximum = 4096, SmallChange = 16 };
    private readonly NumberBox _scale = new() { Header = "Grain / mottle scale (px)", Value = 5, Minimum = 0.25, Maximum = 64, SmallChange = 0.25 };
    private readonly Button _run = new() { Content = "Preview center" };
    private readonly Button _refresh = new() { Content = "Update preview" };
    private readonly Button _save = new() { Content = "Save full FITS…" };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly Button _openOutput = new() { Content = "Open saved image", Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 440 };
    private readonly ToggleSwitch _compare = new() { Header = "Center preview", OffContent = "Restored", OnContent = "Original", IsEnabled = false };
    private readonly TextBlock _previewHint = new() { Text = "Preview the center tiles before processing the full image.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _root = new() { RowSpacing = 12, Padding = new Thickness(20) };
    private CancellationTokenSource? _operation;
    private SafeRestorationHandle? _result;
    private ImageSource? _originalBitmap;
    private ImageSource? _restoredBitmap;
    private readonly RestorationSourceSelection _sourceSelection;
    private string? _savedPath;
    private string? _preferredModel;
    private NoiseModelCatalog? _catalog;
    private IReadOnlyList<InstalledNoiseModel> _installedModels = [];
    private bool _refreshingModelChoices;
    private bool _runtimeReady;
    private bool _closed;

    internal RestorationWindow(string? source)
    {
        Title = "AI Models — Seiza";
        WindowIcon.Apply(AppWindow);
        _sourceSelection = new RestorationSourceSelection(source);
        _preferredModel = CatalogSettingsStore.LoadPreferredNoiseModel();
        SetSourceLabel();

        var advanced = new StackPanel { Spacing = 8 };
        advanced.Children.Add(_model);
        advanced.Children.Add(_download);
        advanced.Children.Add(_import);
        advanced.Children.Add(Buttons(_verify, _remove));
        advanced.Children.Add(_runtimeStatus);
        advanced.Children.Add(_backend);
        advanced.Children.Add(_device);
        advanced.Children.Add(_precision);
        advanced.Children.Add(_tile);
        advanced.Children.Add(_scale);
        _backend.Items.Add(new RestorationBackend("", "Automatic"));
        _backend.SelectedIndex = 0;

        var settings = new StackPanel { Spacing = 12 };
        settings.Children.Add(_sourceLabel);
        settings.Children.Add(_browse);
        settings.Children.Add(_grain);
        settings.Children.Add(_mottle);
        settings.Children.Add(new Expander
        {
            Header = "Advanced processing", Content = advanced,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        var comparison = new StackPanel { Spacing = 8 };
        comparison.Children.Add(_compare);
        comparison.Children.Add(_previewHint);
        comparison.Children.Add(_preview);
        comparison.Children.Add(_details);
        var main = new Grid { ColumnSpacing = 20 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(settings);
        Grid.SetColumn(comparison, 1);
        main.Children.Add(comparison);
        main.SizeChanged += (_, args) =>
        {
            bool narrow = args.NewSize.Width < 650;
            main.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(320);
            main.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            main.RowSpacing = narrow ? 16 : 0;
            Grid.SetColumn(comparison, narrow ? 0 : 1);
            Grid.SetRow(comparison, narrow ? 1 : 0);
        };

        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(_progress);
        footer.Children.Add(_status);
        footer.Children.Add(new ScrollViewer
        {
            Content = Buttons(_run, _refresh, _save, _cancel),
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        footer.Children.Add(_openOutput);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(new ScrollViewer { Content = main });
        Grid.SetRow(footer, 1);
        _root.Children.Add(footer);
        Content = _root;
        _root.Loaded += ResizeWindow;

        _browse.Click += (_, _) => ChooseImage();
        _download.Click += (_, _) => Perform(async (progress, token) =>
        {
            if (!await RefreshModelsAsync(token, refreshCatalog: true))
            {
                Show("The Seiza catalog is unavailable. You can still use installed models.", InfoBarSeverity.Warning);
                return;
            }
            var model = await EnsureModelAsync(progress, token, updateInstalled: true);
            Show($"{model.Name} is ready.", InfoBarSeverity.Success);
        }, "Checking the Seiza model catalog…", canCancel: false);
        _import.Click += (_, _) => ImportModel();
        _verify.Click += (_, _) => Perform(async (_, _) =>
        {
            var model = SelectedModel!;
            await RestorationService.VerifyAsync(model);
            Show($"{model.Name} passed verification.", InfoBarSeverity.Success);
        }, "Checking model files…", canCancel: false);
        _remove.Click += (_, _) => RemoveModel();
        _model.SelectionChanged += (_, _) =>
        {
            if (_refreshingModelChoices || _model.SelectedItem is not ModelChoice choice) return;
            SetPreferredModel(choice.Name);
            UpdatePrecisions();
            InvalidateResult();
            if (SelectedModel is null)
            {
                Perform(async (progress, token) =>
                {
                    var model = await EnsureModelAsync(progress, token);
                    Show($"{model.Name} is ready.", InfoBarSeverity.Success);
                }, "Preparing the selected model…", canCancel: false);
            }
        };
        _backend.SelectionChanged += (_, _) =>
        {
            _device.Visibility = (_backend.SelectedItem as RestorationBackend)?.Name == "directml"
                ? Visibility.Visible : Visibility.Collapsed;
            InvalidateResult();
        };
        _device.ValueChanged += (_, _) => InvalidateResult();
        _precision.SelectionChanged += (_, _) => InvalidateResult();
        _tile.ValueChanged += (_, _) => InvalidateResult();
        _grain.ValueChanged += (_, _) => { _grain.Header = $"Grain reduction · {_grain.Value:0}%"; MarkPreviewStale(); };
        _mottle.ValueChanged += (_, _) => { _mottle.Header = $"Mottle reduction · {_mottle.Value:0}%"; MarkPreviewStale(); };
        _scale.ValueChanged += (_, _) => MarkPreviewStale();
        _compare.Toggled += (_, _) => _preview.Source = _compare.IsOn ? _originalBitmap : _restoredBitmap;
        _run.Click += (_, _) => Run();
        _refresh.Click += (_, _) => Perform(async (_, token) => await UpdatePreviewAsync(token), "Updating comparison…");
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) =>
        {
            _operation?.Cancel();
            _cancel.IsEnabled = false;
            Show("Cancelling…");
        };
        _openOutput.Click += async (_, _) =>
        {
            if (_savedPath is not string path) return;
            try { await App.OpenDocumentPathsAsync([path]); }
            catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _operation?.Cancel();
            if (_operation is null) _result?.Dispose();
        };
        Perform(async (progress, token) =>
        {
            await RefreshModelsAsync(token, refreshCatalog: true);
            await EnsureRuntimeAsync(progress, token);
            await EnsureModelAsync(progress, token);
            ShowSourceStatus();
        }, "Checking the Seiza model catalog…", canCancel: false);
    }

    private sealed record ModelChoice(string? Name, bool IsInstalled)
    {
        public override string ToString() => Name is null ? "Recommended" : IsInstalled ? Name : $"{Name} (download)";
    }

    private string? RequestedModelName => _preferredModel ?? _catalog?.Recommended;

    private InstalledNoiseModel? SelectedModel => RequestedModelName is { } name
        ? _installedModels.FirstOrDefault(model => model.Name == name)
        : _installedModels.Count > 0 ? _installedModels[0] : null;

    private void ResizeWindow(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= ResizeWindow;
        RectInt32 area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = _root.XamlRoot.RasterizationScale;
        int width = Math.Min((int)(1000 * scale), area.Width - 48);
        int height = Math.Min((int)(740 * scale), area.Height - 48);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    internal void UseSource(string? source)
    {
        if (_closed) return;
        if (_sourceSelection.Request(source, busy: _operation is not null)) SourceChanged();
    }

    private void SourceChanged()
    {
        SetSourceLabel();
        InvalidateResult();
        _savedPath = null;
        _openOutput.Visibility = Visibility.Collapsed;
        ShowSourceStatus();
    }

    private void ShowSourceStatus()
    {
        if (_sourceSelection.Path is null)
            Show("Open an image in Seiza, or choose a linear FITS or XISF stack.");
        else if (!_sourceSelection.CanProcess)
            Show("This image format cannot be processed by the noise model. Use a linear FITS or XISF stack.", InfoBarSeverity.Warning);
        else
            Show("Current image selected. Preview the center tiles or save a full restored FITS.");
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (Button button in buttons) panel.Children.Add(button);
        return panel;
    }

    private async Task<bool> RefreshModelsAsync(CancellationToken token, bool refreshCatalog = false)
    {
        string? previous = SelectedModel?.CardHash;
        NoiseModelCatalog? catalog = _catalog;
        bool catalogAvailable = true;
        if (refreshCatalog)
        {
            try { catalog = await RestorationService.FetchCatalogAsync(token); }
            catch (Exception error) when (error is SeizaCoreException or JsonException)
            {
                catalogAvailable = false;
            }
        }
        token.ThrowIfCancellationRequested();
        var models = await RestorationService.ListAsync();
        token.ThrowIfCancellationRequested();
        _catalog = catalog;
        _installedModels = models;

        var names = models.Select(model => model.Name).Concat(catalog?.Models.Select(model => model.Name) ?? []);
        if (_preferredModel is not null) names = names.Append(_preferredModel);
        var choices = new List<ModelChoice> { new(null, SelectedModel is not null) };
        choices.AddRange(names.Distinct().OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new ModelChoice(name, models.Any(model => model.Name == name))));
        _refreshingModelChoices = true;
        try
        {
            _model.ItemsSource = choices;
            _model.SelectedItem = choices.First(choice => choice.Name == _preferredModel);
        }
        finally { _refreshingModelChoices = false; }
        if (previous != SelectedModel?.CardHash)
        {
            UpdatePrecisions();
            InvalidateResult();
        }
        return catalogAvailable;
    }

    private void SetPreferredModel(string? name)
    {
        _preferredModel = name;
        try { CatalogSettingsStore.SavePreferredNoiseModel(name); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<InstalledNoiseModel> EnsureModelAsync(IProgress<RestorationProgress> progress, CancellationToken token,
        bool updateInstalled = false)
    {
        string? name = RequestedModelName;
        var published = _catalog?.Models.FirstOrDefault(model => model.Name == name);
        if (SelectedModel is { } current && (!updateInstalled || published is null
            || string.Equals(current.CardHash, published.CardHash, StringComparison.OrdinalIgnoreCase)))
            return current;

        Show("Downloading and installing the noise model…");
        var installed = await RestorationService.DownloadAsync(name, progress, token);
        await RefreshModelsAsync(token);
        return installed;
    }

    private async Task EnsureRuntimeAsync(IProgress<RestorationProgress> progress, CancellationToken token)
    {
        if (_runtimeReady) return;
        if (!RestorationRuntime.CanLoad)
        {
            Show("Downloading and installing ONNX Runtime…");
            await RestorationRuntime.InstallAsync(progress, token);
        }
        token.ThrowIfCancellationRequested();
        var (version, backends) = await RestorationService.BackendsAsync();
        token.ThrowIfCancellationRequested();
        _backend.Items.Clear();
        _backend.Items.Add(new RestorationBackend("", "Automatic"));
        foreach (var backend in backends) _backend.Items.Add(backend);
        _backend.SelectedIndex = 0;
        _runtimeReady = true;
        _runtimeStatus.Text = $"ONNX Runtime {version} · {string.Join(", ", backends.Select(backend => backend.Label))}";
    }

    private void UpdatePrecisions()
    {
        _precision.Items.Clear();
        _precision.Items.Add(new RestorationBackend("", "Automatic"));
        foreach (string precision in SelectedModel?.Precisions ?? [])
            _precision.Items.Add(new RestorationBackend(precision, precision == "f32" ? "32-bit float" : "16-bit float"));
        _precision.SelectedIndex = 0;
    }

    private void ChooseImage() => Perform(async (_, token) =>
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        foreach (string extension in ImageFileTypes.AstronomyExtensions) picker.FileTypeFilter.Add(extension);
        var selected = await picker.PickSingleFileAsync();
        if (selected is null || _closed) return;
        token.ThrowIfCancellationRequested();
        if (_sourceSelection.Select(selected.Path)) SourceChanged();
        else ShowSourceStatus();
    }, "Choose a linear FITS or XISF image.", canCancel: false);

    private void ImportModel() => Perform(async (_, token) =>
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        var selected = await picker.PickSingleFolderAsync();
        if (selected is null || _closed) return;
        Show("Verifying and installing the model…");
        var installed = await RestorationService.ImportAsync(selected.Path, token);
        SetPreferredModel(installed.Name);
        await RefreshModelsAsync(token);
        Show($"{installed.Name} is installed and selected.", InfoBarSeverity.Success);
    }, "Choose the folder containing card.json.", canCancel: false);

    private void RemoveModel() => Perform(async (_, token) =>
    {
        if (SelectedModel is not { } model) return;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = $"Remove {model.Name}?",
            Content = "You can download or import the model again.",
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed) return;
        await RestorationService.RemoveAsync(model);
        SetPreferredModel(null);
        await RefreshModelsAsync(token);
        InvalidateResult();
        Show($"{model.Name} was removed.");
    }, "Remove a model from this app.", canCancel: false);

    private void Run() => Perform(async (progress, token) =>
    {
        if (!_sourceSelection.CanProcess || _sourceSelection.Path is not string input) return;
        await EnsureRuntimeAsync(progress, token);
        var model = await EnsureModelAsync(progress, token);
        var options = ReadOptions();
        InvalidateResult();
        Show("Processing the center tiles…");
        _result = await RestorationService.RunPreviewAsync(input, model, options, progress, token);
        token.ThrowIfCancellationRequested();
        await UpdatePreviewAsync(token);
    }, "Preparing the center preview…");

    private RestorationOptions ReadOptions()
    {
        double tile = _tile.Value;
        if (!double.IsFinite(tile) || tile != Math.Truncate(tile))
            throw new InvalidOperationException("Enter a whole tile size or 0 for automatic.");
        string? backend = (_backend.SelectedItem as RestorationBackend)?.Name;
        string? precision = (_precision.SelectedItem as RestorationBackend)?.Name;
        int? device = null;
        if (backend == "directml")
        {
            double index = _device.Value;
            if (!double.IsFinite(index) || index != Math.Truncate(index))
                throw new InvalidOperationException("Enter a whole DirectML adapter index or -1 for automatic.");
            if (index >= 0) device = (int)index;
        }
        return new RestorationOptions(backend is "" ? null : backend, precision is "" ? null : precision,
            tile == 0 ? null : (int)tile, device);
    }

    private RestorationBands ReadBands()
    {
        double scale = _scale.Value;
        if (!double.IsFinite(scale) || scale < 0.25 || scale > 64)
            throw new InvalidOperationException("Enter a grain / mottle scale from 0.25 to 64 pixels.");
        return new RestorationBands(_grain.Value / 100, _mottle.Value / 100, scale);
    }

    private async Task UpdatePreviewAsync(CancellationToken token)
    {
        if (_result is null) return;
        Show("Preparing the center crop…");
        _progress.IsIndeterminate = true;
        var rendered = await RestorationService.PreviewAsync(_result, ReadBands(), token);
        token.ThrowIfCancellationRequested();
        if (_closed) return;
        _originalBitmap = ToBitmap(rendered.Original);
        _restoredBitmap = ToBitmap(rendered.Restored);
        _compare.IsEnabled = true;
        _compare.IsOn = false;
        _preview.Source = _restoredBitmap;
        _previewHint.Text = "Both views use the same display stretch.";
        _details.Text = rendered.Details;
        Show("Center preview ready. Save full FITS to process the entire image.", InfoBarSeverity.Success);
    }

    private static WriteableBitmap ToBitmap(RenderedImageData image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using Stream pixels = bitmap.PixelBuffer.AsStream();
        pixels.Write(image.Bgra);
        return bitmap;
    }

    private void Save() => Perform(async (progress, token) =>
    {
        if (!_sourceSelection.CanProcess || _sourceSelection.Path is not string input) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(input) + "-restored",
            DefaultFileExtension = ".fits", ShowOverwritePrompt = true,
        };
        picker.FileTypeChoices.Add("Linear FITS", [".fits"]);
        var selected = await picker.PickSaveFileAsync();
        if (selected is null || _closed) return;
        await EnsureRuntimeAsync(progress, token);
        var model = await EnsureModelAsync(progress, token);
        Show("Processing the full image…");
        await RestorationService.SaveAsync(input, selected.Path, model, ReadOptions(), ReadBands(), progress, token);
        if (_closed) return;
        _savedPath = selected.Path;
        _openOutput.Visibility = Visibility.Visible;
        Show($"Saved {Path.GetFileName(selected.Path)}.", InfoBarSeverity.Success);
    }, "Choose a destination for the restored FITS.");

    private void SetSourceLabel()
    {
        string? source = _sourceSelection.Path;
        _sourceLabel.Text = source is null ? "No image selected" : $"Current image: {Path.GetFileName(source)}";
        _browse.Content = source is null ? "Choose image…" : "Change image…";
        ToolTipService.SetToolTip(_sourceLabel, source);
    }

    private void MarkPreviewStale()
    {
        if (_preview.Source is not null) _previewHint.Text = "Amounts changed. Update preview to compare them.";
    }

    private void InvalidateResult()
    {
        _result?.Dispose();
        _result = null;
        _originalBitmap = null;
        _restoredBitmap = null;
        _compare.IsEnabled = false;
        _preview.Source = null;
        _previewHint.Text = "Preview the center tiles before processing the full image.";
        _details.Text = string.Empty;
        UpdateButtons();
    }

    private async void Perform(Func<IProgress<RestorationProgress>, CancellationToken, Task> action, string message, bool canCancel = true)
    {
        if (_closed || _operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        _cancel.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = canCancel;
        UpdateButtons();
        Show(message);
        var progress = new Progress<RestorationProgress>(update =>
        {
            if (_closed || _operation != operation || operation.IsCancellationRequested) return;
            Show(update.Message);
            _progress.IsIndeterminate = update.Fraction is null;
            if (update.Fraction is double fraction) _progress.Value = Math.Clamp(fraction, 0, 1) * 100;
        });
        try { await action(progress, operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { Show("Cancelled."); }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        finally
        {
            _operation = null;
            if (_closed) _result?.Dispose();
            else
            {
                _progress.Visibility = Visibility.Collapsed;
                _cancel.Visibility = Visibility.Collapsed;
                if (_sourceSelection.ApplyPending()) SourceChanged();
                UpdateButtons();
            }
        }
    }

    private void UpdateButtons()
    {
        bool idle = _operation is null;
        _run.IsEnabled = _save.IsEnabled = idle && _sourceSelection.CanProcess;
        _refresh.IsEnabled = idle && _result is not null;
        _verify.IsEnabled = _remove.IsEnabled = idle && SelectedModel is not null;
        _browse.IsEnabled = _download.IsEnabled = _import.IsEnabled = _model.IsEnabled = idle;
        _grain.IsEnabled = _mottle.IsEnabled = _scale.IsEnabled = idle;
        _precision.IsEnabled = _backend.IsEnabled = _tile.IsEnabled = _device.IsEnabled = idle;
    }

    private void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_closed) return;
        _status.Message = message;
        _status.Severity = severity;
    }
}
