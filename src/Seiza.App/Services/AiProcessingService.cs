using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>Called under ImageRenderService's gate, which also protects cached file readers.</summary>
internal static class AiProcessingService
{
    private static readonly ProcessingResultCache<InferenceKey, SafeRestorationHandle> Results = new(2, 384L * 1024 * 1024);
    private static readonly string Folder = Path.Combine(RestorationService.DataDirectory, "Processing", Guid.NewGuid().ToString("N"));
    private static string? _automaticModel;
    private static BlendKey? _blend;
    private static readonly string Output = Path.Combine(Folder, "linear.fits");

    static AiProcessingService() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        RestorationService.DeleteStagingDirectory(Folder);
    };

    internal static void RefreshAutomaticModel() => _automaticModel = null;

    private sealed record FileStamp(string Path, long Length, long Modified, string? Identity)
    {
        internal static FileStamp Read(string path)
        {
            var file = new FileInfo(path);
            return new(file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, WindowsFileIdentity.TryGet(path));
        }
    }
    private sealed record InferenceKey(FileStamp Source, string Model, string ModelFiles,
        RestorationOptions Options, ProcessingRegion? Region);
    private sealed record BlendKey(InferenceKey Inference, RestorationBands Bands);

    internal static async Task<string> PrepareAsync(string path, AiProcessing settings, ProcessingRegion? region,
        IProgress<RestorationProgress>? progress, CancellationToken token)
    {
        if (settings.ValidationMessage is { } validation) throw new InvalidOperationException(validation);
        progress ??= new InlineProgress<RestorationProgress>(_ => { });
        token.ThrowIfCancellationRequested();
        if (!RestorationRuntime.CanLoad) await RestorationRuntime.InstallAsync(progress, token).ConfigureAwait(false);
        var models = await RestorationService.ListAsync().ConfigureAwait(false);
        string? name = settings.Model ?? _automaticModel;
        if (name is null)
        {
            try { name = (await RestorationService.FetchCatalogAsync(token).ConfigureAwait(false)).Recommended; }
            catch (Exception error) when (error is SeizaCoreException or IOException or System.Text.Json.JsonException && models.Count > 0 && !token.IsCancellationRequested) { name = models[0].Name; }
            _automaticModel = name;
        }
        var model = models.FirstOrDefault(model => model.Name == name)
            ?? await RestorationService.DownloadAsync(name, progress, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // Graph/card replacement invalidates residuals even when the model name is unchanged.
        string ModelFiles() => string.Join("|", Directory.EnumerateFiles(model.Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(file => FileStamp.Read(file).ToString()));
        var options = new RestorationOptions(settings.Backend, settings.Precision, settings.Tile, settings.DeviceId);
        var source = FileStamp.Read(path);
        var key = new InferenceKey(source, model.CardHash, ModelFiles(), options, region);
        var bands = new RestorationBands(settings.High, settings.Low, settings.Scale);
        var blend = new BlendKey(key, bands);
        if (_blend == blend && File.Exists(Output))
        {
            progress.Report(new("Using cached AI result"));
            return Output;
        }

        SafeRestorationHandle? result = Results.Find(key);
        bool transient = false;
        if (result is null)
        {
            result = await RestorationService.RunProcessingAsync(path, model, options, region, progress, token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (source != FileStamp.Read(path)) throw new IOException("The source changed during AI processing. Try again with a stable image.");
                if (key.ModelFiles != ModelFiles()) throw new IOException("The model changed during AI processing. Try again.");
                var header = ImageSourceService.ReadHeader(path);
                long width = region?.Width ?? header.Headers["NAXIS1"].GetInt32();
                long height = region?.Height ?? header.Headers["NAXIS2"].GetInt32();
                int planes = header.Headers.TryGetValue("NAXIS3", out var channels) ? channels.GetInt32() : 1;
                transient = !Results.Add(key, result, checked(width * height * planes * 24));
            }
            catch { result.Dispose(); throw; }
        }
        else progress.Report(new("Using cached inference · applying noise strengths…"));

        try
        {
            Directory.CreateDirectory(Folder);
            _blend = null;
            await RestorationService.WriteProcessingAsync(result, bands, Output, region is not null ? path : null, token).ConfigureAwait(false);
            if (source != FileStamp.Read(path)) throw new IOException("The source changed during AI processing.");
            if (key.ModelFiles != ModelFiles()) throw new IOException("The model changed during AI processing. Try again.");
            _blend = blend;
            return Output;
        }
        finally { if (transient) result.Dispose(); }
    }
}
