using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Seiza.App.Interop;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace Seiza.App.Windows.Tests;

public sealed class ParallaxNativeVideoIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OfflineNativeLayerSceneExportsAndDecodesAsSilentH264WithoutChangingSources()
    {
        using var fixture = new LayerFixture();
        byte[] originalStarless = await File.ReadAllBytesAsync(fixture.Request.Inputs.Starless!);
        byte[] originalStars = await File.ReadAllBytesAsync(fixture.Request.Inputs.Stars!);
        using ParallaxVideo video = await ParallaxCore.PrepareAsync(fixture.Request);
        Assert.Equal(66, video.Width);
        Assert.Equal(48, video.Height);
        Assert.Equal(24, video.FramesPerSecond);
        Assert.Equal(12, video.FrameCount);
        Assert.Equal(400, video.Summary.BackgroundDistanceParsecs);
        byte[] nativeOpening = video.RenderBgraFrame(0);

        string path = Path.Combine(fixture.DirectoryPath, "native-scene.mp4");
        // Exercise the production ParallaxVideo overload, not a synthetic
        // IParallaxVideoFrameSource or a replacement renderer.
        await ParallaxVideoExporter.ExportAsync(video, path, ParallaxVideoCodec.H264);

        MediaClip clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        VideoEncodingProperties encoding = clip.GetVideoEncodingProperties();
        Assert.Equal((uint)video.Width, encoding.Width);
        Assert.Equal((uint)video.Height, encoding.Height);
        Assert.Equal(MediaEncodingSubtypes.H264, encoding.Subtype, ignoreCase: true);
        Assert.Equal((double)video.FramesPerSecond,
            (double)encoding.FrameRate.Numerator / encoding.FrameRate.Denominator, precision: 6);
        Assert.Equal(video.FrameCount,
            ParallaxVideoExporterTests.CountVideoSamples(await File.ReadAllBytesAsync(path)));
        Assert.InRange(clip.OriginalDuration.TotalSeconds, 0.499, 0.501);
        Assert.Empty(clip.EmbeddedAudioTracks);

        // Decode an actual video frame through Windows, beyond parsing MP4
        // metadata. Check native BGRA channel order survives the encode path.
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using ImageStream thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero,
            video.Width, video.Height, VideoFramePrecision.NearestFrame);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
        Assert.Equal((uint)video.Width, decoder.PixelWidth);
        Assert.Equal((uint)video.Height, decoder.PixelHeight);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] decoded = pixels.DetachPixelData();
        Assert.Equal(nativeOpening.Length, decoded.Length);
        double rgbDifference = Enumerable.Range(0, decoded.Length)
            .Where(index => index % 4 != 3)
            .Average(index => Math.Abs((double)decoded[index] - nativeOpening[index]));
        Assert.InRange(rgbDifference, 0, 20);
        Assert.Contains(decoded.Where((_, index) => index % 4 != 3), value => value > 20);

        Assert.Equal(originalStarless, await File.ReadAllBytesAsync(fixture.Request.Inputs.Starless!));
        Assert.Equal(originalStars, await File.ReadAllBytesAsync(fixture.Request.Inputs.Stars!));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Request.Inputs.CatalogDirectory!));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Request.Inputs.GaiaCache!));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".seiza-parallax-*.mp4"));
        if (Environment.GetEnvironmentVariable("SEIZA_PARALLAX_FIXTURE_DIRECTORY") is string diagnostics &&
            !string.IsNullOrWhiteSpace(diagnostics))
        {
            // Explicit opt-in for local GUI diagnostics; ordinary test runs
            // retain no input images or encoded movies outside their temp root.
            diagnostics = Path.GetFullPath(diagnostics);
            Directory.CreateDirectory(diagnostics);
            ParallaxInputs inputs = fixture.Request.Inputs with
            {
                Image = Path.Combine(diagnostics, "starless.png"),
                Starless = Path.Combine(diagnostics, "starless.png"),
                Stars = Path.Combine(diagnostics, "stars.png"),
                CatalogDirectory = Path.Combine(diagnostics, "catalogs"),
                GaiaCache = Path.Combine(diagnostics, "gaia-cache"),
            };
            Directory.CreateDirectory(inputs.CatalogDirectory);
            Directory.CreateDirectory(inputs.GaiaCache);
            File.Copy(fixture.Request.Inputs.Starless!, inputs.Starless!, overwrite: true);
            File.Copy(fixture.Request.Inputs.Stars!, inputs.Stars!, overwrite: true);
            File.Copy(path, Path.Combine(diagnostics, "native-scene.mp4"), overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(diagnostics, "request.json"),
                (fixture.Request with { Inputs = inputs }).ToJson());
            output.WriteLine($"Diagnostic fixture retained at {diagnostics}");
        }
        output.WriteLine($"seiza {Marshal.PtrToStringUTF8(NativeMethods.GetCoreVersion())}: " +
            $"native layers → H.264 → decoded {video.Width}×{video.Height}, " +
            $"{video.FrameCount} frames at {video.FramesPerSecond} fps, " +
            $"{clip.OriginalDuration.TotalSeconds:F3}s, no audio; mean RGB difference {rgbDifference:F2}.");
    }

    private sealed class LayerFixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "SeizaParallaxNativeEncoderTests-" + Guid.NewGuid().ToString("N"));
        internal ParallaxRequest Request { get; }

        internal LayerFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            string starless = Path.Combine(DirectoryPath, "starless.png");
            string stars = Path.Combine(DirectoryPath, "stars.png");
            string catalogs = Path.Combine(DirectoryPath, "catalogs");
            string gaiaCache = Path.Combine(DirectoryPath, "gaia-cache");
            Directory.CreateDirectory(catalogs);
            Directory.CreateDirectory(gaiaCache);
            WritePng(starless, stars: false);
            WritePng(stars, stars: true);
            Request = new()
            {
                Inputs = new()
                {
                    Image = starless, Starless = starless, Stars = stars,
                    CatalogDirectory = catalogs, GaiaCache = gaiaCache,
                    Wcs = new() { Crval = [56.75, 24.12], Crpix = [48, 32], Cd = [[-0.001, 0], [0, 0.001]] },
                },
                Scene = new() { DistanceParsecs = 400, UnmatchedDistanceParsecs = 400, Online = false },
                Video = new()
                {
                    Size = "66x48", Fps = 24,
                    Tour = [new() { Travel = 0, Hold = 0.1 }, new()
                    {
                        Name = "Point", Focus = [35, 27], Dolly = 0.3, RotateDegrees = 360,
                        Pan = 0.3, Travel = 0.3, Hold = 0.1,
                    }],
                },
            };
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);

        private static void WritePng(string path, bool stars)
        {
            // Same deterministic separated-layer fixture as ParallaxCoreTests:
            // an RGB gradient background and one Gaussian star. No SXT license,
            // real imagery, downloaded catalog, or network request is involved.
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
            using var png = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            byte[] header = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(header, width);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; header[9] = 2;
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", compressed.ToArray());
            WriteChunk(png, "IEND", []);
        }

        private static void WriteChunk(Stream png, string type, byte[] data)
        {
            byte[] name = Encoding.ASCII.GetBytes(type);
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(word, checked((uint)data.Length));
            png.Write(word); png.Write(name); png.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
            png.Write(word);
        }
    }
}
