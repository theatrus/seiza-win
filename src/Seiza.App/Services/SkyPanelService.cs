using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed record SkySourceStamp(long Length, DateTime Modified, string? Identity)
{
    public static SkySourceStamp Capture(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc, WindowsFileIdentity.TryGet(path));
    }
}

internal sealed record SkyPanelAnalysis(IReadOnlyList<SkyField> Fields,
    IReadOnlyList<SkySolveFailure> Failures, IReadOnlyDictionary<string, SkySourceStamp> Sources,
    IReadOnlyList<SkySolvedFrame> Solved, double[][] Overlaps)
{
    public IReadOnlyList<ImageStackGroup> SelectedGroups(IReadOnlyList<string> paths, bool splitByFilter, IReadOnlyDictionary<string, string?>? filters = null)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int[] indices = Enumerable.Range(0, Solved.Count).Where(i => selected.Contains(Solved[i].Path)).ToArray();
        var frames = indices.Select(i => Solved[i]).ToArray();
        var overlaps = indices.Select(i => indices.Select(j => Overlaps[i][j]).ToArray()).ToArray();
        // Removing a bridge panel can disconnect a field. Recompute output
        // components so unrelated remaining islands never share one stack.
        var outputs = new List<ImageStackGroup>();
        var indexByPath = frames.Select((frame, index) => (frame.Path, index))
            .ToDictionary(pair => pair.Path, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        for (int fieldIndex = 0; fieldIndex < Fields.Count; fieldIndex++)
        {
            SkyField field = Fields[fieldIndex];
            string[] fieldPaths = field.Panels.SelectMany(panel => panel.Frames).Select(frame => frame.Path)
                .Where(selected.Contains).ToArray();
            foreach (ImageStackGroup filter in ImageStackGrouping.Groups(fieldPaths, splitByFilter, filters))
            {
                int[] channel = filter.Inputs.Select(path => indexByPath[path]).ToArray();
                var channelFrames = channel.Select(i => frames[i]).ToArray();
                var channelOverlap = channel.Select(i => channel.Select(j => overlaps[i][j]).ToArray()).ToArray();
                // A panel in a different filter cannot provide registration
                // connectivity for this output's actual light frames.
                var regions = SkyPanelGrouping.Group(channelFrames, channelOverlap);
                for (int region = 0; region < regions.Count; region++)
                    outputs.Add(new ImageStackGroup($"{field.Id}-{filter.Id}-{region}", filter.Filter,
                        regions[region].Panels.SelectMany(panel => panel.Frames).Select(frame => frame.Path).ToArray())
                    {
                        FieldTitle = $"Sky field {fieldIndex + 1}" + (regions.Count > 1 ? $" region {region + 1}" : string.Empty),
                    });
            }
        }
        return outputs;
    }

    public bool SourcesUnchanged(IEnumerable<string> selected)
    {
        try { return selected.All(path => Sources.TryGetValue(path, out var stamp) && stamp == SkySourceStamp.Capture(path)); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal static partial class SkyPanelService
{
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_sky_overlaps_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Overlaps(string request, out nint error);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_string_free")]
    private static partial void Free(nint value);

    public static async Task<SkyPanelAnalysis> AnalyzeAsync(IReadOnlyList<string> paths, string? catalog,
        IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (paths.Count < 2) throw new ArgumentException("Select at least two lights to solve and group.");
        var solved = new List<SkySolvedFrame>();
        var failures = new List<SkySolveFailure>();
        var stamps = new Dictionary<string, SkySourceStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Solving {stamps.Count + failures.Count + 1}/{paths.Count}: {Path.GetFileName(path)}");
            try
            {
                SkySourceStamp before = SkySourceStamp.Capture(path);
                CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(path, token).ConfigureAwait(false);
                if (probe.Role is CalibrationFrameRoles.Bias or CalibrationFrameRoles.Dark or CalibrationFrameRoles.DarkFlat or CalibrationFrameRoles.Flat)
                    throw new InvalidOperationException("This is a calibration frame, not a light exposure.");
                if (probe.Signature.Width is not > 0 || probe.Signature.Height is not > 0)
                    throw new InvalidOperationException("Image dimensions could not be read.");
                SolveResult result = await PlateSolveService.SolveAsync(path, catalog, token).ConfigureAwait(false);
                ValidateSolution(result);
                if (before != SkySourceStamp.Capture(path))
                    throw new IOException("The source changed during solving. Solve it again.");
                solved.Add(new(path, result, probe.Signature.Width.Value, probe.Signature.Height.Value));
                stamps.Add(path, before);
            }
            catch (OperationCanceledException) { throw; }
            catch (CatalogNotReadyException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                SeizaCoreException or InvalidOperationException or ArgumentException)
            { failures.Add(new(path, exception.Message)); }
        }
        double[][] matrix = await Task.Run(() => OverlapMatrix(solved, progress, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new(SkyPanelGrouping.Group(solved, matrix, token), failures, stamps, solved, matrix);
    }

    internal static void ValidateSolution(SolveResult result)
    {
        if (result.IsUncheckedHeader || result.MatchedStars <= 0)
            throw new InvalidOperationException("No stars confirmed the image's coordinates.");
    }

    internal static double[][] OverlapMatrix(IReadOnlyList<SkySolvedFrame> frames, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var footprints = new JsonArray();
        foreach (SkySolvedFrame frame in frames)
        {
            token.ThrowIfCancellationRequested();
            footprints.Add(new JsonObject
            {
                ["width"] = frame.Width, ["height"] = frame.Height,
                ["wcsHeaders"] = ColorWorkflowService.HeadersFor(frame.Solution),
            });
        }
        var matrix = new double[frames.Count][];
        // Bound each native call to eight rows and about 4096 comparisons,
        // or one row for very large selections. The full matrix remains O(n²).
        int rowsPerBatch = Math.Clamp(4096 / Math.Max(1, frames.Count), 1, 8);
        for (int row = 0; row < frames.Count; row += rowsPerBatch)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"Comparing sky footprints: {row}/{frames.Count}");
            token.ThrowIfCancellationRequested();
            int count = Math.Min(rowsPerBatch, frames.Count - row);
            var request = new JsonObject { ["frames"] = footprints.DeepClone(), ["startRow"] = row, ["rowCount"] = count };
            nint response = Overlaps(request.ToJsonString(), out nint error);
            try
            {
                token.ThrowIfCancellationRequested();
                if (response == 0) throw new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Could not compare solved footprints.");
                var rows = JsonNode.Parse(Marshal.PtrToStringUTF8(response)!)!.AsArray();
                for (int k = 0; k < count; k++) matrix[row + k] = rows[k]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            }
            finally { if (response != 0) Free(response); if (error != 0) Free(error); }
        }
        token.ThrowIfCancellationRequested();
        return matrix;
    }
}
