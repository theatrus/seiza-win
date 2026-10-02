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

    [Fact]
    public async Task AnEarlyTimerWakeupCannotShortenTheCandidateStabilityWindow()
    {
        string light = CreateFile("early-timer.fits");
        var premature = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var earlyClock = new EarlyTimerTimeProvider();
        Assert.Empty(premature.ObserveExisting(earlyClock.GetUtcNow()));
        await Task.Delay(TimeSpan.FromSeconds(2), earlyClock, CancellationToken.None);
        // Reproduces the old single-delay scan: its unchanged light is dropped
        // because a timer wakeup at 1.999 seconds is not two stable seconds.
        Assert.Equal(TimeSpan.FromMilliseconds(1999), earlyClock.Elapsed);
        Assert.Empty(premature.ObserveExisting(earlyClock.GetUtcNow()));

        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var clock = new EarlyTimerTimeProvider();
        DateTimeOffset start = clock.GetUtcNow();

        StackFileReadyCandidate ready = Assert.Single(await observations.ObserveStableExistingAsync(
            clock, CancellationToken.None));

        Assert.Equal(light, ready.Path);
        Assert.Equal(2, clock.TimerCalls);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetUtcNow() - start);
        Assert.True(LiveStackReferenceCandidates.IsUnchanged(ready));
    }

    [Fact]
    public async Task ABackwardClockAdjustmentBoundsStartupAndLeavesTheCaptureUnseeded()
    {
        string light = CreateFile("backward-clock.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var clock = new EarlyTimerTimeProvider(moveClockBackward: true);
        DateTimeOffset start = clock.GetUtcNow();

        Assert.Empty(await observations.ObserveStableExistingAsync(clock, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(3), clock.Elapsed);
        Assert.Equal(2, clock.TimerCalls);
        Assert.True(observations.HasPendingCandidates);
        StackFileReadyCandidate ready = Assert.Single(observations.ObserveExisting(start.AddSeconds(2)));
        Assert.Equal(light, ready.Path);
    }

    [Fact]
    public async Task ASlowInitialScanStillWaitsTheFullStableDurationAfterEnumeration()
    {
        string light = CreateFile("slow-scan.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var clock = new EarlyTimerTimeProvider(initialScanDuration: TimeSpan.FromSeconds(3));

        StackFileReadyCandidate ready = Assert.Single(await observations.ObserveStableExistingAsync(
            clock, CancellationToken.None));

        Assert.Equal(light, ready.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        Assert.Equal(2, clock.TimerCalls);
    }

    [Fact]
    public async Task StartupObservationHonorsCancellationBeforeScanning()
    {
        _ = CreateFile("cancelled.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observations.ObserveStableExistingAsync(
            TimeProvider.System, cancellation.Token));

        Assert.False(observations.HasPendingCandidates);
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
        Assert.True(scoreCalls == 1,
            $"Expected one scorer call, got {scoreCalls}. Startup attention: " +
            string.Join("; ", coordinator.CurrentSnapshot.Attention.Select(item => item.Message)));
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

    private sealed class EarlyTimerTimeProvider(
        bool moveClockBackward = false,
        TimeSpan initialScanDuration = default) : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        private TimeSpan _elapsed;
        private int _utcReads;
        public int TimerCalls { get; private set; }
        public TimeSpan Elapsed
        {
            get
            {
                lock (_sync)
                {
                    return _elapsed;
                }
            }
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                if (++_utcReads == 2)
                {
                    _now += initialScanDuration;
                    _elapsed += initialScanDuration;
                }
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimeSpan elapsed;
            bool adjustClock;
            lock (_sync)
            {
                TimerCalls++;
                elapsed = dueTime - (TimerCalls == 1 ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero);
                adjustClock = moveClockBackward && TimerCalls == 1;
            }
            var timer = new OneShotTimer(() =>
            {
                lock (_sync)
                {
                    _now += elapsed;
                    _elapsed += elapsed;
                    if (adjustClock)
                    {
                        _now -= TimeSpan.FromHours(1);
                    }
                }
                callback(state);
            });
            ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            return timer;
        }

        private sealed class OneShotTimer(Action callback) : ITimer
        {
            private int _disposed;
            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback();
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
