namespace Seiza.App.Models;

internal sealed record StackReferenceScore(
    int Stars,
    double MedianStarArea,
    double Background,
    double BackgroundVariation,
    double Score);

internal sealed record StackReferenceSelection(
    int SchemaVersion,
    int ReferenceIndex,
    string ReferencePath,
    StackReferenceScore?[] Scores)
{
    public void Validate(IReadOnlyList<string> candidates)
    {
        if (SchemaVersion != 1 || Scores is null || Scores.Length != candidates.Count ||
            ReferenceIndex < 0 || ReferenceIndex >= candidates.Count ||
            !string.Equals(ReferencePath, candidates[ReferenceIndex], StringComparison.Ordinal) ||
            Scores[ReferenceIndex] is null ||
            Scores.OfType<StackReferenceScore>().Any(score =>
                score.Stars < 0 || !double.IsFinite(score.MedianStarArea) || score.MedianStarArea <= 0 ||
                !double.IsFinite(score.Background) || !double.IsFinite(score.BackgroundVariation) ||
                score.BackgroundVariation < 0 || !double.IsFinite(score.Score) || score.Score <= 0))
        {
            throw new InvalidDataException("Seiza returned an inconsistent reference selection.");
        }
    }
}
