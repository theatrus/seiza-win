using System.Text.Json;

namespace Seiza.App.Services;

internal sealed record ProjectAnalysisProgress(string Phase, int Completed, int Total, string Detail, string? Path)
{
    internal double? Percent => Total > 0 ? Math.Clamp(100.0 * Completed / Total, 0, 100) : null;

    internal static ProjectAnalysisProgress Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(root.GetProperty("phase").GetString()!, root.GetProperty("completed").GetInt32(),
            root.GetProperty("total").GetInt32(), root.GetProperty("detail").GetString()!,
            root.GetProperty("path").GetString());
    }

    // Estimate only work for which we have observed throughput. Frame analysis
    // and replay have very different costs; never extrapolate one into the other.
    internal TimeSpan? EstimateRemaining(TimeSpan phaseElapsed) =>
        Completed >= 2 && Total > Completed && phaseElapsed > TimeSpan.Zero
            ? TimeSpan.FromSeconds(phaseElapsed.TotalSeconds / Completed * (Total - Completed))
            : null;
}
