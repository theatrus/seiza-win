using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Seiza.App.Services;
using Seiza.App.ViewModels;
using Windows.Graphics;

namespace Seiza.App;

public sealed partial class CatalogSettingsWindow : Window
{
    private bool _loaded;
    private bool _initializing = true;
    private readonly string? _settingsPath;

    public CatalogSettingsViewModel ViewModel { get; } = CatalogSettingsViewModel.Instance;

    public CatalogSettingsWindow() : this(null) { }

    internal CatalogSettingsWindow(string? settingsPath)
    {
        _settingsPath = settingsPath;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ContentRoot.Loaded += ContentRoot_Loaded;
        Closed += CatalogSettingsWindow_Closed;
        AutomaticUpdateChecksToggle.IsOn =
            CatalogSettingsStore.LoadAutomaticallyCheckForUpdates();
        UseStackingGpuToggle.IsOn = CatalogSettingsStore.LoadUseGpuForStacking(_settingsPath);
        StackingGpuMemoryBox.Value = CatalogSettingsStore.LoadStackingGpuMemoryMib(_settingsPath) ?? 0;
        StackingGpuMemoryBox.IsEnabled = UseStackingGpuToggle.IsOn;
        RefreshRcAstroPath();
        UpdateVisualState();
        _initializing = false;
    }

    private async void ContentRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        SizeAndCenterWindow();
        await ViewModel.RefreshAsync();
    }

    private void RefreshRcAstroPath()
    {
        string? path = CatalogSettingsStore.LoadRcAstroExecutable();
        RcAstroPathText.Text = path is null ? "Not configured" :
            File.Exists(path) ? path : $"{path} (not found; choose its new location)";
    }

    private async void ChooseRcAstro_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFileAsync() is { } file)
            {
                CatalogSettingsStore.SaveRcAstroExecutable(file.Path);
                RefreshRcAstroPath();
                RcAstroErrorBar.IsOpen = false;
            }
        }
        catch (Exception error)
        {
            RcAstroErrorBar.Message = error.Message;
            RcAstroErrorBar.IsOpen = true;
        }
    }

    private void ClearRcAstro_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CatalogSettingsStore.SaveRcAstroExecutable(null);
            RefreshRcAstroPath();
            RcAstroErrorBar.IsOpen = false;
        }
        catch (Exception error)
        {
            RcAstroErrorBar.Message = error.Message;
            RcAstroErrorBar.IsOpen = true;
        }
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        await ViewModel.ChooseDirectoryAsync(handle);
    }

    private async void UseDefault_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.UseDefaultDirectoryAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshAsync();

    private async void Install_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.StartSetupAsync();

    private async void InstallParallax_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.StartParallaxSetupAsync();

    private void AutomaticUpdateChecks_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
            CatalogSettingsStore.SaveAutomaticallyCheckForUpdates(AutomaticUpdateChecksToggle.IsOn);
    }

    private void StackingGpu_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            CatalogSettingsStore.SaveUseGpuForStacking(UseStackingGpuToggle.IsOn, _settingsPath);
            StackingGpuErrorBar.IsOpen = false;
        }
        catch (Exception error)
        {
            _initializing = true;
            try { UseStackingGpuToggle.IsOn = CatalogSettingsStore.LoadUseGpuForStacking(_settingsPath); }
            finally { _initializing = false; }
            StackingGpuErrorBar.Message = $"Could not save stacking GPU preference: {error.Message}";
            StackingGpuErrorBar.IsOpen = true;
        }
        StackingGpuMemoryBox.IsEnabled = UseStackingGpuToggle.IsOn;
    }

    private void StackingGpuMemory_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing) return;
        try
        {
            double value = sender.Value;
            if (!double.IsFinite(value) || value != Math.Truncate(value) ||
                value is < 0 or > ImageStackOptions.MaximumGpuMemoryMib)
                throw new ArgumentException("Choose a whole MiB value, or 0 for automatic.");
            CatalogSettingsStore.SaveStackingGpuMemoryMib(value == 0 ? null : checked((int)value), _settingsPath);
            StackingGpuErrorBar.IsOpen = false;
        }
        catch (Exception error)
        {
            _initializing = true;
            try { sender.Value = CatalogSettingsStore.LoadStackingGpuMemoryMib(_settingsPath) ?? 0; }
            finally { _initializing = false; }
            StackingGpuErrorBar.Message = $"Could not save GPU memory budget: {error.Message}";
            StackingGpuErrorBar.IsOpen = true;
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await App.Updates.CheckForUpdatesAsync(ContentRoot, userInitiated: true);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        UpdateVisualState();

    private void UpdateVisualState()
    {
        UseDefaultButton.Visibility = ViewModel.IsUsingCustomDirectory
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProgressPanel.Visibility = ViewModel.HasProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorInfoBar.IsOpen = !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
    }

    private void SizeAndCenterWindow()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        double scale = ContentRoot.XamlRoot.RasterizationScale;
        int margin = Math.Max(24, (int)Math.Round(24 * scale));
        int width = Math.Min((int)Math.Round(820 * scale), workArea.Width - (margin * 2));
        int height = Math.Min((int)Math.Round(820 * scale), workArea.Height - (margin * 2));
        int x = workArea.X + ((workArea.Width - width) / 2);
        int y = workArea.Y + ((workArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void CatalogSettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        App.NotifyCatalogSettingsClosed(this);
    }
}
