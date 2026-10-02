using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackReferenceSelectionTests
{
    private static readonly string[] Candidates =
        [@"C:\lights\unreadable.fits", @"C:\lights\best.fits", @"C:\lights\other.fits"];

    [Fact]
    public void PublishedReferenceContractKeepsParallelNullScoresForUnreadableFiles()
    {
        const string json = """
            {"schemaVersion":1,"referenceIndex":1,"referencePath":"C:\\lights\\best.fits",
             "scores":[null,{"stars":40,"medianStarArea":12.5,"background":1000,
             "backgroundVariation":2.5,"score":8.5},{"stars":32,"medianStarArea":14,
             "background":900,"backgroundVariation":3,"score":6}]}
            """;
        StackReferenceSelection selection = JsonSerializer.Deserialize(
            json, SeizaJsonSerializerContext.Default.StackReferenceSelection)!;

        selection.Validate(Candidates);

        Assert.Equal(1, selection.ReferenceIndex);
        Assert.Equal(Candidates[1], selection.ReferencePath);
        Assert.Null(selection.Scores[0]);
        Assert.Equal(8.5, selection.Scores[1]!.Score);
        Assert.Equal(6, selection.Scores[2]!.Score);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeReferenceIndicesAreRejected(int index)
    {
        StackReferenceSelection selection = ValidSelection() with { ReferenceIndex = index };

        Assert.Throws<InvalidDataException>(() => selection.Validate(Candidates));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnknownReferenceSchemaVersionsAreRejected(int schema)
    {
        StackReferenceSelection selection = ValidSelection() with { SchemaVersion = schema };

        Assert.Throws<InvalidDataException>(() => selection.Validate(Candidates));
    }

    [Theory]
    [InlineData(@"C:\lights\other.fits")]
    [InlineData(@"C:\lights\BEST.fits")]
    [InlineData("")]
    [InlineData(null)]
    public void SelectedPathMustExactlyIdentifyTheCandidateAtItsIndex(string? path)
    {
        StackReferenceSelection selection = ValidSelection() with { ReferencePath = path! };

        Assert.Throws<InvalidDataException>(() => selection.Validate(Candidates));
    }

    [Fact]
    public void SelectedScoreCannotBeNullAndScoreArrayMustStayParallel()
    {
        StackReferenceSelection valid = ValidSelection();
        StackReferenceSelection nullArray = valid with { Scores = null! };
        StackReferenceSelection shortArray = valid with { Scores = [valid.Scores[1]] };
        StackReferenceSelection nullSelected = valid with { Scores = [valid.Scores[1], null, null] };

        Assert.Throws<InvalidDataException>(() => nullArray.Validate(Candidates));
        Assert.Throws<InvalidDataException>(() => shortArray.Validate(Candidates));
        Assert.Throws<InvalidDataException>(() => nullSelected.Validate(Candidates));
        Assert.Throws<InvalidDataException>(() => valid.Validate([]));
    }

    [Theory]
    [InlineData("area", 0)]
    [InlineData("area", -1)]
    [InlineData("area", double.NaN)]
    [InlineData("area", double.PositiveInfinity)]
    [InlineData("background", double.NaN)]
    [InlineData("background", double.NegativeInfinity)]
    [InlineData("variation", -1)]
    [InlineData("variation", double.NaN)]
    [InlineData("variation", double.PositiveInfinity)]
    [InlineData("score", 0)]
    [InlineData("score", -1)]
    [InlineData("score", double.NaN)]
    [InlineData("score", double.PositiveInfinity)]
    public void MalformedScoresAreRejectedEvenOnUnselectedCandidates(string field, double value)
    {
        StackReferenceSelection selection = ValidSelection();
        StackReferenceScore score = selection.Scores[1]!;
        StackReferenceScore malformed = field switch
        {
            "area" => score with { MedianStarArea = value },
            "background" => score with { Background = value },
            "variation" => score with { BackgroundVariation = value },
            "score" => score with { Score = value },
            _ => throw new ArgumentException("Unknown score field.", nameof(field)),
        };
        StackReferenceSelection invalid = selection with { Scores = [malformed, score, null] };

        Assert.Throws<InvalidDataException>(() => invalid.Validate(Candidates));
    }

    [Fact]
    public void NegativeStarCountsAreRejectedWithoutRecomputingTheNativeRanking()
    {
        StackReferenceSelection selection = ValidSelection();
        StackReferenceScore score = selection.Scores[1]!;
        StackReferenceSelection invalid = selection with { Scores = [null, score with { Stars = -1 }, null] };

        Assert.Throws<InvalidDataException>(() => invalid.Validate(Candidates));
        // Physical background values may be negative after calibration.
        (selection with { Scores = [null, score with { Background = -100, BackgroundVariation = 0 }, null] })
            .Validate(Candidates);
    }

    [Fact]
    public void ReferenceServiceRejectsEmptyInputBeforeCallingNative()
    {
        Assert.Throws<ArgumentException>(() => { _ = StackReferenceService.ChooseAsync([]); });
        Assert.Throws<ArgumentNullException>(() => { _ = StackReferenceService.ChooseAsync(null!); });
    }

    [Fact]
    public async Task PreCancelledReferenceSelectionDoesNotEnterNativeScoring()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StackReferenceService.ChooseAsync(Candidates, cancellation.Token));
    }

    private static StackReferenceSelection ValidSelection() => new(
        1, 1, Candidates[1], [null, new StackReferenceScore(40, 12.5, 1000, 2.5, 8.5), null]);
}
