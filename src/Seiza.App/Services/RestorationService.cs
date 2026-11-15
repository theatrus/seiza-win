using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed record InstalledNoiseModel(string Name, string Path, string CardHash, string[] Precisions)
{
    public override string ToString() => Name;
}

internal sealed record PublishedNoiseModel(string Name, string CardHash);
internal sealed record NoiseModelCatalog(string? Recommended, PublishedNoiseModel[] Models);
internal sealed record RestorationProgress(string Message, double? Fraction = null);
internal sealed record RestorationOptions(string? Backend, string? Precision, int? Tile);
internal sealed record RestorationBands(double High, double Low, double Scale);
internal sealed record RestorationPreview(RenderedImageData Original, RenderedImageData Restored, string Details);
internal sealed record RestorationBackend(string Name, string Label)
{
    public override string ToString() => Label;
}

internal static class RestorationService
{
    private const string ModelCatalogUrl = "https://downloads.seiza.fyi/models/v1/manifest.json";
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seiza", "Restoration");
    private static readonly string ModelDirectory = Path.Combine(DataDirectory, "Models");
    private static readonly string PreviousDirectory = Path.Combine(DataDirectory, "Previous model");
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static readonly HttpClient CatalogClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    internal static async Task<NoiseModelCatalog> FetchCatalogAsync(CancellationToken token)
    {
        string json = await CatalogClient.GetStringAsync(ModelCatalogUrl, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement manifest = document.RootElement;
        string? recommended = manifest.TryGetProperty("latest", out JsonElement latest) &&
            latest.TryGetProperty("denoise", out JsonElement denoise) ? denoise.GetString() : null;
        // The catalog drives selection and update detection. Seiza verifies the
        // manifest and bundle again through its downloader before installation.
        PublishedNoiseModel[] models = manifest.GetProperty("models").EnumerateArray()
            .Select(model => new PublishedNoiseModel(
                model.GetProperty("name").GetString()!,
                model.GetProperty("files").EnumerateArray()
                    .First(file => file.GetProperty("name").GetString() == "card.json")
                    .GetProperty("sha256").GetString()!))
            .ToArray();
        return new NoiseModelCatalog(recommended, models);
    }

    internal static Task<IReadOnlyList<InstalledNoiseModel>> ListAsync() => InStoreAsync<IReadOnlyList<InstalledNoiseModel>>(() =>
    {
        using JsonDocument document = ReadModelResponse(NativeMethods.ListModels(ModelDirectory, out nint error), error);
        return document.RootElement.EnumerateArray()
            .Where(model => model.GetProperty("task").GetString() == "denoise")
            .Select(ReadModel)
            .ToArray();
    });

    internal static Task<InstalledNoiseModel> DownloadAsync(string? name, IProgress<RestorationProgress> progress,
        CancellationToken token) => InStoreAsync(() => InstallStaged(staging => DownloadModel(name, staging, progress), token));

    internal static Task<InstalledNoiseModel> ImportAsync(string path, CancellationToken token) =>
        InStoreAsync(() => InstallStaged(staging =>
            ReadModelResponse(NativeMethods.InstallModel(path, staging, out nint error), error), token));

    internal static Task VerifyAsync(InstalledNoiseModel model) => InStoreAsync(() =>
    {
        if (!NativeMethods.VerifyModel(model.Path, ModelDirectory, out nint error))
            throw NativeString.TakeError(error, "Could not verify the model.");
        if (error != 0) NativeMethods.FreeString(error);
        return true;
    });

    internal static Task RemoveAsync(InstalledNoiseModel model) => InStoreAsync(() =>
    {
        if (!NativeMethods.RemoveModel(model.Name, ModelDirectory, out nint error))
            throw NativeString.TakeError(error, "Could not remove the model.");
        if (error != 0) NativeMethods.FreeString(error);
        return true;
    });

    private static async Task<T> InStoreAsync<T>(Func<T> operation)
    {
        await StoreGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                RecoverPreviousModel();
                return operation();
            }).ConfigureAwait(false);
        }
        finally { StoreGate.Release(); }
    }

    private static void RecoverPreviousModel()
    {
        if (!Directory.Exists(PreviousDirectory)) return;
        foreach (string previous in Directory.EnumerateDirectories(PreviousDirectory))
        {
            string target = Path.Combine(ModelDirectory, Path.GetFileName(previous));
            if (Directory.Exists(target))
                Directory.Delete(previous, recursive: true);
            else
            {
                Directory.CreateDirectory(ModelDirectory);
                Directory.Move(previous, target);
            }
        }
    }

    private static InstalledNoiseModel InstallStaged(Func<string, JsonDocument> install, CancellationToken token)
    {
        string staging = Path.Combine(DataDirectory, $".model-{Guid.NewGuid():N}");
        try
        {
            token.ThrowIfCancellationRequested();
            using JsonDocument installed = install(staging);
            token.ThrowIfCancellationRequested();
            if (installed.RootElement.GetProperty("task").GetString() != "denoise")
                throw new InvalidOperationException("Choose a Seiza noise model bundle.");
            InstalledNoiseModel model = ReadModel(installed.RootElement);
            string name = Path.GetFileName(model.Path);
            string target = Path.Combine(ModelDirectory, name);
            string previous = Path.Combine(PreviousDirectory, name);
            Directory.CreateDirectory(ModelDirectory);
            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(PreviousDirectory);
                Directory.Move(target, previous);
            }
            try { Directory.Move(model.Path, target); }
            catch
            {
                RecoverPreviousModel();
                throw;
            }
            DeleteStagingDirectory(previous);
            return model with { Path = target };
        }
        finally { DeleteStagingDirectory(staging); }
    }

    private static InstalledNoiseModel ReadModel(JsonElement model) => new(
        model.GetProperty("name").GetString()!, model.GetProperty("path").GetString()!,
        model.GetProperty("cardSha256").GetString()!,
        model.GetProperty("precisions").EnumerateArray().Select(value => value.GetString()!).ToArray());

    private static JsonDocument ReadModelResponse(nint json, nint error)
    {
        if (json == 0) throw NativeString.TakeError(error, "The model operation failed.");
        if (error != 0) NativeMethods.FreeString(error);
        return JsonDocument.Parse(NativeString.TakeOwned(json, "null"));
    }

    private static unsafe JsonDocument DownloadModel(string? name, string staging, IProgress<RestorationProgress> progress)
    {
        var context = GCHandle.Alloc(new CallbackState(progress, CancellationToken.None));
        try
        {
            return ReadModelResponse(NativeMethods.DownloadModel(name, staging, null, &OnProgress,
                GCHandle.ToIntPtr(context), out nint error), error);
        }
        finally { context.Free(); }
    }

    internal static Task<(string Version, RestorationBackend[] Backends)> BackendsAsync() => Task.Run(() =>
    {
        string runtime = RestorationRuntime.Load();
        using JsonDocument document = ReadPlatform(NativeMethods.GetRestorationBackends(runtime, out nint error), error);
        var backends = document.RootElement.GetProperty("backends").EnumerateArray()
            .Where(backend => backend.GetProperty("unavailable").ValueKind == JsonValueKind.Null)
            .Select(backend => backend.GetProperty("name").GetString()!)
            .Select(name => new RestorationBackend(name, name switch
            {
                "cpu" => "CPU",
                "cuda" => "NVIDIA CUDA",
                "tensorrt" => "NVIDIA TensorRT",
                "tensorrt-rtx" => "NVIDIA TensorRT for RTX",
                "webgpu" => "WebGPU",
                _ => name,
            })).ToArray();
        return (document.RootElement.GetProperty("version").GetString()!, backends);
    });

    internal static Task<SafeRestorationHandle> RunPreviewAsync(string input, InstalledNoiseModel model,
        RestorationOptions options, IProgress<RestorationProgress> progress, CancellationToken token) =>
        InStoreAsync(() => Run(input, model, options, preview: true, progress, token));

    private static unsafe SafeRestorationHandle Run(string input, InstalledNoiseModel model, RestorationOptions options,
        bool preview, IProgress<RestorationProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ImageFileService.IsAstronomyImage(input)) throw new InvalidOperationException("Choose a linear FITS or XISF image.");
        string request = new JsonObject
        {
            ["input"] = input, ["model"] = model.Path, ["library"] = RestorationRuntime.Load(),
            ["backend"] = options.Backend, ["precision"] = options.Precision, ["tile"] = options.Tile,
            ["preview"] = preview,
        }.ToJsonString();
        var context = GCHandle.Alloc(new CallbackState(progress, token));
        try
        {
            nint result = NativeMethods.RunRestoration(request, &OnProgress, &IsCancelled, GCHandle.ToIntPtr(context), out nint error);
            if (token.IsCancellationRequested)
            {
                if (result != 0) NativeMethods.FreeRestoration(result);
                if (error != 0) NativeMethods.FreeRestorationString(error);
                token.ThrowIfCancellationRequested();
            }
            if (result == 0) throw PlatformError(error);
            if (error != 0) NativeMethods.FreeRestorationString(error);
            return new SafeRestorationHandle(result);
        }
        finally { context.Free(); }
    }

    internal static Task<RestorationPreview> PreviewAsync(SafeRestorationHandle result, RestorationBands bands, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        string folder = Path.Combine(DataDirectory, $".preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string original = Path.Combine(folder, "original.fits");
            string restored = Path.Combine(folder, "restored.fits");
            string details = Blend(result, bands, restored, original);
            token.ThrowIfCancellationRequested();
            return new RestorationPreview(SeizaCore.Render(original, 768), SeizaCore.Render(restored, 768), details);
        }
        finally { DeleteStagingDirectory(folder); }
    }, token);

    internal static async Task SaveAsync(string input, string output, InstalledNoiseModel model, RestorationOptions options,
        RestorationBands bands, IProgress<RestorationProgress> progress, CancellationToken token)
    {
        void ValidateDestination()
        {
            if (StackSourceIdentity.RefersToSameFile(input, output))
                throw new SeizaCoreException("Choose a separate destination for the restored image.");
        }
        ValidateDestination();
        using var result = await InStoreAsync(() => Run(input, model, options, preview: false, progress, token)).ConfigureAwait(false);
        await Task.Run(() => AtomicOutputFile.Write(output, staging => Blend(result, bands, staging),
            ValidateDestination, token), token).ConfigureAwait(false);
    }

    private static string Blend(SafeRestorationHandle result, RestorationBands bands, string output, string? original = null)
    {
        string request = new JsonObject
        {
            ["output"] = output, ["originalPreview"] = original,
            ["high"] = bands.High, ["low"] = bands.Low, ["scalePx"] = bands.Scale,
        }.ToJsonString();
        using JsonDocument report = ReadPlatform(NativeMethods.BlendRestoration(result, request, out nint error), error);
        JsonElement data = report.RootElement;
        string backend = data.GetProperty("backend").GetString()!;
        return $"{(backend == "cpu" ? "CPU" : backend)} · {data.GetProperty("precision").GetString()} · {data.GetProperty("tile").GetInt32()} px tiles";
    }

    private static JsonDocument ReadPlatform(nint json, nint error)
    {
        try
        {
            if (json == 0) throw new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Restoration failed.");
            return JsonDocument.Parse(Marshal.PtrToStringUTF8(json) ?? "null");
        }
        finally
        {
            if (json != 0) NativeMethods.FreeRestorationString(json);
            if (error != 0) NativeMethods.FreeRestorationString(error);
        }
    }

    private static SeizaCoreException PlatformError(nint error)
    {
        try { return new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Restoration failed."); }
        finally { if (error != 0) NativeMethods.FreeRestorationString(error); }
    }

    internal static void DeleteStagingDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CallbackState(IProgress<RestorationProgress> progress, CancellationToken token)
    {
        private long _lastUpdate;
        internal CancellationToken Token => token;

        internal void Report(JsonElement value)
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastUpdate, now) < TimeSpan.FromMilliseconds(125)) return;
            _lastUpdate = now;
            if (value.TryGetProperty("message", out JsonElement message))
            {
                progress.Report(new(message.GetString()!, value.TryGetProperty("fraction", out JsonElement fraction) ? fraction.GetDouble() : null));
                return;
            }
            switch (value.GetProperty("event").GetString())
            {
                case "fetching-manifest": progress.Report(new("Checking the Seiza model catalog…")); break;
                case "download-progress":
                    string file = value.GetProperty("name").GetString()!;
                    double bytes = value.GetProperty("downloaded").GetDouble();
                    double total = value.GetProperty("total").GetDouble();
                    progress.Report(new($"Downloading {file} · {bytes / 1048576:N0} of {total / 1048576:N0} MiB", total > 0 ? bytes / total : null));
                    break;
                case "verifying" or "installing": progress.Report(new("Verifying the model bundle…")); break;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProgress(nint json, nint context)
    {
        try
        {
            var state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
            using JsonDocument document = JsonDocument.Parse(Marshal.PtrToStringUTF8(json)!);
            state.Report(document.RootElement);
        }
        catch (Exception) { /* Exceptions cannot cross the native callback. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte IsCancelled(nint context) =>
        ((CallbackState)GCHandle.FromIntPtr(context).Target!).Token.IsCancellationRequested ? (byte)1 : (byte)0;
}
