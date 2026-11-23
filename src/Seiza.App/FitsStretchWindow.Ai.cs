using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;

namespace Seiza.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Window.Closed cancels model operations; each operation disposes its cancellation source in finally.")]
public sealed partial class FitsStretchWindow
{
    private AiProcessing? _ai;
    private AiProcessing _retainedAi = new();
    private ProcessingRegion? _aiRegion;
    private bool _modelBusy;
    private CancellationTokenSource? _modelCancellation;
    private ComboBox _aiModel = new() { Header = "Model", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _aiModelStatus = new() { TextWrapping = TextWrapping.Wrap };

    internal AiProcessing? ResultAi => _ai;
    internal Action? PickPreviewRegionRequested { get; set; }
    internal Func<AiProcessing, IProgress<RestorationProgress>, CancellationToken, Task<bool>>? SaveLinearAiRequested { get; set; }

    private void InitializeAi(AiProcessing? ai, ProcessingRegion region)
    {
        _ai = ai;
        _retainedAi = ai ?? new();
        _aiRegion = region;
        _isUpdating = true;
        AiToggle.IsOn = ai is not null;
        _isUpdating = false;
        BuildAiControls();
    }

    private void AiToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _ai = AiToggle.IsOn ? _retainedAi : null;
        BuildAiControls();
        DraftChanged();
    }

    private void ChangeAi(Func<AiProcessing, AiProcessing> change)
    {
        _retainedAi = change(_retainedAi);
        if (_ai is not null) _ai = _retainedAi;
        DraftChanged();
    }

    private void BuildAiControls()
    {
        AiParameterPanel.Children.Clear();
        AiParameterPanel.Visibility = _ai is null ? Visibility.Collapsed : Visibility.Visible;
        if (_ai is null) return;
        void Strength(string label, double value, double min, double max, Action<double> changed)
        {
            var slider = new Slider { Header = $"{label} ({value:P0})", Minimum = min, Maximum = max, Value = value, StepFrequency = 0.01 };
            slider.ValueChanged += (_, args) => { slider.Header = $"{label} ({args.NewValue:P0})"; changed(args.NewValue); };
            AiParameterPanel.Children.Add(slider);
        }
        Strength("Grain reduction", _ai.High, 0, 1, value => ChangeAi(ai => ai with { High = value }));
        Strength("Mottle reduction", _ai.Low, 0, 1, value => ChangeAi(ai => ai with { Low = value }));
        var saveLinear = new Button { Content = "Save linear AI FITS…" };
        saveLinear.Click += async (_, _) => await ModelActionAsync(async token =>
        {
            if (SaveLinearAiRequested is not null)
            {
                bool saved = await SaveLinearAiRequested(_retainedAi, new Progress<RestorationProgress>(step => SetPreviewStatus(true, step.Message)), token);
                SetPreviewStatus(false, saved ? "Linear AI image saved." : string.Empty);
            }
        });
        AiParameterPanel.Children.Add(new TextBlock { Text = "Export saves all processing steps. Save linear AI FITS keeps only noise reduction, before background correction and stretch.", TextWrapping = TextWrapping.Wrap });
        AiParameterPanel.Children.Add(saveLinear);
        var regionText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _regionText = regionText;
        RefreshRegionLabel();
        AiParameterPanel.Children.Add(regionText);
        var select = new Button { Content = "Select preview rectangle…" };
        select.Click += (_, _) =>
        {
            CancelPreview();
            AppWindow.Hide();
            PickPreviewRegionRequested?.Invoke();
        };
        AiParameterPanel.Children.Add(select);
        var advanced = new StackPanel { Spacing = 10 };
        // A fresh panel owns each control; do not reuse a parented ComboBox.
        if (_aiModel.Parent is Panel old) old.Children.Remove(_aiModel);
        if (_aiModelStatus.Parent is Panel oldStatus) oldStatus.Children.Remove(_aiModelStatus);
        _aiModel = new ComboBox { Header = "Model", HorizontalAlignment = HorizontalAlignment.Stretch };
        _aiModel.ItemsSource = new[] { new AiModelChoice(null) }.Concat(_retainedAi.Model is { } name ? [new AiModelChoice(name)] : []);
        _aiModel.SelectedIndex = _retainedAi.Model is null ? 0 : 1;
        _aiModel.SelectionChanged += (_, _) => { if (!_isUpdating && _aiModel.SelectedItem is AiModelChoice choice) ChangeAi(ai => ai with { Model = choice.Name }); };
        advanced.Children.Add(_aiModel);
        var backend = new ComboBox { Header = "Device", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Automatic", "DirectML", "CPU" } };
        backend.SelectedIndex = _retainedAi.Backend switch { "directml" => 1, "cpu" => 2, _ => 0 };
        backend.SelectionChanged += (_, _) => ChangeAi(ai => ai with { Backend = backend.SelectedIndex switch { 1 => "directml", 2 => "cpu", _ => null } });
        advanced.Children.Add(backend);
        var precision = new ComboBox { Header = "Precision", ItemsSource = new[] { "Automatic", "32-bit float", "16-bit float" } };
        precision.SelectedIndex = _retainedAi.Precision switch { "f32" => 1, "f16" => 2, _ => 0 };
        precision.SelectionChanged += (_, _) => ChangeAi(ai => ai with { Precision = precision.SelectedIndex switch { 1 => "f32", 2 => "f16", _ => null } });
        advanced.Children.Add(precision);
        void Number(string label, double value, double min, double max, Action<double> changed)
        {
            var box = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            box.ValueChanged += (_, args) => { if (double.IsFinite(args.NewValue)) changed(args.NewValue); };
            advanced.Children.Add(box);
        }
        Number("Noise scale (pixels)", _retainedAi.Scale, 0.1, 100, value => ChangeAi(ai => ai with { Scale = value }));
        Number("Tile size (0 = automatic)", _retainedAi.Tile ?? 0, 0, 4096, value => ChangeAi(ai => ai with { Tile = value == 0 ? null : (int)value }));
        Number("DirectML device index (0 = first GPU)", _retainedAi.DeviceId ?? 0, 0, 32, value => ChangeAi(ai => ai with { DeviceId = (int)value }));
        var refresh = new Button { Content = "Check models for updates" };
        refresh.Click += async (_, _) => await ModelActionAsync(async token =>
        {
            var catalog = await RestorationService.FetchCatalogAsync(token);
            var installed = await RestorationService.ListAsync();
            _isUpdating = true;
            try
            {
                var choices = new[] { new AiModelChoice(null) }.Concat(catalog.Models.Select(model => model.Name)
                    .Concat(installed.Select(model => model.Name)).Append(_retainedAi.Model).Where(name => name is not null)
                    .Distinct().Order().Select(name => new AiModelChoice(name))).ToArray();
                _aiModel.ItemsSource = choices;
                _aiModel.SelectedItem = choices.First(choice => choice.Name == _retainedAi.Model);
            }
            finally { _isUpdating = false; }
            AiProcessingService.RefreshAutomaticModel();
            _aiModelStatus.Text = "Catalog updated. The selected model installs automatically when needed.";
        });
        advanced.Children.Add(refresh);
        var update = new Button { Content = "Download / update selected model" };
        update.Click += async (_, _) => await ModelActionAsync(async token =>
        {
            var model = await RestorationService.DownloadAsync(_retainedAi.Model, ModelProgress(), token);
            AiProcessingService.RefreshAutomaticModel();
            _aiModelStatus.Text = $"{model.Name} is installed.";
            DraftChanged();
        });
        advanced.Children.Add(update);
        var import = new Button { Content = "Import model folder…" };
        import.Click += async (_, _) => await ModelActionAsync(async token =>
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var model = await RestorationService.ImportAsync(folder.Path, token);
            ChangeAi(ai => ai with { Model = model.Name });
            BuildAiControls();
            _aiModelStatus.Text = $"Using {model.Name}.";
        });
        advanced.Children.Add(import);
        var verify = new Button { Content = "Verify installed model" };
        verify.Click += async (_, _) => await ModelActionAsync(async token =>
        {
            var installed = await RestorationService.ListAsync();
            var selected = installed.FirstOrDefault(model => model.Name == _retainedAi.Model)
                ?? throw new InvalidOperationException("Select an installed model to verify.");
            token.ThrowIfCancellationRequested();
            await RestorationService.VerifyAsync(selected);
            _aiModelStatus.Text = $"Verified {selected.Name}.";
        });
        advanced.Children.Add(verify);
        var remove = new Button { Content = "Remove installed model…" };
        remove.Click += async (_, _) => await ModelActionAsync(async token =>
        {
            var installed = await RestorationService.ListAsync();
            var selected = installed.FirstOrDefault(model => model.Name == _retainedAi.Model)
                ?? throw new InvalidOperationException("Select an installed model to remove.");
            var confirm = new ContentDialog { XamlRoot = ContentRoot.XamlRoot, Title = $"Remove {selected.Name}?",
                Content = "The downloaded model will be removed. It can be downloaded again later.",
                PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            await RestorationService.RemoveAsync(selected);
            AiToggle.IsOn = false;
        });
        advanced.Children.Add(remove);


        advanced.Children.Add(_aiModelStatus);
        AiParameterPanel.Children.Add(new Expander { Header = "Model and device settings", Content = advanced,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }

    private TextBlock? _regionText;
    private void RefreshRegionLabel()
    {
        if (_regionText is not null && _aiRegion is { } region)
            _regionText.Text = $"Preview: {region.Width:N0} × {region.Height:N0} pixels at ({region.X:N0}, {region.Y:N0}). Saving applies AI to the full image.";
    }

    internal void ApplyPreviewRegion(ProcessingRegion region)
    {
        _aiRegion = region;
        RefreshRegionLabel();
        AppWindow.Show();
        Activate();
        DraftChanged();
    }

    private Progress<RestorationProgress> ModelProgress() => new Progress<RestorationProgress>(step => _aiModelStatus.Text = step.Message);
    private async Task ModelActionAsync(Func<CancellationToken, Task> action)
    {
        if (_modelBusy) return;
        _modelBusy = true;
        _modelCancellation = new();
        try { await action(_modelCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _aiModelStatus.Text = error.Message; SetPreviewStatus(false, error.Message); }
        finally { _modelBusy = false; _modelCancellation?.Dispose(); _modelCancellation = null; }
    }

    private sealed record AiModelChoice(string? Name)
    {
        public override string ToString() => Name ?? "Automatic (recommended model)";
    }
}
