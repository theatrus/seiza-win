using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class ImageRenderService
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    public static async Task<RenderedImageData> RenderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await RenderGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderedImageData rendered = await Task.Run(() => SeizaCore.Render(path));
            cancellationToken.ThrowIfCancellationRequested();
            return rendered;
        }
        finally
        {
            RenderGate.Release();
        }
    }
}
