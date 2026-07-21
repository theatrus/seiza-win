using CommunityToolkit.Mvvm.ComponentModel;
using Seiza.App.Models;
using Seiza.App.Services;

namespace Seiza.App.ViewModels;

public sealed record CatalogPresetOption(
    CatalogSetupPreset Preset,
    string Name,
    string Description);

public partial class CatalogSettingsViewModel : ObservableObject
{
    private const string ReadyGlyph = "\uE73E";
    private const string MissingGlyph = "\uE711";
    private const string CheckingGlyph = "\uE895";

    private int _statusGeneration;
    private string? _configuredDirectory = CatalogSettingsStore.LoadCatalogDirectory();

    public static CatalogSettingsViewModel Instance { get; } = new();

    public IReadOnlyList<CatalogPresetOption> Presets { get; } =
    [
        new(
            CatalogSetupPreset.StandardBlind,
            "Standard (recommended)",
            "G≤17 stars, blind index, and all overlay catalogs."),
        new(
            CatalogSetupPreset.DeepestBlind,
            "Deep",
            "G≤20 stars for dense fields, plus blind index and overlays."),
        new(
            CatalogSetupPreset.All,
            "Everything",
            "Every published Seiza star catalog, index, and overlay catalog."),
    ];

    [ObservableProperty]
    public partial CatalogPresetOption SelectedPreset { get; set; }

    [ObservableProperty]
    public partial CatalogStatus? Status { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasProgress { get; set; }

    [ObservableProperty]
    public partial string SetupMessage { get; set; } = "Catalog setup has not run yet.";

    [ObservableProperty]
    public partial string SetupDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    private CatalogSettingsViewModel()
    {
        SelectedPreset = Presets[0];
    }

    public string DirectoryText =>
        Status?.Directory ?? _configuredDirectory ?? "Checking the default location…";

    public string DirectoryModeText => _configuredDirectory is null
        ? "Using Seiza’s default catalog location"
        : "Using a custom catalog location";

    public bool IsUsingCustomDirectory => _configuredDirectory is not null;

    public bool CanChangeDirectory => !IsRunning;

    public bool CanRefresh => !IsRefreshing && !IsRunning;

    public bool CanStartSetup => !IsRefreshing && !IsRunning;

    public string SetupButtonText => Status is { ReadyForSolving: true, ReadyForOverlays: true }
        ? "Verify and repair"
        : "Install catalogs";

    public string SolvingState => ReadinessState(Status?.ReadyForSolving, "Ready to solve", "Catalogs required");

    public string SolvingGlyph => ReadinessGlyph(Status?.ReadyForSolving);

    public string OverlayState => ReadinessState(Status?.ReadyForOverlays, "Overlays ready", "Catalogs required");

    public string OverlayGlyph => ReadinessGlyph(Status?.ReadyForOverlays);

    public string StarCatalogState => ComponentState(Status?.StarCatalog);

    public string StarCatalogPath => ComponentPath(Status?.StarCatalog);

    public string StarCatalogGlyph => ComponentGlyph(Status?.StarCatalog);

    public string BlindIndexState => ComponentState(Status?.BlindIndex);

    public string BlindIndexPath => ComponentPath(Status?.BlindIndex);

    public string BlindIndexGlyph => ComponentGlyph(Status?.BlindIndex);

    public string ObjectsState => ComponentState(Status?.Objects);

    public string ObjectsPath => ComponentPath(Status?.Objects);

    public string ObjectsGlyph => ComponentGlyph(Status?.Objects);

    public string TransientsState => ComponentState(Status?.Transients);

    public string TransientsPath => ComponentPath(Status?.Transients);

    public string TransientsGlyph => ComponentGlyph(Status?.Transients);

    public string MinorBodiesState => ComponentState(Status?.MinorBodies);

    public string MinorBodiesPath => ComponentPath(Status?.MinorBodies);

    public string MinorBodiesGlyph => ComponentGlyph(Status?.MinorBodies);

    public async Task RefreshAsync()
    {
        int generation = Interlocked.Increment(ref _statusGeneration);
        IsRefreshing = true;
        ErrorMessage = null;
        try
        {
            string? directory = _configuredDirectory;
            CatalogStatus status = await Task.Run(() => SeizaCore.GetCatalogStatus(directory));
            if (generation == _statusGeneration)
            {
                Status = status;
            }
        }
        catch (Exception exception) when (generation == _statusGeneration)
        {
            ErrorMessage = DescribeException(exception);
        }
        finally
        {
            if (generation == _statusGeneration)
            {
                IsRefreshing = false;
            }
        }
    }

    public async Task ChooseDirectoryAsync(nint ownerWindow)
    {
        if (!CanChangeDirectory)
        {
            return;
        }

        try
        {
            string? path = await CatalogDirectoryPicker.PickAsync(ownerWindow);
            if (path is null)
            {
                return;
            }

            CatalogSettingsStore.SaveCatalogDirectory(path);
            _configuredDirectory = path;
            NotifyDirectoryChanged();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = DescribeException(exception);
        }
    }

    public async Task UseDefaultDirectoryAsync()
    {
        if (!CanChangeDirectory || _configuredDirectory is null)
        {
            return;
        }

        try
        {
            CatalogSettingsStore.SaveCatalogDirectory(null);
            CatalogDirectoryPicker.ClearAccess();
            _configuredDirectory = null;
            NotifyDirectoryChanged();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = DescribeException(exception);
        }
    }

    public async Task StartSetupAsync()
    {
        if (!CanStartSetup)
        {
            return;
        }

        string? directory = _configuredDirectory;
        CatalogSetupPreset preset = SelectedPreset.Preset;
        IsRunning = true;
        HasProgress = true;
        ErrorMessage = null;
        SetupMessage = "Preparing catalog setup…";
        SetupDetail = "This can continue while the Settings window is closed.";
        ProgressPercent = 0;
        IsProgressIndeterminate = true;

        try
        {
            await Task.Run(() => SeizaCore.SetupCatalog(directory, preset, QueueProgress));
            SetupMessage = "Catalog setup complete.";
            SetupDetail = "All selected files were downloaded and SHA-256 verified.";
            ProgressPercent = 100;
            IsProgressIndeterminate = false;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            ErrorMessage = DescribeException(exception);
            SetupMessage = "Catalog setup stopped.";
            SetupDetail = "No unverified catalog file was installed.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    partial void OnStatusChanged(CatalogStatus? value) => NotifyCatalogStateChanged();

    partial void OnIsRefreshingChanged(bool value) => NotifyCommandStateChanged();

    partial void OnIsRunningChanged(bool value) => NotifyCommandStateChanged();

    private void QueueProgress(CatalogSetupProgress progress)
    {
        App.DispatcherQueue.TryEnqueue(() => ApplyProgress(progress));
    }

    private void ApplyProgress(CatalogSetupProgress progress)
    {
        SetupMessage = progress.Message;
        SetupDetail = FormatProgressDetail(progress);

        if (progress.BytesTotal is > 0 && progress.BytesCompleted is not null)
        {
            ProgressPercent = Math.Clamp(
                progress.BytesCompleted.Value * 100.0 / progress.BytesTotal.Value,
                0,
                100);
            IsProgressIndeterminate = false;
        }
        else if (progress.FilesTotal > 0)
        {
            ProgressPercent = Math.Clamp(
                progress.FilesCompleted * 100.0 / progress.FilesTotal,
                0,
                100);
            IsProgressIndeterminate = progress.FilesCompleted == 0;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private void NotifyDirectoryChanged()
    {
        OnPropertyChanged(nameof(DirectoryText));
        OnPropertyChanged(nameof(DirectoryModeText));
        OnPropertyChanged(nameof(IsUsingCustomDirectory));
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanChangeDirectory));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanStartSetup));
    }

    private void NotifyCatalogStateChanged()
    {
        OnPropertyChanged(nameof(DirectoryText));
        OnPropertyChanged(nameof(SetupButtonText));
        OnPropertyChanged(nameof(SolvingState));
        OnPropertyChanged(nameof(SolvingGlyph));
        OnPropertyChanged(nameof(OverlayState));
        OnPropertyChanged(nameof(OverlayGlyph));
        OnPropertyChanged(nameof(StarCatalogState));
        OnPropertyChanged(nameof(StarCatalogPath));
        OnPropertyChanged(nameof(StarCatalogGlyph));
        OnPropertyChanged(nameof(BlindIndexState));
        OnPropertyChanged(nameof(BlindIndexPath));
        OnPropertyChanged(nameof(BlindIndexGlyph));
        OnPropertyChanged(nameof(ObjectsState));
        OnPropertyChanged(nameof(ObjectsPath));
        OnPropertyChanged(nameof(ObjectsGlyph));
        OnPropertyChanged(nameof(TransientsState));
        OnPropertyChanged(nameof(TransientsPath));
        OnPropertyChanged(nameof(TransientsGlyph));
        OnPropertyChanged(nameof(MinorBodiesState));
        OnPropertyChanged(nameof(MinorBodiesPath));
        OnPropertyChanged(nameof(MinorBodiesGlyph));
    }

    private static string ReadinessState(bool? ready, string readyText, string missingText) => ready switch
    {
        true => readyText,
        false => missingText,
        null => "Checking…",
    };

    private static string ReadinessGlyph(bool? ready) => ready switch
    {
        true => ReadyGlyph,
        false => MissingGlyph,
        null => CheckingGlyph,
    };

    private static string ComponentState(CatalogComponentStatus? component) => component switch
    {
        { Available: true } => "Ready",
        not null => "Not installed",
        null => "Checking…",
    };

    private static string ComponentPath(CatalogComponentStatus? component) =>
        component?.Path ?? (component is null ? "" : "Required for this feature");

    private static string ComponentGlyph(CatalogComponentStatus? component) => component switch
    {
        { Available: true } => ReadyGlyph,
        not null => MissingGlyph,
        null => CheckingGlyph,
    };

    private static string FormatProgressDetail(CatalogSetupProgress progress)
    {
        List<string> details = [];
        if (!string.IsNullOrWhiteSpace(progress.FileName))
        {
            details.Add(progress.FileName);
        }

        if (progress.BytesTotal is > 0 && progress.BytesCompleted is not null)
        {
            details.Add($"{FormatBytes(progress.BytesCompleted.Value)} of {FormatBytes(progress.BytesTotal.Value)}");
        }

        if (progress.WrittenBytes is > 0 && progress.WrittenBytes != progress.BytesCompleted)
        {
            details.Add($"{FormatBytes(progress.WrittenBytes.Value)} written");
        }

        if (progress.FilesTotal > 0)
        {
            details.Add($"{progress.FilesCompleted:N0} of {progress.FilesTotal:N0} files");
        }

        return string.Join(" · ", details);
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string DescribeException(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
}
