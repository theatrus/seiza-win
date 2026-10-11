using Seiza.App.Models;

namespace Seiza.App.Services;

// The display source can itself be starless. Use the private unscreened stars
// copy for solving whenever manual layers exist, independently of camera size.
internal static class ParallaxRequestSource
{
    internal static ParallaxRequest Create(ParallaxComposition composition, bool preview,
        string snapshotPath, bool automaticSeparation, string? starless, string? stars,
        ParallaxInputs inputs)
    {
        inputs.Image = !automaticSeparation && stars is not null ? stars : snapshotPath;
        inputs.Starless = automaticSeparation ? null : starless;
        inputs.Stars = automaticSeparation ? null : stars;
        return new() { Inputs = inputs, Scene = composition.Scene.DeepClone(), Video = composition.VideoSettings(preview) };
    }

    internal static async Task EnsureWcsAsync(ParallaxRequest request,
        Func<string, Task<WcsResult>> blindSolve, CancellationToken cancellationToken)
    {
        // Preserve an existing document/scene solution; never replace it by
        // blind-solving a different layer unnecessarily.
        if (request.Inputs.Wcs is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        WcsResult wcs = await blindSolve(request.Inputs.Image);
        cancellationToken.ThrowIfCancellationRequested();
        request.Inputs.Wcs = wcs;
    }
}
