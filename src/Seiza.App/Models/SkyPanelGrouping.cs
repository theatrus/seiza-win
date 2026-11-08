namespace Seiza.App.Models;

internal sealed record SkySolvedFrame(string Path, SolveResult Solution, long Width, long Height);
internal sealed record SkyPanel(string Id, IReadOnlyList<SkySolvedFrame> Frames)
{
    public string Position => FormattableString.Invariant(
        $"RA {Frames[0].Solution.CenterRaDegrees:F3}°, Dec {Frames[0].Solution.CenterDecDegrees:F3}°");
}
internal sealed record SkyField(string Id, IReadOnlyList<SkyPanel> Panels);
internal sealed record SkySolveFailure(string Path, string Reason);

/// <summary>Selection policy, not registration: repeated pointings require 85%
/// mutual footprint coverage; separate panels connect at 5%. Complete-link panel
/// clustering prevents a chain of dithers from swallowing an entire mosaic.</summary>
internal static class SkyPanelGrouping
{
    public static IReadOnlyList<SkyField> Group(IReadOnlyList<SkySolvedFrame> frames, double[][] overlaps,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int n = frames.Count;
        if (overlaps.Length != n)
            throw new ArgumentException("Invalid sky-footprint overlap matrix.", nameof(overlaps));
        foreach (double[] row in overlaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Length != n || row.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
                throw new ArgumentException("Invalid sky-footprint overlap matrix.", nameof(overlaps));
        }
        var panels = new List<List<int>>();
        // Sorting makes group identifiers and panel membership independent of
        // discovery/solve completion order.
        foreach (int i in Enumerable.Range(0, n).OrderBy(i => frames[i].Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var panel = panels.FirstOrDefault(p => p.All(j => Coverage(i, j) >= 0.85));
            if (panel is null) panels.Add([i]);
            else panel.Add(i);
        }
        var remaining = new HashSet<int>(Enumerable.Range(0, panels.Count));
        var fields = new List<SkyField>();
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int seed = remaining.Min();
            remaining.Remove(seed);
            var connected = new List<int> { seed };
            for (int k = 0; k < connected.Count; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (int other in remaining.Order().ToArray())
                    if (panels[connected[k]].Any(i => panels[other].Any(j => Coverage(i, j) >= 0.05)))
                    {
                        remaining.Remove(other);
                        connected.Add(other);
                    }
            }
            fields.Add(new SkyField($"field-{fields.Count + 1}", connected.Order().Select(p =>
                new SkyPanel($"panel-{p + 1}", panels[p].Select(i => frames[i]).ToArray())).ToArray()));
        }
        return fields;

        double Coverage(int i, int j) => Math.Min(overlaps[i][j], overlaps[j][i]);
    }

}
