using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed record StackInputRoutingResult(string[] Lights, string[] Calibration, string[] Notes, IReadOnlyDictionary<string, string?> Filters);
internal static class StackInputRouting
{
    public static async Task<StackInputRoutingResult> RouteAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        var lights = new List<string>();
        var calibration = new List<string>();
        var notes = new List<string>();
        var filters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(path, token).ConfigureAwait(false);
                filters[path] = probe.Signature.Filter;
                if (probe.Role is "bias" or "dark" or "dark-flat" or "flat") calibration.Add(path);
                else if (probe.IsMaster) notes.Add($"Set aside {Path.GetFileName(path)}: other master image.");
                else
                {
                    lights.Add(path);
                    if (probe.Role != "light") notes.Add($"{Path.GetFileName(path)}: unknown image type; offered as a light.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { notes.Add($"Set aside {Path.GetFileName(path)}: {e.Message}"); }
        }
        return new(lights.ToArray(), calibration.ToArray(), notes.ToArray(), filters);
    }
}
