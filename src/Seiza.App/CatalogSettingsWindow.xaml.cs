using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Seiza.App.ViewModels;
using Windows.Graphics;

namespace Seiza.App;

public sealed partial class CatalogSettingsWindow : Window
{
    private bool _loaded;

    public CatalogSettingsViewModel ViewModel { get; } = CatalogSettingsViewModel.Instance;

    public CatalogSettingsWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ContentRoot.Loaded += ContentRoot_Loaded;
        Closed += CatalogSettingsWindow_Closed;
        UpdateVisualState();
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
