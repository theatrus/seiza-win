using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static partial class ColorToolsService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_color_tools_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Process(string request, out nint error);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_string_free")]
    private static partial void Free(nint value);

    public static async Task<ColorToolsResult> RunAsync(ColorToolsRequest request, bool analyze = false, CancellationToken token = default)
    {
        request = request with { Inputs = [.. request.Inputs], Saturations = [.. request.Saturations],
            ClippingMasks = [.. request.ClippingMasks], ManualCoefficients = request.ManualCoefficients is null ? null : [.. request.ManualCoefficients],
            Plan = request.Plan?.Clone() };
        request.Validate(analyze);
        string directory = analyze ? Path.GetTempPath() : Path.GetDirectoryName(Path.GetFullPath(request.Output!))!;
        string staging = Path.Combine(directory, ".seiza-color-tools-" + Guid.NewGuid().ToString("N"));
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            request.Validate(analyze);
            var sources = request.ProtectedInputs.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, Capture);
            Directory.CreateDirectory(staging);
            string? temporary = analyze ? null : Path.Combine(staging, "result" + Path.GetExtension(request.Output));
            string json = request.ToNativeJson(temporary, analyze);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                nint result = Process(json, out nint error);
                string reportText;
                try
                {
                    if (result == 0) throw new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Color processing failed.");
                    reportText = Marshal.PtrToStringUTF8(result) ?? throw new SeizaCoreException("Invalid color response.");
                }
                finally { if (result != 0) Free(result); if (error != 0) Free(error); }
                token.ThrowIfCancellationRequested();
                request.Validate(analyze);
                if (sources.Any(source => source.Value != Capture(source.Key)))
                    throw new IOException("An input changed during processing. No output was replaced; use stable prepared images.");
                var report = JsonNode.Parse(reportText)!.AsObject();
                report["coreVersion"] = SeizaCore.Version;
                string status = report["status"]?.GetValue<string>() ?? throw new SeizaCoreException("Missing color result status.");
                string? published = null;
                if (!analyze && status == "success")
                {
                    request.ProtectDestination(request.Output!);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary!, request.Output!, overwrite: true);
                    published = request.Output;
                }
                using var document = JsonDocument.Parse(report.ToJsonString());
                return new ColorToolsResult(published, status, Summary(request, status, report), document.RootElement.Clone());
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            finally { Gate.Release(); }
        }
    }

    private static (long Length, DateTime Modified, string? Identity) Capture(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc, WindowsFileIdentity.TryGet(path));
    }

    private static string Summary(ColorToolsRequest request, string status, JsonObject report)
    {
        if (status == "skipped") return "Fit skipped: " + report["detail"]?.GetValue<string>() + " No output was changed.";
        if (status == "fitted")
        {
            string coefficients = string.Join(", ", report["plan"]!["coefficients"]!.AsArray()
                .Select(value => value!.GetValue<double>().ToString("G6", System.Globalization.CultureInfo.InvariantCulture)));
            var diagnostics = report["plan"]!["diagnostics"]!;
            string rms = diagnostics["validationFractionalRms"]!.GetValue<double>().ToString("G3", System.Globalization.CultureInfo.InvariantCulture);
            return $"Fit validated. Coefficients: {coefficients}. {diagnostics["validationStars"]} validation stars; fractional RMS {rms}. Review before building the subtraction preview.";
        }
        if (request.Tool == ColorTool.Continuum)
            return "Saved signed linear continuum residuals. Negative samples and missing coverage are preserved; no display stretch or hidden pedestal was applied.";
        string message = "Saved a display-referred image. Reopening starts with No Stretch; the scientific linear sources are unchanged.";
        if (report["balance"]?["channels"] is JsonArray channels)
        {
            string[] skipped = channels.Where(channel => channel?["status"]?.GetValue<string>() == "skipped")
                .Select(channel => channel!["channel"]!.GetValue<string>() + ": " + channel["reason"]?.GetValue<string>()).ToArray();
            if (skipped.Length > 0) message += " Balance skipped for " + string.Join("; ", skipped) + ".";
        }
        return message;
    }

    public static async Task SaveImageAsync(ColorToolsResult preview, ColorToolsRequest request, string path, CancellationToken token = default)
    {
        if (preview.Output is null || !File.Exists(preview.Output)) throw new InvalidOperationException("Build a preview before saving.");
        request = request with { Inputs = [.. request.Inputs], ClippingMasks = [.. request.ClippingMasks] };
        void Protect()
        {
            request.ProtectDestination(path);
            if (StackSourceIdentity.RefersToSameFile(preview.Output, path)) throw new InvalidOperationException("Choose a separate destination for the built image.");
        }
        Protect();
        string staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".seiza-color-save-" + Guid.NewGuid().ToString("N"));
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(staging);
            string temporary = Path.Combine(staging, "result" + Path.GetExtension(path));
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var json = new JsonObject { ["operation"] = "copy-result", ["inputs"] = new JsonArray(JsonValue.Create(preview.Output)), ["output"] = temporary };
                nint result = Process(json.ToJsonString(), out nint error);
                try { if (result == 0) throw new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Could not save the built image."); }
                finally { if (result != 0) Free(result); if (error != 0) Free(error); }
                token.ThrowIfCancellationRequested(); Protect();
                File.Move(temporary, path, overwrite: true);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } finally { Gate.Release(); } }
    }

    public static async Task SaveReportAsync(ColorToolsResult result, ColorToolsRequest request, string path, CancellationToken token = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a JSON report file.");
        string[] protectedInputs = request.ProtectedInputs.Append(result.Output).Where(input => !string.IsNullOrWhiteSpace(input)).Select(input => Path.GetFullPath(input!)).ToArray();
        void Protect()
        {
            if (protectedInputs.Any(input => StackSourceIdentity.RefersToSameFile(input, path)))
                throw new InvalidOperationException("A report cannot replace an input, plan or result image.");
        }
        Protect();
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".seiza-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, result.Report.GetRawText(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Protect();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
