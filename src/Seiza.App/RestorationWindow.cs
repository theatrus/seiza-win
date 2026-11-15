using System.Runtime.InteropServices.WindowsRuntime;
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
    private readonly Button _runtime = new() { Content = "Install ONNX Runtime from Seiza" };
    private readonly TextBlock _runtimeStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _download = new() { Content = "Download latest model" };
    private readonly Button _import = new() { Content = "Import model folder…" };
    private readonly Button _verify = new() { Content = "Verify model" };
    private readonly Button _remove = new() { Content = "Remove model…" };
    private readonly Expander _setup = new() { Header = "Model and runtime setup", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Slider _grain = new() { Header = "Grain reduction · 70%", Minimum = 0, Maximum = 100, Value = 70, StepFrequency = 1 };
    private readonly Slider _mottle = new() { Header = "Mottle reduction · 90%", Minimum = 0, Maximum = 100, Value = 90, StepFrequency = 1 };
    private readonly ComboBox _backend = new() { Header = "Backend", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _precision = new() { Header = "Precision", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _tile = new() { Header = "Tile size (px, 0 = automatic)", Value = 0, Minimum = 0, Maximum = 4096, SmallChange = 16 };
    private readonly NumberBox _scale = new() { Header = "Grain / mottle scale (px)", Value = 5, Minimum = 0.25, Maximum = 64, SmallChange = 0.25 };
    private readonly Button _run = new() { Content = "Restore preview" };
    private readonly Button _refresh = new() { Content = "Update preview" };
    private readonly Button _save = new() { Content = "Save restored FITS…" };
    private readonly Button _cancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly Button _openOutput = new() { Content = "Open saved image", Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 440 };
    private readonly ToggleSwitch _compare = new() { Header = "Center crop", OffContent = "Restored", OnContent = "Original", IsEnabled = false };
    private readonly TextBlock _previewHint = new() { Text = "Restore a linear image to compare its center crop.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _root = new() { RowSpacing = 12, Padding = new Thickness(20) };
    private CancellationTokenSource? _operation;
    private SafeRestorationHandle? _result;
    private ImageSource? _originalBitmap;
    private ImageSource? _restoredBitmap;
    private string? _source;
    private string? _savedPath;
    private bool _runtimeReady;
    private bool _closed;

    internal RestorationWindow(string? source)
    {
        Title = "Restore image — Seiza";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _source = source;
        SetSourceLabel();

        var setup = new StackPanel { Spacing = 8 };
        setup.Children.Add(_runtimeStatus);
        setup.Children.Add(_runtime);
        setup.Children.Add(_download);
        setup.Children.Add(_import);
        setup.Children.Add(Buttons(_verify, _remove));
        _setup.Content = setup;

        var advanced = new StackPanel { Spacing = 8 };
        advanced.Children.Add(_backend);
        advanced.Children.Add(_precision);
        advanced.Children.Add(_tile);
        advanced.Children.Add(_scale);
        _backend.Items.Add(new RestorationBackend("", "Automatic"));
        _backend.SelectedIndex = 0;

        var settings = new StackPanel { Spacing = 12 };
        settings.Children.Add(_sourceLabel);
        settings.Children.Add(_browse);
        settings.Children.Add(_model);
        settings.Children.Add(_setup);
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
        _runtime.Click += (_, _) => Perform(async (progress, token) =>
        {
            await RestorationRuntime.InstallAsync(progress, token);
            token.ThrowIfCancellationRequested();
            await RefreshRuntimeAsync();
            Show("ONNX Runtime is ready.", InfoBarSeverity.Success);
        }, "Downloading ONNX Runtime…");
        _download.Click += (_, _) => Perform(async (progress, token) =>
        {
            var installed = await RestorationService.DownloadLatestAsync(progress, token);
            await RefreshModelsAsync(installed.CardHash);
            Show($"{installed.Name} is installed and selected.", InfoBarSeverity.Success);
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
            UpdatePrecisions();
            InvalidateResult();
            if (SelectedModel is { } model)
            {
                try { CatalogSettingsStore.SavePreferredNoiseModel(model.Name); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        };
        _backend.SelectionChanged += (_, _) => InvalidateResult();
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
        Perform(async (_, _) =>
        {
            await RefreshModelsAsync();
            await RefreshRuntimeAsync();
            if (_closed) return;
            _setup.IsExpanded = !_runtimeReady || SelectedModel is null;
            Show(_setup.IsExpanded ? "Install a noise model and ONNX Runtime to get started."
                : _source is null ? "Choose a linear mono or RGB image." : "Ready to restore the selected image.");
        }, "Checking restoration setup…", canCancel: false);
    }

    private InstalledNoiseModel? SelectedModel => _model.SelectedItem as InstalledNoiseModel;

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
        if (_operation is not null || source is null || string.Equals(source, _source, StringComparison.OrdinalIgnoreCase)) return;
        _source = source;
        SetSourceLabel();
        InvalidateResult();
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (Button button in buttons) panel.Children.Add(button);
        return panel;
    }

    private async Task RefreshModelsAsync(string? preferred = null)
    {
        preferred ??= SelectedModel?.CardHash;
        string? saved = CatalogSettingsStore.LoadPreferredNoiseModel();
        var models = await RestorationService.ListAsync();
        if (_closed) return;
        _model.ItemsSource = models;
        _model.SelectedItem = models.FirstOrDefault(model => model.CardHash == preferred)
            ?? models.FirstOrDefault(model => model.Name == saved) ?? (models.Count == 0 ? null : models[0]);
        _download.Content = models.Count == 0 ? "Download latest model" : "Download model update";
    }

    private async Task RefreshRuntimeAsync()
    {
        _runtimeReady = false;
        if (!RestorationRuntime.CanLoad)
        {
            _runtimeStatus.Text = "ONNX Runtime is required for restoration.";
            return;
        }
        var (version, backends) = await RestorationService.BackendsAsync();
        if (_closed) return;
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
        _source = selected.Path;
        SetSourceLabel();
        InvalidateResult();
        Show("Image selected.");
    }, "Choose a linear FITS or XISF image.", canCancel: false);

    private void ImportModel() => Perform(async (_, token) =>
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        var selected = await picker.PickSingleFolderAsync();
        if (selected is null || _closed) return;
        Show("Verifying and installing the model…");
        var installed = await RestorationService.ImportAsync(selected.Path, token);
        await RefreshModelsAsync(installed.CardHash);
        Show($"{installed.Name} is installed and selected.", InfoBarSeverity.Success);
    }, "Choose the folder containing card.json.", canCancel: false);

    private void RemoveModel() => Perform(async (_, _) =>
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
        await RefreshModelsAsync();
        Show($"{model.Name} was removed.");
    }, "Remove a model from this app.", canCancel: false);

    private void Run() => Perform(async (progress, token) =>
    {
        if (_source is null || SelectedModel is not { } model) return;
        double tile = _tile.Value;
        if (!double.IsFinite(tile) || tile != Math.Truncate(tile))
            throw new InvalidOperationException("Enter a whole tile size or 0 for automatic.");
        string? backend = (_backend.SelectedItem as RestorationBackend)?.Name;
        string? precision = (_precision.SelectedItem as RestorationBackend)?.Name;
        var options = new RestorationOptions(backend is "" ? null : backend, precision is "" ? null : precision, tile == 0 ? null : (int)tile);
        InvalidateResult();
        _result = await RestorationService.RunAsync(_source, model, options, progress, token);
        token.ThrowIfCancellationRequested();
        await UpdatePreviewAsync(token);
    }, "Reading the linear image…");

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
        Show("Preview ready. Adjust the amounts or save a restored FITS.", InfoBarSeverity.Success);
    }

    private static WriteableBitmap ToBitmap(RenderedImageData image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using Stream pixels = bitmap.PixelBuffer.AsStream();
        pixels.Write(image.Bgra);
        return bitmap;
    }

    private void Save() => Perform(async (_, token) =>
    {
        if (_source is not string input || _result is null) return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(input) + "-restored",
            DefaultFileExtension = ".fits", ShowOverwritePrompt = true,
        };
        picker.FileTypeChoices.Add("Linear FITS", [".fits"]);
        var selected = await picker.PickSaveFileAsync();
        if (selected is null || _closed) return;
        Show("Saving the full linear image…");
        await RestorationService.SaveAsync(_result, ReadBands(), input, selected.Path, token);
        if (_closed) return;
        _savedPath = selected.Path;
        _openOutput.Visibility = Visibility.Visible;
        Show($"Saved {Path.GetFileName(selected.Path)}.", InfoBarSeverity.Success);
    }, "Choose a destination for the restored FITS.");

    private void SetSourceLabel()
    {
        _sourceLabel.Text = _source is null ? "Choose a linear FITS or XISF stack." : Path.GetFileName(_source);
        ToolTipService.SetToolTip(_sourceLabel, _source);
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
        _previewHint.Text = "Restore a linear image to compare its center crop.";
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
                UpdateButtons();
            }
        }
    }

    private void UpdateButtons()
    {
        bool idle = _operation is null;
        _run.IsEnabled = idle && _runtimeReady && _source is not null && SelectedModel is not null;
        _refresh.IsEnabled = _save.IsEnabled = idle && _result is not null;
        _runtime.IsEnabled = idle && !_runtimeReady;
        _verify.IsEnabled = _remove.IsEnabled = idle && SelectedModel is not null;
        _browse.IsEnabled = _download.IsEnabled = _import.IsEnabled = _model.IsEnabled = idle;
        _grain.IsEnabled = _mottle.IsEnabled = _scale.IsEnabled = idle;
        _precision.IsEnabled = _backend.IsEnabled = _tile.IsEnabled = idle;
    }

    private void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (_closed) return;
        _status.Message = message;
        _status.Severity = severity;
    }
}
