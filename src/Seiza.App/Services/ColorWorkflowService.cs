using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed record ColorWorkflowRequest(string? Red, string? Green, string? Blue,
    string? Luminance, string? Rgb, string Output, bool Aligned, bool Calibrate, string? Catalog, bool Solve = false);
internal sealed record ColorWorkflowResult(string? Output, string Message, bool Calibrated);

internal static partial class ColorWorkflowService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_color_combine_rgb_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CombineRgb(string request, nint cancel, out nint error);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_color_combine_lrgb_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CombineLrgb(string request, nint cancel, out nint error);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_color_calibrate_file_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CalibrateFile(string request, out nint error);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_wcs_fits_header_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint WcsHeaders(string wcs, out nint error);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_string_free")]
    private static partial void Free(nint value);

    public static void Validate(ColorWorkflowRequest request)
    {
        string[] inputs = Inputs(request);
        if (request.Rgb is null && (!request.Aligned || new[] { request.Red, request.Green, request.Blue }.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidOperationException("Choose R, G and B stacks and confirm they share the same pixel grid. Use a shared alignment reference when stacking.");
        if (request.Rgb is not null && !request.Calibrate)
            throw new InvalidOperationException("Enable Gaia calibration for an existing RGB stack.");
        if (request.Rgb is not null && string.IsNullOrWhiteSpace(request.Rgb))
            throw new InvalidOperationException("Choose an existing linear RGB stack.");
        if (inputs.Any(path => !File.Exists(path))) throw new FileNotFoundException("An input stack is missing.");
        if (inputs.Where((path, index) => inputs.Take(index).Any(other => StackSourceIdentity.RefersToSameFile(path, other))).Any())
            throw new InvalidOperationException("Choose a distinct source for each channel.");
        if (inputs.Any(path => StackSourceIdentity.RefersToSameFile(path, request.Output)))
            throw new InvalidOperationException("The output cannot replace any input stack.");
        if (Path.GetExtension(request.Output).ToLowerInvariant() is not (".fits" or ".fit" or ".fts" or ".xisf"))
            throw new InvalidOperationException("Choose a FITS or XISF output.");
    }

    private static string[] Inputs(ColorWorkflowRequest request) =>
        (request.Rgb is null ? new[] { request.Red, request.Green, request.Blue, request.Luminance } : [request.Rgb])
        .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!)).ToArray();

    public static async Task<ColorWorkflowResult> RunAsync(ColorWorkflowRequest request, CancellationToken token = default)
    {
        Validate(request);
        // Stage beside the destination so final publication is a same-volume
        // rename. Neither cancellation nor skipped calibration replaces output.
        string staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.Output))!, ".seiza-color-" + Guid.NewGuid().ToString("N"));
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Validate(request);
            var sources = Inputs(request).ToDictionary(path => path, CaptureSource);
            Directory.CreateDirectory(staging);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string composed = Path.Combine(staging, "composed" + Path.GetExtension(request.Output));
                if (request.Rgb is null)
                {
                    var combine = new JsonObject { ["red"] = request.Red, ["green"] = request.Green, ["blue"] = request.Blue,
                        ["output"] = composed, ["normalization"] = "none", ["crop"] = "none" };
                    if (!string.IsNullOrWhiteSpace(request.Luminance)) combine["luminance"] = request.Luminance;
                    nint error;
                    nint result = combine.ContainsKey("luminance") ? CombineLrgb(combine.ToJsonString(), 0, out error) : CombineRgb(combine.ToJsonString(), 0, out error);
                    if (result == 0) throw NativeString.TakeError(error, "Color composition failed.");
                    NativeMethods.FreeString(result);
                    if (error != 0) NativeMethods.FreeString(error);
                }
                string publish = composed;
                string message = "Combined aligned channels without rescaling their linear values.";
                if (request.Calibrate)
                {
                    token.ThrowIfCancellationRequested();
                    publish = Path.Combine(staging, "calibrated" + Path.GetExtension(request.Output));
                    var calibrationRequest = new JsonObject { ["input"] = request.Rgb ?? composed, ["output"] = publish,
                        ["catalog"] = request.Catalog, ["neutralizeBackground"] = true };
                    if (request.Solve)
                    {
                        SolveResult solution = SeizaCore.Solve(request.Rgb ?? composed, request.Catalog, cancellationToken: token);
                        if (solution.IsUncheckedHeader)
                            throw new InvalidOperationException("The solver could not verify the header coordinates. Check the image solution, or explicitly turn off verification to use its existing WCS.");
                        calibrationRequest["wcsHeaders"] = HeadersFor(solution);
                    }
                    string json = calibrationRequest.ToJsonString();
                    using JsonDocument outcome = JsonDocument.Parse(ReadPlatform(CalibrateFile(json, out nint error), error));
                    if (outcome.RootElement.GetProperty("status").GetString() != "calibrated")
                        return new ColorWorkflowResult(null, "Calibration skipped: " + outcome.RootElement.GetProperty("detail").GetString() + ". No output was changed.", false);
                    JsonElement fit = outcome.RootElement.GetProperty("calibration");
                    string gains = string.Join(", ", fit.GetProperty("gains").EnumerateArray().Select(value => value.GetDouble().ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
                    message = $"Gaia calibration succeeded: {fit.GetProperty("stars_measured").GetInt32()} stars measured; RGB gains {gains}. Linear output saved; display stretches preserve the calibrated balance.";
                }
                token.ThrowIfCancellationRequested();
                Validate(request);
                if (sources.Any(source => source.Value != CaptureSource(source.Key)))
                    throw new IOException("An input stack changed during color processing. No output was changed; try again with stable snapshots.");
                File.Move(publish, request.Output, overwrite: true);
                return new ColorWorkflowResult(request.Output, message, request.Calibrate);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { Gate.Release(); }
        }
    }

    private static (long Length, DateTime Modified, string? Identity) CaptureSource(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc, WindowsFileIdentity.TryGet(path));
    }

    internal static JsonNode HeadersFor(SolveResult solution)
    {
        var solved = JsonNode.Parse(JsonSerializer.Serialize(solution, SeizaJsonSerializerContext.Default.SolveResult))!;
        nint headers = WcsHeaders(solved["wcs"]!.ToJsonString(), out nint error);
        if (headers == 0) throw NativeString.TakeError(error, "Could not carry the solved image geometry into calibration.");
        if (error != 0) NativeMethods.FreeString(error);
        return JsonNode.Parse(NativeString.TakeOwned(headers, "{}"))!["cards"]!.DeepClone();
    }

    private static string ReadPlatform(nint result, nint error)
    {
        try
        {
            if (result == 0) throw new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Color calibration failed.");
            return Marshal.PtrToStringUTF8(result) ?? throw new SeizaCoreException("Invalid color calibration response.");
        }
        finally { if (result != 0) Free(result); if (error != 0) Free(error); }
    }

    internal static FitsImageProcessingConfiguration PreserveColor(FitsImageProcessingConfiguration processing)
    {
        var result = processing.Clone();
        foreach (var stage in result.StretchStack.Stages)
            if (stage.ColorStrategy == FitsStretchColorStrategy.Unlinked) stage.ColorStrategy = FitsStretchColorStrategy.Linked;
        return result;
    }

    public static FitsImageProcessingConfiguration ProcessingFor(string path, FitsImageProcessingConfiguration processing)
    {
        ImageSourceHeader? header = ImageSourceService.TryReadHeader(path);
        var result = header?.Text("SEIZAPCC") == "GAIA" ? PreserveColor(processing) : processing.Clone();
        result.PreserveDisplaySamples = header?.Text("SEIZATRF") == "DISPLAY";
        return result;
    }

    internal static bool HasDisplayTransfer(string path) => ImageSourceService.TryReadHeader(path)?.Text("SEIZATRF") == "DISPLAY";

    internal static FitsImageProcessingConfiguration InitialProcessingFor(string path) => InitialProcessingFromHeader(ImageSourceService.TryReadHeader(path));

    internal static FitsImageProcessingConfiguration InitialProcessingFromHeader(ImageSourceHeader? header) => header?.Text("SEIZATRF") == "DISPLAY"
        ? new(new FitsStretchStack([FitsStretchConfiguration.CreateIdentity()]), null) { PreserveDisplaySamples = true }
        : FitsImageProcessingConfiguration.Default.Clone();
}
