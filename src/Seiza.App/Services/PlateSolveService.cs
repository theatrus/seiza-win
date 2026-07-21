using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed class CatalogNotReadyException(string directory) : Exception(
    $"Catalog setup is required before plate solving. Install and verify the standard blind-solving package in {directory}.")
{
}

internal static class PlateSolveService
{
    private static readonly SemaphoreSlim SolveGate = new(1, 1);

    public static async Task<SolveResult> SolveAsync(
        string path,
        string? catalogDirectory,
        CancellationToken cancellationToken = default)
    {
        await SolveGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SolveResult result = await Task.Run(() =>
            {
                CatalogStatus status = SeizaCore.GetCatalogStatus(catalogDirectory);
                if (!status.ReadyForSolving)
                {
                    throw new CatalogNotReadyException(status.Directory);
                }

                return SeizaCore.Solve(path, catalogDirectory);
            });
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            SolveGate.Release();
        }
    }
}
