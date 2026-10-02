using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>Calls Seiza's reference ranking; Windows never reproduces its scoring.</summary>
internal static class StackReferenceService
{
    public static Task<StackReferenceSelection> ChooseAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("Choose at least one reference candidate.", nameof(paths));
        }
        string[] fullPaths = paths.Select(Path.GetFullPath).ToArray();
        string pathsJson = JsonSerializer.Serialize(fullPaths, SeizaJsonSerializerContext.Default.StringArray);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Bound full-frame scoring to one worker. Cancellation abandons the
            // result after the synchronous native call has freed its allocations.
            nint json = NativeMethods.ChooseStackReferenceJson(pathsJson, 1, out nint error);
            if (json == 0)
            {
                throw NativeString.TakeError(error, "Seiza could not choose a reference frame.");
            }
            if (error != 0)
            {
                NativeMethods.FreeString(error);
            }
            string response = NativeString.TakeOwned(json, string.Empty);
            cancellationToken.ThrowIfCancellationRequested();
            StackReferenceSelection selection = JsonSerializer.Deserialize(
                response, SeizaJsonSerializerContext.Default.StackReferenceSelection)
                ?? throw new SeizaCoreException("Seiza returned no reference selection.");
            selection.Validate(fullPaths);
            return selection;
        }, cancellationToken);
    }
}
