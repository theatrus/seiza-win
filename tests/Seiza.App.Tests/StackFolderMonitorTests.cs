using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackFolderMonitorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"seiza-folder-monitor-{Guid.NewGuid():N}");

    public StackFolderMonitorTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CandidateNeedsTwoObservationsAndTheFullStableDuration()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "light-001.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(tracker.Observe(Observation(path, start)));
        Assert.Null(tracker.Observe(Observation(path, start.AddSeconds(1))));

        StackFileReadyCandidate ready = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(2))));
        Assert.Equal(1, ready.Attempt);
    }

    [Fact]
    public void ASizeChangeRestartsTheStabilityWindow()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "light-002.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(tracker.Observe(Observation(path, start, length: 100)));
        Assert.Null(tracker.Observe(Observation(path, start.AddSeconds(2), length: 200)));
        Assert.Null(tracker.Observe(Observation(path, start.AddSeconds(3), length: 200)));
        Assert.NotNull(tracker.Observe(Observation(path, start.AddSeconds(4), length: 200)));
    }

    [Fact]
    public void RetryableFailuresBackOffAndTerminalResultsStayDeduplicated()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "light-003.xisf");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.Observe(Observation(path, start));
        StackFileReadyCandidate first = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(2))));

        tracker.Complete(
            first,
            new StackFileProcessingResult(StackFileProcessingDisposition.RetryableFailure),
            start.AddSeconds(2));
        Assert.Null(tracker.Observe(Observation(path, start.AddSeconds(2.5))));
        StackFileReadyCandidate second = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(3))));
        Assert.Equal(2, second.Attempt);

        tracker.Complete(
            second,
            new StackFileProcessingResult(StackFileProcessingDisposition.Rejected),
            start.AddSeconds(3));
        Assert.Null(tracker.Observe(Observation(path, start.AddMinutes(1))));
    }

    [Fact]
    public void UnreadableTerminalReopensWhenTheFileIdentityChanges()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "unreadable.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.Observe(Observation(path, start, identity: "volume:old"));
        StackFileReadyCandidate first = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(2), identity: "volume:old")));
        tracker.Complete(
            first,
            new StackFileProcessingResult(StackFileProcessingDisposition.Unreadable),
            start.AddSeconds(2));

        Assert.Null(tracker.Observe(Observation(
            path,
            start.AddSeconds(3),
            identity: "volume:new")));
        StackFileReadyCandidate retried = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(
                path,
                start.AddSeconds(5),
                identity: "volume:new")));

        Assert.Equal(1, retried.Attempt);
        Assert.Equal("volume:new", retried.FileIdentity);
    }

    [Fact]
    public void UnchangedUnreadableFileRetriesAfterTheMaximumCooldown()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "locked-then-readable.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.Observe(Observation(path, start, identity: "volume:same"));
        StackFileReadyCandidate first = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(
                path,
                start.AddSeconds(2),
                identity: "volume:same")));
        tracker.Complete(
            first,
            new StackFileProcessingResult(StackFileProcessingDisposition.Unreadable),
            start.AddSeconds(2));

        Assert.Null(tracker.Observe(Observation(
            path,
            start.AddSeconds(9),
            identity: "volume:same")));
        Assert.Null(tracker.Observe(Observation(
            path,
            start.AddSeconds(10),
            identity: "volume:same")));
        Assert.NotNull(tracker.Observe(Observation(
            path,
            start.AddSeconds(11),
            identity: "volume:same")));
    }

    [Fact]
    public void RetryNowReopensOnlyAnUnreadableTerminal()
    {
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        string unreadablePath = Path.Combine(_directory, "retry-unreadable.fits");
        StackFileCandidateTracker tracker = CreateTracker();
        tracker.Observe(Observation(unreadablePath, start));
        StackFileReadyCandidate unreadable = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(unreadablePath, start.AddSeconds(2))));
        tracker.Complete(
            unreadable,
            new StackFileProcessingResult(StackFileProcessingDisposition.Unreadable),
            start.AddSeconds(2));

        tracker.RetryNow(unreadablePath);
        Assert.NotNull(tracker.Observe(Observation(unreadablePath, start.AddSeconds(3))));

        string acceptedPath = Path.Combine(_directory, "accepted.fits");
        tracker.Observe(Observation(acceptedPath, start));
        StackFileReadyCandidate accepted = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(acceptedPath, start.AddSeconds(2))));
        tracker.Complete(
            accepted,
            new StackFileProcessingResult(StackFileProcessingDisposition.Accepted),
            start.AddSeconds(2));
        tracker.RetryNow(acceptedPath);
        Assert.Null(tracker.Observe(Observation(
            acceptedPath,
            start.AddSeconds(10),
            length: 200)));
    }

    [Theory]
    [InlineData((int)StackFileProcessingDisposition.Accepted)]
    [InlineData((int)StackFileProcessingDisposition.Rejected)]
    [InlineData((int)StackFileProcessingDisposition.Ignored)]
    public void DurableTerminalResultsStayTerminalAfterIdentityChanges(
        int dispositionValue)
    {
        var disposition = (StackFileProcessingDisposition)dispositionValue;
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, $"terminal-{disposition}.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.Observe(Observation(path, start, identity: "volume:first"));
        StackFileReadyCandidate ready = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(2), identity: "volume:first")));
        tracker.Complete(ready, new StackFileProcessingResult(disposition), start.AddSeconds(2));

        Assert.Null(tracker.Observe(Observation(
            path,
            start.AddSeconds(10),
            length: 200,
            identity: "volume:replacement")));
    }

    [Fact]
    public void PersistedUnreadableIdentityCanRetryAfterRestartAndReplacement()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "persisted-unreadable.fits");
        DateTimeOffset writeTime = new(2026, 8, 19, 11, 59, 0, TimeSpan.Zero);
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.SeedProcessedFrames(
        [
            new LiveStackPersistedFrame
            {
                Path = path,
                Disposition = LiveStackPersistedFrameDisposition.Unreadable,
                Length = 100,
                LastWriteTimeUtc = writeTime,
                FileIdentity = "volume:old",
            },
        ]);

        Assert.Null(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            start,
            "volume:new")));
        Assert.NotNull(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            start.AddSeconds(2),
            "volume:new")));
    }

    [Fact]
    public void PersistedUnreadableRetriesAfterCooldownWithoutAFileChange()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "persisted-locked.fits");
        DateTimeOffset writeTime = new(2026, 8, 19, 11, 59, 0, TimeSpan.Zero);
        DateTimeOffset processedAt = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.SeedProcessedFrames(
        [
            new LiveStackPersistedFrame
            {
                Path = path,
                Disposition = LiveStackPersistedFrameDisposition.Unreadable,
                Length = 100,
                LastWriteTimeUtc = writeTime,
                ProcessedAtUtc = processedAt,
                FileIdentity = "volume:same",
            },
        ]);

        Assert.Null(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            processedAt.AddSeconds(7),
            "volume:same")));
        Assert.Null(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            processedAt.AddSeconds(8),
            "volume:same")));
        Assert.NotNull(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            processedAt.AddSeconds(9),
            "volume:same")));
    }

    [Fact]
    public void LegacyPersistedUnreadableRetriesWhenAStableIdentityBecomesAvailable()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "legacy-unreadable.fits");
        DateTimeOffset writeTime = new(2026, 8, 19, 11, 59, 0, TimeSpan.Zero);
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.SeedProcessedFrames(
        [
            new LiveStackPersistedFrame
            {
                Path = path,
                Disposition = LiveStackPersistedFrameDisposition.Unreadable,
                Length = 100,
                LastWriteTimeUtc = writeTime,
            },
        ]);

        Assert.Null(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            start,
            "volume:first-known")));
        Assert.NotNull(tracker.Observe(new StackFileObservation(
            path,
            100,
            writeTime,
            start.AddSeconds(2),
            "volume:first-known")));
    }

    [Fact]
    public void StableFileIdentityDeduplicatesRenamesAndHardLinkAliases()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        string firstPath = Path.Combine(_directory, "identity-first.fits");
        string aliasPath = Path.Combine(_directory, "identity-alias.fits");
        tracker.Observe(Observation(firstPath, start, identity: "volume:file-id"));
        StackFileReadyCandidate first = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(
                firstPath,
                start.AddSeconds(2),
                identity: "volume:file-id")));
        tracker.Complete(
            first,
            new StackFileProcessingResult(StackFileProcessingDisposition.Accepted),
            start.AddSeconds(2));

        Assert.Null(tracker.Observe(Observation(
            aliasPath,
            start.AddSeconds(3),
            identity: "volume:file-id")));
        Assert.Null(tracker.Observe(Observation(
            aliasPath,
            start.AddSeconds(10),
            identity: "volume:file-id")));
    }

    [Fact]
    public async Task WindowsFileIdentitySurvivesARename()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        string firstPath = Path.Combine(_directory, "identity-source.fits");
        string renamedPath = Path.Combine(_directory, "identity-renamed.fits");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
        string? before = WindowsFileIdentity.TryGet(firstPath);

        File.Move(firstPath, renamedPath);
        string? after = WindowsFileIdentity.TryGet(renamedPath);

        Assert.NotNull(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ExplicitAndStagingExclusionsNeverBecomeCandidates()
    {
        string output = Path.Combine(_directory, "stacked.fits");
        var options = Options() with { ExcludedPaths = [output] };
        var tracker = new StackFileCandidateTracker(options);

        Assert.False(tracker.ShouldConsider(output));
        Assert.False(tracker.ShouldConsider(Path.Combine(_directory, ".seiza-stack-123.fits")));
        Assert.False(tracker.ShouldConsider(Path.Combine(_directory, "notes.txt")));
        Assert.True(tracker.ShouldConsider(Path.Combine(_directory, "light-004.fit")));
    }

    [Fact]
    public void ReservedOutputInvalidatesAQueuedCandidateAndCanBeReleased()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "snapshot.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.Observe(Observation(path, start));
        StackFileReadyCandidate queued = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(2))));
        Assert.True(tracker.IsCandidateCurrent(queued));

        tracker.ReservePath(path);

        Assert.False(tracker.ShouldConsider(path));
        Assert.True(tracker.IsPathReserved(path));
        Assert.False(tracker.IsCandidateCurrent(queued));

        tracker.ReleaseReservedPath(path);

        Assert.True(tracker.ShouldConsider(path));
        Assert.NotNull(tracker.Observe(Observation(path, start.AddSeconds(3))));
    }

    [Fact]
    public void CommittedReservedOutputStaysTerminal()
    {
        StackFileCandidateTracker tracker = CreateTracker();
        string path = Path.Combine(_directory, "saved-snapshot.fits");
        DateTimeOffset start = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        tracker.Observe(Observation(path, start));
        StackFileReadyCandidate queued = Assert.IsType<StackFileReadyCandidate>(
            tracker.Observe(Observation(path, start.AddSeconds(2))));

        tracker.ReservePath(path);
        tracker.CommitReservedPath(path);

        Assert.False(tracker.IsPathReserved(path));
        Assert.False(tracker.IsCandidateCurrent(queued));
        Assert.Null(tracker.Observe(Observation(path, start.AddMinutes(1), length: 200)));
    }

    [Fact]
    public void DriveRootContainsItsChildren()
    {
        string root = Path.GetPathRoot(_directory)!;
        string child = Path.Combine(root, "seiza", "light.fits");

        Assert.True(LiveStackPath.IsWithinDirectory(child, root));
    }

    [Fact]
    public async Task InitialAndPeriodicScansFindAFileWithoutWatcherEvents()
    {
        string path = Path.Combine(_directory, "already-there.fits");
        await File.WriteAllBytesAsync(path, new byte[32]);
        var monitor = new StackFolderMonitor(Options() with
        {
            ObservationInterval = TimeSpan.FromMilliseconds(50),
            MinimumStableDuration = TimeSpan.FromMilliseconds(100),
            ReconciliationInterval = TimeSpan.FromMilliseconds(100),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        StackFileReadyCandidate? found = null;
        await foreach (StackFileReadyCandidate candidate in monitor.WatchAsync(timeout.Token))
        {
            found = candidate;
            break;
        }

        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(path), found.Path, StringComparer.OrdinalIgnoreCase);
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

    private StackFileCandidateTracker CreateTracker() => new(Options());

    private StackFolderMonitorOptions Options() => new()
    {
        FolderPath = _directory,
        ObservationInterval = TimeSpan.FromSeconds(1),
        MinimumStableDuration = TimeSpan.FromSeconds(2),
        ReconciliationInterval = TimeSpan.FromSeconds(15),
        InitialRetryDelay = TimeSpan.FromSeconds(1),
        MaximumRetryDelay = TimeSpan.FromSeconds(8),
    };

    private static StackFileObservation Observation(
        string path,
        DateTimeOffset observedAt,
        long length = 100,
        string? identity = null) => new(
            path,
            length,
            new DateTimeOffset(2026, 8, 19, 11, 59, 0, TimeSpan.Zero),
            observedAt,
            identity);
}
