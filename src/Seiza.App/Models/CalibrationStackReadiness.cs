namespace Seiza.App.Models;

// Presentation guard only: native matching and execution still own calibration decisions.
internal static class CalibrationStackReadiness
{
    public static string? ValidationMessage(string policy, IReadOnlyList<string> reviewedLights,
        IReadOnlyList<bool> missing, IReadOnlyList<ImageStackGroup> groups)
    {
        if (reviewedLights.Count != missing.Count)
            return "Calibration matching is out of date. Refresh from files in Review.";
        if (policy == "stop" && missing.Any(value => value))
            return "Some lights have incomplete calibration. Review matches or change Incomplete lights in Calibration.";
        if (policy == "exclude")
        {
            var complete = reviewedLights.Where((_, index) => !missing[index])
                .Select(LiveStackPath.NormalizeForComparison).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (ImageStackGroup group in groups)
                if (group.Inputs.Count(path => complete.Contains(LiveStackPath.NormalizeForComparison(path))) < 2)
                    return $"{group.Title} needs at least two lights after excluding incomplete calibration.";
        }
        return null;
    }
}
