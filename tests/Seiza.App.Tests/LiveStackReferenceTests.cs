using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackReferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));

    public LiveStackReferenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ExistingCandidatesUseWatcherStabilityAndEveryExclusion()
    {
        string sessions = Path.Combine(_directory, "sessions");
        Directory.CreateDirectory(sessions);
        string light = CreateFile("light.fits");
        string output = CreateFile("output.fits");
        string bias = CreateFile("bias.fits");
        string dark = CreateFile("dark.fits");
        string flat = CreateFile("flat.fits");
        _ = CreateFile(".seiza-stack-in-progress.fits");
        _ = CreateFile(Path.Combine("sessions", "session-image.fits"));
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions
        {
            FolderPath = _directory,
            IncludeSubdirectories = true,
            ExcludedPaths = [output, bias, dark, flat],
            ExcludedDirectories = [sessions],
        });
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.Empty(observations.ObserveExisting(start));
        Assert.True(observations.HasPendingCandidates);
        Assert.Empty(observations.ObserveExisting(start.AddSeconds(1)));
        StackFileReadyCandidate ready = Assert.Single(observations.ObserveExisting(start.AddSeconds(2)));
        Assert.Equal(light, ready.Path);
        Assert.True(LiveStackReferenceCandidates.IsUnchanged(ready));

        File.AppendAllText(light, "writer is still active");
        Assert.False(LiveStackReferenceCandidates.IsUnchanged(ready));
    }

    [Fact]
    public void AChangingCaptureMustCompleteAnotherFullStableWindow()
    {
        string light = CreateFile("changing.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        DateTimeOffset start = DateTimeOffset.UtcNow;
        Assert.Empty(observations.ObserveExisting(start));
        File.AppendAllText(light, "new pixels");
        Assert.Empty(observations.ObserveExisting(start.AddSeconds(2)));
        Assert.Empty(observations.ObserveExisting(start.AddSeconds(3)));
        Assert.Single(observations.ObserveExisting(start.AddSeconds(4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibleRestoreDoesNotRankMissingOrUnscorableSources(bool removeOriginal)
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string reference = Path.Combine(_directory, "reference.fits");
        StackingOptionsNativeTests.WriteStarField(reference, 0, bayer: false);
        var configuration = Configuration(captures) with { InitialReferencePath = reference };
        await using (var original = new LiveStackCoordinator(configuration))
        {
            await original.StartAsync();
            await original.PauseAndSaveAsync();
            Assert.Equal(1, original.CurrentSnapshot.AcceptedFrames);
        }
        if (removeOriginal)
        {
            File.Delete(reference);
        }
        // A valid raw light too small for the scorer's 64px sky tiles cannot
        // be ranked. Its presence must not stop a compatible restore.
        StackingOptionsNativeTests.WriteStarField(Path.Combine(captures, "unscorable.fits"),
            0, bayer: false, side: 64);
        int scoreCalls = 0;
        Task<StackReferenceSelection> NeverScore(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            scoreCalls++;
            throw new InvalidOperationException("A restored stack must never rank new references.");
        }
        await using var restored = new LiveStackCoordinator(
            configuration with { InitialReferencePath = null, ChooseReferenceAutomatically = true },
            referenceSelector: NeverScore);

        await restored.StartAsync();
        await restored.PauseAndSaveAsync();

        Assert.Equal(0, scoreCalls);
        Assert.Equal(1, restored.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(LiveStackRunState.Paused, restored.CurrentSnapshot.State);
    }

    [Fact]
    public async Task ASelectionChangedDuringScoringIsNotSeededAndTheWatcherCanRetryIt()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string reference = Path.Combine(captures, "reference.fits");
        StackingOptionsNativeTests.WriteStarField(reference, 0, bayer: false);
        int scoreCalls = 0;
        Task<StackReferenceSelection> ChangeWhileScoring(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            scoreCalls++;
            Assert.Equal(reference, Assert.Single(paths));
            File.SetLastWriteTimeUtc(reference, File.GetLastWriteTimeUtc(reference).AddSeconds(10));
            return Task.FromResult(new StackReferenceSelection(1, 0, reference,
                [new StackReferenceScore(40, 12, 1000, 2, 8)]));
        }
        await using var coordinator = new LiveStackCoordinator(
            Configuration(captures) with { ResumeExisting = false, ChooseReferenceAutomatically = true },
            referenceSelector: ChangeWhileScoring);
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += (_, _) =>
        {
            if (coordinator.CurrentSnapshot.AcceptedFrames > 0)
            {
                accepted.TrySetResult(true);
            }
        };

        await coordinator.StartAsync();
        Assert.Equal(1, scoreCalls);
        Assert.Equal(0, coordinator.CurrentSnapshot.AcceptedFrames);
        Assert.Contains(coordinator.CurrentSnapshot.Attention, item => item.Message.Contains("changed during scoring", StringComparison.Ordinal));
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await coordinator.PauseAndSaveAsync();

        Assert.Equal(1, coordinator.CurrentSnapshot.AcceptedFrames);
    }

    [Fact]
    public async Task UnscorableCandidatesFallBackToWatchingInsteadOfFailingStartup()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(captures, "reference.fits"), 0, bayer: false);
        Task<StackReferenceSelection> Unscorable(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
            throw new SeizaCoreException("no frame could be scored as a reference");
        await using var coordinator = new LiveStackCoordinator(
            Configuration(captures) with { ResumeExisting = false, ChooseReferenceAutomatically = true },
            referenceSelector: Unscorable);

        await coordinator.StartAsync();
        Assert.Equal(LiveStackRunState.WaitingForLight, coordinator.CurrentSnapshot.State);
        Assert.Contains(coordinator.CurrentSnapshot.Attention, item => item.Message.Contains("Could not rank", StringComparison.Ordinal));
        await coordinator.PauseAndSaveAsync();
    }

    private LiveStackRunConfiguration Configuration(string captures) => new()
    {
        WatchFolder = captures,
        SessionRootDirectory = Path.Combine(_directory, "sessions"),
        PreviewMaxDimension = 128,
    };

    private string CreateFile(string relativePath)
    {
        string path = Path.Combine(_directory, relativePath);
        File.WriteAllText(path, "candidate");
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
