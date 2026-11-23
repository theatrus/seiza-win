using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>Portable, versioned recipes beside source images; never rendered pixels.</summary>
internal static class ProcessingSidecarService
{
    // Keep reads behind pending writes, including when navigating away and back.
    private static readonly SemaphoreSlim FileGate = new(1, 1);

    internal static string PathFor(string imagePath) => Path.GetFullPath(imagePath) + ".seiza.json";

    internal static async Task<FitsImageProcessingConfiguration?> ReadAsync(
        string imagePath, CancellationToken cancellationToken = default)
    {
        await FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string json;
            try { json = await File.ReadAllTextAsync(PathFor(imagePath), cancellationToken).ConfigureAwait(false); }
            catch (FileNotFoundException) { return null; }
            // Reuse the validated, versioned processing interchange format.
            return FitsImageProcessingConfiguration.FromClipboardJson(json).Clone(interactivePreview: false);
        }
        finally { FileGate.Release(); }
    }

    internal static async Task SaveAsync(string imagePath,
        FitsImageProcessingConfiguration processing, CancellationToken cancellationToken = default)
    {
        // Snapshot before yielding: a later edit must not change this write.
        string json = processing.Clone(interactivePreview: false).ToClipboardJson();
        string destination = PathFor(imagePath);
        await FileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => AtomicOutputFile.Write(destination,
                staging => File.WriteAllText(staging, json), cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { FileGate.Release(); }
    }
}
