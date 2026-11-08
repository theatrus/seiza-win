using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class CalibrationLibraryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaGraded-").FullName;
    private string Library => Path.Combine(_root, "library");
    private string Cache => Path.Combine(_root, "cache");
    private string[] Fixture()
    {
        Directory.CreateDirectory(Library);
        ushort[] sky = Sky(200, 128);
        string[] lights = Enumerable.Range(0, 3).Select(i =>
        {
            int gain = i == 1 ? 200 : 100, bias = gain == 100 ? 500 : 1500;
            string path = Path.Combine(_root, $"light-{i}.fits");
            Write(path, "LIGHT", gain, (x, y) => checked((ushort)(bias + sky[y * 200 + x + (i == 1 ? 20 : 0)])));
            return path;
        }).ToArray();
        foreach (int gain in new[] { 100, 200 })
            for (int i = 0; i < 3; i++) Write(Path.Combine(Library, $"bias-{gain}-{i}.fits"), "BIAS", gain,
                (x, y) => (ushort)((gain == 100 ? 500 : 1500) + (x + y * 160) % 7 - 3));
        return lights;
    }
    private static void Write(string path, string kind, int gain, Func<int, int, ushort> pixels,
        int rotation = 0, params string[] extra) => CalibrationParityFixtures.WriteFrame(path, 160, 128, pixels, kind, gain, rotation,
            extraCards: ["DATE-OBS= '2026-10-20T20:00:00Z'", .. extra]);
    private LibraryRequest Request(string[] lights, CalibrationLibraryPreferences? prefs = null) =>
        LibraryRequest.Create("analyze", Library, lights, [], prefs ?? new()) with { Cache = Cache };
    private static ImageStackOptions Options(bool expanded, bool gpu = false) => new()
    {
        UseGpu = gpu, GpuMemoryMib = gpu ? 64 : null, TraceGpu = gpu,
        CanvasMode = expanded ? StackCanvasMode.Expanded : StackCanvasMode.Reference,
        MaximumCanvasMegasamples = 4, Normalization = StackNormalizationMode.None,
        Rejection = StackRejectionMode.None, RemoveTransients = true,
    };
    private static async Task<ImageStackResult> Stack(string[] lights, string output, LibraryPreparedPlan plan, bool expanded, bool gpu = false)
    {
        var request = new ImageStackRequest(lights, output, Options(expanded, gpu), new()) { LibraryPlan = plan };
        return Assert.Single((await ImageStackService.StackBatchAsync([new(new("all", null, lights), request)], null)).Results);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualDriverUsesGradedABAForPlanningIntegrationAndReplay(bool gpu)
    {
        string[] lights = Fixture();
        byte[][] original = lights.Select(File.ReadAllBytes).ToArray();
        LibraryRequest request = Request(lights);
        var progress = new List<string>();
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request,
            new InlineProgress<LibraryProgress>(p => progress.Add(p.Phase)));
        Assert.Equal([0, 1, 0], review.Assignments);
        Assert.All(review.Missing, m => Assert.False(m));
        Assert.All(review.Frames, f => Assert.Equal("ok", f.Grade));
        Assert.Contains("evidence", progress);
        Assert.DoesNotContain(Directory.GetFiles(Cache), p => Path.GetFileName(p).StartsWith("master-", StringComparison.Ordinal));
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        var plan = new LibraryPreparedPlan(built, lights);
        Assert.NotEqual(plan.Session(lights[0]).Bias, plan.Session(lights[1]).Bias);
        Assert.Equal(plan.Session(lights[0]).Bias, plan.Session(lights[2]).Bias);
        foreach (bool expanded in new[] { false, true })
        {
            string output = Path.Combine(_root, $"stack-{expanded}-{gpu}.fits");
            ImageStackResult result = await Stack(plan.Inputs(lights, false), output, plan, expanded, gpu);
            Assert.Equal(3, result.AcceptedFrames); Assert.Equal(0, result.RejectedFrames);
            Assert.Null(result.TransientRemovalNote);
            if (expanded) Assert.Contains("Expanded mosaic canvas", result.CanvasNote);
            DarkQualityMeasurement measured = CalibrationQualityService.MeasureFrame(new(output));
            Assert.InRange(measured.Level, 90, 110);
        }
        string[] masters = built.FileSessions.Select(s => s.Bias!).Distinct().ToArray();
        DateTime[] timestamps = masters.Select(File.GetLastWriteTimeUtc).ToArray();
        await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        Assert.Equal(timestamps, masters.Select(File.GetLastWriteTimeUtc));
        for (int i = 0; i < lights.Length; i++) Assert.Equal(SHA256.HashData(original[i]), SHA256.HashData(File.ReadAllBytes(lights[i])));
        File.WriteAllText(lights[0], "changed");
        await Assert.ThrowsAnyAsync<Exception>(() => Stack(lights, Path.Combine(_root, "stale.fits"), plan, false));
    }

    [Fact]
    public async Task IncompleteExecutionPolicyReusesReviewAndExclusionKeepsOrder()
    {
        string[] lights = Fixture();
        foreach (string p in Directory.GetFiles(Library, "*200*")) File.Delete(p);
        LibraryRequest request = Request(lights);
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        Assert.Equal([false, true, false], review.Missing);
        await Assert.ThrowsAnyAsync<Exception>(() => CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey }));
        foreach (string policy in new[] { "keep", "exclude" })
        {
            LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey, Incomplete = policy });
            Assert.Equal(review.ReviewKey, built.ReviewKey);
            var plan = new LibraryPreparedPlan(built, lights);
            Assert.Equal(new[] { lights[0], lights[2] }.Select(Path.GetFileName), plan.Inputs(lights, true).Select(Path.GetFileName));
            Assert.Equal(3, plan.Inputs(lights, false).Length);
        }
    }

    [Fact]
    public async Task RigGatesReachBothOpenersAndHistoricalSourcesAreProtected()
    {
        string[] all = Fixture(); string[] lights = [all[0], all[2]];
        string flat = Path.Combine(Library, "external-flat.fits");
        Write(flat, "MASTER FLAT", 100, (_, _) => 1, 10, "FLATNORM=                    T");
        LibraryRequest request = Request(lights);
        LibraryReview first = await CalibrationLibraryService.PerformAsync(request);
        Assert.All(first.Missing, Assert.True);
        var prefs = new CalibrationLibraryPreferences(); prefs.Settings["rotation_tolerance_deg"] = 1;
        prefs.Rigs[first.Rigs[0].Id] = new() { ["rotation_tolerance_deg"] = JsonValue.Create(12) };
        LibraryRequest changed = Request(lights, prefs);
        await Assert.ThrowsAnyAsync<Exception>(() => CalibrationLibraryService.PerformAsync(changed with { Operation = "execute", ReviewKey = first.ReviewKey }));
        LibraryReview review = await CalibrationLibraryService.PerformAsync(changed);
        Assert.All(review.Missing, m => Assert.False(m));
        LibraryReview built = await CalibrationLibraryService.PerformAsync(changed with { Operation = "execute", ReviewKey = review.ReviewKey });
        Assert.Equal(12, built.FileSessions[0].Tolerances["rotationDeg"]!.GetValue<double>());
        var plan = new LibraryPreparedPlan(built, lights);
        foreach (bool expanded in new[] { false, true })
        {
            Assert.Equal(2, (await Stack(lights, Path.Combine(_root, $"gate-{expanded}.fits"), plan, expanded)).AcceptedFrames);
            await Assert.ThrowsAnyAsync<Exception>(() => Stack(lights, flat, plan, expanded));
        }
        string master = plan.Session(lights[0]).Bias!;
        File.WriteAllText(master, "changed historical master");
        await Assert.ThrowsAnyAsync<Exception>(() => Stack(lights, Path.Combine(_root, "historical.fits"), plan, false));
    }

    [Fact]
    public async Task RecursiveMixedFolderUsesNativeRolesAndProvidedFramesWithoutLibrary()
    {
        string[] original = Fixture();
        string nested = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        string renamed = Path.Combine(nested, "flat-looking-name.fits"); File.Move(original[0], renamed);
        Write(Path.Combine(nested, "old-stack.fits"), "MASTER LIGHT", 100, (_, _) => 500, 0, "SEIZAMST=                    T");
        File.WriteAllText(Path.Combine(nested, "broken.fits"), "invalid");
        var scan = ImageDirectoryScanner.Scan(_root, true);
        StackInputRoutingResult routed = await StackInputRouting.RouteAsync(scan.Paths);
        Assert.Equal(3, routed.Lights.Length); Assert.Equal(6, routed.Calibration.Length);
        Assert.Contains(renamed, routed.Lights); Assert.Contains(routed.Notes, n => n.Contains("old-stack.fits", StringComparison.Ordinal));
        Assert.Contains(routed.Notes, n => n.Contains("broken.fits", StringComparison.Ordinal));
        var prefs = new CalibrationLibraryPreferences { FlatAssociation = "one_set" };
        LibraryRequest request = LibraryRequest.Create("analyze", null, routed.Lights, routed.Calibration, prefs) with { Cache = Cache };
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        Assert.Equal(6, review.Frames.Length); Assert.All(review.Missing, m => Assert.False(m));
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        var plan = new LibraryPreparedPlan(built, routed.Lights);
        foreach (bool expanded in new[] { false, true })
            Assert.Equal(3, (await Stack(plan.Inputs(routed.Lights, false), Path.Combine(_root, $"mixed-{expanded}.fits"), plan, expanded)).AcceptedFrames);
    }

    [Fact]
    public async Task NativeSchemaAndDraftPreferencesKeepWireKeysAndSourceBoundChoices()
    {
        LibrarySchema schema = await CalibrationLibraryService.SchemaAsync();
        Assert.Contains(schema.Settings, f => f.Key == "rotation_tolerance_deg");
        Assert.Contains(schema.Settings, f => f.Key == "grade_flat_dim");
        string path = Path.Combine(_root, "preferences.json");
        var saved = new CalibrationLibraryPreferences(); CalibrationLibraryPreferencesStore.Save(saved, path);
        var draft = CalibrationLibraryPreferencesStore.Load(path).Copy();
        draft.Settings["rotation_tolerance_deg"] = 12;
        draft.LightChoices.Add(new("source.fits", "fingerprint", new() { ["choice"] = "before", ["changeKey"] = "unchanged_id" }));
        Assert.Empty(CalibrationLibraryPreferencesStore.Load(path).Settings);
        CalibrationLibraryPreferencesStore.Save(draft, path);
        var restored = CalibrationLibraryPreferencesStore.Load(path);
        Assert.Equal(12, restored.Settings["rotation_tolerance_deg"]!.GetValue<int>());
        LibraryRequest request = LibraryRequest.Create("analyze", null, [], [], restored);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(request, LibraryJsonContext.Default.LibraryRequest));
        Assert.Equal("rotation_tolerance_deg", json.RootElement.GetProperty("settings")[0].GetProperty("key").GetString());
        Assert.Equal("unchanged_id", json.RootElement.GetProperty("lightChoices")[0].GetProperty("row").GetProperty("changeKey").GetString());
    }

    [Fact]
    public async Task CancellationBeforeNativeCallDoesNotPublishMasters()
    {
        string[] lights = Fixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalibrationLibraryService.PerformAsync(Request(lights), token: cancelled.Token));
        Assert.False(Directory.Exists(Cache));
    }

    [WindowsStackSourceIdentityFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task CancellationInterruptsNativeCacheLockWait()
    {
        string[] lights = Fixture(); Directory.CreateDirectory(Cache);
        using var file = new FileStream(Path.Combine(Cache, "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        file.Lock(0, long.MaxValue);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            Task<LibraryReview> pending = CalibrationLibraryService.PerformAsync(Request(lights), token: cancel.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { file.Unlock(0, long.MaxValue); }
        LibraryReview review = await CalibrationLibraryService.PerformAsync(Request(lights));
        Assert.Equal([0, 1, 0], review.Assignments);
    }

    [Fact]
    public async Task CorrectedFlatReachesBothCanvasesAndTransientReplay()
    {
        Directory.CreateDirectory(Library);
        const int width = 240, height = 160;
        ushort[] sky = Sky(width, height);
        double Response(int x, int y, int angle, bool dust)
        {
            double v = 1 - 0.3 * (Math.Pow(x - 120, 2) + Math.Pow(y - 80, 2)) / (120 * 120 + 80 * 80);
            if (angle == 90) v *= 1 - 0.04 * Math.Clamp((14 - Math.Sqrt(Math.Pow(x - 170, 2) + Math.Pow(y - 40, 2))) / 2, 0, 1);
            if (dust) v *= 1 - 0.03 * Math.Clamp((14 - Math.Sqrt(Math.Pow(x - 60, 2) + Math.Pow(y - 90, 2))) / 2, 0, 1);
            return v;
        }
        string Frame(string name, string kind, string filter, int angle, int day, bool dust)
        {
            string path = Path.Combine(kind == "LIGHT" ? _root : Library, name + ".fits");
            CalibrationParityFixtures.WriteFrame(path, width, height, (x, y) => (ushort)Math.Round(kind switch
            {
                "BIAS" => 500 + (x + y * width) % 7 - 3,
                "FLAT" => 500 + 20000 * Response(x, y, angle, dust) + (x + y * width) % 7 - 3,
                _ => 500 + sky[y * width + x] * Response(x, y, angle, dust),
            }), kind, rotation: angle, extraCards: [$"DATE-OBS= '2026-10-{day}T20:00:00Z'", $"FILTER  = '{filter}'", "OBJECT  = 'M42'"]);
            return path;
        }
        for (int i = 0; i < 3; i++) Frame($"bias-{i}", "BIAS", "Ha", 0, 20, false);
        int set = 0;
        foreach (var (filter, angle, day, dust) in new[] { ("Ha", 0, 20, false), ("OIII", 0, 20, false), ("Ha", 0, 26, true), ("OIII", 0, 26, true), ("Ha", 90, 26, true) })
        {
            for (int i = 0; i < 3; i++) Frame($"flat-{set}-{i}", "FLAT", filter, angle, day, dust);
            set++;
        }
        string[] lights = Enumerable.Range(0, 3).Select(i => Frame($"light-{i}", "LIGHT", "Ha", 90, 19, false)).ToArray();
        LibraryRequest request = Request(lights);
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        Assert.Single(review.FlatWork["corrections"]!.AsArray());
        Assert.All(review.Missing, m => Assert.False(m));
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        Assert.Contains("corrected-flat-", built.FileSessions[0].Flat!);
        var plan = new LibraryPreparedPlan(built, lights);
        foreach (bool expanded in new[] { false, true })
        {
            string output = Path.Combine(_root, $"corrected-{expanded}.fits");
            ImageStackResult result = await Stack(lights, output, plan, expanded);
            Assert.Equal(3, result.AcceptedFrames); Assert.Null(result.TransientRemovalNote);
            Assert.InRange(CalibrationQualityService.MeasureFrame(new(output)).Level, 75, 110);
        }
    }

    [Fact]
    public async Task NormalizedXisfIsStagedInPhysicalUnitsAndOriginalsRemainProtected()
    {
        string[] lights = Fixture();
        ushort[] sky = Sky(200, 128);
        string normalized = Path.Combine(_root, "normalized.xisf");
        const int width = 160, height = 128, offset = 4096;
        var xml = new System.Xml.Linq.XElement("xisf", new System.Xml.Linq.XAttribute("version", "1.0"),
            new System.Xml.Linq.XElement("Image", new System.Xml.Linq.XAttribute("geometry", $"{width}:{height}:1"),
                new System.Xml.Linq.XAttribute("sampleFormat", "Float32"), new System.Xml.Linq.XAttribute("colorSpace", "Gray"),
                new System.Xml.Linq.XAttribute("bounds", "0:1"), new System.Xml.Linq.XAttribute("location", $"attachment:{offset}:{width * height * 4}"),
                new[] { ("IMAGETYP", "'LIGHT'"), ("INSTRUME", "'Parity camera'"), ("TELESCOP", "'Parity scope'"), ("GAIN", "100"),
                    ("XBINNING", "1"), ("YBINNING", "1"), ("EXPTIME", "60"), ("ROTATANG", "0"), ("FOCALLEN", "500"),
                    ("DATE-OBS", "'2026-10-20T20:00:00Z'") }.Select(v => new System.Xml.Linq.XElement("FITSKeyword",
                        new System.Xml.Linq.XAttribute("name", v.Item1), new System.Xml.Linq.XAttribute("value", v.Item2)))));
        byte[] header = System.Text.Encoding.UTF8.GetBytes(xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        using (var stream = File.Create(normalized))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("XISF0100")); writer.Write(header.Length); writer.Write(0); writer.Write(header);
            stream.Position = offset;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) writer.Write((sky[y * 200 + x] + 500) / 65535f);
        }
        lights[0] = normalized;
        byte[] before = File.ReadAllBytes(normalized);
        LibraryRequest request = Request(lights);
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        Assert.NotEqual(normalized, review.Paths[0]); Assert.EndsWith(".fits", review.Paths[0]);
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        var plan = new LibraryPreparedPlan(built, lights);
        foreach (bool expanded in new[] { false, true })
        {
            string output = Path.Combine(_root, $"xisf-stack-{expanded}.fits");
            Assert.Equal(3, (await Stack(plan.Inputs(lights, false), output, plan, expanded)).AcceptedFrames);
            Assert.InRange(CalibrationQualityService.MeasureFrame(new(output)).Level, 90, 110);
        }
        Assert.Equal(before, File.ReadAllBytes(normalized));
        await Assert.ThrowsAnyAsync<Exception>(() => Stack(plan.Inputs(lights, false), normalized, plan, false));
    }

    [Fact]
    public async Task LargeGradedSessionsExerciseDevicePassesAndMatchCpu()
    {
        Directory.CreateDirectory(Library);
        const int width = 1056, height = 1024, globalWidth = 1100;
        ushort[] sky = Sky(globalWidth, height, 180);
        string[] lights = Enumerable.Range(0, 3).Select(i =>
        {
            int bias = i == 1 ? 1500 : 500, gain = i == 1 ? 200 : 100;
            string path = Path.Combine(_root, $"large-{i}.fits");
            CalibrationParityFixtures.WriteFrame(path, width, height,
                (x, y) => (ushort)(sky[y * globalWidth + x + (i == 1 ? 20 : 0)] + bias), gain: gain,
                extraCards: ["DATE-OBS= '2026-10-20T20:00:00Z'"]);
            return path;
        }).ToArray();
        foreach (int gain in new[] { 100, 200 }) for (int i = 0; i < 3; i++)
            CalibrationParityFixtures.WriteFrame(Path.Combine(Library, $"bias-{gain}-{i}.fits"), width, height,
                (x, y) => (ushort)((gain == 100 ? 500 : 1500) + (x + y) % 7 - 3), "BIAS", gain,
                extraCards: ["DATE-OBS= '2026-10-20T20:00:00Z'"]);
        LibraryRequest request = Request(lights);
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        var plan = new LibraryPreparedPlan(built, lights);
        foreach (bool expanded in new[] { false, true })
        {
            string cpu = Path.Combine(_root, $"large-cpu-{expanded}.fits"), gpu = Path.Combine(_root, $"large-gpu-{expanded}.fits");
            ImageStackResult expected = await Stack(lights, cpu, plan, expanded);
            ImageStackResult actual = await Stack(lights, gpu, plan, expanded, true);
            Assert.Equal(3, actual.AcceptedFrames); Assert.Equal(expected.RejectedFrames, actual.RejectedFrames);
            Assert.Null(actual.TransientRemovalNote);
            double cpuLevel = CalibrationQualityService.MeasureFrame(new(cpu)).Level, gpuLevel = CalibrationQualityService.MeasureFrame(new(gpu)).Level;
            Assert.InRange(cpuLevel, 90, 110); Assert.InRange(gpuLevel, 90, 110); Assert.InRange(Math.Abs(cpuLevel - gpuLevel), 0, 0.05);
        }
    }

    [Fact]
    public async Task SharedReferenceFallbackOpensEveryCandidateWithItsOwnSession()
    {
        string[] lights = Fixture();
        Write(lights[0], "LIGHT", 100, (_, _) => 600);
        LibraryRequest request = Request(lights);
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        var plan = new LibraryPreparedPlan(built, lights);
        string output = Path.Combine(_root, "fallback.fits");
        var stack = new ImageStackRequest(lights, output, Options(false), new())
        {
            LibraryPlan = plan, AlignmentReferencePath = lights[2], ChannelReferenceCandidates = [lights[0], lights[1]],
        };
        ImageStackResult result = Assert.Single((await ImageStackService.StackBatchAsync([new(new("all", null, lights), stack)], null)).Results);
        Assert.Equal(1, result.ReferenceSelection!.ChannelReferenceIndex);
        Assert.Equal(lights[1], result.ReferenceSelection.ChannelReferencePath);
        Assert.Single(result.ReferenceSelection.Skipped);
        Assert.Equal(2, result.AcceptedFrames);
        Assert.InRange(CalibrationQualityService.MeasureFrame(new(output)).Level, 90, 110);
    }

    [Fact]
    public async Task FlatOnlyFittedPedestalReachesFixedAndExpandedIntegrationAndReplay()
    {
        Directory.CreateDirectory(Library);
        const int width = 512, height = 512;
        ushort[] sky = Sky(width, height, 95);
        double Response(int x, int y) => 1 - 0.35 * (Math.Pow(x - 256, 2) + Math.Pow(y - 256, 2)) / (2.0 * 256 * 256);
        for (int i = 0; i < 3; i++) CalibrationParityFixtures.WriteFrame(Path.Combine(Library, $"flat-{i}.fits"), width, height,
            (x, y) => (ushort)Math.Round(Response(x, y) * 20000), "FLAT", extraCards: ["DATE-OBS= '2026-10-20T20:00:00Z'"]);
        string[] lights = Enumerable.Range(0, 3).Select(i =>
        {
            string path = Path.Combine(_root, $"pedestal-light-{i}.fits");
            // A flat-shaped sky with a 500 ADU camera offset. The native fit
            // intentionally reads low; compare its direct result below.
            CalibrationParityFixtures.WriteFrame(path, width, height, (x, y) => (ushort)Math.Round(500 +
                (900 + (sky[y * width + x] > 110 ? sky[y * width + x] - 100 : 0)) * Response(x, y)),
                extraCards: ["DATE-OBS= '2026-10-20T20:00:00Z'"]);
            return path;
        }).ToArray();
        LibraryRequest request = Request(lights);
        LibraryReview review = await CalibrationLibraryService.PerformAsync(request);
        Assert.Equal("needs_pedestal_fit", review.Snapshot["sessions"]![0]!["application"]!["flatUse"]!.GetValue<string>());
        LibraryReview built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        LibraryFileSession session = Assert.Single(built.FileSessions);
        Assert.Contains("pedestal-", session.Bias!);
        float[] lightSamples = Enumerable.Range(0, width * height).Select(i => (float)Math.Round(500 +
            (900 + (sky[i] > 110 ? sky[i] - 100 : 0)) * Response(i % width, i / width))).ToArray();
        float expectedPedestal = DirectPedestal(lightSamples, ReadFloatFits(session.Flat!), width, height);
        Assert.InRange(Math.Abs(CalibrationQualityService.MeasureFrame(new(session.Bias!)).Level - expectedPedestal), 0, 0.001);
        var plan = new LibraryPreparedPlan(built, lights);
        foreach (bool expanded in new[] { false, true })
        {
            string output = Path.Combine(_root, $"pedestal-stack-{expanded}.fits");
            ImageStackResult result = await Stack(lights, output, plan, expanded);
            Assert.Equal(3, result.AcceptedFrames); Assert.Null(result.TransientRemovalNote);
            Assert.InRange(CalibrationQualityService.MeasureFrame(new(output)).Level, 650, 1050);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_fit_flat_pedestal")]
    private static unsafe partial int FitPedestal(float* light, float* flat, nuint width, nuint height, out float pedestal, out nint error);
    private static unsafe float DirectPedestal(float[] light, float[] flat, int width, int height)
    {
        fixed (float* l = light, f = flat)
        {
            int result = FitPedestal(l, f, (nuint)width, (nuint)height, out float pedestal, out nint error);
            string? message = error == 0 ? null : Seiza.App.Interop.NativeString.TakeOwned(error, "");
            Assert.True(result == 1, message); return pedestal;
        }
    }
    private static float[] ReadFloatFits(string path)
    {
        byte[] bytes = File.ReadAllBytes(path); int end = 0;
        while (System.Text.Encoding.ASCII.GetString(bytes, end, 8).Trim() != "END") end += 80;
        int offset = ((end + 80 + 2879) / 2880) * 2880;
        return Enumerable.Range(0, 512 * 512).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(offset + i * 4, 4))).ToArray();
    }

    internal static ushort[] Sky(int width, int height, int spacing = 22)
    {
        var data = new ushort[width * height]; var random = new Random(42);
        for (int i = 0; i < data.Length; i++) data[i] = (ushort)(100 + random.Next(9) - 4);
        for (int cy = 15; cy < height - 10; cy += spacing)
            for (int cx = 15; cx < width - 10; cx += spacing + 5)
                for (int dy = -5; dy <= 5; dy++) for (int dx = -5; dx <= 5; dx++)
                    data[(cy + dy) * width + cx + dx] += (ushort)(3000 * Math.Exp(-(dx * dx + dy * dy) / 4.0));
        return data;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
