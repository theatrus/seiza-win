using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Seiza.App.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Seiza.App;

/// <summary>
/// Owns process-wide services and independent document-window sessions.
/// </summary>
public partial class App : Application
{
    private static readonly HashSet<MainWindow> DocumentWindows = [];
    private static CatalogSettingsWindow? _catalogSettingsWindow;
    private static AppInstance? _mainInstance;
    private static AppActivationArguments? _initialActivation;
    private static string[] _initialCommandLinePaths = [];
    private static int _automaticUpdateCheckStarted;

    internal static UpdateController Updates { get; } = new();

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    internal static bool TryBeginAutomaticUpdateCheck() =>
        Interlocked.Exchange(ref _automaticUpdateCheckStarted, 1) == 0;

    internal static void ConfigureInitialActivation(
        AppInstance? mainInstance,
        AppActivationArguments? initialActivation,
        string[] commandLinePaths)
    {
        _mainInstance = mainInstance;
        _initialActivation = initialActivation;
        _initialCommandLinePaths = commandLinePaths;
    }

    public static MainWindow CreateDocumentWindow()
    {
        MainWindow window = new();
        DocumentWindows.Add(window);
        window.Closed += DocumentWindow_Closed;
        window.Activate();
        return window;
    }

    internal static async Task OpenDocumentPathsAsync(
        IEnumerable<string> paths,
        MainWindow? preferredWindow = null)
    {
        MainWindow? reusableWindow = preferredWindow is not null &&
            DocumentWindows.Contains(preferredWindow) &&
            !preferredWindow.HasDocument
                ? preferredWindow
                : null;

        foreach (string path in paths.Where(File.Exists))
        {
            string fullPath = Path.GetFullPath(path);
            MainWindow? existing = DocumentWindows.FirstOrDefault(window =>
                string.Equals(
                    window.CurrentPath,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Activate();
                continue;
            }

            MainWindow window = reusableWindow ?? CreateDocumentWindow();
            reusableWindow = null;
            await window.OpenPathAsync(fullPath);
            window.Activate();
        }
    }

    internal static async Task OpenFolderInWindowAsync(
        string path,
        MainWindow? preferredWindow = null)
    {
        MainWindow window = preferredWindow is not null &&
            DocumentWindows.Contains(preferredWindow) &&
            !preferredWindow.HasDocument
                ? preferredWindow
                : CreateDocumentWindow();
        await window.OpenFolderAsync(Path.GetFullPath(path));
        window.Activate();
    }

    public static void ShowCatalogSettings()
    {
        _catalogSettingsWindow ??= new CatalogSettingsWindow();
        _catalogSettingsWindow.Activate();
    }

    internal static void NotifyCatalogSettingsClosed(CatalogSettingsWindow window)
    {
        if (ReferenceEquals(_catalogSettingsWindow, window))
        {
            _catalogSettingsWindow = null;
        }
        DisposeUpdatesIfLastWindowClosed();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (_mainInstance is not null)
        {
            _mainInstance.Activated += MainInstance_Activated;
            _ = CommandLineActivationRelay.ListenAsync(
                _mainInstance.ProcessId,
                RelayedCommandLinePathsReceived);
        }
        _ = InitializeActivationAsync(args.Arguments);
    }

    private static async Task InitializeActivationAsync(string fallbackArguments)
    {
        try
        {
            if (_initialActivation is not null)
            {
                await HandleActivationAsync(
                    _initialActivation,
                    fallbackArguments,
                    useCommandLineFallback: true);
            }
            else if (_initialCommandLinePaths.Length > 0)
            {
                await OpenDocumentPathsAsync(_initialCommandLinePaths);
            }
            else
            {
                CreateDocumentWindow();
            }
        }
        catch (Exception)
        {
            // If AppInstance or the local relay is unavailable, preserve the
            // user's file-open request in this process instead of failing startup.
            if (_initialCommandLinePaths.Length > 0)
            {
                await OpenDocumentPathsAsync(_initialCommandLinePaths);
            }
            else if (DocumentWindows.Count == 0)
            {
                CreateDocumentWindow();
            }
        }
        finally
        {
            _initialActivation = null;
            _initialCommandLinePaths = [];
        }
    }

    private static void RelayedCommandLinePathsReceived(string[] paths)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            MainWindow? reusable = DocumentWindows.FirstOrDefault(window => !window.HasDocument);
            _ = OpenDocumentPathsAsync(paths, reusable);
        });
    }

    private static void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
            _ = HandleActivationAsync(args, string.Empty, useCommandLineFallback: false));
    }

    private static async Task HandleActivationAsync(
        AppActivationArguments activation,
        string fallbackArguments,
        bool useCommandLineFallback)
    {
        string[] paths = ActivationPaths(activation, fallbackArguments);
        if (paths.Length == 0 && useCommandLineFallback)
        {
            paths = CommandLineActivationRelay.GetSupportedPaths();
        }

        if (paths.Length > 0)
        {
            MainWindow? reusable = DocumentWindows.FirstOrDefault(window => !window.HasDocument);
            await OpenDocumentPathsAsync(paths, reusable);
        }
        else
        {
            CreateDocumentWindow();
        }
    }

    private static string[] ActivationPaths(
        AppActivationArguments activation,
        string fallbackArguments)
    {
        if (activation.Data is IFileActivatedEventArgs fileActivation)
        {
            return fileActivation.Files
                .OfType<StorageFile>()
                .Select(file => file.Path)
                .Where(path => File.Exists(path) && ImageFileService.IsSupportedImage(path))
                .ToArray();
        }

        string arguments = activation.Data is ILaunchActivatedEventArgs launch
            ? launch.Arguments
            : fallbackArguments;
        string candidate = arguments.Trim().Trim('"');
        return File.Exists(candidate) && ImageFileService.IsSupportedImage(candidate)
            ? [candidate]
            : [];
    }

    private static void DocumentWindow_Closed(object sender, WindowEventArgs args)
    {
        if (sender is MainWindow window)
        {
            window.Closed -= DocumentWindow_Closed;
            DocumentWindows.Remove(window);
        }
        DisposeUpdatesIfLastWindowClosed();
    }

    private static void DisposeUpdatesIfLastWindowClosed()
    {
        if (DocumentWindows.Count == 0 && _catalogSettingsWindow is null)
        {
            Updates.Dispose();
        }
    }
}
