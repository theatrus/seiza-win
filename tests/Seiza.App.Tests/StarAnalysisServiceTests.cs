using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StarAnalysisServiceTests
{
    [Fact]
    public async Task EmptyDetectionIsAValidMeasuredResult()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((_, _) => ValidResultJson());
            var service = CreateService(native);

            StarAnalysisResult result = await service.AnalyzeAsync(path);

            Assert.Empty(result.Stars);
            Assert.Equal(9, result.Cells.Length);
            Assert.Null(result.Tilt.TiltPercent);
            Assert.False(result.HasPsfMeasurements);
            Assert.Equal(1, native.CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OptionsUseTheNativeCamelCaseContractAndRejectAmbiguity()
    {
        string path = CreateImagePath();
        string? capturedOptions = null;
        try
        {
            var native = new FakeNativeClient((_, options) =>
            {
                capturedOptions = options;
                return ValidResultJson();
            });
            var service = CreateService(native);
            var options = new StarAnalysisOptions
            {
                Preset = StarDetectionPreset.LongFocal,
                PsfType = StarPsfType.Moffat4,
                StructureRemoval = StarStructureRemoval.Atrous,
                DetectionBinning = 2,
                KeepSaturated = true,
                NoiseReductionRadius = 3,
                Sensitivity = 8.5,
            };

            await service.AnalyzeAsync(path, options);

            using JsonDocument document = JsonDocument.Parse(capturedOptions!);
            JsonElement root = document.RootElement;
            Assert.Equal("longfocal", root.GetProperty("preset").GetString());
            Assert.Equal("moffat4", root.GetProperty("psfType").GetString());
            Assert.Equal("atrous", root.GetProperty("structureRemoval").GetString());
            Assert.Equal(2, root.GetProperty("detectionBinning").GetInt32());
            Assert.True(root.GetProperty("keepSaturated").GetBoolean());
            Assert.Equal(3, root.GetProperty("noiseReductionRadius").GetInt32());
            Assert.Equal(8.5, root.GetProperty("sensitivity").GetDouble());
            Assert.False(root.TryGetProperty("focalLengthMm", out _));

            var ambiguous = new StarAnalysisOptions
            {
                Preset = StarDetectionPreset.Standard,
                FocalLengthMm = 749,
                PixelSizeUm = 3.76,
            };
            await Assert.ThrowsAsync<ArgumentException>(
                () => service.AnalyzeAsync(path, ambiguous));

            var unknownPreset = new StarAnalysisOptions
            {
                Preset = (StarDetectionPreset)999,
            };
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.AnalyzeAsync(path, unknownPreset));
            Assert.Equal(1, native.CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InteractiveDefaultsBoundLargeFramesWithoutOverridingHeaderClassification()
    {
        string json = StarAnalysisOptions.InteractiveDefault.ToJson();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("detectionBinning").GetInt32());
        Assert.Equal(30, root.GetProperty("sensitivity").GetDouble());
        Assert.Equal("moffat4", root.GetProperty("psfType").GetString());
        Assert.False(root.TryGetProperty("preset", out _));
        Assert.False(root.TryGetProperty("focalLengthMm", out _));
        Assert.False(root.TryGetProperty("pixelSizeUm", out _));
    }

    [Fact]
    public async Task CacheKeyIncludesFileIdentityCoreVersionAndOptions()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((_, _) => ValidResultJson());
            var service = CreateService(native);

            StarAnalysisResult first = await service.AnalyzeAsync(path);
            StarAnalysisResult cached = await service.AnalyzeAsync(path);
            Assert.Same(first, cached);
            Assert.Equal(1, native.CallCount);

            native.CoreVersion = "0.18.6";
            await service.AnalyzeAsync(path);
            Assert.Equal(2, native.CallCount);

            await service.AnalyzeAsync(
                path,
                new StarAnalysisOptions { PsfType = StarPsfType.Gaussian });
            Assert.Equal(3, native.CallCount);

            File.AppendAllBytes(path, [4]);
            await service.AnalyzeAsync(path);
            Assert.Equal(4, native.CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CacheEvictsTheLeastRecentlyUsedResultAtItsBound()
    {
        string[] paths = Enumerable.Range(0, 3).Select(_ => CreateImagePath()).ToArray();
        try
        {
            var native = new FakeNativeClient((_, _) => ValidResultJson());
            var service = CreateService(native, cacheCapacity: 2);

            await service.AnalyzeAsync(paths[0]);
            await service.AnalyzeAsync(paths[1]);
            await service.AnalyzeAsync(paths[2]);
            await service.AnalyzeAsync(paths[0]);

            Assert.Equal(4, native.CallCount);
        }
        finally
        {
            foreach (string path in paths)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ChangedSourceIsDiscardedInsteadOfBeingCached()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((nativePath, _) =>
            {
                File.AppendAllBytes(nativePath, [5]);
                return ValidResultJson();
            });
            var service = CreateService(native);

            await Assert.ThrowsAsync<StarAnalysisSourceChangedException>(
                () => service.AnalyzeAsync(path));

            Assert.Equal(1, native.CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CancellationAbandonsTheWaitButLetsNativeFinishAndCache()
    {
        string path = CreateImagePath();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        try
        {
            var native = new FakeNativeClient((_, _) =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                finished.Set();
                return ValidResultJson();
            });
            var service = CreateService(native);
            using var cancellation = new CancellationTokenSource();

            Task<StarAnalysisResult> analysis = service.AnalyzeAsync(
                path,
                cancellationToken: cancellation.Token);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

            await service.AnalyzeAsync(path);
            Assert.Equal(1, native.CallCount);
        }
        finally
        {
            release.Set();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameIdentityShareOneNativeOperation()
    {
        string path = CreateImagePath();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var native = new FakeNativeClient((_, _) =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return ValidResultJson();
            });
            var service = CreateService(native);

            Task<StarAnalysisResult> first = service.AnalyzeAsync(path);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Task<StarAnalysisResult> second = service.AnalyzeAsync(path);
            release.Set();

            StarAnalysisResult[] results = await Task.WhenAll(first, second);
            Assert.Same(results[0], results[1]);
            Assert.Equal(1, native.CallCount);
        }
        finally
        {
            release.Set();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CanceledQueuedAnalysisNeverDelaysTheNewerImage()
    {
        string runningPath = CreateImagePath();
        string abandonedPath = CreateImagePath();
        string latestPath = CreateImagePath();
        using var runningStarted = new ManualResetEventSlim();
        using var releaseRunning = new ManualResetEventSlim();
        try
        {
            var calledPaths = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var native = new FakeNativeClient((path, _) =>
            {
                calledPaths.Enqueue(path);
                if (string.Equals(path, runningPath, StringComparison.OrdinalIgnoreCase))
                {
                    runningStarted.Set();
                    releaseRunning.Wait(TimeSpan.FromSeconds(10));
                }

                return ValidResultJson();
            });
            var service = CreateService(native);

            Task<StarAnalysisResult> running = service.AnalyzeAsync(runningPath);
            Assert.True(runningStarted.Wait(TimeSpan.FromSeconds(5)));

            using var abandonedCancellation = new CancellationTokenSource();
            Task<StarAnalysisResult> abandoned = service.AnalyzeAsync(
                abandonedPath,
                cancellationToken: abandonedCancellation.Token);
            abandonedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

            Task<StarAnalysisResult> latest = service.AnalyzeAsync(latestPath);
            releaseRunning.Set();
            await Task.WhenAll(running, latest);

            string[] calls = calledPaths.ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal(runningPath, calls[0], ignoreCase: true);
            Assert.Equal(latestPath, calls[1], ignoreCase: true);
            Assert.DoesNotContain(
                calls,
                path => string.Equals(
                    path,
                    abandonedPath,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            releaseRunning.Set();
            File.Delete(runningPath);
            File.Delete(abandonedPath);
            File.Delete(latestPath);
        }
    }

    [Fact]
    public async Task UnsupportedOrMalformedSchemaIsRejected()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((_, _) => ValidResultJson(schemaVersion: 2));
            var service = CreateService(native);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AnalyzeAsync(path));
            Assert.Contains("schema version 2", exception.Message, StringComparison.Ordinal);

            native.Handler = (_, _) => "{\"schemaVersion\":1}";
            exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AnalyzeAsync(path));
            Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OutOfFrameStarCoordinateIsRejected()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((_, _) => ResultJsonWithOutOfFrameStar());
            var service = CreateService(native);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AnalyzeAsync(path));
            Assert.Contains("star 0 X", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static StarAnalysisService CreateService(
        FakeNativeClient native,
        int cacheCapacity = 4) => new(
            native,
            cacheCapacity,
            new SemaphoreSlim(1, 1));

    private static string CreateImagePath()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"seiza-star-analysis-{Guid.NewGuid():N}.fits");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static string ValidResultJson(int schemaVersion = 1)
    {
        StarAnalysisCell[] cells = Enumerable.Range(0, 9)
            .Select(index => new StarAnalysisCell
            {
                Row = index / 3,
                Col = index % 3,
                StarCount = 0,
                MedianHfr = null,
                MedianEccentricity = null,
                MeanTheta = null,
                ThetaCoherence = 0,
            })
            .ToArray();
        var result = new StarAnalysisResult
        {
            SchemaVersion = schemaVersion,
            Width = 100,
            Height = 80,
            MajorAxisOrientationsNormalized = true,
            AverageHfr = 0,
            AverageFwhm = 0,
            NoiseSigma = 12.5,
            BackgroundMean = 900,
            Stars = [],
            Cells = cells,
            Tilt = new StarAnalysisTilt
            {
                CenterHfr = null,
                Corners =
                [
                    new() { Corner = StarAnalysisCornerPosition.TopLeft, Hfr = null },
                    new() { Corner = StarAnalysisCornerPosition.TopRight, Hfr = null },
                    new() { Corner = StarAnalysisCornerPosition.BottomLeft, Hfr = null },
                    new() { Corner = StarAnalysisCornerPosition.BottomRight, Hfr = null },
                ],
                MeanHfr = null,
                TiltPercent = null,
                CurvaturePercent = null,
                WorstCorner = null,
                BestCorner = null,
            },
        };
        return JsonSerializer.Serialize(
            result,
            SeizaJsonSerializerContext.Default.StarAnalysisResult);
    }

    private static string ResultJsonWithOutOfFrameStar() => """
        {
          "schemaVersion": 1,
          "width": 100,
          "height": 80,
          "majorAxisOrientationsNormalized": true,
          "averageHfr": 2.1,
          "averageFwhm": 3.5,
          "noiseSigma": 12.5,
          "backgroundMean": 900,
          "stars": [{
            "x": 100,
            "y": 20,
            "hfr": 2.1,
            "fwhm": 3.5,
            "brightness": 1000,
            "background": 900,
            "snr": 20,
            "flux": 5000,
            "pixelCount": 12,
            "saturated": false
          }],
          "cells": [
            {"row":0,"col":0,"starCount":1,"medianHfr":2.1,"medianEccentricity":0,"meanTheta":null,"thetaCoherence":0},
            {"row":0,"col":1,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":0,"col":2,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":1,"col":0,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":1,"col":1,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":1,"col":2,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":2,"col":0,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":2,"col":1,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0},
            {"row":2,"col":2,"starCount":0,"medianHfr":null,"medianEccentricity":null,"meanTheta":null,"thetaCoherence":0}
          ],
          "tilt": {
            "centerHfr": null,
            "corners": [
              {"corner":"top-left","hfr":2.1},
              {"corner":"top-right","hfr":null},
              {"corner":"bottom-left","hfr":null},
              {"corner":"bottom-right","hfr":null}
            ],
            "meanHfr": 2.1,
            "tiltPercent": null,
            "curvaturePercent": null,
            "worstCorner": null,
            "bestCorner": null
          }
        }
        """;

    private sealed class FakeNativeClient(
        Func<string, string, string> handler) : IStarAnalysisNativeClient
    {
        private int _callCount;

        public string CoreVersion { get; set; } = "0.18.5";

        public Func<string, string, string> Handler { get; set; } = handler;

        public int CallCount => Volatile.Read(ref _callCount);

        public string DetectPath(string path, string optionsJson)
        {
            Interlocked.Increment(ref _callCount);
            return Handler(path, optionsJson);
        }
    }
}
