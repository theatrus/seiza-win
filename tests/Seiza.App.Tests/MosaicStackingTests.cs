using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Interop;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class MosaicStackingTests
{
    [Fact]
    public void SnrAbiIncludesTheAppendedBinningField()
    {
        Assert.Equal(80, Marshal.SizeOf<NativeSnrSample>());
        Assert.Equal(72, Marshal.OffsetOf<NativeSnrSample>(nameof(NativeSnrSample.BinFactor)).ToInt32());
    }
    [Fact]
    public void CanvasDefaultsAndHostPolicyDoNotChangeNativeOptionsIdentity()
    {
        var fixedOptions = new ImageStackOptions();
        var mosaic = new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded };
        Assert.Equal(0.6, fixedOptions.MinimumOverlap);
        Assert.Equal(0.05, mosaic.MinimumOverlap);
        using JsonDocument json = JsonDocument.Parse(mosaic.ToJson());
        Assert.Equal(0.05, json.RootElement.GetProperty("acceptance").GetProperty("minimum_overlap_fraction").GetDouble());
        Assert.False(json.RootElement.TryGetProperty("canvasMode", out _));
        Assert.False(json.RootElement.TryGetProperty("maximumCanvasMegasamples", out _));
        mosaic.MinimumOverlap = 0.12;
        Assert.Equal(0.12, mosaic.Copy().MinimumOverlap);
        fixedOptions.MinimumOverlap = 0.05;
        Assert.Equal(fixedOptions.ToJson(), new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded }.ToJson());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void InvalidCanvasLimitsAreRejected(int limit)
    {
        var options = new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded, MaximumCanvasMegasamples = limit };
        Assert.NotNull(options.ValidationMessage);
        Assert.Throws<ArgumentException>(() => options.ToJson());
    }

    [Fact]
    public void BayerDrizzleCannotBeSilentlyUsedOnAMosaic()
    {
        Assert.NotNull(new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded, CfaIntegration = StackCfaIntegration.BayerDrizzle }.ValidationMessage);
        Assert.Throws<SeizaCoreException>(() => new StackingCoreCapabilities(true, true).RequireExpandedCanvas());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    public void ExpandedCanvasRefusesLocalNormalizationBeforeReadingAnyInputs(int mode, bool supported)
    {
        var options = new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded, Normalization = (StackNormalizationMode)mode };
        if (supported) Assert.Null(options.ValidationMessage);
        else
        {
            Assert.Contains("Local normalization", options.ValidationMessage);
            Assert.Throws<ArgumentException>(() => { _ = ImageStackSession.OpenExpandedAsync(
                [@"C:\missing-first.fits", @"C:\missing-second.fits"], options, new ImageStackCalibration()); });
        }
        options.CanvasMode = StackCanvasMode.Reference;
        Assert.Null(options.ValidationMessage);
    }

    [Fact]
    public void MosaicPolicyCannotLeakIntoFixedOrLiveOpenPaths()
    {
        var options = new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded };
        Assert.Throws<ArgumentException>(() => { _ = ImageStackSession.OpenAsync(@"C:\anchor.fits", options, new ImageStackCalibration()); });
        Assert.Throws<ArgumentException>(() => { _ = ImageStackSession.OpenWithReferenceImageAsync(@"C:\grid.fits", [@"C:\light.fits"], options, new ImageStackCalibration()); });
        Assert.Throws<ArgumentException>(() => new LiveStackRunConfiguration
        {
            WatchFolder = @"C:\lights", SessionRootDirectory = @"C:\sessions", Options = options,
        }.Validate());
        var request = new ImageStackRequest([@"C:\first.fits", @"C:\second.fits"], @"C:\out.fits", options,
            new ImageStackCalibration()) { AlignmentReferencePath = @"C:\grid.fits" };
        Assert.Throws<ArgumentException>(() => ImageStackValidation.ValidateBatch([new(new("all", null, request.Inputs), request)]));
    }

    [Fact]
    public async Task PublishedBatchMosaicCountsItsAnchorOnceAndCarriesPlanningNotes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "anchor.fits"), second = Path.Combine(directory, "offset.fits");
            StackingOptionsNativeTests.WriteStarField(first, 0, bayer: false);
            StackingOptionsNativeTests.WriteStarField(second, 20, bayer: false);
            string[] inputs = [first, second];
            var request = new ImageStackRequest(inputs, Path.Combine(directory, "out.fits"),
                new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded, MaximumCanvasMegasamples = 1 },
                new ImageStackCalibration());
            ImageStackBatchResult batch = await ImageStackService.StackBatchAsync([new(new("all", null, inputs), request)], progress: null);
            ImageStackResult result = Assert.Single(batch.Results);
            Assert.Equal(2, result.AcceptedFrames);
            Assert.Equal(0, result.RejectedFrames);
            Assert.Equal(2, result.Dispositions.Count);
            Assert.Contains("Expanded mosaic canvas", result.CanvasNote);
            Assert.True(File.Exists(result.OutputPath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PublishedCanvasMemoryCeilingIsEnforced()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "anchor.fits"), second = Path.Combine(directory, "offset.fits");
            StackingOptionsNativeTests.WriteStarField(first, 0, bayer: false, side: 1024);
            StackingOptionsNativeTests.WriteStarField(second, 20, bayer: false, side: 1024);
            var options = new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded, MaximumCanvasMegasamples = 1 };
            var error = await Assert.ThrowsAsync<SeizaCoreException>(() => ImageStackSession.OpenExpandedAsync([first, second], options, new ImageStackCalibration()));
            Assert.Contains("sample", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PublishedMosaicPlansAnEmptyCanvasThenPushesEveryLightAndReintegrates()
    {
        Assert.True(StackingCoreCapabilities.Current.SupportsExpandedCanvas);
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "anchor.fits"), second = Path.Combine(directory, "offset.fits");
            StackingOptionsNativeTests.WriteStarField(first, 0, bayer: false);
            StackingOptionsNativeTests.WriteStarField(second, 20, bayer: false);
            byte[] original = SHA256.HashData(File.ReadAllBytes(first));
            var options = new ImageStackOptions { CanvasMode = StackCanvasMode.Expanded, MaximumCanvasMegasamples = 1 };
            await using var session = await ImageStackSession.OpenExpandedAsync([first, second], options, new ImageStackCalibration());
            var state = await session.GetStateAsync();
            Assert.Equal(0, state.AcceptedFrames);
            Assert.True(state.Width > 256 || state.Height > 256);
            Assert.Contains("Expanded mosaic canvas", session.CanvasNote);
            Assert.True((await session.PushFrameAsync(first)).Disposition.Accepted);
            Assert.True((await session.PushFrameAsync(second)).Disposition.Accepted);
            Assert.Equal(2, (await session.GetCountsAsync()).AcceptedFrames);
            using ImageStackSnapshot snapshot = await session.ReintegrateAsync(0, 0, progress: null);
            Assert.Equal(2, snapshot.AcceptedFrames);
            string output = Path.Combine(directory, "mosaic.fits");
            await snapshot.WriteFitsAsync(output, [first, second]);
            Assert.True(File.Exists(output));
            await Assert.ThrowsAsync<SeizaCoreException>(() => snapshot.WriteFitsAsync(first, [first, second]));
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(first)));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageStackSession.OpenExpandedAsync([first, second], options, new ImageStackCalibration(), canceled.Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PublishedFixedStackPreviewRemainsRenderable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "anchor.fits");
            StackingOptionsNativeTests.WriteStarField(path, 0, bayer: false);
            await using var session = await ImageStackSession.OpenAsync(path, new ImageStackOptions(), new ImageStackCalibration());
            RenderedImageData preview = await session.RenderPreviewAsync(LiveStackRunConfiguration.DefaultPreviewProcessingJson, 128);
            Assert.True(preview.Width <= 128 && preview.Height <= 128);
            Assert.NotEmpty(preview.Bgra);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
