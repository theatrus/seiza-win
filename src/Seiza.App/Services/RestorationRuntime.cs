using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Seiza.App.Services;

internal static partial class RestorationRuntime
{
    // Seiza's Windows archive includes the ONNX provider and its app-local CRT.
    internal const string Version = "1.31.1";
    private const string ArchiveUrl = "https://github.com/theatrus/seiza/releases/download/v0.51.0/seiza-cli-0.51.0-windows-x86_64.zip";
    private const string ArchiveSha256 = "D08568C743B917C1B6F6206A81AD37E9F55F0DAAB83DBA2DAC166238EA46E0B8";
    private const int ArchiveBytes = 39_238_491;
    private static readonly string[] Files = ["onnxruntime.dll", "onnxruntime_providers_shared.dll",
        "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll",
        "LICENSE-onnxruntime.txt", "ThirdPartyNotices-onnxruntime.txt"];
    private static readonly string DirectoryPath = Path.Combine(RestorationService.DataDirectory, "ONNX Runtime", Version);
    private static readonly string BundledLibrary = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
    private static readonly HttpClient Client = new();
    private static readonly object LoadGate = new();
    private static string? _loadedPath;

    private static bool IsInstalled => Files.All(name => File.Exists(Path.Combine(DirectoryPath, name)));
    private static string? ConfiguredLibrary => Environment.GetEnvironmentVariable("SEIZA_ONNXRUNTIME");
    internal static bool CanLoad => File.Exists(BundledLibrary) || IsInstalled || !string.IsNullOrWhiteSpace(ConfiguredLibrary);

    internal static async Task InstallAsync(IProgress<RestorationProgress> progress, CancellationToken token)
    {
        if (IsInstalled) return;
        string parent = Path.GetDirectoryName(DirectoryPath)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".runtime-{Guid.NewGuid():N}");
        string archive = staging + ".zip";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ArchiveUrl);
            using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536];
                long received = 0;
                long lastReport = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > ArchiveBytes) throw new IOException("The Seiza runtime download exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (lastReport < 0 || received - lastReport >= 1024 * 1024)
                    {
                        progress.Report(new("Downloading ONNX Runtime from Seiza…", (double)received / ArchiveBytes));
                        lastReport = received;
                    }
                }
                if (received != ArchiveBytes) throw new IOException("The Seiza runtime download was incomplete.");
            }
            progress.Report(new("Verifying and installing ONNX Runtime…"));
            await using (FileStream downloaded = File.OpenRead(archive))
            {
                if (!Convert.ToHexString(await SHA256.HashDataAsync(downloaded, token).ConfigureAwait(false))
                    .Equals(ArchiveSha256, StringComparison.Ordinal))
                    throw new IOException("The Seiza runtime archive failed SHA-256 verification.");
            }
            Directory.CreateDirectory(staging);
            using (ZipArchive zip = ZipFile.OpenRead(archive))
            {
                foreach (string name in Files)
                {
                    token.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = zip.GetEntry(name) ?? throw new IOException($"The runtime archive is missing {name}.");
                    entry.ExtractToFile(Path.Combine(staging, name));
                }
            }
            token.ThrowIfCancellationRequested();
            lock (LoadGate)
            {
                // A concurrent successful install wins. Put an incomplete install
                // aside until the verified replacement has been moved into place.
                if (IsInstalled) return;
                string previous = staging + ".previous";
                if (Directory.Exists(DirectoryPath)) Directory.Move(DirectoryPath, previous);
                try { Directory.Move(staging, DirectoryPath); }
                catch
                {
                    if (Directory.Exists(previous)) Directory.Move(previous, DirectoryPath);
                    throw;
                }
                RestorationService.DeleteStagingDirectory(previous);
            }
        }
        finally
        {
            RestorationService.DeleteStagingDirectory(staging);
            try { File.Delete(archive); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string Load()
    {
        lock (LoadGate)
        {
            if (_loadedPath is not null) return _loadedPath;
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
                if (!IsInstalled) throw new InvalidOperationException("Install ONNX Runtime from Seiza first.");
                path = Path.Combine(DirectoryPath, "onnxruntime.dll");
            }
            // Search the DLL's own folder for the runtime shipped beside it.
            const uint searchDllLoadDirectory = 0x100;
            const uint searchDefaultDirectories = 0x1000;
            // Seiza keeps the runtime for the life of the process.
            if (LoadLibraryEx(path, 0, searchDllLoadDirectory | searchDefaultDirectories) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ONNX Runtime could not start. Check its DLL and the libraries shipped beside it.");
            return _loadedPath = path;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string path, nint file, uint flags);
}
