using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationStackReadinessTests
{
    private static readonly string[] Lights = Enumerable.Range(1, 4)
        .Select(i => Path.GetFullPath($"light-{i}.fits")).ToArray();
    private static ImageStackGroup Group(params string[] lights) => new("all", null, lights);

    [Theory]
    [InlineData("stop")]
    [InlineData("keep")]
    [InlineData("exclude")]
    public void CompleteMatchingNeedsNoReviewApproval(string policy)
    {
        Assert.Null(CalibrationStackReadiness.ValidationMessage(policy, Lights,
            [false, false, false, false], [Group(Lights)]));
    }

    [Fact]
    public void DefaultStopsForIncompleteLightsButKeepIsExplicitlyAllowed()
    {
        bool[] missing = [false, true, false, true];
        Assert.Contains("incomplete", CalibrationStackReadiness.ValidationMessage("stop", Lights, missing, [Group(Lights)]));
        Assert.Null(CalibrationStackReadiness.ValidationMessage("keep", Lights, missing, [Group(Lights)]));
        Assert.Null(CalibrationStackReadiness.ValidationMessage("exclude", Lights, missing, [Group(Lights)]));
    }

    [Fact]
    public void ExcludeChecksEachOutputBeforeShowingTheOutputPicker()
    {
        ImageStackGroup[] groups = [Group(Lights[..2]), Group(Lights[2..]) with { FieldTitle = "Second field" }];
        Assert.Contains("Second field", CalibrationStackReadiness.ValidationMessage("exclude", Lights,
            [false, false, false, true], groups));
    }

    [Fact]
    public void UnreviewedLightsCannotMakeAnExcludedGroupAppearReady()
    {
        Assert.NotNull(CalibrationStackReadiness.ValidationMessage("exclude", Lights[..2], [false, false],
            [Group(Lights[1..3])]));
    }

    [Fact]
    public void InconsistentReviewIsNotReady()
    {
        Assert.NotNull(CalibrationStackReadiness.ValidationMessage("keep", Lights, [false], [Group(Lights)]));
    }
}
