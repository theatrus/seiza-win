using System.Diagnostics;
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
internal sealed record RestorationOptions(string? Backend, string? Precision, int? Tile, int? DeviceId);
internal sealed record RestorationBands(double High, double Low, double Scale);
internal sealed record RestorationBackend(string Name, string Label)
{
    public override string ToString() => Label;
}

internal static class RestorationService
{
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seiza", "Restoration");
    private static readonly string ModelDirectory = Path.Combine(DataDirectory, "Models");
    private static readonly string PreviousDirectory = Path.Combine(DataDirectory, "Previous model");
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    internal static Task<NoiseModelCatalog> FetchCatalogAsync(CancellationToken token) => Task.Run(() =>
    {
        using JsonDocument document = ReadModelResponse(NativeMethods.GetModelCatalog(null, out nint error), error);
        JsonElement manifest = document.RootElement;
        string? recommended = manifest.TryGetProperty("latest", out JsonElement latest) &&
            latest.TryGetProperty("denoise", out JsonElement denoise) ? denoise.GetString() : null;
        PublishedNoiseModel[] models = manifest.GetProperty("models").EnumerateArray()
            .Select(model => new PublishedNoiseModel(
                model.GetProperty("name").GetString()!,
                model.GetProperty("files").EnumerateArray()
                    .First(file => file.GetProperty("name").GetString() == "card.json")
                    .GetProperty("sha256").GetString()!))
            .ToArray();
        return new NoiseModelCatalog(recommended, models);
    }, token);

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
        var context = GCHandle.Alloc(new CallbackState(progress));
        try
        {
            return ReadModelResponse(NativeMethods.DownloadModel(name, staging, null, &OnProgress,
                GCHandle.ToIntPtr(context), out nint error), error);
        }
        finally { context.Free(); }
    }

    internal static string? InstalledRuntimePath(string directory)
    {
        using JsonDocument document = ReadModelResponse(NativeMethods.InstalledOnnxRuntime(directory, out nint error), error);
        return document.RootElement.ValueKind == JsonValueKind.Null ? null
            : document.RootElement.GetProperty("library").GetString();
    }

    internal static Task<string> InstallRuntimeAsync(string directory, IProgress<RestorationProgress> progress,
        CancellationToken token) => Task.Run(() => InstallRuntime(directory, progress, token), token);

    private static unsafe string InstallRuntime(string directory, IProgress<RestorationProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint signal = NativeMethods.CreateCancelSignal();
        if (signal == 0) throw new SeizaCoreException("Could not create the cancellation signal.");
        using var cancellation = new SafeCancelSignalHandle(signal);
        using var registration = token.UnsafeRegister(static value => ((SafeCancelSignalHandle)value!).Cancel(), cancellation);
        var context = GCHandle.Alloc(new CallbackState(progress));
        try
        {
            nint result = NativeMethods.InstallOnnxRuntime(directory, &OnProgress, cancellation,
                GCHandle.ToIntPtr(context), out nint error);
            if (token.IsCancellationRequested)
            {
                if (result != 0) NativeMethods.FreeString(result);
                if (error != 0) NativeMethods.FreeString(error);
                token.ThrowIfCancellationRequested();
            }
            using JsonDocument document = ReadModelResponse(result, error);
            return document.RootElement.GetProperty("library").GetString()!;
        }
        finally { context.Free(); }
    }

    internal static Task<(string Version, RestorationBackend[] Backends)> BackendsAsync() => Task.Run(() =>
    {
        string runtime = RestorationRuntime.GetLibraryPath();
        var options = new JsonObject { ["library"] = runtime };
        using JsonDocument document = ReadModelResponse(NativeMethods.GetRestorationBackends(options.ToJsonString(), out nint error), error);
        JsonElement library = document.RootElement.GetProperty("runtime");
        if (library.ValueKind == JsonValueKind.Null) throw new SeizaCoreException("Seiza could not initialize ONNX Runtime.");
        var backends = document.RootElement.GetProperty("backends").EnumerateArray()
            .Where(backend => backend.GetProperty("unavailable").ValueKind == JsonValueKind.Null)
            .Select(backend => backend.GetProperty("backend").GetString()!)
            .Select(name => new RestorationBackend(name, name switch
            {
                "cpu" => "CPU",
                "directml" => "DirectML",
                "cuda" => "NVIDIA CUDA",
                "tensorrt" => "NVIDIA TensorRT",
                "tensorrt-rtx" => "NVIDIA TensorRT for RTX",
                "webgpu" => "WebGPU",
                _ => name,
            })).ToArray();
        return (library.GetProperty("version").GetString()!, backends);
    });

    private static unsafe SafeRestorationHandle Run(string input, InstalledNoiseModel model, RestorationOptions options,
        bool preview, IProgress<RestorationProgress> progress, CancellationToken token, ProcessingRegion? region = null)
    {
        token.ThrowIfCancellationRequested();
        if (!ImageFileTypes.IsAstronomyImage(input)) throw new InvalidOperationException("Choose a linear FITS or XISF image.");
        string request = new JsonObject
        {
            ["library"] = RestorationRuntime.GetLibraryPath(),
            ["backend"] = options.Backend, ["precision"] = options.Precision, ["tile"] = options.Tile,
            ["deviceId"] = options.DeviceId,
        }.ToJsonString();
        progress.Report(new("Opening the model…"));
        nint opened = NativeMethods.OpenNoiseModel(model.Path, request, out nint error);
        if (opened == 0) throw NativeString.TakeError(error, "Could not open the model.");
        using var nativeModel = new SafeNoiseModelHandle(opened);
        token.ThrowIfCancellationRequested();

        nint signal = NativeMethods.CreateCancelSignal();
        if (signal == 0) throw new SeizaCoreException("Could not create the cancellation signal.");
        using var cancellation = new SafeCancelSignalHandle(signal);
        using var registration = token.UnsafeRegister(static value => ((SafeCancelSignalHandle)value!).Cancel(), cancellation);
        var context = GCHandle.Alloc(new CallbackState(progress));
        try
        {
            nint result = region is null
                ? NativeMethods.RunRestoration(nativeModel, input, preview, &OnInferenceProgress,
                    cancellation, GCHandle.ToIntPtr(context), out error)
                : NativeMethods.RunRestorationRegion(nativeModel, input, (nuint)region.X, (nuint)region.Y,
                    (nuint)region.Width, (nuint)region.Height, &OnInferenceProgress,
                    cancellation, GCHandle.ToIntPtr(context), out error);
            if (token.IsCancellationRequested)
            {
                if (result != 0) NativeMethods.FreeRestoration(result);
                if (error != 0) NativeMethods.FreeString(error);
                token.ThrowIfCancellationRequested();
            }
            if (result == 0) throw NativeString.TakeError(error, "Restoration failed.");
            if (error != 0) NativeMethods.FreeString(error);
            return new SafeRestorationHandle(result);
        }
        finally { context.Free(); }
    }

    internal static Task<SafeRestorationHandle> RunProcessingAsync(string input, InstalledNoiseModel model,
        RestorationOptions options, ProcessingRegion? region, IProgress<RestorationProgress> progress, CancellationToken token) =>
        InStoreAsync(() => Run(input, model, options, region is not null, progress, token, region));

    internal static Task WriteProcessingAsync(SafeRestorationHandle result, RestorationBands bands, string output,
        string? previewSource, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (previewSource is not null)
        {
            using var report = ReadModelResponse(NativeMethods.BlendRestorationPreviewInContext(result, previewSource, output,
                (float)bands.High, (float)bands.Low, (float)bands.Scale, out nint error), error);
        }
        else Blend(result, bands, output);
        token.ThrowIfCancellationRequested();
    }, token);

    private static string Blend(SafeRestorationHandle result, RestorationBands bands, string output, string? original = null)
    {
        using JsonDocument report = ReadModelResponse(NativeMethods.BlendRestoration(result, output, original,
            (float)bands.High, (float)bands.Low, (float)bands.Scale, out nint error), error);
        JsonElement data = report.RootElement;
        string backend = data.GetProperty("backend").GetString()!;
        string label = backend switch { "cpu" => "CPU", "directml" => "DirectML", _ => backend };
        return $"{label} · {data.GetProperty("precision").GetString()} · {data.GetProperty("tile").GetInt32()} px tiles";
    }

    internal static void DeleteStagingDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CallbackState(IProgress<RestorationProgress> progress)
    {
        private long _lastUpdate;
        private bool ShouldReport()
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastUpdate, now) < TimeSpan.FromMilliseconds(125)) return false;
            _lastUpdate = now;
            return true;
        }

        internal void ReportTile(nuint channel, nuint channels, nuint tile, nuint tiles)
        {
            if (ShouldReport())
                progress.Report(new($"Channel {channel + 1} of {channels} · tile {tile} of {tiles}",
                    ((double)channel + (double)tile / (double)tiles) / (double)channels));
        }

        internal void Report(JsonElement value)
        {
            if (!ShouldReport()) return;
            switch (value.GetProperty("event").GetString())
            {
                case "fetching-manifest": progress.Report(new("Checking the Seiza model catalog…")); break;
                case "download-progress":
                    string file = value.GetProperty("name").GetString()!;
                    double bytes = value.GetProperty("downloaded").GetDouble();
                    double total = value.GetProperty("total").GetDouble();
                    progress.Report(new($"Downloading {file} · {bytes / 1048576:N0} of {total / 1048576:N0} MiB", total > 0 ? bytes / total : null));
                    break;
                case "verifying" or "installing": progress.Report(new("Verifying and installing downloaded files…")); break;
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
    private static void OnInferenceProgress(nuint channel, nuint channels, nuint tile, nuint tiles, nint context)
    {
        try
        {
            var state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
            state.ReportTile(channel, channels, tile, tiles);
        }
        catch (Exception) { /* Exceptions cannot cross the native callback. */ }
    }
}
