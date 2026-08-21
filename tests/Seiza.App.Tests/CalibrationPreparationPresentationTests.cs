using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationPreparationPresentationTests
{
    [Fact]
    public void WarningTextDeduplicatesBeforeApplyingDisplayLimit()
    {
        string[] warnings = Enumerable.Range(1, 13)
            .Select(index => $"warning {index}")
            .Append("warning 1")
            .ToArray();

        string content = CalibrationPreparationWarningText.Format(warnings);

        Assert.Contains("warning 12", content, StringComparison.Ordinal);
        Assert.DoesNotContain("warning 13", content, StringComparison.Ordinal);
        Assert.EndsWith("…and 1 more warning(s).", content, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningTextIgnoresEmptyEntries()
    {
        Assert.Equal(
            "Keep this warning",
            CalibrationPreparationWarningText.Format(
                ["", "Keep this warning", " ", "Keep this warning"]));
    }
}
