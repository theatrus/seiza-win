using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed class ParallaxRealSourceIntegrationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new() { WriteIndented = true };

    [ExistingMovieFact]
    public async Task ExistingGuiMovieHasExpectedEncodingAndDecodesWithoutChangingTheMovie()
    {
        string movie = Path.GetFullPath(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_EXISTING_MOVIE")!);
        Dictionary<string, string> originalHashes = await HashFilesAsync([movie]);
        string diagnostics = Path.GetFullPath(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_OUTPUT_DIRECTORY") ??
            Path.GetDirectoryName(movie)!);
        Directory.CreateDirectory(diagnostics);
        string prefix = Path.Combine(diagnostics, Path.GetFileNameWithoutExtension(movie) + "-verified-" + Guid.NewGuid().ToString("N"));
        MediaClip clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(movie));
        VideoEncodingProperties encoding = clip.GetVideoEncodingProperties();
        Assert.Equal(1280u, encoding.Width);
        Assert.Equal(720u, encoding.Height);
        Assert.Equal(MediaEncodingSubtypes.H264, encoding.Subtype, ignoreCase: true);
        Assert.Equal(24.0, (double)encoding.FrameRate.Numerator / encoding.FrameRate.Denominator, precision: 6);
        Assert.Equal(48, ParallaxVideoExporterTests.CountVideoSamples(await File.ReadAllBytesAsync(movie)));
        Assert.InRange(clip.OriginalDuration.TotalSeconds, 1.999, 2.001);
        Assert.Empty(clip.EmbeddedAudioTracks);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        byte[] first = await DecodeAsync(composition, TimeSpan.Zero, 1280, 720);
        byte[] last = await DecodeAsync(composition, ParallaxVideoExporter.FrameTime(47, 24), 1280, 720);
        await SavePngAsync(prefix + "-decoded-first.png", first, 1280, 720);
        await SavePngAsync(prefix + "-decoded-last.png", last, 1280, 720);
        Assert.Equal(originalHashes[movie], (await HashFilesAsync([movie]))[movie]);
        output.WriteLine($"Verified GUI MP4: {movie}; 1280×720, 48 H.264 frames, 24 fps, {clip.OriginalDuration.TotalSeconds:F3}s, silent; SHA-256 unchanged.");
        output.WriteLine($"Decoded diagnostic PNG prefix: {prefix}");
    }

    [RealSourceFact]
    public async Task SuppliedSeparatedTiffsPrepareExportAndDecodeOfflineWithoutChangingOriginals()
    {
        string sourceDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_SOURCE_DIRECTORY")!);
        string starless = Directory.EnumerateFiles(sourceDirectory, "*-sxt.tif").Order(StringComparer.OrdinalIgnoreCase).First();
        string stem = Path.GetFileName(starless)[..^"-sxt.tif".Length];
        string source = Path.Combine(sourceDirectory, stem + ".tif");
        string stars = Path.Combine(sourceDirectory, stem + "-sxt-stars.tif");
        Assert.True(File.Exists(source), $"Missing original source: {source}");
        Assert.True(File.Exists(stars), $"Missing separated stars layer: {stars}");
        string diagnostics = Path.GetFullPath(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_OUTPUT_DIRECTORY") ??
            Path.Combine(Path.GetTempPath(), "SeizaParallaxRealQA"));
        string relative = Path.GetRelativePath(sourceDirectory, diagnostics);
        Assert.True(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative),
            "Diagnostic output must be outside the supplied source directory.");
        Dictionary<string, string> originalHashes = await HashFilesAsync(Directory.EnumerateFiles(sourceDirectory));
        try
        {
            Directory.CreateDirectory(diagnostics);
            string prefix = Path.Combine(diagnostics, $"{stem}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            string gaiaCache = Path.Combine(diagnostics, "offline-gaia-cache");
            Directory.CreateDirectory(gaiaCache);
            string sidecar = Path.Combine(sourceDirectory, stem + ".wcs");
            var request = new ParallaxRequest
            {
                Inputs = new()
                {
                    Image = source, Starless = starless, Stars = stars, GaiaCache = gaiaCache,
                    // Null uses the already-installed default native catalog;
                    // Prepare never installs or downloads a solve catalog.
                    CatalogDirectory = null,
                    Wcs = File.Exists(sidecar) ? ReadTanWcs(await File.ReadAllBytesAsync(sidecar)) : null,
                },
                Scene = new() { DistanceParsecs = 430, UnmatchedDistanceParsecs = 250, Online = false },
                Video = new() { Size = "1280x720", Fps = 24, Seconds = 2, Dolly = 0.2 },
            };
            await File.WriteAllTextAsync(prefix + "-request.json", request.ToJson());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using ParallaxVideo video = await ParallaxCore.PrepareAsync(request,
                new InlineProgress<ParallaxEvent>(update => output.WriteLine($"{update.Kind}: {update.Message}")), cancellation.Token);
            Assert.Equal(1280, video.Width);
            Assert.Equal(720, video.Height);
            Assert.Equal(24, video.FramesPerSecond);
            Assert.Equal(48, video.FrameCount);
            Assert.Equal(430, video.Summary.BackgroundDistanceParsecs);
            Assert.True(video.Summary.DetectedStars > 0, "The real stars layer should produce detected stars.");
            byte[] first = video.RenderBgraFrame(0, cancellation.Token);
            byte[] last = video.RenderBgraFrame(video.FrameCount - 1, cancellation.Token);
            await SavePngAsync(prefix + "-native-first.png", first, video.Width, video.Height);
            await SavePngAsync(prefix + "-native-last.png", last, video.Width, video.Height);

            string movie = prefix + ".mp4";
            await ParallaxVideoExporter.ExportAsync(video, movie, ParallaxVideoCodec.H264,
                cancellationToken: cancellation.Token);
            MediaClip clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(movie));
            VideoEncodingProperties encoding = clip.GetVideoEncodingProperties();
            Assert.Equal((uint)video.Width, encoding.Width);
            Assert.Equal((uint)video.Height, encoding.Height);
            Assert.Equal(MediaEncodingSubtypes.H264, encoding.Subtype, ignoreCase: true);
            Assert.Equal((double)video.FramesPerSecond,
                (double)encoding.FrameRate.Numerator / encoding.FrameRate.Denominator, precision: 6);
            Assert.Equal(video.FrameCount, ParallaxVideoExporterTests.CountVideoSamples(await File.ReadAllBytesAsync(movie)));
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 1.999, 2.001);
            Assert.Empty(clip.EmbeddedAudioTracks);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            byte[] decodedFirst = await DecodeAsync(composition, TimeSpan.Zero, video.Width, video.Height);
            byte[] decodedLast = await DecodeAsync(composition,
                ParallaxVideoExporter.FrameTime(video.FrameCount - 1, video.FramesPerSecond), video.Width, video.Height);
            await SavePngAsync(prefix + "-decoded-first.png", decodedFirst, video.Width, video.Height);
            await SavePngAsync(prefix + "-decoded-last.png", decodedLast, video.Width, video.Height);
            double difference = Enumerable.Range(0, first.Length).Where(index => index % 4 != 3)
                .Average(index => Math.Abs((double)first[index] - decodedFirst[index]));
            Assert.InRange(difference, 0, 35);
            Assert.Empty(Directory.EnumerateFileSystemEntries(gaiaCache));
            Assert.Empty(Directory.GetFiles(diagnostics, ".seiza-parallax-*.mp4"));
            await File.WriteAllTextAsync(prefix + "-summary.json", JsonSerializer.Serialize(new
            {
                video.Summary, DurationSeconds = clip.OriginalDuration.TotalSeconds,
                MeanOpeningRgbDifference = difference, OriginalSha256 = originalHashes,
            }, DiagnosticJsonOptions));
            output.WriteLine($"Real-source H.264 diagnostic: {movie}");
            output.WriteLine($"{video.Width}×{video.Height}, {video.FrameCount} frames, {video.FramesPerSecond} fps, " +
                $"{clip.OriginalDuration.TotalSeconds:F3}s, silent; mean RGB encode/decode difference {difference:F2}.");
        }
        finally
        {
            Dictionary<string, string> finalHashes = await HashFilesAsync(Directory.EnumerateFiles(sourceDirectory));
            Assert.Equal(originalHashes.Keys.Order(), finalHashes.Keys.Order());
            foreach ((string path, string hash) in originalHashes) Assert.Equal(hash, finalHashes[path]);
            output.WriteLine($"SHA-256 verified unchanged for all {originalHashes.Count} original source/sidecar/tour files.");
        }
    }

    [Fact]
    public void TanSidecarConvertsFitsOneBasedReferencePixelsWithoutChangingSkyTransform()
    {
        string[] cards =
        {
            "SIMPLE  = T", "CTYPE1  = 'RA---TAN'", "CTYPE2  = 'DEC--TAN'",
            "CRVAL1  = 315.13658124092", "CRVAL2  = 67.777619134104",
            "CRPIX1  = 4798.4996532079", "CRPIX2  = 3174.0006521529",
            "CD1_1   = -9.3630090186883E-5", "CD1_2   = -2.5755866238438E-4",
            "CD2_1   = 2.5754411506530E-4", "CD2_2   = -9.3585910274246E-5", "END",
        };
        string header = string.Concat(cards.Select(card => card.PadRight(80)));
        WcsResult wcs = ReadTanWcs(Encoding.ASCII.GetBytes(header));
        Assert.Equal([315.13658124092, 67.777619134104], wcs.Crval);
        Assert.Equal([4797.4996532079, 3173.0006521529], wcs.Crpix);
        Assert.Equal(-9.3630090186883E-5, wcs.Cd[0][0]);
        Assert.Equal(-2.5755866238438E-4, wcs.Cd[0][1]);
        Assert.Equal(2.5754411506530E-4, wcs.Cd[1][0]);
        Assert.Equal(-9.3585910274246E-5, wcs.Cd[1][1]);
        Assert.Null(wcs.Sip);
    }

    private static WcsResult ReadTanWcs(byte[] header)
    {
        var cards = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int offset = 0; offset + 80 <= header.Length; offset += 80)
        {
            string card = Encoding.ASCII.GetString(header, offset, 80);
            string key = card[..8].Trim();
            if (key == "END") break;
            if (card[8] == '=') cards[key] = card[10..].Split('/')[0].Trim().Trim('\'');
        }
        if (cards.GetValueOrDefault("CTYPE1") != "RA---TAN" || cards.GetValueOrDefault("CTYPE2") != "DEC--TAN")
            throw new InvalidDataException("This diagnostic sidecar reader supports linear TAN WCS only; omit the sidecar to auto-solve.");
        double Number(string key)
        {
            double value = double.Parse(cards[key].Replace('D', 'E'), CultureInfo.InvariantCulture);
            return double.IsFinite(value) ? value : throw new InvalidDataException($"Non-finite WCS value {key}.");
        }
        return new()
        {
            Crval = [Number("CRVAL1"), Number("CRVAL2")],
            // WcsExportService performs the inverse +1 conversion on export.
            Crpix = [Number("CRPIX1") - 1, Number("CRPIX2") - 1],
            Cd = [[Number("CD1_1"), Number("CD1_2")], [Number("CD2_1"), Number("CD2_2")]],
        };
    }

    private static async Task<Dictionary<string, string>> HashFilesAsync(IEnumerable<string> paths)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes.Add(path, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        }
        return hashes;
    }

    private static async Task<byte[]> DecodeAsync(MediaComposition composition, TimeSpan timestamp, int width, int height)
    {
        using ImageStream thumbnail = await composition.GetThumbnailAsync(timestamp, width, height, VideoFramePrecision.NearestFrame);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
        Assert.Equal((uint)width, decoder.PixelWidth);
        Assert.Equal((uint)height, decoder.PixelHeight);
        return (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }

    private static async Task SavePngAsync(string path, byte[] bgra, int width, int height)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using IRandomAccessStream stream = file.AsRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RealSourceFactAttribute : FactAttribute
    {
        public RealSourceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_SOURCE_DIRECTORY")))
                Skip = "Opt-in only: set SEIZA_PARALLAX_SOURCE_DIRECTORY to a directory containing separated *-sxt TIFF layers.";
        }
    }

    private sealed class ExistingMovieFactAttribute : FactAttribute
    {
        public ExistingMovieFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_EXISTING_MOVIE")))
                Skip = "Opt-in only: set SEIZA_PARALLAX_EXISTING_MOVIE to a 1280×720, 24 fps, two-second GUI H.264 export to inspect.";
        }
    }
}
