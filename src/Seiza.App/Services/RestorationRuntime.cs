namespace Seiza.App.Services;

internal static class RestorationRuntime
{
    private static readonly string DirectoryPath = Path.Combine(RestorationService.DataDirectory, "ONNX Runtime");
    private static readonly string BundledLibrary = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
    private static readonly object SelectionGate = new();
    private static string? _selectedPath;
    private static string? _installedPath;
    private static string? ConfiguredLibrary => Environment.GetEnvironmentVariable("SEIZA_ONNXRUNTIME");

    // An installed fallback is checked by Seiza off the UI thread in InstallAsync.
    internal static bool CanLoad => File.Exists(BundledLibrary) ||
        !string.IsNullOrWhiteSpace(ConfiguredLibrary) || _installedPath is not null;

    internal static async Task InstallAsync(IProgress<RestorationProgress> progress, CancellationToken token)
    {
        string path = await RestorationService.InstallRuntimeAsync(DirectoryPath, progress, token).ConfigureAwait(false);
        lock (SelectionGate) { _installedPath = path; }
    }

    internal static string GetLibraryPath()
    {
        lock (SelectionGate)
        {
            if (_selectedPath is not null) return _selectedPath;
            string? configured = ConfiguredLibrary;
            string path;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                path = Path.GetFullPath(configured);
                if (!File.Exists(path)) throw new FileNotFoundException("SEIZA_ONNXRUNTIME must point to an ONNX Runtime DLL.", path);
            }
            else if (File.Exists(BundledLibrary))
            {
                path = BundledLibrary;
            }
            else
            {
                path = _installedPath ?? RestorationService.InstalledRuntimePath(DirectoryPath)
                    ?? throw new InvalidOperationException("Install ONNX Runtime from Seiza first.");
            }
            // Seiza loads this library and its dependencies through the C ABI.
            return _selectedPath = path;
        }
    }
}
