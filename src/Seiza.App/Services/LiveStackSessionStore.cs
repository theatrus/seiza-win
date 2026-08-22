using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>
/// Save-only native boundary used by the session store. Opening a checkpoint is
/// left to the coordinator so a large context is never opened twice merely to
/// validate a restore.
/// </summary>
internal interface ILiveStackCheckpointWriter
{
    ValueTask<LiveStackNativeState> SaveContextAsync(
        string destinationPath,
        CancellationToken cancellationToken);
}

internal sealed record LiveStackStoredGeneration(
    long Generation,
    string ContextPath,
    string ManifestPath,
    LiveStackPersistedState State,
    LiveStackNativeState ExpectedNativeState,
    bool UsedPreviousGeneration);

/// <summary>
/// Publishes an opaque native context and its app manifest as one generation.
/// A pointer names the current and previous complete pairs; the previous pair
/// remains available when current validation fails.
/// </summary>
internal sealed class LiveStackSessionStore : IDisposable
{
    private const int PointerSchemaVersion = 1;
    private const int ManifestSchemaVersion = 1;
    private const string PointerFileName = "current.json";
    private const string RetirementFileName = "completed.json";
    private const string LeaseFileName = "session.lock";
    private const string GenerationPrefix = "generation-";
    private const string ContextExtension = ".seiza-stack";
    private const string ManifestExtension = ".json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly FileStream _lease;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private long? _lastAuthoritativeGeneration;
    private bool _disposed;

    public LiveStackSessionStore(string groupDirectory)
    {
        if (string.IsNullOrWhiteSpace(groupDirectory))
        {
            throw new ArgumentException("A session group directory is required.", nameof(groupDirectory));
        }
        GroupDirectory = Path.GetFullPath(groupDirectory);
        Directory.CreateDirectory(GroupDirectory);
        try
        {
            _lease = new FileStream(
                Path.Combine(GroupDirectory, LeaseFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "This capture folder already has an active live-stack session, " +
                "or its checkpoint directory is unavailable.",
                exception);
        }
    }

    public string GroupDirectory { get; }

    public static string SafeGroupDirectoryName(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        string readable = string.Concat(groupId.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')).Trim('-');
        if (readable.Length == 0)
        {
            readable = "filter";
        }
        if (readable.Length > 48)
        {
            readable = readable[..48];
        }
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(groupId));
        return $"{readable}-{Convert.ToHexString(digest.AsSpan(0, 5)).ToLowerInvariant()}";
    }

    public async ValueTask<LiveStackStoredGeneration> PublishAsync(
        LiveStackPersistedState state,
        ILiveStackCheckpointWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(writer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateState(state);
        state = Snapshot(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(GroupDirectory);
            GenerationPointer? oldPointer = await ReadPointerAsync(cancellationToken);
            long generation = checked(FindMaximumGeneration() + 1);
            string contextPath = ContextPath(generation);
            string manifestPath = ManifestPath(generation);

            LiveStackNativeState nativeState = await writer.SaveContextAsync(
                contextPath,
                cancellationToken);
            ValidateNativeState(nativeState);
            nativeState = Snapshot(nativeState);
            cancellationToken.ThrowIfCancellationRequested();

            var context = new FileInfo(contextPath);
            context.Refresh();
            if (!context.Exists || context.Length <= 0)
            {
                throw new InvalidDataException(
                    "The native stack checkpoint did not publish a non-empty context file.");
            }

            var manifest = new GenerationManifest
            {
                SchemaVersion = ManifestSchemaVersion,
                Generation = generation,
                ContextFileName = Path.GetFileName(contextPath),
                ContextLength = context.Length,
                PublishedAtUtc = DateTimeOffset.UtcNow,
                State = state,
                NativeState = nativeState,
            };
            await WriteJsonAtomicallyAsync(manifestPath, manifest, cancellationToken);

            long? previous;
            lock (_stateSync)
            {
                previous = _lastAuthoritativeGeneration ?? oldPointer?.CurrentGeneration;
            }
            var pointer = new GenerationPointer
            {
                SchemaVersion = PointerSchemaVersion,
                CurrentGeneration = generation,
                PreviousGeneration = previous,
            };
            await WriteJsonAtomicallyAsync(
                Path.Combine(GroupDirectory, PointerFileName),
                pointer,
                cancellationToken);

            CleanupOldGenerations(generation, previous);
            lock (_stateSync)
            {
                _lastAuthoritativeGeneration = generation;
            }

            return new LiveStackStoredGeneration(
                generation,
                contextPath,
                manifestPath,
                state,
                nativeState,
                false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns locally well-formed generation pairs in authoritative restore
    /// order. The caller opens each context once, reads state from that same
    /// handle, and passes both to <see cref="TryAcceptRestoredGeneration"/>.
    /// </summary>
    public async ValueTask<IReadOnlyList<LiveStackStoredGeneration>> GetRestoreCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(GroupDirectory))
            {
                return [];
            }

            GenerationPointer? pointer = await ReadPointerAsync(cancellationToken);
            RetirementMarker? retirement = await ReadRetirementAsync(cancellationToken);
            (long Generation, bool IsPrevious)[] candidates = pointer is null
                ? EnumerateManifestGenerations()
                    .OrderDescending()
                    .Select((generation, index) => (generation, index > 0))
                    .ToArray()
                : new[] { (pointer.CurrentGeneration, false) }
                    .Concat(pointer.PreviousGeneration is long previous
                        ? [(previous, true)]
                        : [])
                    .DistinctBy(candidate => candidate.Item1)
                    .ToArray();

            var restored = new List<LiveStackStoredGeneration>(candidates.Length);
            foreach ((long generation, bool isPrevious) in candidates)
            {
                LiveStackStoredGeneration? candidate = await TryReadCandidateAsync(
                    generation,
                    isPrevious,
                    cancellationToken);
                if (candidate is not null && !IsRetired(candidate, retirement))
                {
                    restored.Add(candidate);
                }
            }
            return restored;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically marks every published generation of one completed session as
    /// non-resumable. Generation files remain available for diagnostics; a
    /// failed export never calls this method and therefore retains recovery.
    /// </summary>
    public async ValueTask RetireAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            GenerationPointer? pointer = await ReadPointerAsync(cancellationToken);
            long generation;
            lock (_stateSync)
            {
                generation = _lastAuthoritativeGeneration ??
                    pointer?.CurrentGeneration ??
                    FindMaximumGeneration();
            }
            if (generation <= 0)
            {
                throw new InvalidOperationException(
                    "The live-stack session has no checkpoint generation to retire.");
            }

            await WriteJsonAtomicallyAsync(
                Path.Combine(GroupDirectory, RetirementFileName),
                new RetirementMarker
                {
                    SchemaVersion = PointerSchemaVersion,
                    SessionId = sessionId,
                    RetiredThroughGeneration = generation,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates metadata against state read from the already-open native
    /// session. The caller keeps that handle after success and disposes it on
    /// failure before trying the next candidate.
    /// </summary>
    public bool TryAcceptRestoredGeneration(
        LiveStackStoredGeneration candidate,
        LiveStackNativeState actualNativeState)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(actualNativeState);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCandidateFromThisStore(candidate) ||
            !candidate.ExpectedNativeState.DescribesSameCheckpoint(actualNativeState))
        {
            return false;
        }

        lock (_stateSync)
        {
            _lastAuthoritativeGeneration = candidate.Generation;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lease.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask<LiveStackStoredGeneration?> TryReadCandidateAsync(
        long generation,
        bool usedPreviousGeneration,
        CancellationToken cancellationToken)
    {
        if (generation <= 0)
        {
            return null;
        }

        string manifestPath = ManifestPath(generation);
        GenerationManifest? manifest = await ReadJsonAsync<GenerationManifest>(
            manifestPath,
            cancellationToken);
        if (manifest is null ||
            manifest.SchemaVersion != ManifestSchemaVersion ||
            manifest.Generation != generation ||
            !IsValidState(manifest.State) ||
            !IsValidNativeState(manifest.NativeState) ||
            !string.Equals(
                manifest.ContextFileName,
                Path.GetFileName(ContextPath(generation)),
                StringComparison.Ordinal))
        {
            return null;
        }

        string contextPath = Path.Combine(GroupDirectory, manifest.ContextFileName);
        var context = new FileInfo(contextPath);
        context.Refresh();
        if (!context.Exists || context.Length != manifest.ContextLength)
        {
            return null;
        }

        return new LiveStackStoredGeneration(
            generation,
            contextPath,
            manifestPath,
            Snapshot(manifest.State),
            Snapshot(manifest.NativeState),
            usedPreviousGeneration);
    }

    private bool IsCandidateFromThisStore(LiveStackStoredGeneration candidate)
    {
        if (candidate.Generation <= 0)
        {
            return false;
        }

        return LiveStackPath.Equals(candidate.ContextPath, ContextPath(candidate.Generation)) &&
            LiveStackPath.Equals(candidate.ManifestPath, ManifestPath(candidate.Generation));
    }

    private async ValueTask<GenerationPointer?> ReadPointerAsync(
        CancellationToken cancellationToken)
    {
        GenerationPointer? pointer = await ReadJsonAsync<GenerationPointer>(
            Path.Combine(GroupDirectory, PointerFileName),
            cancellationToken);
        if (pointer is null ||
            pointer.SchemaVersion != PointerSchemaVersion ||
            pointer.CurrentGeneration <= 0 ||
            (pointer.PreviousGeneration is long previous &&
             (previous <= 0 || previous >= pointer.CurrentGeneration)))
        {
            return null;
        }
        return pointer;
    }

    private async ValueTask<RetirementMarker?> ReadRetirementAsync(
        CancellationToken cancellationToken)
    {
        RetirementMarker? retirement = await ReadJsonAsync<RetirementMarker>(
            Path.Combine(GroupDirectory, RetirementFileName),
            cancellationToken);
        return retirement is not null &&
            retirement.SchemaVersion == PointerSchemaVersion &&
            !string.IsNullOrWhiteSpace(retirement.SessionId) &&
            retirement.RetiredThroughGeneration > 0
                ? retirement
                : null;
    }

    private static bool IsRetired(
        LiveStackStoredGeneration candidate,
        RetirementMarker? retirement) =>
        retirement is not null &&
        candidate.Generation <= retirement.RetiredThroughGeneration &&
        string.Equals(
            candidate.State.SessionId,
            retirement.SessionId,
            StringComparison.Ordinal);

    private async ValueTask<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async ValueTask WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            GroupDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private long FindMaximumGeneration() =>
        EnumerateManifestGenerations()
            .Concat(EnumerateContextGenerations())
            .DefaultIfEmpty(0)
            .Max();

    private IEnumerable<long> EnumerateManifestGenerations() =>
        EnumerateGenerations(ManifestExtension);

    private IEnumerable<long> EnumerateContextGenerations() =>
        EnumerateGenerations(ContextExtension);

    private IEnumerable<long> EnumerateGenerations(string extension)
    {
        if (!Directory.Exists(GroupDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(
            GroupDirectory,
            $"{GenerationPrefix}*{extension}",
            SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            string digits = name[GenerationPrefix.Length..^extension.Length];
            if (long.TryParse(digits, out long generation) && generation > 0)
            {
                yield return generation;
            }
        }
    }

    private void CleanupOldGenerations(long current, long? previous)
    {
        var keep = new HashSet<long> { current };
        if (previous is long previousGeneration)
        {
            keep.Add(previousGeneration);
        }

        long[] generations;
        try
        {
            generations = EnumerateManifestGenerations()
                .Concat(EnumerateContextGenerations())
                .Distinct()
                .Where(generation => !keep.Contains(generation))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (long generation in generations)
        {
            DeleteBestEffort(ManifestPath(generation));
            DeleteBestEffort(ContextPath(generation));
        }
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string ContextPath(long generation) => Path.Combine(
        GroupDirectory,
        $"{GenerationPrefix}{generation:D12}{ContextExtension}");

    private string ManifestPath(long generation) => Path.Combine(
        GroupDirectory,
        $"{GenerationPrefix}{generation:D12}{ManifestExtension}");

    private static void ValidateState(LiveStackPersistedState state)
    {
        if (!IsValidState(state))
        {
            throw new ArgumentException("The live-stack manifest state is incomplete.", nameof(state));
        }
    }

    private static bool IsValidState(LiveStackPersistedState? state) =>
        state is not null &&
        state.SchemaVersion == LiveStackPersistedState.CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(state.SessionId) &&
        !string.IsNullOrWhiteSpace(state.GroupId) &&
        !string.IsNullOrWhiteSpace(state.WatchFolder) &&
        state.CalibrationHistory is not null &&
        state.ExportedPaths is not null &&
        state.ExportedPaths.All(path => !string.IsNullOrWhiteSpace(path)) &&
        state.Frames is not null &&
        state.SnrSamples is not null &&
        state.Frames.All(frame =>
            frame is not null &&
            (frame.ExposureSeconds is null ||
             (double.IsFinite(frame.ExposureSeconds.Value) && frame.ExposureSeconds.Value > 0))) &&
        state.SnrSamples.All(sample =>
            sample is not null &&
            sample.ChannelNoise is not null &&
            (sample.CumulativeExposureSeconds is null ||
             (double.IsFinite(sample.CumulativeExposureSeconds.Value) &&
              sample.CumulativeExposureSeconds.Value >= 0)));

    private static void ValidateNativeState(LiveStackNativeState state)
    {
        if (!IsValidNativeState(state))
        {
            throw new InvalidDataException("The native checkpoint state is invalid.");
        }
    }

    private static bool IsValidNativeState(LiveStackNativeState? state) =>
        state is not null &&
        state.SchemaVersion == LiveStackNativeState.CurrentSchemaVersion &&
        state.Width > 0 &&
        state.Height > 0 &&
        state.Channels is 1 or 3 &&
        state.AcceptedFrames > 0 &&
        state.RejectedFrames >= 0 &&
        state.InputPaths is not null &&
        state.InputPaths.All(path => !string.IsNullOrWhiteSpace(path));

    private static LiveStackPersistedState Snapshot(LiveStackPersistedState state) => state with
    {
        CalibrationHistory = [.. state.CalibrationHistory],
        ExportedPaths = [.. state.ExportedPaths],
        Frames = [.. state.Frames],
        SnrSamples = state.SnrSamples
            .Select(sample => sample with { ChannelNoise = [.. sample.ChannelNoise] })
            .ToArray(),
    };

    private static LiveStackNativeState Snapshot(LiveStackNativeState state) => state with
    {
        InputPaths = [.. state.InputPaths],
        ReferenceFrame = state.ReferenceFrame is null
            ? null
            : state.ReferenceFrame with
            {
                Signature = state.ReferenceFrame.Signature with { },
                CalibrationState = state.ReferenceFrame.CalibrationState with { },
            },
    };

    private sealed record GenerationPointer
    {
        public int SchemaVersion { get; init; }
        public long CurrentGeneration { get; init; }
        public long? PreviousGeneration { get; init; }
    }

    private sealed record RetirementMarker
    {
        public int SchemaVersion { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public long RetiredThroughGeneration { get; init; }
        public DateTimeOffset CompletedAtUtc { get; init; }
    }

    private sealed record GenerationManifest
    {
        public int SchemaVersion { get; init; }
        public long Generation { get; init; }
        public string ContextFileName { get; init; } = string.Empty;
        public long ContextLength { get; init; }
        public DateTimeOffset PublishedAtUtc { get; init; }
        public LiveStackPersistedState State { get; init; } = new();
        public LiveStackNativeState NativeState { get; init; } = new();
    }
}
