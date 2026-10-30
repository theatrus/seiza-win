using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class BatchStackSourceProtectionTests
{
    [WindowsStackSourceIdentityFact]
    public async Task GradedDiscoverySkipsJunctionDirectoriesButExplicitFilesUseCanonicalSources()
    {
        string library = Directory.CreateDirectory(PathFor("library")).FullName;
        string physical = Directory.CreateDirectory(PathFor("physical")).FullName;
        var sky = CalibrationLibraryTests.Sky(160, 128);
        string[] lights = Enumerable.Range(0, 2).Select(i => PathFor($"light-{i}.fits")).ToArray();
        foreach (string light in lights) CalibrationParityFixtures.WriteFrame(light, 160, 128, (x, y) => (ushort)(sky[y * 160 + x] + 500));
        string[] biases = Enumerable.Range(0, 3).Select(i => Path.Combine(physical, $"bias-{i}.fits")).ToArray();
        foreach (string bias in biases) CalibrationParityFixtures.WriteFrame(bias, 160, 128, (x, y) => (ushort)(500 + (x + y) % 5), "BIAS");
        string alias = Path.Combine(library, "linked");
        Directory.Move(CreateJunction(physical), alias);
        _junction = alias;
        var prefs = new CalibrationLibraryPreferences { Incomplete = "keep" };
        var scan = LibraryRequest.Create("analyze", library, lights, [], prefs) with { Cache = PathFor("cache") };
        var empty = await Assert.ThrowsAsync<SeizaCoreException>(() => CalibrationLibraryService.PerformAsync(scan));
        Assert.Contains("Choose calibration files", empty.Message);
        var request = LibraryRequest.Create("analyze", library, lights, biases.Select(p => Path.Combine(alias, Path.GetFileName(p))), prefs) with { Cache = PathFor("cache") };
        var review = await CalibrationLibraryService.PerformAsync(request);
        var built = await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        var plan = new LibraryPreparedPlan(built, lights);
        Assert.Equal(3, built.Frames.Length);
        Assert.All(built.Frames, f => Assert.Contains(biases, p => StackSourceIdentity.RefersToSameFile(p, f.Path)));
        string master = plan.Session(lights[0]).Bias!;
        DateTime modified = File.GetLastWriteTimeUtc(master);
        await CalibrationLibraryService.PerformAsync(request with { Operation = "execute", ReviewKey = review.ReviewKey });
        Assert.Equal(modified, File.GetLastWriteTimeUtc(master));
        var job = new ImageStackJob(new("all", null, lights), new(lights, Path.Combine(alias, Path.GetFileName(biases[0])),
            new ImageStackOptions { UseGpu = false }, new()) { LibraryPlan = plan });
        Assert.Throws<ArgumentException>(() => ImageStackValidation.ValidateBatch([job]));
        File.WriteAllText(biases[0], "changed source");
        Assert.ThrowsAny<Exception>(() => CalibrationLibraryService.Verify(built.Sources));
    }
}
