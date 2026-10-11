using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

/// <summary>Native contract tests require the actual shipped C ABI DLL.</summary>
public sealed class ParallaxCoreTests
{
    [Fact]
    public void PreparedVideoRendersRandomFramesAndPaddedBgraWithoutTouchingPadding()
    {
        using var fixture = new Fixture();
        var warnings = new List<string>();
        using var video = ParallaxCore.Prepare(fixture.Request, new InlineProgress<ParallaxEvent>(update =>
        {
            if (update.Kind == "warning" && update.Message is string text) warnings.Add(text);
        }));
        Assert.Equal(12, video.FrameCount); Assert.Equal(66, video.Width); Assert.Equal(400, video.Summary.BackgroundDistanceParsecs);
        Assert.NotEmpty(warnings);
        byte[] last = video.RenderBgraFrame(11);
        _ = video.RenderBgraFrame(0);
        Assert.Equal(last, video.RenderBgraFrame(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => video.RenderBgraFrame(12));
        const int stride = 384;
        byte[] padded = Enumerable.Repeat((byte)0xA5, stride * video.Height).ToArray();
        video.RenderBgraFrame(11, padded, stride);
        for (int row = 0; row < video.Height; row++)
        {
            Assert.Equal(last.AsSpan(row * video.Width * 4, video.Width * 4).ToArray(), padded.AsSpan(row * stride, video.Width * 4).ToArray());
            Assert.All(padded.AsSpan(row * stride + video.Width * 4, stride - video.Width * 4).ToArray(), value => Assert.Equal(0xA5, value));
        }
        Assert.Throws<ArgumentException>(() => video.RenderBgraFrame(0, new byte[4], video.Width * 4));
    }

    [Fact]
    public async Task ReconfigurationSharesSceneWithoutFilesAndPreservesOldConcurrentFrames()
    {
        using var fixture = new Fixture();
        using var original = ParallaxCore.Prepare(fixture.Request);
        byte[] before = original.RenderBgraFrame(2);
        File.Delete(fixture.Request.Inputs.Starless!); File.Delete(fixture.Request.Inputs.Stars!);
        var settings = fixture.Request.Video with
        {
            Focus = [20, 30], Tour = [], Seconds = 0.5, Size = "80x60", Fps = 60, Quality = "high", RotateDegrees = [15, -345],
            Truck = 0.1, TruckAngleDegrees = 30, Zoom = 1.1, ZoomEnd = 1.3, Pan = 0.2, Easing = "linear", GrowthLimit = 3, FadeFrom = 5,
            Watermark = ParallaxWatermark.FromText("Test"), Labels = [new() { X = 20, Y = 30, Text = "Here" }], LabelColor = "#FFCC00",
        };
        using var second = ParallaxCore.Reconfigure(original, settings);
        Assert.Equal(80, second.Width); Assert.Equal(30, second.FrameCount);
        Assert.Equal(original.Summary.BackgroundFocus, second.Summary.BackgroundFocus);
        Assert.Equal(original.Summary.BackgroundDistanceParsecs, second.Summary.BackgroundDistanceParsecs);
        Assert.Equal(original.Summary.DetectedStars, second.Summary.DetectedStars);
        Assert.Equal(1.1, second.Summary.Fit.Zoom.Asked);
        Task<byte[]> oldFrame = Task.Run(() => original.RenderBgraFrame(2));
        Task<byte[]> newFrame = Task.Run(() => second.RenderBgraFrame(10));
        await Task.WhenAll(oldFrame, newFrame);
        Assert.Equal(before, await oldFrame); Assert.Equal(80 * 60 * 4, (await newFrame).Length);
        original.Dispose();
        Assert.Equal(80 * 60 * 4, second.RenderBgraFrame(29).Length);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ParallaxCore.Reconfigure(second, settings, cancellationToken: cancellation.Token));
        Assert.Equal(80 * 60 * 4, second.RenderBgraFrame(0).Length);
    }

    [Fact]
    public void CancellationDuringPreparationStopsBeforeDistanceLookupAndCanRetry()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var messages = new List<string>();
        Assert.Throws<OperationCanceledException>(() => ParallaxCore.Prepare(fixture.Request, new InlineProgress<ParallaxEvent>(update =>
        {
            if (update.Message is not string message) return;
            messages.Add(message);
            if (message.Contains("stars found in the stars image", StringComparison.Ordinal)) cancellation.Cancel();
        }), cancellation.Token));
        Assert.DoesNotContain(messages, message => message.Contains("matched to Gaia", StringComparison.Ordinal));
        using var retry = ParallaxCore.Prepare(fixture.Request);
        Assert.Equal(66 * 48 * 4, retry.RenderBgraFrame(0).Length);
    }

    [Fact]
    public void ThrowingProgressIsRethrownAfterNativeCancellationAndDoesNotLeakTheScene()
    {
        using var fixture = new Fixture();
        var expected = new InvalidOperationException("Synthetic event failure");
        var actual = Assert.Throws<InvalidOperationException>(() => ParallaxCore.Prepare(fixture.Request, new InlineProgress<ParallaxEvent>(_ => throw expected)));
        Assert.Same(expected, actual);
        using var retry = ParallaxCore.Prepare(fixture.Request);
        Assert.Equal(66, retry.Width);
    }

    [Fact]
    public void FramingSummaryReportsEdgesAndRefitDoesNotMutateOriginal()
    {
        using var fixture = new Fixture();
        fixture.Request.Video.Tour = []; fixture.Request.Video.Seconds = 0.5;
        fixture.Request.Video.Focus = [0, 0]; fixture.Request.Video.Start = "focus";
        using var original = ParallaxCore.Prepare(fixture.Request);
        Assert.False(original.Summary.Fit.Inside); Assert.NotNull(original.Summary.Fit.EdgeWarning);
        using var fitted = ParallaxCore.Reconfigure(original, fixture.Request.Video with { Focus = [47.5, 31.5], Start = "whole" });
        Assert.True(fitted.Summary.Fit.Inside); Assert.Null(fitted.Summary.Fit.EdgeWarning);
        Assert.False(original.Summary.Fit.Inside);
    }

    [Fact]
    public void NativeValidationRejectsOutsideStopsAndDisposedHandlesSafely()
    {
        using var fixture = new Fixture();
        using var original = ParallaxCore.Prepare(fixture.Request);
        var settings = fixture.Request.Video with { Tour = fixture.Request.Video.Tour.Select(stop => stop.DeepClone()).ToList() };
        settings.Tour[1].Focus = [96, 32];
        var exception = Assert.Throws<SeizaCoreException>(() => ParallaxCore.Reconfigure(original, settings));
        Assert.Contains("tour stop 2", exception.Message); Assert.Contains("outside", exception.Message);
        Assert.Equal(66, original.Width);
        original.Dispose();
        Assert.Throws<ObjectDisposedException>(() => original.RenderBgraFrame(0));
    }

    [Fact]
    public void DisposingOriginalInsideNativeRefitCallbackKeepsPInvokeLeaseAlive()
    {
        using var fixture = new Fixture();
        using var original = ParallaxCore.Prepare(fixture.Request);
        bool called = false;
        using var result = ParallaxCore.Reconfigure(original, fixture.Request.Video, new InlineProgress<ParallaxEvent>(_ =>
        {
            called = true; original.Dispose();
        }));
        Assert.True(called);
        Assert.Equal(66 * 48 * 4, result.RenderBgraFrame(0).Length);
        Assert.Throws<ObjectDisposedException>(() => original.RenderBgraFrame(0));
    }

    [Fact]
    public void PlannerReturnsEditableTitledStopsFromAnOfflineObjectCatalog()
    {
        using var fixture = new Fixture();
        fixture.WriteObjectCatalog();
        fixture.Request.AutoTour = new() { Targets = 1, Hold = 0.25, Motion = 0.2 };
        fixture.Request.Video.Tour = [];
        ParallaxTourPlan plan = ParallaxCore.Plan(fixture.Request);
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Contains(plan.Tour, stop => stop.Name == "NGC 9001" && stop.Title == "NGC 9001");
        Assert.Equal("NGC 9001", plan.FocusName);
        fixture.Request.AutoTour = null;
        fixture.Request.Scene.DistanceFocus = plan.Focus;
        fixture.Request.Video.Tour = plan.Tour;
        using var video = ParallaxCore.Prepare(fixture.Request);
        Assert.Equal(plan.Focus, video.Summary.BackgroundFocus);
        Assert.Equal(66 * 48 * 4, video.RenderBgraFrame(video.FrameCount - 1).Length);
    }

    [Fact]
    public void LoopClosureTimingAndTitleRefitMatchNativeWithoutRebuildingScene()
    {
        using var fixture = new Fixture();
        var composition = new ParallaxComposition
        {
            Motion = ParallaxMotion.Tour, Stops = fixture.Request.Video.Tour, TourLoop = true, TourTitles = true,
            Width = 640, Height = 360, Fps = 24,
        };
        composition.Stops[1].Title = "Target"; composition.Stops[1].Hold = 2;
        fixture.Request.Video = composition.VideoSettings(false);
        using var original = ParallaxCore.Prepare(fixture.Request);
        Assert.Equal(3.3, composition.LoopClosingDuration, precision: 8);
        Assert.Equal((int)Math.Round(composition.Duration * 24), original.FrameCount);
        Assert.Equal(composition.Stops.Count + 1, original.Summary.Fit.Stops.Length);
        using var untitled = ParallaxCore.Reconfigure(original, fixture.Request.Video with { TourTitles = false });
        Assert.NotEqual(original.RenderBgraFrame(30), untitled.RenderBgraFrame(30));
        Assert.Equal(original.Summary.DetectedStars, untitled.Summary.DetectedStars);
    }

    [Fact]
    public void CoincidentLoopStopsKeepMovingAndTheJoinIsOneNormalFrameInterval()
    {
        using var fixture = new Fixture();
        fixture.Request.Video.Size = "320x240";
        fixture.Request.Video.Tour = [new() { Travel = 0, Hold = 1 }, new() { Travel = 1, Hold = 1 }];
        fixture.Request.Video.TourLoop = true; fixture.Request.Video.TourGlide = 0;
        using var video = ParallaxCore.Prepare(fixture.Request);
        byte[] opening = video.RenderBgraFrame(0), second = video.RenderBgraFrame(1), last = video.RenderBgraFrame(video.FrameCount - 1), penultimate = video.RenderBgraFrame(video.FrameCount - 2);
        Assert.NotEqual(opening, video.RenderBgraFrame(12));
        static double Difference(byte[] first, byte[] next) => first.Zip(next).Average(pair => Math.Abs((double)pair.First - pair.Second));
        Assert.True(Difference(last, opening) <= 2 * Math.Max(Difference(opening, second), Difference(penultimate, last)) + 0.01);
        Assert.True(video.Summary.Fit.Inside);
    }

    [Fact]
    public void MissingObjectCatalogAndUnsupportedTypefaceGlyphWarnWithoutLosingScene()
    {
        using var fixture = new Fixture();
        fixture.Request.Inputs.Objects = Path.Combine(Path.GetDirectoryName(fixture.Request.Inputs.Image)!, "missing-objects.bin");
        using var original = ParallaxCore.Prepare(fixture.Request);
        var warnings = new List<string>();
        using var labelled = ParallaxCore.Reconfigure(original, fixture.Request.Video with { Overlay = true, Watermark = ParallaxWatermark.FromText("Iris 🌌") },
            new InlineProgress<ParallaxEvent>(update => { if (update.Kind == "warning" && update.Message is string message) warnings.Add(message); }));
        Assert.Contains(warnings, warning => warning.Contains("no catalogued objects labelled", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("typeface", StringComparison.Ordinal) && warning.Contains("🌌", StringComparison.Ordinal));
        Assert.Equal(original.Summary.DetectedStars, labelled.Summary.DetectedStars);
        Assert.Equal(66 * 48 * 4, labelled.RenderBgraFrame(0).Length);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", "parallax-" + Guid.NewGuid().ToString("N"));
        internal ParallaxRequest Request { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            string starless = Path.Combine(_directory, "starless.png"), stars = Path.Combine(_directory, "stars.png");
            WritePng(starless, false); WritePng(stars, true);
            Request = new()
            {
                Inputs = new()
                {
                    Image = starless, Starless = starless, Stars = stars, CatalogDirectory = _directory,
                    Wcs = new() { Crval = [56.75, 24.12], Crpix = [48, 32], Cd = [[-0.001, 0], [0, 0.001]] },
                },
                Scene = new() { DistanceParsecs = 400 },
                Video = new()
                {
                    Size = "66x48", Fps = 24,
                    Tour = [new() { Travel = 0, Hold = 0.1 }, new() { Name = "Point", Focus = [35, 27], Dolly = 0.3, RotateDegrees = 360, Pan = 0.3, Travel = 0.3, Hold = 0.1 }],
                },
            };
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        internal void WriteObjectCatalog()
        {
            string path = Path.Combine(_directory, "objects.bin");
            // The core explicitly supports its fixed-layout legacy SEIZAOB1
            // format. This tiny offline fixture needs no downloaded catalog.
            using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
            writer.Write("SEIZAOB1"u8); writer.Write(1u); writer.Write((byte)3); // nebula
            writer.Write(56.75); writer.Write(24.12); writer.Write(float.NaN);
            writer.Write(1.0f); writer.Write(1.0f); writer.Write(float.NaN);
            byte[] name = "NGC 9001"u8.ToArray(); writer.Write(checked((ushort)name.Length)); writer.Write(name); writer.Write((ushort)0);
            Request.Inputs.Objects = path;
        }

        private static void WritePng(string path, bool stars)
        {
            const int width = 96, height = 64;
            byte[] samples = new byte[(width * 3 + 1) * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * (width * 3 + 1) + 1 + x * 3;
                    if (stars)
                    {
                        byte light = (byte)(240 * Math.Exp(-((x - 35.0) * (x - 35) + (y - 27.0) * (y - 27)) / 4));
                        samples[index] = samples[index + 1] = samples[index + 2] = light;
                    }
                    else { samples[index] = (byte)(30 + x); samples[index + 1] = (byte)(20 + y); samples[index + 2] = 70; }
                }
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(samples);
            using var output = File.Create(path);
            output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            byte[] header = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header, width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; header[9] = 2;
            WriteChunk(output, "IHDR", header); WriteChunk(output, "IDAT", compressed.ToArray()); WriteChunk(output, "IEND", []);
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            byte[] name = Encoding.ASCII.GetBytes(type);
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, checked((uint)data.Length)); output.Write(word);
            output.Write(name); output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); output.Write(word);
        }
    }
}
