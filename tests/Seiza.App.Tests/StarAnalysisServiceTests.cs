using System.Text.Json;
using System.Text.Json.Nodes;
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
            Assert.Null(result.TriangleTilt);
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
                return ValidTriangleResultJson(5);
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
                TriangleAngleDegrees = 725,
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
            Assert.Equal(725, root.GetProperty("triangleAngleDegrees").GetDouble());
            Assert.False(root.TryGetProperty("focalLengthMm", out _));

            using JsonDocument focalOnly = JsonDocument.Parse(
                new StarAnalysisOptions { FocalLengthMm = 749 }.ToJson());
            Assert.Equal(
                749,
                focalOnly.RootElement.GetProperty("focalLengthMm").GetDouble());
            Assert.False(focalOnly.RootElement.TryGetProperty("pixelSizeUm", out _));

            using JsonDocument pixelOnly = JsonDocument.Parse(
                new StarAnalysisOptions { PixelSizeUm = 3.76 }.ToJson());
            Assert.Equal(
                3.76,
                pixelOnly.RootElement.GetProperty("pixelSizeUm").GetDouble());
            Assert.False(pixelOnly.RootElement.TryGetProperty("focalLengthMm", out _));

            var ambiguous = new StarAnalysisOptions
            {
                Preset = StarDetectionPreset.Standard,
                FocalLengthMm = 749,
                PixelSizeUm = 3.76,
            };
            await Assert.ThrowsAsync<ArgumentException>(
                () => service.AnalyzeAsync(path, ambiguous));

            var presetWithPixelOnly = new StarAnalysisOptions
            {
                Preset = StarDetectionPreset.Standard,
                PixelSizeUm = 3.76,
            };
            await Assert.ThrowsAsync<ArgumentException>(
                () => service.AnalyzeAsync(path, presetWithPixelOnly));

            var unknownPreset = new StarAnalysisOptions
            {
                Preset = (StarDetectionPreset)999,
            };
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.AnalyzeAsync(path, unknownPreset));

            var nonfiniteTriangleAngle = new StarAnalysisOptions
            {
                TriangleAngleDegrees = double.NaN,
            };
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.AnalyzeAsync(path, nonfiniteTriangleAngle));
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
        Assert.Equal(0, root.GetProperty("triangleAngleDegrees").GetDouble());
    }

    [Fact]
    public async Task TriangleTiltContractIsDeserializedAndValidated()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((_, _) => ValidTriangleResultJson());
            var service = CreateService(native);

            StarAnalysisResult result = await service.AnalyzeAsync(
                path,
                StarAnalysisOptions.InteractiveDefault);

            StarAnalysisTriangleTilt triangle = Assert.IsType<StarAnalysisTriangleTilt>(
                result.TriangleTilt);
            Assert.True(triangle.Ready);
            Assert.Equal(0, triangle.AngleDegrees);
            Assert.Equal([1, 2, 3], triangle.Sectors.Select(sector => sector.Sector));
            Assert.Equal([0, 120, 240], triangle.Sectors.Select(
                sector => sector.AxisAngleDegrees));
            Assert.Equal(2, triangle.OverallMedianHfr);
            Assert.Equal(50, triangle.TiltPercent);
            Assert.Equal(1, triangle.BestSector);
            Assert.Equal(3, triangle.WorstSector);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("angle", "triangle angle")]
    [InlineData("radius", "image dimensions")]
    [InlineData("minimum", "minimum stars per region must be 3")]
    [InlineData("sector-order", "ordered 1, 2, 3")]
    [InlineData("axis", "axis angle is inconsistent")]
    [InlineData("median", "median HFR availability")]
    [InlineData("readiness", "readiness is inconsistent")]
    [InlineData("partial-verdict", "verdict and readiness")]
    [InlineData("best-worst", "best/worst sectors")]
    [InlineData("tilt", "tilt percent is inconsistent")]
    [InlineData("overall", "overall median HFR availability")]
    [InlineData("count", "exceed the detected-star count")]
    [InlineData("empty-annulus", "without a usable annulus")]
    public async Task InconsistentTriangleTiltContractIsRejected(
        string defect,
        string expectedMessage)
    {
        string path = CreateImagePath();
        try
        {
            JsonObject root = JsonNode.Parse(ValidTriangleResultJson())!.AsObject();
            JsonObject triangle = root["triangleTilt"]!.AsObject();
            JsonArray sectors = triangle["sectors"]!.AsArray();
            switch (defect)
            {
                case "angle":
                    triangle["angleDegrees"] = 360;
                    break;
                case "radius":
                    triangle["innerRadiusPixels"] = 17;
                    break;
                case "minimum":
                    triangle["minimumStarsPerRegion"] = 2;
                    break;
                case "sector-order":
                    sectors[0]!["sector"] = 2;
                    break;
                case "axis":
                    sectors[1]!["axisAngleDegrees"] = 121;
                    break;
                case "median":
                    sectors[0]!["medianHfr"] = null;
                    break;
                case "readiness":
                    triangle["ready"] = false;
                    break;
                case "partial-verdict":
                    triangle["bestSector"] = null;
                    break;
                case "best-worst":
                    triangle["bestSector"] = 2;
                    break;
                case "tilt":
                    triangle["tiltPercent"] = 51;
                    break;
                case "overall":
                    triangle["overallMedianHfr"] = null;
                    break;
                case "count":
                    triangle["center"]!["starCount"] = 1;
                    triangle["center"]!["medianHfr"] = 2;
                    break;
                case "empty-annulus":
                    root["width"] = 1000;
                    root["height"] = 100;
                    triangle["innerRadiusPixels"] =
                        0.25 * Math.Sqrt(Math.Pow(500, 2) + Math.Pow(50, 2));
                    triangle["outerRadiusPixels"] = 50;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown defect {defect}.");
            }

            var native = new FakeNativeClient((_, _) => root.ToJsonString());
            var service = CreateService(native);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AnalyzeAsync(
                    path,
                    new StarAnalysisOptions { TriangleAngleDegrees = 0 }));
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SparseTriangleRetainsMeasurementsButWithholdsVerdict()
    {
        string path = CreateImagePath();
        try
        {
            JsonObject root = JsonNode.Parse(ValidTriangleResultJson())!.AsObject();
            JsonObject triangle = root["triangleTilt"]!.AsObject();
            triangle["sectors"]![2]!["starCount"] = 2;
            triangle["ready"] = false;
            triangle["tiltPercent"] = null;
            triangle["bestSector"] = null;
            triangle["worstSector"] = null;
            var native = new FakeNativeClient((_, _) => root.ToJsonString());
            var service = CreateService(native);

            StarAnalysisResult result = await service.AnalyzeAsync(
                path,
                new StarAnalysisOptions { TriangleAngleDegrees = 0 });

            Assert.False(result.TriangleTilt!.Ready);
            Assert.Equal(2.5, result.TriangleTilt.Sectors[2].MedianHfr);
            Assert.Equal(2, result.TriangleTilt.OverallMedianHfr);
            Assert.Null(result.TriangleTilt.TiltPercent);
            Assert.Null(result.TriangleTilt.BestSector);
            Assert.Null(result.TriangleTilt.WorstSector);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CacheKeyIncludesExplicitTriangleAngle()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient((_, optionsJson) =>
            {
                using JsonDocument document = JsonDocument.Parse(optionsJson);
                return document.RootElement.TryGetProperty(
                    "triangleAngleDegrees",
                    out JsonElement angle)
                    ? ValidTriangleResultJson(NormalizeDegrees(angle.GetDouble()))
                    : ValidResultJson();
            });
            var service = CreateService(native);

            await service.AnalyzeAsync(path);
            await service.AnalyzeAsync(
                path,
                new StarAnalysisOptions { TriangleAngleDegrees = 0 });
            await service.AnalyzeAsync(
                path,
                new StarAnalysisOptions { TriangleAngleDegrees = 0 });
            await service.AnalyzeAsync(
                path,
                new StarAnalysisOptions { TriangleAngleDegrees = 120 });

            Assert.Equal(3, native.CallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("missing", "missing from a request")]
    [InlineData("unexpected", "without being requested")]
    [InlineData("angle", "does not match the requested angle")]
    public async Task TriangleTiltMustCorrelateWithItsRequest(
        string defect,
        string expectedMessage)
    {
        string path = CreateImagePath();
        try
        {
            string response = defect switch
            {
                "missing" => ValidResultJson(),
                "unexpected" => ValidTriangleResultJson(),
                "angle" => ValidTriangleResultJson(120),
                _ => throw new InvalidOperationException($"Unknown defect {defect}."),
            };
            var native = new FakeNativeClient((_, _) => response);
            var service = CreateService(native);
            StarAnalysisOptions? options = defect == "unexpected"
                ? null
                : new StarAnalysisOptions { TriangleAngleDegrees = 0 };

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AnalyzeAsync(path, options));
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
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
    public async Task SameSizeTimestampPreservingReplacementIsRejectedByFileIdentity()
    {
        string path = CreateImagePath();
        string replacement = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"seiza-star-analysis-replacement-{Guid.NewGuid():N}.fits");
        long originalLength = new FileInfo(path).Length;
        long originalWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
        string? originalIdentity = WindowsFileIdentity.TryGet(path);
        try
        {
            Assert.NotNull(originalIdentity);
            var native = new FakeNativeClient((nativePath, _) =>
            {
                File.WriteAllBytes(replacement, [7, 8, 9]);
                File.SetLastWriteTimeUtc(
                    replacement,
                    new DateTime(originalWriteTicks, DateTimeKind.Utc));
                File.Move(replacement, nativePath, overwrite: true);

                var replaced = new FileInfo(nativePath);
                Assert.Equal(originalLength, replaced.Length);
                Assert.Equal(originalWriteTicks, replaced.LastWriteTimeUtc.Ticks);
                Assert.NotEqual(originalIdentity, WindowsFileIdentity.TryGet(nativePath));
                return ValidResultJson();
            });
            var service = CreateService(native);

            await Assert.ThrowsAsync<StarAnalysisSourceChangedException>(
                () => service.AnalyzeAsync(path));

            Assert.Equal(1, native.CallCount);
        }
        finally
        {
            File.Delete(replacement);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CacheDoesNotCrossTimestampPreservingFileReplacement()
    {
        string path = CreateImagePath();
        string replacement = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"seiza-star-analysis-replacement-{Guid.NewGuid():N}.fits");
        long originalWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
        try
        {
            string? originalIdentity = WindowsFileIdentity.TryGet(path);
            Assert.NotNull(originalIdentity);
            var native = new FakeNativeClient((_, _) => ValidResultJson());
            var service = CreateService(native);
            StarAnalysisResult first = await service.AnalyzeAsync(path);

            File.WriteAllBytes(replacement, [7, 8, 9]);
            File.SetLastWriteTimeUtc(
                replacement,
                new DateTime(originalWriteTicks, DateTimeKind.Utc));
            File.Move(replacement, path, overwrite: true);
            Assert.NotEqual(originalIdentity, WindowsFileIdentity.TryGet(path));

            StarAnalysisResult second = await service.AnalyzeAsync(path);

            Assert.NotSame(first, second);
            Assert.Equal(2, native.CallCount);
        }
        finally
        {
            File.Delete(replacement);
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

    [Theory]
    [InlineData("mean", "mean HFR does not match")]
    [InlineData("best-worst", "best/worst corners")]
    [InlineData("tilt", "tilt percent is inconsistent")]
    [InlineData("curvature", "curvature percent is inconsistent")]
    [InlineData("zero-cell", "median HFR must be finite and positive")]
    public async Task InconsistentParallelogramTiltContractIsRejected(
        string defect,
        string expectedMessage)
    {
        string path = CreateImagePath();
        try
        {
            JsonObject root = JsonNode.Parse(ValidTiltResultJson())!.AsObject();
            JsonObject tilt = root["tilt"]!.AsObject();
            switch (defect)
            {
                case "mean":
                    tilt["meanHfr"] = 4;
                    break;
                case "best-worst":
                    tilt["bestCorner"] = "top-right";
                    break;
                case "tilt":
                    tilt["tiltPercent"] = 159;
                    break;
                case "curvature":
                    tilt["curvaturePercent"] = 1;
                    break;
                case "zero-cell":
                    root["cells"]!.AsArray()[0]!.AsObject()["medianHfr"] = 0;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown defect {defect}.");
            }

            var native = new FakeNativeClient((_, _) => root.ToJsonString());
            var service = CreateService(native);

            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.AnalyzeAsync(path));
            Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SymmetricParallelogramUsesNativeStableCornerTieOrder()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient(
                (_, _) => ValidTiltResultJson(Enumerable.Repeat(5.0, 9).ToArray()));
            var service = CreateService(native);

            StarAnalysisResult result = await service.AnalyzeAsync(path);

            Assert.Equal(StarAnalysisCornerPosition.TopLeft, result.Tilt.BestCorner);
            Assert.Equal(StarAnalysisCornerPosition.BottomRight, result.Tilt.WorstCorner);
            Assert.Equal(0, result.Tilt.TiltPercent);
            Assert.Equal(0, result.Tilt.CurvaturePercent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParallelogramWorstCornerUsesLastEqualMaximum()
    {
        string path = CreateImagePath();
        try
        {
            var native = new FakeNativeClient(
                (_, _) => ValidTiltResultJson([1, 2, 9, 4, 5, 6, 7, 8, 9]));
            var service = CreateService(native);

            StarAnalysisResult result = await service.AnalyzeAsync(path);

            Assert.Equal(StarAnalysisCornerPosition.TopLeft, result.Tilt.BestCorner);
            Assert.Equal(StarAnalysisCornerPosition.BottomRight, result.Tilt.WorstCorner);
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

    private static string ValidTiltResultJson(double[]? hfrValues = null)
    {
        hfrValues ??= [1, 2, 3, 4, 5, 6, 7, 8, 9];
        if (hfrValues.Length != 9 || hfrValues.Any(hfr => hfr <= 0))
        {
            throw new ArgumentException("Exactly nine positive HFR values are required.");
        }

        StarAnalysisStar[] stars = Enumerable.Range(0, 9)
            .Select(index => new StarAnalysisStar
            {
                X = 10 + (index % 3) * 40,
                Y = 10 + (index / 3) * 30,
                Hfr = hfrValues[index],
                Fwhm = 3,
                Brightness = 1000,
                Background = 900,
                Snr = 20,
                Flux = 5000,
                PixelCount = 12,
                Saturated = false,
                Eccentricity = null,
                Theta = null,
                RSquared = null,
            })
            .ToArray();
        StarAnalysisCell[] cells = stars
            .Select((star, index) => new StarAnalysisCell
            {
                Row = index / 3,
                Col = index % 3,
                StarCount = 1,
                MedianHfr = star.Hfr,
                MedianEccentricity = 0,
                MeanTheta = null,
                ThetaCoherence = 0,
            })
            .ToArray();
        StarAnalysisCornerPosition[] orderedCorners =
        [
            StarAnalysisCornerPosition.TopLeft,
            StarAnalysisCornerPosition.TopRight,
            StarAnalysisCornerPosition.BottomLeft,
            StarAnalysisCornerPosition.BottomRight,
        ];
        double[] cornerHfrs = [hfrValues[0], hfrValues[2], hfrValues[6], hfrValues[8]];
        (StarAnalysisCornerPosition Corner, double Hfr)[] sortedCorners = orderedCorners
            .Select((corner, index) => (Corner: corner, Hfr: cornerHfrs[index]))
            .OrderBy(measurement => measurement.Hfr)
            .ToArray();
        StarAnalysisCornerPosition bestCorner = sortedCorners[0].Corner;
        StarAnalysisCornerPosition worstCorner = sortedCorners[^1].Corner;
        double meanHfr = TestMedian(hfrValues);
        double cornerMean = cornerHfrs.Average();
        var result = new StarAnalysisResult
        {
            SchemaVersion = 1,
            Width = 100,
            Height = 80,
            MajorAxisOrientationsNormalized = true,
            AverageHfr = hfrValues.Average(),
            AverageFwhm = 3,
            NoiseSigma = 12.5,
            BackgroundMean = 900,
            Stars = stars,
            Cells = cells,
            Tilt = new StarAnalysisTilt
            {
                CenterHfr = hfrValues[4],
                Corners =
                [
                    new() { Corner = StarAnalysisCornerPosition.TopLeft, Hfr = cornerHfrs[0] },
                    new() { Corner = StarAnalysisCornerPosition.TopRight, Hfr = cornerHfrs[1] },
                    new() { Corner = StarAnalysisCornerPosition.BottomLeft, Hfr = cornerHfrs[2] },
                    new() { Corner = StarAnalysisCornerPosition.BottomRight, Hfr = cornerHfrs[3] },
                ],
                MeanHfr = meanHfr,
                TiltPercent = 100 * (cornerHfrs.Max() - cornerHfrs.Min()) / meanHfr,
                CurvaturePercent = 100 * (cornerMean / hfrValues[4] - 1),
                WorstCorner = worstCorner,
                BestCorner = bestCorner,
            },
        };
        return JsonSerializer.Serialize(
            result,
            SeizaJsonSerializerContext.Default.StarAnalysisResult);
    }

    private static double TestMedian(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        int midpoint = values.Length / 2;
        return values.Length % 2 == 1
            ? values[midpoint]
            : (values[midpoint - 1] + values[midpoint]) / 2;
    }

    private static string ValidTriangleResultJson(double angleDegrees = 0)
    {
        StarAnalysisStar[] stars = Enumerable.Range(0, 9)
            .Select(index => new StarAnalysisStar
            {
                X = 10 + index,
                Y = 20,
                Hfr = 1.5 + (index / 3) * 0.5,
                Fwhm = 3,
                Brightness = 1000,
                Background = 900,
                Snr = 20,
                Flux = 5000,
                PixelCount = 12,
                Saturated = false,
                Eccentricity = null,
                Theta = null,
                RSquared = null,
            })
            .ToArray();
        StarAnalysisCell[] cells = Enumerable.Range(0, 9)
            .Select(index => new StarAnalysisCell
            {
                Row = index / 3,
                Col = index % 3,
                StarCount = index == 0 ? stars.Length : 0,
                MedianHfr = index == 0 ? 2 : null,
                MedianEccentricity = null,
                MeanTheta = null,
                ThetaCoherence = 0,
            })
            .ToArray();
        var result = new StarAnalysisResult
        {
            SchemaVersion = 1,
            Width = 100,
            Height = 80,
            MajorAxisOrientationsNormalized = true,
            AverageHfr = 2,
            AverageFwhm = 3,
            NoiseSigma = 12.5,
            BackgroundMean = 900,
            Stars = stars,
            Cells = cells,
            Tilt = new StarAnalysisTilt
            {
                CenterHfr = null,
                Corners =
                [
                    new() { Corner = StarAnalysisCornerPosition.TopLeft, Hfr = 2 },
                    new() { Corner = StarAnalysisCornerPosition.TopRight, Hfr = null },
                    new() { Corner = StarAnalysisCornerPosition.BottomLeft, Hfr = null },
                    new() { Corner = StarAnalysisCornerPosition.BottomRight, Hfr = null },
                ],
                MeanHfr = 2,
                TiltPercent = null,
                CurvaturePercent = null,
                WorstCorner = null,
                BestCorner = null,
            },
            TriangleTilt = new StarAnalysisTriangleTilt
            {
                AngleDegrees = angleDegrees,
                InnerRadiusPixels = 0.25 * Math.Sqrt(Math.Pow(50, 2) + Math.Pow(40, 2)),
                OuterRadiusPixels = 40,
                MinimumStarsPerRegion = 3,
                Ready = true,
                Center = new StarAnalysisTriangleCenter
                {
                    StarCount = 0,
                    MedianHfr = null,
                },
                Sectors =
                [
                    new()
                    {
                        Sector = 1,
                        AxisAngleDegrees = angleDegrees,
                        StarCount = 3,
                        MedianHfr = 1.5,
                    },
                    new()
                    {
                        Sector = 2,
                        AxisAngleDegrees = NormalizeDegrees(angleDegrees + 120),
                        StarCount = 3,
                        MedianHfr = 2,
                    },
                    new()
                    {
                        Sector = 3,
                        AxisAngleDegrees = NormalizeDegrees(angleDegrees + 240),
                        StarCount = 3,
                        MedianHfr = 2.5,
                    },
                ],
                OverallMedianHfr = 2,
                TiltPercent = 50,
                BestSector = 1,
                WorstSector = 3,
            },
        };
        return JsonSerializer.Serialize(
            result,
            SeizaJsonSerializerContext.Default.StarAnalysisResult);
    }

    private static double NormalizeDegrees(double value)
    {
        double normalized = value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

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
