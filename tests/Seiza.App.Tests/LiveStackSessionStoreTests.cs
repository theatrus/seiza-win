using System.Text;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"seiza-live-session-{Guid.NewGuid():N}");

    public LiveStackSessionStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PublishPreservesCaptureOrderAndExposureTelemetry()
    {
        string firstPath = Path.Combine(_directory, "light-002.fits");
        string secondPath = Path.Combine(_directory, "light-001.fits");
        LiveStackPersistedFrame[] frames =
        [
            Frame(firstPath, 120),
            Frame(secondPath, 120),
        ];
        LiveStackPersistedSnrSample[] samples =
        [
            new()
            {
                AcceptedFrames = 2,
                CumulativeExposureSeconds = 240,
                Noise = 0.1,
                Signal = 0.5,
                ChannelNoise = [0.1],
            },
        ];
        string exportedPath = Path.Combine(_directory, "snapshot.fits");
        LiveStackPersistedState state = State(frames, samples) with
        {
            ExportedPaths = [exportedPath],
        };
        using var store = new LiveStackSessionStore(_directory);

        await store.PublishAsync(
            state,
            new CheckpointWriter(NativeState(firstPath, secondPath)));
        frames[0] = Frame("mutated.fits", null);
        samples[0] = samples[0] with { CumulativeExposureSeconds = null };

        LiveStackStoredGeneration candidate = Assert.Single(
            await store.GetRestoreCandidatesAsync());
        Assert.Equal(
            [firstPath, secondPath],
            candidate.State.Frames.Select(frame => frame.Path));
        Assert.Equal(120, candidate.State.Frames[0].ExposureSeconds);
        Assert.Equal(240, candidate.State.SnrSamples[0].CumulativeExposureSeconds);
        Assert.Equal([exportedPath], candidate.State.ExportedPaths);
    }

    [Fact]
    public async Task RestoreReturnsCurrentThenPreviousAndValidatesAnAlreadyOpenSession()
    {
        string firstPath = Path.Combine(_directory, "light-001.fits");
        string secondPath = Path.Combine(_directory, "light-002.fits");
        LiveStackNativeState firstNative = NativeState(firstPath);
        LiveStackNativeState secondNative = NativeState(firstPath, secondPath);
        using var store = new LiveStackSessionStore(_directory);

        await store.PublishAsync(State([Frame(firstPath, 60)]), new CheckpointWriter(firstNative));
        await store.PublishAsync(
            State([Frame(firstPath, 60), Frame(secondPath, 60)]),
            new CheckpointWriter(secondNative));

        IReadOnlyList<LiveStackStoredGeneration> candidates =
            await store.GetRestoreCandidatesAsync();
        Assert.Equal([2L, 1L], candidates.Select(candidate => candidate.Generation));
        Assert.False(candidates[0].UsedPreviousGeneration);
        Assert.True(candidates[1].UsedPreviousGeneration);
        Assert.False(store.TryAcceptRestoredGeneration(candidates[0], firstNative));
        Assert.True(store.TryAcceptRestoredGeneration(candidates[0], secondNative));
    }

    [Fact]
    public async Task InvalidCurrentPairFallsBackAndBecomesTheNextPredecessor()
    {
        string firstPath = Path.Combine(_directory, "light-001.fits");
        string secondPath = Path.Combine(_directory, "light-002.fits");
        string thirdPath = Path.Combine(_directory, "light-003.fits");
        LiveStackNativeState firstNative = NativeState(firstPath);
        using var store = new LiveStackSessionStore(_directory);

        await store.PublishAsync(State([Frame(firstPath, 30)]), new CheckpointWriter(firstNative));
        LiveStackStoredGeneration second = await store.PublishAsync(
            State([Frame(firstPath, 30), Frame(secondPath, 30)]),
            new CheckpointWriter(NativeState(firstPath, secondPath)));
        await File.AppendAllTextAsync(second.ContextPath, "corrupt");

        LiveStackStoredGeneration fallback = Assert.Single(
            await store.GetRestoreCandidatesAsync());
        Assert.Equal(1, fallback.Generation);
        Assert.True(fallback.UsedPreviousGeneration);
        Assert.True(store.TryAcceptRestoredGeneration(fallback, firstNative));

        await store.PublishAsync(
            State([Frame(firstPath, 30), Frame(thirdPath, 30)]),
            new CheckpointWriter(NativeState(firstPath, thirdPath)));
        IReadOnlyList<LiveStackStoredGeneration> candidates =
            await store.GetRestoreCandidatesAsync();

        Assert.Equal([3L, 1L], candidates.Select(candidate => candidate.Generation));
        Assert.False(File.Exists(second.ContextPath));
        Assert.False(File.Exists(second.ManifestPath));
    }

    [Fact]
    public async Task CorruptPointerFallsBackToCompletePairsInDescendingOrder()
    {
        string firstPath = Path.Combine(_directory, "light-001.fits");
        string secondPath = Path.Combine(_directory, "light-002.fits");
        using var store = new LiveStackSessionStore(_directory);

        await store.PublishAsync(
            State([Frame(firstPath, null)]),
            new CheckpointWriter(NativeState(firstPath)));
        await store.PublishAsync(
            State([Frame(firstPath, null), Frame(secondPath, null)]),
            new CheckpointWriter(NativeState(firstPath, secondPath)));
        await File.WriteAllTextAsync(Path.Combine(_directory, "current.json"), "not-json");

        IReadOnlyList<LiveStackStoredGeneration> candidates =
            await store.GetRestoreCandidatesAsync();

        Assert.Equal([2L, 1L], candidates.Select(candidate => candidate.Generation));
        Assert.False(candidates[0].UsedPreviousGeneration);
        Assert.True(candidates[1].UsedPreviousGeneration);
    }

    [Fact]
    public async Task CompletedSessionIsRetiredButANewerSessionCanPublishNormally()
    {
        string firstPath = Path.Combine(_directory, "light-001.fits");
        string secondPath = Path.Combine(_directory, "light-002.fits");
        using var store = new LiveStackSessionStore(_directory);
        await store.PublishAsync(
            State([Frame(firstPath, 60)]),
            new CheckpointWriter(NativeState(firstPath)));
        await store.PublishAsync(
            State([Frame(firstPath, 60), Frame(secondPath, 60)]),
            new CheckpointWriter(NativeState(firstPath, secondPath)));

        await store.RetireAsync("session-1");

        Assert.Empty(await store.GetRestoreCandidatesAsync());
        LiveStackPersistedState nextSession = State([Frame(firstPath, 60)]) with
        {
            SessionId = "session-2",
        };
        await store.PublishAsync(
            nextSession,
            new CheckpointWriter(NativeState(firstPath)));
        LiveStackStoredGeneration candidate = Assert.Single(
            await store.GetRestoreCandidatesAsync());
        Assert.Equal("session-2", candidate.State.SessionId);
        Assert.Equal(3, candidate.Generation);
    }

    [Fact]
    public async Task WithoutRetirementTheFinalPreExportCheckpointRemainsRecoverable()
    {
        string path = Path.Combine(_directory, "light-001.fits");
        using var store = new LiveStackSessionStore(_directory);
        await store.PublishAsync(
            State([Frame(path, 60)]),
            new CheckpointWriter(NativeState(path)));

        LiveStackStoredGeneration candidate = Assert.Single(
            await store.GetRestoreCandidatesAsync());

        Assert.Equal("session-1", candidate.State.SessionId);
    }

    [Fact]
    public async Task ExtendedNativeLedgerPathValidatesAgainstPersistedOrdinaryPath()
    {
        string ordinary = Path.GetFullPath(Path.Combine(_directory, "light-001.fits"));
        string extended = @"\\?\" + ordinary;
        using var store = new LiveStackSessionStore(_directory);
        await store.PublishAsync(
            State([Frame(ordinary, 60)]),
            new CheckpointWriter(NativeState(ordinary)));
        LiveStackStoredGeneration candidate = Assert.Single(
            await store.GetRestoreCandidatesAsync());

        Assert.True(store.TryAcceptRestoredGeneration(candidate, NativeState(extended)));
    }

    [Fact]
    public void ASessionDirectoryHasOnlyOneActiveOwner()
    {
        using var first = new LiveStackSessionStore(_directory);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new LiveStackSessionStore(_directory));

        Assert.Contains("active live-stack session", exception.Message);
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

    private LiveStackPersistedState State(
        LiveStackPersistedFrame[] frames,
        LiveStackPersistedSnrSample[]? samples = null) => new()
        {
            SessionId = "session-1",
            GroupId = "filter-L",
            GroupTitle = "Luminance",
            FilterName = "L",
            WatchFolder = _directory,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 19, 12, 1, 0, TimeSpan.Zero),
            Frames = frames,
            SnrSamples = samples ?? [],
        };

    private static LiveStackPersistedFrame Frame(string path, double? exposureSeconds) => new()
    {
        Path = path,
        Disposition = LiveStackPersistedFrameDisposition.Accepted,
        ExposureSeconds = exposureSeconds,
        Length = 1024,
        LastWriteTimeUtc = new DateTimeOffset(2026, 8, 19, 11, 59, 0, TimeSpan.Zero),
        ProcessedAtUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
    };

    private static LiveStackNativeState NativeState(params string[] paths) => new()
    {
        CoreVersion = "0.17.0",
        Width = 1024,
        Height = 768,
        Channels = 1,
        AcceptedFrames = paths.Length,
        InputMode = "calibrate-and-prepare",
        ConfigurationFingerprint = "sha256:test",
        InputPaths = paths,
    };

    private sealed class CheckpointWriter(LiveStackNativeState state) : ILiveStackCheckpointWriter
    {
        public async ValueTask<LiveStackNativeState> SaveContextAsync(
            string destinationPath,
            CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(
                destinationPath,
                Encoding.UTF8.GetBytes($"checkpoint-{state.AcceptedFrames}"),
                cancellationToken);
            return state;
        }
    }
}
