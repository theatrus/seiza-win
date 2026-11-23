using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class ImageRenderService
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    public static async Task<RenderedImageData> RenderAsync(
        string path,
        FitsImageProcessingConfiguration? processing = null,
        uint maxDimension = 0,
        ProcessingRegion? region = null,
        IProgress<RestorationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await RenderGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = path;
            processing = ColorWorkflowService.ProcessingFor(path, processing ?? ColorWorkflowService.InitialProcessingFor(path));
            if (processing.Ai is { } ai)
                path = await AiProcessingService.PrepareAsync(path, ai, region, progress, cancellationToken).ConfigureAwait(false);
            RenderedImageData rendered = await Task.Run(() =>
                SeizaCore.Render(path, maxDimension, processing));
            cancellationToken.ThrowIfCancellationRequested();
            return processing.Ai is null ? rendered : rendered with
            { Metadata = rendered.Metadata with { Format = Path.GetExtension(sourcePath).Equals(".xisf", StringComparison.OrdinalIgnoreCase) ? "XISF" : "FITS" } };
        }
        finally
        {
            RenderGate.Release();
        }
    }

    public static async Task<RenderedImage16Data> Render16Async(
        string path,
        FitsImageProcessingConfiguration? processing = null,
        CancellationToken cancellationToken = default)
    {
        await RenderGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = path;
            processing = ColorWorkflowService.ProcessingFor(path, processing ?? ColorWorkflowService.InitialProcessingFor(path));
            if (processing.Ai is { } ai)
                path = await AiProcessingService.PrepareAsync(path, ai, null, null, cancellationToken).ConfigureAwait(false);
            RenderedImage16Data rendered = await Task.Run(() =>
                SeizaCore.Render16(path, processing));
            cancellationToken.ThrowIfCancellationRequested();
            return processing.Ai is null ? rendered : rendered with
            { Metadata = rendered.Metadata with { Format = Path.GetExtension(sourcePath).Equals(".xisf", StringComparison.OrdinalIgnoreCase) ? "XISF" : "FITS" } };
        }
        finally
        {
            RenderGate.Release();
        }
    }
    internal static async Task SaveLinearAiAsync(string path, string destination, AiProcessing ai,
        IProgress<RestorationProgress> progress, CancellationToken token)
    {
        void ValidateDestination()
        {
            if (StackSourceIdentity.RefersToSameFile(path, destination))
                throw new IOException("Choose a separate destination for the processed image.");
        }
        ValidateDestination();
        await RenderGate.WaitAsync(token);
        try
        {
            string linear = await AiProcessingService.PrepareAsync(path, ai, null, progress, token).ConfigureAwait(false);
            await Task.Run(() => AtomicOutputFile.Write(destination, staging => File.Copy(linear, staging, overwrite: true),
                ValidateDestination, token), token).ConfigureAwait(false);
        }
        finally { RenderGate.Release(); }
    }

}
