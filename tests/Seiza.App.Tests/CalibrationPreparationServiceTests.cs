using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationPreparationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"seiza-calibration-preparation-{Guid.NewGuid():N}");

    public CalibrationPreparationServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task BuildsDependenciesInOrderAndReusesContentAddressedCache()
    {
        string source = Path.Combine(_directory, "library");
        string cache = Path.Combine(source, ".seiza-cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "bias-1.fits", "bias-2.fits",
            "dark-1.fits", "dark-2.fits",
            "dark-flat-1.fits", "dark-flat-2.fits",
            "flat-1.xisf", "flat-2.xisf",
            "master-bias.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2, 3, 4]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        var plans = new List<CalibrationPlanRequest>();
        int activeProbes = 0;
        int maximumActiveProbes = 0;
        var service = CreateService(
            builds,
            async (path, token) =>
            {
                int active = Interlocked.Increment(ref activeProbes);
                UpdateMaximum(ref maximumActiveProbes, active);
                try
                {
                    await Task.Delay(20, token);
                    return Probe(path);
                }
                finally
                {
                    Interlocked.Decrement(ref activeProbes);
                }
            },
            plans: plans);
        CalibrationPreparationRequest request = Request(source, cache) with
        {
            Options = new CalibrationPreparationOptions { MaximumProbeConcurrency = 2 },
        };

        using CalibrationPreparationResult first = await service.PrepareAsync(request);

        Assert.Equal(["bias", "dark", "dark", "flat"], builds.Select(build => build.Kind));
        Assert.Equal(
            ["bias", "dark", "dark-flat", "flat"],
            first.Summaries.Select(summary => summary.Kind));
        Assert.Null(builds[0].Bias);
        Assert.Equal(first.Calibration.BiasPath, builds[1].Bias);
        Assert.Equal(first.Calibration.BiasPath, builds[2].Bias);
        Assert.Equal(first.Calibration.BiasPath, builds[3].Bias);
        CalibrationPreparationKindSummary darkFlat = Assert.Single(
            first.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Equal(CalibrationFrameRoles.DarkFlat, darkFlat.Build?.Kind);
        Assert.Equal(darkFlat.MasterPath, builds[3].Dark);
        Assert.NotEqual(first.Calibration.DarkPath, builds[3].Dark);
        CalibrationPlanRequest darkFlatPlan = Assert.Single(
            plans,
            plan => plan.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Equal(CalibrationFrameRoles.Flat, darkFlatPlan.Reference.Role);
        Assert.Contains("flat-", Path.GetFileName(darkFlatPlan.Reference.Path));
        Assert.NotNull(first.Calibration.FlatPath);
        Assert.InRange(maximumActiveProbes, 1, 2);
        Assert.Equal(9, first.DiscoveredFiles);
        Assert.Equal(9, first.ProbedFiles);
        Assert.Contains(first.Warnings, warning => warning.Contains(
            "existing calibration master", StringComparison.Ordinal));
        Assert.DoesNotContain(
            builds.SelectMany(build => build.Inputs),
            path => path.EndsWith("master-bias.fits", StringComparison.OrdinalIgnoreCase));

        using CalibrationPreparationResult second = await service.PrepareAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, builds.Count);
        Assert.All(second.Summaries, summary => Assert.True(summary.CacheReused));
        Assert.Equal(first.Calibration.BiasPath, second.Calibration.BiasPath);
        Assert.Equal(first.Calibration.DarkPath, second.Calibration.DarkPath);
        Assert.Equal(first.Calibration.FlatPath, second.Calibration.FlatPath);

        string changedBias = Path.Combine(source, "bias-1.fits");
        await File.AppendAllTextAsync(changedBias, "changed");
        File.SetLastWriteTimeUtc(changedBias, DateTime.UtcNow.AddSeconds(1));
        using CalibrationPreparationResult third = await service.PrepareAsync(request);

        Assert.Equal(8, builds.Count);
        Assert.NotEqual(first.Calibration.BiasPath, third.Calibration.BiasPath);
        Assert.NotEqual(first.Calibration.DarkPath, third.Calibration.DarkPath);
        Assert.NotEqual(first.Calibration.FlatPath, third.Calibration.FlatPath);
    }

    [Fact]
    public async Task UsesAnExposureMatchedDarkFlatWhenNoBiasIsAvailable()
    {
        string source = Path.Combine(_directory, "dark-flats-and-flats");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "dark-flat-1.fits", "dark-flat-2.fits", "flat-1.fits", "flat-2.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));

        using CalibrationPreparationResult result =
            await service.PrepareAsync(Request(source, cache));

        Assert.Equal(["dark", "flat"], builds.Select(build => build.Kind));
        Assert.Null(result.Calibration.BiasPath);
        Assert.Null(result.Calibration.DarkPath);
        Assert.NotNull(result.Calibration.FlatPath);
        CalibrationPreparationKindSummary darkFlat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Equal(darkFlat.MasterPath, builds[1].Dark);
        Assert.False(darkFlat.Build!.BiasSubtracted);
    }

    [Fact]
    public async Task WithholdsFlatWhenAnUnbiasedDarkFlatHasUnknownExposure()
    {
        string source = Path.Combine(_directory, "unknown-exposure");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "dark-flat-unknown-1.fits", "dark-flat-unknown-2.fits",
            "flat-unknown-1.fits", "flat-unknown-2.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));

        using CalibrationPreparationResult result =
            await service.PrepareAsync(Request(source, cache));

        CalibrationMasterBuildRequest darkFlatBuild = Assert.Single(builds);
        Assert.Equal(CalibrationFrameRoles.Dark, darkFlatBuild.Kind);
        Assert.Null(result.Calibration.FlatPath);
        CalibrationPreparationKindSummary flat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Flat);
        Assert.Contains("pedestal", flat.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithholdsFlatWhenNoPedestalRemovingMasterIsAvailable()
    {
        string source = Path.Combine(_directory, "flats-only");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "flat-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "flat-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));

        using CalibrationPreparationResult result =
            await service.PrepareAsync(Request(source, cache));

        Assert.Empty(builds);
        Assert.Null(result.Calibration.FlatPath);
        CalibrationPreparationKindSummary flat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Flat);
        Assert.True(flat.Plan.Ready);
        Assert.Contains("pedestal", flat.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("withheld", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CoreVersionParticipatesInTheCacheFingerprint()
    {
        string source = Path.Combine(_directory, "biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        string coreVersion = "0.18.0";
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            () => coreVersion);
        CalibrationPreparationRequest request = Request(source, cache);

        using CalibrationPreparationResult first = await service.PrepareAsync(request);
        coreVersion = "0.18.1";
        using CalibrationPreparationResult second = await service.PrepareAsync(request);

        Assert.Equal(2, builds.Count);
        Assert.NotEqual(first.Calibration.BiasPath, second.Calibration.BiasPath);
    }

    [Fact]
    public async Task ExcludesPreprocessedNonMasterFramesFromRawCandidates()
    {
        string source = Path.Combine(_directory, "mixed-biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-processed.fits"), [3]);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));

        using CalibrationPreparationResult result =
            await service.PrepareAsync(Request(source, cache));

        CalibrationMasterBuildRequest build = Assert.Single(builds);
        Assert.Equal(2, build.Inputs.Count);
        Assert.DoesNotContain(
            build.Inputs,
            path => path.Contains("processed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("preprocessed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectsAPreprocessedLightReference()
    {
        string source = Path.Combine(_directory, "biases");
        Directory.CreateDirectory(source);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(
            source,
            Path.Combine(_directory, "cache"));
        request = request with
        {
            Reference = request.Reference with
            {
                CalibrationState = new CalibrationFrameState { DarkSubtracted = true },
            },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request));
        Assert.Empty(builds);
    }

    [Fact]
    public async Task RejectsADarkFlatMinimumBelowTwo()
    {
        string source = Path.Combine(_directory, "library");
        Directory.CreateDirectory(source);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(
            source,
            Path.Combine(_directory, "cache")) with
        {
            Options = new CalibrationPreparationOptions { MinimumDarkFlatFrames = 1 },
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.PrepareAsync(request));
        Assert.Empty(builds);
    }

    [Fact]
    public async Task SendsEveryDistinctTargetToCoreAndEnablesScalingOnlyAfterBiasBuilds()
    {
        string source = Path.Combine(_directory, "multi-target");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "bias-1.fits", "bias-2.fits", "dark-1.fits", "dark-2.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }
        var plans = new List<CalibrationPlanRequest>();
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            plans: plans);
        CalibrationPreparationRequest request = Request(source, cache);
        CalibrationFrameProbe shortExposure = request.Reference with
        {
            Path = Path.Combine(_directory, "light-120.fits"),
            Signature = request.Reference.Signature with
            {
                ExposureSeconds = 120,
                CameraTempC = -8,
            },
        };
        request = request with
        {
            TargetLights = [shortExposure, request.Reference],
        };

        using CalibrationPreparationResult result = await service.PrepareAsync(request);

        CalibrationPlanRequest biasPlan = Assert.Single(
            plans,
            plan => plan.Kind == CalibrationFrameRoles.Bias);
        CalibrationPlanRequest darkPlan = Assert.Single(
            plans,
            plan => plan.Kind == CalibrationFrameRoles.Dark);
        Assert.Equal(2, biasPlan.References.Count);
        Assert.Equal(2, darkPlan.References.Count);
        Assert.Equal([300, 120], darkPlan.References.Select(item => item.Signature.ExposureSeconds));
        Assert.False(biasPlan.Dependencies.BiasAvailable);
        Assert.True(darkPlan.Dependencies.BiasAvailable);
        Assert.NotNull(result.Calibration.BiasPath);
        Assert.NotNull(result.Calibration.DarkPath);
    }

    [Fact]
    public async Task WithholdsUnscaledDarkWhenCoreRejectsHeterogeneousTargets()
    {
        string source = Path.Combine(_directory, "heterogeneous-targets");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "dark-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "dark-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            planner: request =>
            {
                if (request.Kind == CalibrationFrameRoles.Dark &&
                    !request.Dependencies.BiasAvailable &&
                    request.References.Select(item => item.Signature.ExposureSeconds)
                        .Distinct().Count() > 1)
                {
                    return new CalibrationPlanResult
                    {
                        SchemaVersion = 1,
                        Kind = request.Kind,
                        Minimum = request.Minimum,
                    };
                }
                return Plan(request);
            });
        CalibrationPreparationRequest request = Request(source, cache);
        request = request with
        {
            TargetLights =
            [
                request.Reference with
                {
                    Path = Path.Combine(_directory, "light-120.fits"),
                    Signature = request.Reference.Signature with
                    {
                        ExposureSeconds = 120,
                        CameraTempC = 5,
                    },
                },
            ],
        };

        using CalibrationPreparationResult result = await service.PrepareAsync(request);

        Assert.Empty(builds);
        Assert.Null(result.Calibration.DarkPath);
        CalibrationPreparationKindSummary dark = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Dark);
        Assert.False(dark.Plan.Ready);
    }

    [Fact]
    public async Task RejectsAnIneligibleTargetBeforeProbingOrPlanning()
    {
        string source = Path.Combine(_directory, "target-validation");
        Directory.CreateDirectory(source);
        int probes = 0;
        var builds = new List<CalibrationMasterBuildRequest>();
        var plans = new List<CalibrationPlanRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) =>
            {
                probes++;
                return Task.FromResult(Probe(path));
            },
            plans: plans);
        CalibrationPreparationRequest request = Request(
            source,
            Path.Combine(_directory, "cache"));
        request = request with
        {
            TargetLights =
            [
                request.Reference with
                {
                    Path = Path.Combine(_directory, "processed-light.fits"),
                    CalibrationState = new CalibrationFrameState { FlatNormalized = true },
                },
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(request));

        Assert.Equal(0, probes);
        Assert.Empty(plans);
        Assert.Empty(builds);
    }

    [Fact]
    public async Task RejectsConflictingMetadataForTheSameTargetPath()
    {
        string source = Path.Combine(_directory, "conflicting-target");
        Directory.CreateDirectory(source);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(
            source,
            Path.Combine(_directory, "cache"));
        request = request with
        {
            TargetLights =
            [
                request.Reference with
                {
                    Signature = request.Reference.Signature with { ExposureSeconds = 120 },
                },
            ],
        };

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.PrepareAsync(request));

        Assert.Contains("conflicting metadata", exception.Message, StringComparison.Ordinal);
        Assert.Empty(builds);
    }

    [Fact]
    public async Task DoesNotPlanOrBuildDarkFlatsUntilAFlatPlanIsReady()
    {
        string source = Path.Combine(_directory, "dark-flats-only");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "dark-flat-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "dark-flat-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        var plans = new List<CalibrationPlanRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            plans: plans);

        using CalibrationPreparationResult result =
            await service.PrepareAsync(Request(source, cache));

        Assert.DoesNotContain(plans, plan => plan.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Empty(builds);
        CalibrationPreparationKindSummary darkFlat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Contains("skipped", darkFlat.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CacheRetentionRemovesOldFingerprintsButProtectsTheCurrentResult()
    {
        string source = Path.Combine(_directory, "retention");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        string changedBias = Path.Combine(source, "bias-1.fits");
        await File.WriteAllBytesAsync(changedBias, [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(source, cache) with
        {
            Options = new CalibrationPreparationOptions
            {
                MaximumCacheBytes = null,
                MaximumCacheAge = null,
            },
        };
        CalibrationPreparationResult first = await service.PrepareAsync(request);
        string firstBiasPath = first.Calibration.BiasPath!;
        await File.AppendAllTextAsync(changedBias, "changed");
        File.SetLastWriteTimeUtc(changedBias, DateTime.UtcNow.AddSeconds(1));
        first.Dispose();

        using CalibrationPreparationResult second = await service.PrepareAsync(request with
        {
            Options = request.Options with
            {
                MaximumCacheBytes = 1,
            },
        });

        Assert.NotEqual(firstBiasPath, second.Calibration.BiasPath);
        Assert.False(File.Exists(firstBiasPath));
        Assert.False(File.Exists(Path.ChangeExtension(firstBiasPath, ".json")));
        Assert.True(File.Exists(second.Calibration.BiasPath));
        Assert.True(File.Exists(Path.ChangeExtension(second.Calibration.BiasPath, ".json")));
        var cacheLocks = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            typeof(CalibrationPreparationService).GetField(
                "CacheLocks",
                System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!.GetValue(null));
        Assert.Empty(cacheLocks.Keys);
    }

    [Fact]
    public async Task CallerProtectedMastersSurviveLaterGroupPruning()
    {
        string source = Path.Combine(_directory, "protected-retention");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        string changedBias = Path.Combine(source, "bias-1.fits");
        await File.WriteAllBytesAsync(changedBias, [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(source, cache) with
        {
            Options = new CalibrationPreparationOptions
            {
                MaximumCacheBytes = null,
                MaximumCacheAge = null,
            },
        };
        CalibrationPreparationResult first = await service.PrepareAsync(request);
        string firstBiasPath = first.Calibration.BiasPath!;
        await File.AppendAllTextAsync(changedBias, "changed");
        File.SetLastWriteTimeUtc(changedBias, DateTime.UtcNow.AddSeconds(1));
        string[] protectedPaths = first.Summaries
            .Where(summary => summary.MasterPath is not null)
            .Select(summary => summary.MasterPath!)
            .ToArray();
        first.Dispose();

        using CalibrationPreparationResult second = await service.PrepareAsync(request with
        {
            ProtectedMasterPaths = protectedPaths,
            Options = request.Options with { MaximumCacheBytes = 1 },
        });

        Assert.NotEqual(firstBiasPath, second.Calibration.BiasPath);
        Assert.True(File.Exists(firstBiasPath));
        Assert.True(File.Exists(second.Calibration.BiasPath));
    }

    [Fact]
    public async Task ActiveResultLeaseDefersPruningUntilTheCallerDisposesIt()
    {
        string source = Path.Combine(_directory, "active-result-retention");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        string changedBias = Path.Combine(source, "bias-1.fits");
        await File.WriteAllBytesAsync(changedBias, [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(source, cache) with
        {
            Options = new CalibrationPreparationOptions
            {
                MaximumCacheBytes = null,
                MaximumCacheAge = null,
            },
        };
        CalibrationPreparationResult first = await service.PrepareAsync(request);
        string firstBiasPath = first.Calibration.BiasPath!;
        await File.AppendAllTextAsync(changedBias, "changed");
        File.SetLastWriteTimeUtc(changedBias, DateTime.UtcNow.AddSeconds(1));
        CalibrationPreparationRequest pruningRequest = request with
        {
            Options = request.Options with { MaximumCacheBytes = 1 },
        };

        using (CalibrationPreparationResult second =
               await service.PrepareAsync(pruningRequest))
        {
            Assert.NotEqual(firstBiasPath, second.Calibration.BiasPath);
            Assert.True(File.Exists(firstBiasPath));
        }

        first.Dispose();
        using CalibrationPreparationResult third = await service.PrepareAsync(pruningRequest);

        Assert.False(File.Exists(firstBiasPath));
        Assert.False(File.Exists(Path.ChangeExtension(firstBiasPath, ".json")));
        Assert.True(File.Exists(third.Calibration.BiasPath));
    }

    [Fact]
    public async Task CacheRetentionSkipsAnEntryWithAnActiveCrossProcessLease()
    {
        string source = Path.Combine(_directory, "leased-retention");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        string oldMaster = Path.Combine(cache, "master-bias-old.fits");
        string oldReport = Path.ChangeExtension(oldMaster, ".json");
        await File.WriteAllBytesAsync(oldMaster, [9, 9, 9]);
        await File.WriteAllTextAsync(oldReport, "{}");
        DateTime old = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(oldMaster, old);
        File.SetLastWriteTimeUtc(oldReport, old);
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(source, cache) with
        {
            Options = new CalibrationPreparationOptions
            {
                MaximumCacheBytes = null,
                MaximumCacheAge = TimeSpan.FromHours(1),
            },
        };

        using (new FileStream(
            oldMaster + ".lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            using CalibrationPreparationResult prepared = await service.PrepareAsync(request);
            Assert.True(File.Exists(oldMaster));
        }

        using CalibrationPreparationResult second = await service.PrepareAsync(request);
        Assert.False(File.Exists(oldMaster));
        Assert.False(File.Exists(oldReport));
    }

    [Fact]
    public async Task CacheRetentionRemovesAbandonedStagingFiles()
    {
        string source = Path.Combine(_directory, "staging-retention");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [2]);
        string fingerprint = new('a', 64);
        string staging = Path.Combine(
            cache,
            $".master-bias-{fingerprint}-{Guid.NewGuid():N}.tmp.fits");
        await File.WriteAllBytesAsync(staging, [9, 9, 9]);
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddDays(-2));
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)));
        CalibrationPreparationRequest request = Request(source, cache) with
        {
            Options = new CalibrationPreparationOptions
            {
                MaximumCacheBytes = null,
                MaximumCacheAge = TimeSpan.FromHours(1),
            },
        };

        using CalibrationPreparationResult result = await service.PrepareAsync(request);

        Assert.False(File.Exists(staging));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CalibrationPreparationService CreateService(
        List<CalibrationMasterBuildRequest> builds,
        Func<string, CancellationToken, Task<CalibrationFrameProbe>> probe,
        Func<string>? coreVersion = null,
        List<CalibrationPlanRequest>? plans = null,
        Func<CalibrationPlanRequest, CalibrationPlanResult>? planner = null) => new(
            probe,
            (request, _) =>
            {
                plans?.Add(request);
                return Task.FromResult((planner ?? Plan)(request));
            },
            async (request, token) =>
            {
                builds.Add(request);
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return new CalibrationMasterBuildResult
                {
                    SchemaVersion = 1,
                    Kind = request.Kind,
                    Output = request.Output,
                    Width = 16,
                    Height = 16,
                    Channels = 1,
                    InputFrames = request.Inputs.Count,
                    AcceptedSamples = (ulong)(request.Inputs.Count * 256),
                    BiasSubtracted = request.Kind == CalibrationFrameRoles.Flat ||
                        (request.Kind == CalibrationFrameRoles.Dark && request.Bias is not null),
                    DarkSubtracted = request.Kind == CalibrationFrameRoles.Flat &&
                        request.Dark is not null,
                    Normalized = request.Kind == CalibrationFrameRoles.Flat,
                    OutputExposureSeconds = request.Kind == CalibrationFrameRoles.Dark
                        ? request.Inputs.Any(path => Path.GetFileName(path).Contains(
                            "unknown",
                            StringComparison.OrdinalIgnoreCase))
                            ? null
                            : request.Inputs.Any(path => Path.GetFileName(path).StartsWith(
                                "dark-flat-",
                                StringComparison.OrdinalIgnoreCase))
                            ? 2
                            : 300
                        : null,
                    Rejection = request.Rejection,
                };
            },
            coreVersion ?? (() => "0.18.0"));

    private CalibrationPreparationRequest Request(string source, string cache) => new()
    {
        Reference = new CalibrationFrameProbe
        {
            Path = Path.Combine(_directory, "light.fits"),
            Role = CalibrationFrameRoles.Light,
            Signature = new CalibrationFrameSignature
            {
                Camera = "ASI2600MM",
                Width = 16,
                Height = 16,
                Channels = 1,
                Filter = "L",
                ExposureSeconds = 300,
            },
        },
        SourcePaths = [source],
        CacheDirectory = cache,
    };

    private static CalibrationFrameProbe Probe(string path)
    {
        string name = Path.GetFileName(path);
        string role = name.StartsWith("bias-", StringComparison.OrdinalIgnoreCase)
            ? CalibrationFrameRoles.Bias
            : name.StartsWith("dark-flat-", StringComparison.OrdinalIgnoreCase)
                ? CalibrationFrameRoles.DarkFlat
                : name.StartsWith("dark-", StringComparison.OrdinalIgnoreCase)
                    ? CalibrationFrameRoles.Dark
                    : name.StartsWith("flat-", StringComparison.OrdinalIgnoreCase)
                        ? CalibrationFrameRoles.Flat
                        : CalibrationFrameRoles.Bias;
        return new CalibrationFrameProbe
        {
            SchemaVersion = 1,
            Path = path,
            Format = Path.GetExtension(path).Equals(".xisf", StringComparison.OrdinalIgnoreCase)
                ? "XISF"
                : "FITS",
            Role = role,
            IsMaster = name.StartsWith("master-", StringComparison.OrdinalIgnoreCase),
            CalibrationState = new CalibrationFrameState
            {
                BiasSubtracted = name.Contains("processed", StringComparison.OrdinalIgnoreCase),
            },
            Signature = new CalibrationFrameSignature
            {
                Camera = "ASI2600MM",
                Width = 16,
                Height = 16,
                Channels = 1,
                Filter = role == CalibrationFrameRoles.Flat ? "L" : null,
                ExposureSeconds = name.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : role switch
                    {
                        CalibrationFrameRoles.Bias => 0,
                        CalibrationFrameRoles.Flat or CalibrationFrameRoles.DarkFlat => 2,
                        _ => 300,
                    },
            },
        };
    }

    private static CalibrationPlanResult Plan(CalibrationPlanRequest request)
    {
        string[] selected = request.Candidates
            .Where(candidate => candidate.Role == request.Kind)
            .Select(candidate => candidate.Path)
            .ToArray();
        return new CalibrationPlanResult
        {
            SchemaVersion = 1,
            Kind = request.Kind,
            Minimum = request.Minimum,
            Ready = selected.Length >= request.Minimum,
            MatchedPaths = selected,
            SelectedPaths = selected,
        };
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current;
        do
        {
            current = maximum;
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, value, current) != current);
    }
}
