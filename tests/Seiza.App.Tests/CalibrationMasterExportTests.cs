using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationMasterExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"seiza-master-export-{Guid.NewGuid():N}");

    public CalibrationMasterExportTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task BuildsWithoutLightsAndPublishesVerifiedMaster()
    {
        CalibrationMasterBuildRequest request = Request();
        CalibrationMasterBuildResult result = await CalibrationMasterExport.BuildAsync(request, [], build: Build);
        Assert.Equal(request.Output, result.Output);
        Assert.Equal(2, result.InputFrames);
        Assert.Equal("master", await File.ReadAllTextAsync(request.Output));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task RefusesOutputThatIsAnInputOrAnotherProtectedSource()
    {
        CalibrationMasterBuildRequest request = Request();
        foreach (string path in request.Inputs.Append(Path.Combine(_directory, "unselected.fits")))
        {
            await File.WriteAllTextAsync(path, "source");
            await Assert.ThrowsAsync<InvalidOperationException>(() => CalibrationMasterExport.BuildAsync(
                request with { Output = path }, [path], build: (_, _) => throw new InvalidOperationException("Must not build")));
            Assert.Equal("source", await File.ReadAllTextAsync(path));
        }
    }

    [Theory]
    [InlineData("dark", true, false)]
    [InlineData("flat", true, false)]
    [InlineData("flat", false, true)]
    public async Task ForwardsCalibrationDependenciesWithoutRequiringLights(string kind, bool bias, bool dark)
    {
        CalibrationMasterBuildRequest request = Request() with
        {
            Kind = kind,
            Bias = bias ? Path.Combine(_directory, "bias.fits") : null,
            Dark = dark ? Path.Combine(_directory, "dark-flat.fits") : null,
        };
        CalibrationMasterBuildResult result = await CalibrationMasterExport.BuildAsync(request, [],
            build: async (staged, token) =>
            {
                Assert.Equal(request.Bias, staged.Bias);
                Assert.Equal(request.Dark, staged.Dark);
                return (await Build(staged, token)) with
                {
                    BiasSubtracted = bias || kind == "flat",
                    DarkSubtracted = dark,
                    Normalized = kind == "flat",
                };
            });
        Assert.Equal(kind, result.Kind);
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task InvalidNativeAccountingPreservesExistingOutput()
    {
        CalibrationMasterBuildRequest request = Request();
        await File.WriteAllTextAsync(request.Output, "existing");
        await Assert.ThrowsAsync<InvalidDataException>(() => CalibrationMasterExport.BuildAsync(request, [],
            build: async (staged, token) => (await Build(staged, token)) with { Inputs = [] }));
        Assert.Equal("existing", await File.ReadAllTextAsync(request.Output));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task CancellationAfterBuildPreservesExistingOutputAndRemovesStaging()
    {
        CalibrationMasterBuildRequest request = Request();
        await File.WriteAllTextAsync(request.Output, "existing");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalibrationMasterExport.BuildAsync(
            request, [], token: cancellation.Token, build: async (staged, token) =>
            {
                CalibrationMasterBuildResult result = await Build(staged, token);
                cancellation.Cancel();
                return result;
            }));
        Assert.Equal("existing", await File.ReadAllTextAsync(request.Output));
        AssertNoStagingFiles();
    }

    [Fact]
    public async Task NativeFailureRemovesPartialStagingAndPreservesOutput()
    {
        CalibrationMasterBuildRequest request = Request();
        await File.WriteAllTextAsync(request.Output, "existing");
        await Assert.ThrowsAsync<IOException>(() => CalibrationMasterExport.BuildAsync(request, [],
            build: async (staged, token) =>
            {
                await File.WriteAllTextAsync(staged.Output, "partial", token);
                throw new IOException("build failed");
            }));
        Assert.Equal("existing", await File.ReadAllTextAsync(request.Output));
        AssertNoStagingFiles();
    }

    private CalibrationMasterBuildRequest Request() => new()
    {
        Kind = "bias",
        Inputs = [Path.Combine(_directory, "bias-1.fits"), Path.Combine(_directory, "bias-2.fits")],
        Output = Path.Combine(_directory, "master-bias.fits"),
    };

    private static async Task<CalibrationMasterBuildResult> Build(CalibrationMasterBuildRequest request, CancellationToken token)
    {
        await File.WriteAllTextAsync(request.Output, "master", token);
        return new()
        {
            SchemaVersion = 2, Kind = request.Kind, Output = request.Output,
            Width = 8, Height = 8, Channels = 1,
            RequestedFrames = request.Inputs.Count, InputFrames = request.Inputs.Count,
            Inputs = request.Inputs.Select(path => new CalibrationMasterInputResult { Path = path }).ToArray(),
        };
    }

    private void AssertNoStagingFiles() => Assert.Empty(Directory.EnumerateFiles(_directory, ".seiza-master-*.fits"));
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

