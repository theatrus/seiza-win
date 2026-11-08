using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;

namespace Seiza.App.Services;

/// <summary>A capability-gated, lossless JSON boundary for the evolving shared policy.</summary>
/// <remarks>No thresholds, field translation, grouping or choice resolution live in the host.
/// Callers must gate compatible families first. This is not an automatic calibration plan.</remarks>
internal static partial class FlatGroupPolicyService
{
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_flat_group_plan_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Plan(string request, out nint error);

    internal static Task<JsonElement> EvaluateAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CalibrationCoreCapabilities.Current.RequireFlatGroupPlanning();
        if (request.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A shared flat-group request must be a JSON object.", nameof(request));
        // Snapshot before scheduling: the caller may dispose its request document.
        string json = request.GetRawText();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint response = Plan(json, out nint error);
            if (response == 0) throw NativeString.TakeError(error, "Shared flat grouping failed.");
            if (error != 0) NativeMethods.FreeString(error);
            string result = NativeString.TakeOwned(response, "Invalid shared flat-group response.");
            // The batch ABI is synchronous; cancellation is observed at its boundaries.
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new SeizaCoreException("The shared flat-group response must be a JSON object.");
            cancellationToken.ThrowIfCancellationRequested();
            return document.RootElement.Clone();
        }, CancellationToken.None);
    }
}
