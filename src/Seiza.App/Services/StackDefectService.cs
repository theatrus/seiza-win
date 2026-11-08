using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static partial class StackDefectService
{
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_stack_detect_defects_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Detect(string paths, string? bias, string? dark, string? flat, double darkExposure, string options, out nint error);

    public static async Task<ImageStackOptions> PrepareAsync(IReadOnlyList<string> paths, ImageStackOptions options,
        ImageStackCalibration calibration, CancellationToken token)
    {
        var prepared = options.Copy();
        if (!options.LearnDefects || options.LearnedDefects is not null) return prepared;
        if (calibration.DarkPath is not null)
            throw new InvalidOperationException("Learned defects are for lights without a master dark. Turn off learning when using a dark.");
        if (paths.Count < 3) throw new InvalidOperationException("Learning defects needs at least three dithered lights from the same filter and sensor.");
        string json = JsonSerializer.Serialize(paths.ToArray(), SeizaJsonSerializerContext.Default.StringArray);
        prepared.LearnedDefects = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            nint value = Detect(json, calibration.BiasPath, null, calibration.FlatPath, 0,
                DetectionOptionsJson(options), out nint error);
            if (value == 0) throw NativeString.TakeError(error, "Could not learn sensor defects.");
            if (error != 0) NativeMethods.FreeString(error);
            using JsonDocument result = JsonDocument.Parse(NativeString.TakeOwned(value, string.Empty));
            token.ThrowIfCancellationRequested();
            JsonElement map = result.RootElement.GetProperty("defects");
            if (map.GetProperty("frames").GetInt32() < 3) throw new InvalidOperationException("Fewer than three compatible lights could be read for defect learning.");
            return map.Clone();
        }, CancellationToken.None).ConfigureAwait(false);
        return prepared;
    }

    internal static string DetectionOptionsJson(ImageStackOptions options) =>
        "{\"sigma\":5,\"persistence\":0.6,\"maximum_frames\":16,\"gpu\":" + options.ToGpuJson() + "}";
}
