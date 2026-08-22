using System.Text.Json;
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
    public async Task ReportsFramesOutsideTheSelectedCoherentCohort()
    {
        string source = Path.Combine(_directory, "coherent-warning");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        string first = Path.Combine(source, "bias-1.fits");
        string second = Path.Combine(source, "bias-2.fits");
        string excluded = Path.Combine(source, "bias-other-night.fits");
        foreach (string path in new[] { first, second, excluded })
        {
            await File.WriteAllBytesAsync(path, [1, 2]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            planner: request =>
            {
                CalibrationPlanResult plan = Plan(request);
                return request.Kind == CalibrationFrameRoles.Bias
                    ? plan with
                    {
                        Ready = true,
                        MatchedPaths = [first, second, excluded],
                        SelectedPaths = [first, second],
                        Excluded =
                        [
                            new CalibrationPlanExclusion(excluded, "outside-coherent-set"),
                        ],
                    }
                    : plan;
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("outside the selected", StringComparison.OrdinalIgnoreCase) &&
            warning.Contains(Path.GetFileName(excluded), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptsAMiddleSkipWarnsAndReusesOnlyAValidCachedPartition()
    {
        string source = Path.Combine(_directory, "partial-biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[] { "bias-1.fits", "bias-2.fits", "bias-3.fits" })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        string? skippedPath = null;
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            builder: async (request, token) =>
            {
                skippedPath = request.Inputs[1];
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return BuildResult(
                    request,
                    [request.Inputs[0], request.Inputs[2]],
                    [new CalibrationMasterSkippedInputResult
                    {
                        Path = skippedPath,
                        Reason = "camera gain differs from the coherent subset",
                    }]);
            });
        CalibrationPreparationRequest request = Request(source, cache);

        using CalibrationPreparationResult first = await service.PrepareAsync(request);

        CalibrationPreparationKindSummary firstBias = Assert.Single(
            first.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.NotNull(first.Calibration.BiasPath);
        Assert.Equal(3, firstBias.Build!.RequestedFrames);
        Assert.Equal(2, firstBias.Build.InputFrames);
        Assert.Equal(
            [Path.GetFullPath(builds[0].Inputs[0]), Path.GetFullPath(builds[0].Inputs[2])],
            firstBias.Build.Inputs.Select(input => Path.GetFullPath(input.Path)));
        Assert.Equal(Path.GetFullPath(skippedPath!), Path.GetFullPath(
            Assert.Single(firstBias.Build.SkippedInputs).Path));
        Assert.Contains("skipped 1", firstBias.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(first.Warnings, warning => warning.Contains(
            Path.GetFileName(skippedPath!)!,
            StringComparison.OrdinalIgnoreCase));

        using CalibrationPreparationResult second = await service.PrepareAsync(request);

        Assert.Single(builds);
        CalibrationPreparationKindSummary cachedBias = Assert.Single(
            second.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.True(cachedBias.CacheReused);
        Assert.Contains("skipped 1", cachedBias.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(second.Warnings, warning => warning.Contains(
            Path.GetFileName(skippedPath!)!,
            StringComparison.OrdinalIgnoreCase));

        string reportPath = Path.ChangeExtension(firstBias.MasterPath!, ".json");
        CalibrationMasterCacheReport cached = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(reportPath),
            CalibrationPreparationJsonContext.Default.CalibrationMasterCacheReport)!;
        CalibrationMasterCacheReport malformed = cached with
        {
            Build = cached.Build with
            {
                SkippedInputs =
                [
                    new CalibrationMasterSkippedInputResult
                    {
                        Path = cached.Build.Inputs[0].Path,
                        Reason = "duplicates an accepted path",
                    },
                ],
            },
        };
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(
                malformed,
                CalibrationPreparationJsonContext.Default.CalibrationMasterCacheReport));

        using CalibrationPreparationResult third = await service.PrepareAsync(request);

        Assert.Equal(2, builds.Count);
        CalibrationPreparationKindSummary rebuiltBias = Assert.Single(
            third.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.False(rebuiltBias.CacheReused);
    }

    [Fact]
    public async Task RejectsAPartialLegacyResultWithoutASkippedInputPartition()
    {
        string source = Path.Combine(_directory, "legacy-partial-biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[] { "bias-1.fits", "bias-2.fits", "bias-3.fits" })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            builder: async (request, token) =>
            {
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return BuildResult(
                    request,
                    [request.Inputs[0], request.Inputs[2]],
                    [],
                    schemaVersion: 1);
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        Assert.Null(result.Calibration.BiasPath);
        CalibrationPreparationKindSummary bias = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.Null(bias.Build);
        Assert.Contains("invalid", bias.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptsACompleteLegacyInputPartition()
    {
        string source = Path.Combine(_directory, "legacy-complete-biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-1.fits"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(source, "bias-2.fits"), [1, 2]);

        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            builder: async (request, token) =>
            {
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return BuildResult(request, request.Inputs, [], schemaVersion: 1);
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        Assert.NotNull(result.Calibration.BiasPath);
        CalibrationPreparationKindSummary bias = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.Equal(1, bias.Build!.SchemaVersion);
        Assert.Equal(0, bias.Build.RequestedFrames);
        Assert.Empty(bias.Build.SkippedInputs);
    }

    [Fact]
    public async Task RejectsAnOverlappingAcceptedAndSkippedPartition()
    {
        string source = Path.Combine(_directory, "malformed-partial-biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[] { "bias-1.fits", "bias-2.fits", "bias-3.fits" })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            builder: async (request, token) =>
            {
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return BuildResult(
                    request,
                    [request.Inputs[0], request.Inputs[1]],
                    [new CalibrationMasterSkippedInputResult
                    {
                        Path = request.Inputs[1],
                        Reason = "duplicates an accepted path",
                    }]);
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        Assert.Null(result.Calibration.BiasPath);
        CalibrationPreparationKindSummary bias = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.Null(bias.Build);
        Assert.Contains("invalid", bias.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsASkippedInputWithoutAReason()
    {
        string source = Path.Combine(_directory, "reasonless-partial-biases");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[] { "bias-1.fits", "bias-2.fits", "bias-3.fits" })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            builder: async (request, token) =>
            {
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return BuildResult(
                    request,
                    [request.Inputs[0], request.Inputs[1]],
                    [new CalibrationMasterSkippedInputResult
                    {
                        Path = request.Inputs[2],
                        Reason = " ",
                    }]);
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        Assert.Null(result.Calibration.BiasPath);
        CalibrationPreparationKindSummary bias = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Bias);
        Assert.Null(bias.Build);
        Assert.Contains("invalid", bias.Warning, StringComparison.OrdinalIgnoreCase);
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
        var plans = new List<CalibrationPlanRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            plans: plans);

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
        CalibrationPlanRequest darkFlatPlan = Assert.Single(
            plans,
            plan => plan.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Equal(
            ["flat-1.fits", "flat-2.fits"],
            darkFlatPlan.References.Select(reference => Path.GetFileName(reference.Path)));
    }

    [Fact]
    public async Task WithholdsFlatWhenAnUnbiasedDarkFlatDoesNotMatchEveryFlatExposure()
    {
        string source = Path.Combine(_directory, "mixed-flat-exposures");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "dark-flat-1.fits", "dark-flat-2.fits", "flat-2s.fits", "flat-3s.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) =>
            {
                CalibrationFrameProbe probe = Probe(path);
                return Task.FromResult(Path.GetFileName(path).Contains(
                    "flat-3s",
                    StringComparison.OrdinalIgnoreCase)
                    ? probe with
                    {
                        Signature = probe.Signature with { ExposureSeconds = 3 },
                    }
                    : probe);
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        CalibrationMasterBuildRequest darkFlatBuild = Assert.Single(builds);
        Assert.Equal(CalibrationFrameRoles.Dark, darkFlatBuild.Kind);
        Assert.Null(result.Calibration.FlatPath);
        CalibrationPreparationKindSummary flat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Flat);
        Assert.Contains("pedestal", flat.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BiasAllowsTheDarkFlatToScaleAcrossMixedFlatExposures()
    {
        string source = Path.Combine(_directory, "scaled-dark-flat");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "bias-1.fits", "bias-2.fits",
            "dark-flat-1.fits", "dark-flat-2.fits",
            "flat-2s.fits", "flat-3s.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) =>
            {
                CalibrationFrameProbe probe = Probe(path);
                return Task.FromResult(Path.GetFileName(path).Contains(
                    "flat-3s",
                    StringComparison.OrdinalIgnoreCase)
                    ? probe with
                    {
                        Signature = probe.Signature with { ExposureSeconds = 3 },
                    }
                    : probe);
            });

        using CalibrationPreparationResult result = await service.PrepareAsync(
            Request(source, cache));

        Assert.Equal(["bias", "dark", "flat"], builds.Select(build => build.Kind));
        CalibrationMasterBuildRequest flatBuild = builds[2];
        CalibrationPreparationKindSummary darkFlat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.DarkFlat);
        Assert.Equal(darkFlat.MasterPath, flatBuild.Dark);
        Assert.NotNull(flatBuild.Bias);
        Assert.NotNull(result.Calibration.FlatPath);
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
    public async Task WithholdsFlatPlanningWhenTargetFilterCannotBeEstablished()
    {
        string source = Path.Combine(_directory, "unknown-target-filter");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "flat-1.fits"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "flat-2.fits"), [2]);
        var plans = new List<CalibrationPlanRequest>();
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) => Task.FromResult(Probe(path)),
            plans: plans);
        CalibrationPreparationRequest baseline = Request(source, cache);
        CalibrationPreparationRequest request = baseline with
        {
            Reference = baseline.Reference with
            {
                Path = Path.Combine(_directory, "plain-light.fits"),
                Signature = baseline.Reference.Signature with { Filter = null },
            },
        };

        using CalibrationPreparationResult result = await service.PrepareAsync(request);

        Assert.DoesNotContain(plans, plan => plan.Kind == CalibrationFrameRoles.Flat);
        Assert.Null(result.Calibration.FlatPath);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("recognized filename filter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FilenameFilterEnrichmentAppliesOnlyToTargetLights()
    {
        string source = Path.Combine(_directory, "target-only-filter-enrichment");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        string headerlessCandidate = Path.Combine(source, "flat-OIII-1.fits");
        string headerCandidate = Path.Combine(source, "flat-Ha-2.fits");
        await File.WriteAllBytesAsync(headerlessCandidate, [1]);
        await File.WriteAllBytesAsync(headerCandidate, [2]);

        var plans = new List<CalibrationPlanRequest>();
        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) =>
            {
                CalibrationFrameProbe probe = Probe(path);
                return Task.FromResult(Path.GetFullPath(path) == Path.GetFullPath(headerCandidate)
                    ? probe with
                    {
                        Signature = probe.Signature with { Filter = " Luminance " },
                    }
                    : probe with
                    {
                        Signature = probe.Signature with { Filter = null },
                    });
            },
            plans: plans);
        CalibrationPreparationRequest baseline = Request(source, cache);
        CalibrationPreparationRequest request = baseline with
        {
            Reference = baseline.Reference with
            {
                Path = Path.Combine(_directory, "M101-Ha-001.fits"),
                Signature = baseline.Reference.Signature with { Filter = null },
            },
        };

        using CalibrationPreparationResult result = await service.PrepareAsync(request);

        CalibrationPlanRequest flatPlan = Assert.Single(
            plans,
            plan => plan.Kind == CalibrationFrameRoles.Flat);
        Assert.Equal("Ha", flatPlan.Reference.Signature.Filter);
        Assert.All(flatPlan.References, target => Assert.Equal("Ha", target.Signature.Filter));
        CalibrationPlanRecord headerless = Assert.Single(
            flatPlan.Candidates,
            candidate => Path.GetFullPath(candidate.Path) ==
                Path.GetFullPath(headerlessCandidate));
        CalibrationPlanRecord withHeader = Assert.Single(
            flatPlan.Candidates,
            candidate => Path.GetFullPath(candidate.Path) == Path.GetFullPath(headerCandidate));
        Assert.Null(headerless.Signature.Filter);
        Assert.Equal(" Luminance ", withHeader.Signature.Filter);
    }

    [Fact]
    public async Task WithholdsABuiltFlatWhoseWrittenOpticsNoLongerMatchTheTarget()
    {
        string source = Path.Combine(_directory, "master-metadata-recheck");
        string cache = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(source);
        foreach (string name in new[]
        {
            "bias-1.fits", "bias-2.fits", "flat-1.fits", "flat-2.fits",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), [1, 2]);
        }

        var builds = new List<CalibrationMasterBuildRequest>();
        CalibrationPreparationService service = CreateService(
            builds,
            (path, _) =>
            {
                CalibrationFrameProbe probe = Probe(path);
                return Task.FromResult(path.Contains(
                    "master-flat-",
                    StringComparison.OrdinalIgnoreCase)
                    ? probe with
                    {
                        Signature = probe.Signature with { Telescope = null },
                    }
                    : probe with
                    {
                        Signature = probe.Signature with { Telescope = "Askar107PHQ" },
                    });
            });
        CalibrationPreparationRequest baseline = Request(source, cache);
        CalibrationPreparationRequest request = baseline with
        {
            Reference = baseline.Reference with
            {
                Signature = baseline.Reference.Signature with
                {
                    Telescope = "Askar107PHQ",
                },
            },
        };

        using CalibrationPreparationResult result = await service.PrepareAsync(request);

        Assert.Null(result.Calibration.FlatPath);
        CalibrationPreparationKindSummary flat = Assert.Single(
            result.Summaries,
            summary => summary.Kind == CalibrationFrameRoles.Flat);
        Assert.NotNull(flat.Build);
        Assert.Null(flat.MasterPath);
        Assert.Contains("optical metadata", flat.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telescope", flat.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Warnings, warning => warning.Contains(
            "preserve enough metadata",
            StringComparison.OrdinalIgnoreCase));
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
        Func<CalibrationPlanRequest, CalibrationPlanResult>? planner = null,
        Func<CalibrationMasterBuildRequest, CancellationToken,
            Task<CalibrationMasterBuildResult>>? builder = null) => new(
            probe,
            (request, _) =>
            {
                plans?.Add(request);
                return Task.FromResult((planner ?? Plan)(request));
            },
            async (request, token) =>
            {
                builds.Add(request);
                if (builder is not null)
                {
                    return await builder(request, token);
                }
                await File.WriteAllBytesAsync(request.Output, [7, 8, 9], token);
                return BuildResult(request, request.Inputs, []);
            },
            coreVersion ?? (() => "0.18.0"));

    private static CalibrationMasterBuildResult BuildResult(
        CalibrationMasterBuildRequest request,
        IReadOnlyList<string> acceptedInputs,
        IReadOnlyList<CalibrationMasterSkippedInputResult> skippedInputs,
        int schemaVersion = 2) => new()
        {
            SchemaVersion = schemaVersion,
            Kind = request.Kind,
            Output = request.Output,
            Width = 16,
            Height = 16,
            Channels = 1,
            RequestedFrames = schemaVersion >= 2 ? request.Inputs.Count : 0,
            InputFrames = acceptedInputs.Count,
            AcceptedSamples = (ulong)(acceptedInputs.Count * 256),
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
            Inputs = acceptedInputs
                .Select(path => new CalibrationMasterInputResult { Path = path })
                .ToArray(),
            SkippedInputs = skippedInputs.ToArray(),
        };

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
        bool isMaster = name.StartsWith("master-", StringComparison.OrdinalIgnoreCase);
        string role = name.StartsWith("bias-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("master-bias-", StringComparison.OrdinalIgnoreCase)
            ? CalibrationFrameRoles.Bias
            : name.StartsWith("dark-flat-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("master-dark-flat-", StringComparison.OrdinalIgnoreCase)
                ? CalibrationFrameRoles.DarkFlat
                : name.StartsWith("dark-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("master-dark-", StringComparison.OrdinalIgnoreCase)
                    ? CalibrationFrameRoles.Dark
                    : name.StartsWith("flat-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("master-flat-", StringComparison.OrdinalIgnoreCase)
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
            IsMaster = isMaster,
            CalibrationState = new CalibrationFrameState
            {
                BiasSubtracted = name.Contains("processed", StringComparison.OrdinalIgnoreCase),
                FlatNormalized = isMaster && role == CalibrationFrameRoles.Flat,
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
