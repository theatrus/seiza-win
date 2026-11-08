using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Services;
using Windows.Graphics;

namespace Seiza.App;

public sealed class ColorStackWindow : Window, IDisposable
{
    private readonly ComboBox _mode = new() { Header = "Input", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _channels = new() { Spacing = 8 };
    private readonly TextBox _red = new() { Header = "Red stack" };
    private readonly TextBox _green = new() { Header = "Green stack" };
    private readonly TextBox _blue = new() { Header = "Blue stack" };
    private readonly TextBox _luminance = new() { Header = "Luminance stack (optional)" };
    private readonly TextBox _rgb = new() { Header = "Existing linear RGB / OSC stack" };
    private readonly CheckBox _aligned = new() { Content = "These channels share a pixel grid and orientation" };
    private readonly CheckBox _calibrate = new() { Content = "Calibrate broadband RGB against Gaia before stretching", IsChecked = true };
    private readonly CheckBox _solve = new() { Content = "Solve / verify the RGB image before Gaia calibration", IsChecked = true };
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false, Message = "Choose aligned stacks, or an existing linear RGB stack. Inputs are never modified." };
    private readonly StackPanel _inputs = new() { Spacing = 12 };
    private readonly ContentControl _inputHost = new();
    private readonly Button _run = new() { Content = "Save linear color image…" };
    private readonly Button _install = new() { Content = "Download offline Gaia photometry catalog…" };
    private readonly Button _open = new() { Content = "Open result / preview / export…", IsEnabled = false };
    private readonly ProgressRing _progress = new() { IsActive = false, Width = 24, Height = 24 };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _closed;
    private string? _output;

    public ColorStackWindow(string? initialRgb = null)
    {
        Title = "Color stacking — Seiza";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(760, 920));
        _mode.Items.Add("Combine aligned R / G / B (optional L)");
        _mode.Items.Add("Calibrate an existing RGB / OSC stack");
        _inputs.Children.Add(_mode);
        _channels.Children.Add(Picker(_red)); _channels.Children.Add(Picker(_green)); _channels.Children.Add(Picker(_blue));
        _channels.Children.Add(Picker(_luminance)); _channels.Children.Add(_aligned);
        _inputs.Children.Add(_channels);
        var rgbPanel = Picker(_rgb); _inputs.Children.Add(rgbPanel);
        _mode.SelectionChanged += (_, _) =>
        {
            _channels.Visibility = _mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            rgbPanel.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        _mode.SelectedIndex = initialRgb is null ? 0 : 1;
        _rgb.Text = initialRgb ?? string.Empty;
        _inputs.Children.Add(new TextBlock { Text = "Use a shared alignment reference for channel stacks. Matching dimensions alone do not imply alignment. Gaia is intended for broadband RGB, not narrowband palettes. Solving uses your installed solve catalogs; color calibration needs the separate offline photometry catalog. If solving is off, a valid WCS must already be in the input headers.", TextWrapping = TextWrapping.Wrap });
        _inputs.Children.Add(_calibrate); _inputs.Children.Add(_solve); _inputs.Children.Add(_install); _inputs.Children.Add(_run);
        _inputs.Children.Add(new TextBlock { Text = "Calibrated results stay linear. On reopening, Seiza uses linked RGB (or luminance-preserving) display stretches, including exports, to keep the calibrated balance.", TextWrapping = TextWrapping.Wrap });
        _calibrate.Checked += (_, _) => _solve.IsEnabled = true;
        _calibrate.Unchecked += (_, _) => _solve.IsEnabled = false;
        var root = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "Compose and calibrate color", FontSize = 24 });
        var advanced = new Button { Content = "Narrowband, continuum and RGB stars…" };
        advanced.Click += (_, _) => App.ShowColorTools();
        root.Children.Add(advanced);
        _inputHost.Content = _inputs;
        root.Children.Add(_inputHost); root.Children.Add(_progress); root.Children.Add(_status); root.Children.Add(_open);
        Content = new ScrollViewer { Content = root };
        _run.Click += Run_Click; _install.Click += Install_Click;
        _open.Click += async (_, _) => { if (_output is not null) await App.OpenDocumentPathsAsync([_output]); };
        Closed += (_, _) => Dispose();
    }

    private Grid Picker(TextBox field)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(field);
        var button = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(button, 1); grid.Children.Add(button);
        button.Click += async (_, _) =>
        {
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                foreach (string extension in ImageFileTypes.AstronomyExtensions) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync();
                if (!_closed && file is not null) field.Text = file.Path;
            }
            catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        };
        return grid;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            { SuggestedFileName = "color-stack", DefaultFileExtension = ".fits", ShowOverwritePrompt = true };
            picker.FileTypeChoices.Add("Linear FITS", [".fits"]); picker.FileTypeChoices.Add("Linear XISF", [".xisf"]);
            var file = await picker.PickSaveFileAsync();
            if (_closed || file is null) return;
            Show("Composing / calibrating. The native operation finishes safely before cancellation is observed.", InfoBarSeverity.Informational);
            var result = await ColorWorkflowService.RunAsync(new(_red.Text, _green.Text, _blue.Text,
                string.IsNullOrWhiteSpace(_luminance.Text) ? null : _luminance.Text, _mode.SelectedIndex == 1 ? _rgb.Text : null,
                file.Path, _aligned.IsChecked == true, _calibrate.IsChecked == true, CatalogSettingsStore.LoadCatalogDirectory(), _solve.IsChecked == true), _lifetime.Token);
            if (!_closed)
            {
                _output = result.Output; _open.IsEnabled = _output is not null;
                Show(result.Message, result.Output is null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            string? directory = CatalogSettingsStore.LoadCatalogDirectory();
            await Task.Run(() => ParallaxCatalogService.InstallPhotometry(directory, update => DispatcherQueue.TryEnqueue(() => Show(update.Message, InfoBarSeverity.Informational))));
            Show("Offline Gaia photometry catalog installed and verified.", InfoBarSeverity.Success);
        }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); }
    }

    private void Show(string message, InfoBarSeverity severity)
    { if (!_closed) { _status.Message = message; _status.Severity = severity; } }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed) { if (!busy) _lifetime.Dispose(); return; }
        _inputHost.IsEnabled = !busy; _open.IsEnabled = !busy && _output is not null; _progress.IsActive = busy;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        if (!_busy) _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
