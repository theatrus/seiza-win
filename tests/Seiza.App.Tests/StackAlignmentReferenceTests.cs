using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackAlignmentReferenceTests
{
    private static readonly string[] Candidates = [@"C:\lights\turned.fits", @"C:\lights\usable.fits"];

    [Fact]
    public void NativeSelectionIndicesReferToThePassedRankedArray()
    {
        const string json = """
            {"schemaVersion":1,"channelReferenceIndex":1,"channelReferencePath":"C:\\lights\\usable.fits",
             "skipped":[{"index":0,"path":"C:\\lights\\turned.fits","reason":"did not align"}]}
            """;
        StackAlignmentReferenceSelection selection = JsonSerializer.Deserialize(
            json, SeizaJsonSerializerContext.Default.StackAlignmentReferenceSelection)!;

        selection.Validate(Candidates);
        Assert.Equal(1, selection.ChannelReferenceIndex);
        Assert.Equal("did not align", Assert.Single(selection.Skipped).Reason);
    }

    [Fact]
    public void MalformedSelectionAndSkippedAccountingAreRejected()
    {
        StackAlignmentReferenceSelection valid = ValidSelection();
        StackAlignmentReferenceSelection[] invalid =
        [
            valid with { SchemaVersion = 2 },
            valid with { ChannelReferenceIndex = -1 },
            valid with { ChannelReferenceIndex = 2 },
            valid with { ChannelReferencePath = Candidates[0] },
            valid with { Skipped = null! },
            valid with { Skipped = [] },
            valid with { Skipped = [new(1, Candidates[0], "did not align")] },
            valid with { Skipped = [new(0, Candidates[1], "did not align")] },
            valid with { Skipped = [new(0, Candidates[0], " ")] },
            valid with { Skipped = [null!] },
        ];
        foreach (StackAlignmentReferenceSelection selection in invalid)
        {
            Assert.Throws<InvalidDataException>(() => selection.Validate(Candidates));
        }
    }

    [Fact]
    public void OldCoresDoNotClaimSharedReferenceSupport()
    {
        var capabilities = new StackingCoreCapabilities(false, false);
        SeizaCoreException exception = Assert.Throws<SeizaCoreException>(() => capabilities.RequireReferenceImage());
        Assert.Contains("published", exception.Message, StringComparison.Ordinal);
        Assert.Throws<SeizaCoreException>(() => new StackingCoreCapabilities(true, false).RequireReferenceImage(needsFallback: true));
        new StackingCoreCapabilities(true, false).RequireReferenceImage();
        new StackingCoreCapabilities(false, true).RequireReferenceImage(needsFallback: true);
    }

    [Fact]
    public async Task PreCancelledSharedReferenceOpeningDoesNotEnterNative()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageStackSession.OpenWithReferenceImageAsync(
            @"C:\shared.fits", Candidates, new ImageStackOptions(), new ImageStackCalibration(), cancellation.Token));
    }

    [Fact]
    public void SharedReferenceOpeningRejectsEmptyOrDuplicateCandidates()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = ImageStackSession.OpenWithReferenceImageAsync(
                @"C:\shared.fits", [], new ImageStackOptions(), new ImageStackCalibration());
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = ImageStackSession.OpenWithReferenceImageAsync(
                @"C:\shared.fits", [Candidates[0], Candidates[0].ToUpperInvariant()], new ImageStackOptions(), new ImageStackCalibration());
        });
    }

    [Fact]
    public void EmptyNativeStateRequiresAnUnintegratedReferenceIncludingExpandedCanvases()
    {
        LiveStackNativeState state = NativeState() with
        {
            AcceptedFrames = 0,
            ReferenceIntegrated = false,
            ReferenceImagePath = @"C:\shared.fits",
        };
        ImageStackSession.ValidateNativeState(state);
        ImageStackSession.ValidateNativeState(state with { AcceptedFrames = 3 });
        Assert.Throws<SeizaCoreException>(() => ImageStackSession.ValidateNativeState(state with { ReferenceIntegrated = null }));
        Assert.Throws<SeizaCoreException>(() => ImageStackSession.ValidateNativeState(state with { ReferenceIntegrated = true }));
        ImageStackSession.ValidateNativeState(state with { ReferenceImagePath = null });
        Assert.Throws<SeizaCoreException>(() => ImageStackSession.ValidateNativeState(state with { ReferenceImagePath = null, ReferenceIntegrated = true }));
        Assert.Throws<SeizaCoreException>(() => ImageStackSession.ValidateNativeState(state with { ReferenceImagePath = " " }));
        Assert.Throws<SeizaCoreException>(() => ImageStackSession.ValidateNativeState(state with { RejectedFrames = -1 }));
        ImageStackSession.ValidateNativeState(NativeState());
    }

    [Fact]
    public void CheckpointReferenceFieldsAreAdditiveAndAuthoritativeWhenCaptured()
    {
        LiveStackNativeState legacy = NativeState();
        LiveStackNativeState newer = legacy with { ReferenceIntegrated = true };
        Assert.True(legacy.DescribesSameCheckpoint(newer));
        Assert.False(newer.DescribesSameCheckpoint(legacy));
        LiveStackNativeState shared = newer with { ReferenceIntegrated = false, ReferenceImagePath = @"C:\shared.fits" };
        Assert.False(newer.DescribesSameCheckpoint(shared));
        Assert.False(shared.DescribesSameCheckpoint(shared with { ReferenceImagePath = @"C:\other.fits" }));
        Assert.False(shared.DescribesSameCheckpoint(shared with { ReferenceImagePath = null }));
        Assert.True(shared.DescribesSameCheckpoint(shared with { ReferenceImagePath = @"c:\SHARED.fits" }));
    }

    private static StackAlignmentReferenceSelection ValidSelection() =>
        new(1, 1, Candidates[1], [new(0, Candidates[0], "did not align")]);

    private static LiveStackNativeState NativeState() => new()
    {
        CoreVersion = "0.26.0",
        Width = 128,
        Height = 128,
        Channels = 1,
        AcceptedFrames = 1,
        ConfigurationFingerprint = new string('a', 64),
        InputMode = "calibrate-and-prepare",
        InputPaths = [],
    };
}
