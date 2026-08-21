using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed record StackFolderMonitorOptions
{
    public string FolderPath { get; init; } = string.Empty;
    public bool IncludeSubdirectories { get; init; }
    public TimeSpan ObservationInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MinimumStableDuration { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int WatcherBufferBytes { get; init; } = 16 * 1024;
    public string[] SupportedExtensions { get; init; } = [".fits", ".fit", ".fts", ".xisf"];
    public string[] ExcludedPaths { get; init; } = [];
    public string[] ExcludedDirectories { get; init; } = [];
    public string[] ExcludedFileNamePrefixes { get; init; } = [".seiza-stack-"];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            throw new ArgumentException("A watched folder is required.", nameof(FolderPath));
        }
        if (ObservationInterval <= TimeSpan.Zero ||
            MinimumStableDuration < TimeSpan.Zero ||
            ReconciliationInterval < ObservationInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ObservationInterval),
                "Observation and reconciliation intervals must be positive and ordered.");
        }
        if (InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialRetryDelay),
                "Retry delays must be positive and ordered.");
        }
        if (WatcherBufferBytes is < 4096 or > 65536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WatcherBufferBytes),
                "The watcher buffer must be between 4 KiB and 64 KiB.");
        }
    }
}

internal sealed record StackFileObservation(
    string Path,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    DateTimeOffset ObservedAtUtc,
    string? FileIdentity = null);

internal sealed record StackFileReadyCandidate(
    string Path,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    int Attempt,
    long Revision,
    string? FileIdentity = null);

internal enum StackFileProcessingDisposition
{
    Accepted,
    Rejected,
    RetryableFailure,
    Unreadable,
    Ignored,
}

internal sealed record StackFileProcessingResult(
    StackFileProcessingDisposition Disposition,
    string? Reason = null);

internal enum StackFolderMonitorState
{
    Starting,
    Scanning,
    Watching,
    ReconciliationOnly,
    FolderUnavailable,
    WatcherError,
    Stopped,
}

internal sealed class StackFolderMonitorStateChangedEventArgs(
    StackFolderMonitorState state,
    string? message = null) : EventArgs
{
    public StackFolderMonitorState State { get; } = state;
    public string? Message { get; } = message;
}

/// <summary>
/// Turns duplicate and lossy FileSystemWatcher notifications into stable file
/// candidates. Periodic directory enumeration is the source of truth.
/// </summary>
internal sealed class StackFolderMonitor
{
    private const int HintCapacity = 1024;
    private readonly StackFolderMonitorOptions _options;
    private readonly StackFileCandidateTracker _tracker;
    private readonly TimeProvider _timeProvider;
    private int _watching;
    private string? _watcherErrorMessage;
    private StackFolderMonitorState? _lastState;

    public StackFolderMonitor(
        StackFolderMonitorOptions options,
        TimeProvider? timeProvider = null)
    {
        options.Validate();
        _options = options with
        {
            FolderPath = LiveStackPath.NormalizeForComparison(options.FolderPath),
        };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tracker = new StackFileCandidateTracker(_options);
    }

    public event EventHandler<StackFolderMonitorStateChangedEventArgs>? StateChanged;

    public void SeedProcessedPaths(IEnumerable<string> paths) => _tracker.SeedProcessedPaths(paths);

    public void SeedProcessedFrames(IEnumerable<LiveStackPersistedFrame> frames) =>
        _tracker.SeedProcessedFrames(frames);

    public void ReservePath(string path) => _tracker.ReservePath(path);

    public void ReleaseReservedPath(string path) => _tracker.ReleaseReservedPath(path);

    public void CommitReservedPath(string path) => _tracker.CommitReservedPath(path);

    public bool IsCandidateCurrent(StackFileReadyCandidate candidate) =>
        _tracker.IsCandidateCurrent(candidate);

    public void ReportProcessingResult(
        StackFileReadyCandidate candidate,
        StackFileProcessingResult result) =>
        _tracker.Complete(candidate, result, _timeProvider.GetUtcNow());

    public void RetryNow(string path) => _tracker.RetryNow(path);

    public async IAsyncEnumerable<StackFileReadyCandidate> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _watching, 1) != 0)
        {
            throw new InvalidOperationException("This folder monitor is already running.");
        }

        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<StackFolderHint>(new BoundedChannelOptions(HintCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        FileSystemWatcher? watcher = null;
        Task ticker = RunTickerAsync(channel.Writer, stopped.Token);
        DateTimeOffset nextReconciliation = DateTimeOffset.MinValue;
        SetState(StackFolderMonitorState.Starting);
        channel.Writer.TryWrite(StackFolderHint.FullScan());

        try
        {
            await foreach (StackFolderHint hint in channel.Reader.ReadAllAsync(stopped.Token))
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                string? watcherError = Interlocked.Exchange(ref _watcherErrorMessage, null);
                if (watcherError is not null)
                {
                    watcher?.Dispose();
                    watcher = null;
                    SetState(StackFolderMonitorState.WatcherError, watcherError);
                    nextReconciliation = DateTimeOffset.MinValue;
                }

                watcher ??= TryCreateWatcher(channel.Writer);
                bool fullScan = hint.Kind == StackFolderHintKind.FullScan ||
                    now >= nextReconciliation;
                IReadOnlyList<string> paths;
                if (fullScan)
                {
                    paths = EnumerateCandidates(watcher is not null);
                    nextReconciliation = now + _options.ReconciliationInterval;
                }
                else if (hint.Kind == StackFolderHintKind.Path && hint.Path is not null)
                {
                    paths = [hint.Path];
                }
                else
                {
                    paths = _tracker.PendingPaths;
                }

                foreach (string path in paths)
                {
                    StackFileReadyCandidate? candidate = ObservePath(path, now);
                    if (candidate is not null)
                    {
                        yield return candidate;
                    }
                }
            }
        }
        finally
        {
            stopped.Cancel();
            watcher?.Dispose();
            channel.Writer.TryComplete();
            try
            {
                await ticker;
            }
            catch (OperationCanceledException)
            {
            }
            SetState(StackFolderMonitorState.Stopped);
            Volatile.Write(ref _watching, 0);
        }
    }

    private FileSystemWatcher? TryCreateWatcher(ChannelWriter<StackFolderHint> writer)
    {
        if (!Directory.Exists(_options.FolderPath))
        {
            SetState(StackFolderMonitorState.FolderUnavailable, "The watched folder is unavailable.");
            return null;
        }

        try
        {
            var watcher = new FileSystemWatcher(_options.FolderPath)
            {
                IncludeSubdirectories = _options.IncludeSubdirectories,
                InternalBufferSize = _options.WatcherBufferBytes,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, args) => writer.TryWrite(StackFolderHint.ForPath(args.FullPath));
            watcher.Changed += (_, args) => writer.TryWrite(StackFolderHint.ForPath(args.FullPath));
            watcher.Renamed += (_, args) => writer.TryWrite(StackFolderHint.ForPath(args.FullPath));
            watcher.Error += (_, args) =>
            {
                Interlocked.Exchange(
                    ref _watcherErrorMessage,
                    args.GetException()?.Message ?? "The folder watcher lost changes.");
                writer.TryWrite(StackFolderHint.Tick());
            };
            watcher.EnableRaisingEvents = true;
            SetState(StackFolderMonitorState.Watching);
            return watcher;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            SetState(StackFolderMonitorState.FolderUnavailable, exception.Message);
            return null;
        }
    }

    private string[] EnumerateCandidates(bool hasWatcher)
    {
        SetState(StackFolderMonitorState.Scanning);
        try
        {
            if (!Directory.Exists(_options.FolderPath))
            {
                SetState(StackFolderMonitorState.FolderUnavailable, "The watched folder is unavailable.");
                return [];
            }
            string[] paths = Directory
                .EnumerateFiles(
                    _options.FolderPath,
                    "*",
                    _options.IncludeSubdirectories
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly)
                .Where(_tracker.ShouldConsider)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SetState(hasWatcher
                ? StackFolderMonitorState.Watching
                : StackFolderMonitorState.ReconciliationOnly);
            return paths;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            SetState(StackFolderMonitorState.FolderUnavailable, exception.Message);
            return [];
        }
    }

    private StackFileReadyCandidate? ObservePath(string path, DateTimeOffset now)
    {
        if (!_tracker.ShouldConsider(path))
        {
            return null;
        }
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists)
            {
                _tracker.RemovePending(path);
                return null;
            }
            return _tracker.Observe(new StackFileObservation(
                file.FullName,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                now,
                WindowsFileIdentity.TryGet(file.FullName)));
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return null;
        }
    }

    private async Task RunTickerAsync(
        ChannelWriter<StackFolderHint> writer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.ObservationInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            writer.TryWrite(StackFolderHint.Tick());
        }
    }

    private void SetState(StackFolderMonitorState state, string? message = null)
    {
        if (_lastState == state && message is null)
        {
            return;
        }
        _lastState = state;
        StateChanged?.Invoke(this, new StackFolderMonitorStateChangedEventArgs(state, message));
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private enum StackFolderHintKind
    {
        Tick,
        FullScan,
        Path,
    }

    private readonly record struct StackFolderHint(
        StackFolderHintKind Kind,
        string? Path,
        string? Message)
    {
        public static StackFolderHint Tick() => new(StackFolderHintKind.Tick, null, null);
        public static StackFolderHint FullScan() => new(StackFolderHintKind.FullScan, null, null);
        public static StackFolderHint ForPath(string path) =>
            new(StackFolderHintKind.Path, path, null);
    }
}

internal sealed class StackFileCandidateTracker
{
    private readonly Dictionary<string, CandidateState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _excludedPaths;
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _terminalFileIdentities =
        new(StringComparer.Ordinal);
    private readonly string[] _excludedDirectories;
    private readonly string[] _excludedPrefixes;
    private readonly string _root;
    private readonly bool _includesSubdirectories;
    private readonly TimeSpan _minimumStableDuration;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maximumRetryDelay;

    public StackFileCandidateTracker(StackFolderMonitorOptions options)
    {
        options.Validate();
        _root = LiveStackPath.NormalizeForComparison(options.FolderPath);
        _includesSubdirectories = options.IncludeSubdirectories;
        _minimumStableDuration = options.MinimumStableDuration;
        _initialRetryDelay = options.InitialRetryDelay;
        _maximumRetryDelay = options.MaximumRetryDelay;
        _extensions = new HashSet<string>(options.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        _excludedPaths = new HashSet<string>(
            options.ExcludedPaths.Select(LiveStackPath.NormalizeForComparison),
            StringComparer.OrdinalIgnoreCase);
        _excludedDirectories = options.ExcludedDirectories
            .Select(path => Path.TrimEndingDirectorySeparator(
                LiveStackPath.NormalizeForComparison(path)))
            .ToArray();
        _excludedPrefixes = options.ExcludedFileNamePrefixes;
    }

    public IReadOnlyList<string> PendingPaths
    {
        get
        {
            lock (_sync)
            {
                return _states
                    .Where(pair => !pair.Value.IsTerminal)
                    .Select(pair => pair.Key)
                    .ToArray();
            }
        }
    }

    public void SeedProcessedPaths(IEnumerable<string> paths)
    {
        lock (_sync)
        {
            foreach (string path in paths)
            {
                string fullPath = LiveStackPath.NormalizeForComparison(path);
                string? fileIdentity = WindowsFileIdentity.TryGet(fullPath);
                _states[fullPath] = CandidateState.Terminal(
                    StackFileProcessingDisposition.Accepted,
                    fileIdentity: fileIdentity);
                AddTerminalIdentity(fileIdentity);
            }
        }
    }

    public void SeedProcessedFrames(IEnumerable<LiveStackPersistedFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        lock (_sync)
        {
            foreach (LiveStackPersistedFrame frame in frames)
            {
                string fullPath = LiveStackPath.NormalizeForComparison(frame.Path);
                StackFileProcessingDisposition disposition = frame.Disposition switch
                {
                    LiveStackPersistedFrameDisposition.Accepted =>
                        StackFileProcessingDisposition.Accepted,
                    LiveStackPersistedFrameDisposition.Rejected =>
                        StackFileProcessingDisposition.Rejected,
                    LiveStackPersistedFrameDisposition.Unreadable =>
                        StackFileProcessingDisposition.Unreadable,
                    _ => StackFileProcessingDisposition.Ignored,
                };
                // Preserve the identity observed when an unreadable result was
                // recorded. For legacy unreadable entries with no identity,
                // leaving this null deliberately lets the first available
                // Windows identity count as a replacement and reopen the file
                // once after restart. Durable results may safely learn their
                // current identity to deduplicate renamed and hard-linked aliases.
                string? fileIdentity = frame.FileIdentity ??
                    (disposition == StackFileProcessingDisposition.Unreadable
                        ? null
                        : WindowsFileIdentity.TryGet(fullPath));
                _states[fullPath] = CandidateState.Terminal(
                    disposition,
                    frame.Length,
                    frame.LastWriteTimeUtc,
                    fileIdentity,
                    disposition == StackFileProcessingDisposition.Unreadable
                        ? frame.ProcessedAtUtc + _maximumRetryDelay
                        : DateTimeOffset.MaxValue);
                if (disposition != StackFileProcessingDisposition.Unreadable)
                {
                    AddTerminalIdentity(fileIdentity);
                }
            }
        }
    }

    public void ReservePath(string path)
    {
        string fullPath = LiveStackPath.NormalizeForComparison(path);
        lock (_sync)
        {
            _reservedPaths.Add(fullPath);
            if (_states.TryGetValue(fullPath, out CandidateState? state) && !state.IsTerminal)
            {
                // A watcher hint may already have yielded this path. Make that
                // candidate stale while the caller publishes the reserved file.
                state.InvalidatePendingCandidate();
            }
        }
    }

    public void ReleaseReservedPath(string path)
    {
        string fullPath = LiveStackPath.NormalizeForComparison(path);
        lock (_sync)
        {
            _reservedPaths.Remove(fullPath);
        }
    }

    public void CommitReservedPath(string path)
    {
        string fullPath = LiveStackPath.NormalizeForComparison(path);
        lock (_sync)
        {
            _reservedPaths.Remove(fullPath);
            string? fileIdentity = WindowsFileIdentity.TryGet(fullPath);
            _states[fullPath] = CandidateState.Terminal(
                StackFileProcessingDisposition.Accepted,
                fileIdentity: fileIdentity);
            AddTerminalIdentity(fileIdentity);
        }
    }

    public bool IsPathReserved(string path)
    {
        string fullPath = LiveStackPath.NormalizeForComparison(path);
        lock (_sync)
        {
            return _reservedPaths.Contains(fullPath);
        }
    }

    public bool IsCandidateCurrent(StackFileReadyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string fullPath = LiveStackPath.NormalizeForComparison(candidate.Path);
        lock (_sync)
        {
            return !_reservedPaths.Contains(fullPath) &&
                _states.TryGetValue(fullPath, out CandidateState? state) &&
                !state.IsTerminal &&
                state.AwaitingDisposition &&
                state.Revision == candidate.Revision;
        }
    }

    public bool ShouldConsider(string path)
    {
        string fullPath;
        try
        {
            fullPath = LiveStackPath.NormalizeForComparison(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null ||
            (!_includesSubdirectories &&
             !string.Equals(
                 Path.TrimEndingDirectorySeparator(parent),
                 Path.TrimEndingDirectorySeparator(_root),
                 StringComparison.OrdinalIgnoreCase)) ||
            (_includesSubdirectories && !LiveStackPath.IsWithinDirectory(fullPath, _root)) ||
            !_extensions.Contains(Path.GetExtension(fullPath)) ||
            _excludedPaths.Contains(fullPath) ||
            _excludedDirectories.Any(directory =>
                LiveStackPath.IsWithinDirectory(fullPath, directory)) ||
            _excludedPrefixes.Any(prefix =>
                Path.GetFileName(fullPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        lock (_sync)
        {
            return !_reservedPaths.Contains(fullPath);
        }
    }

    public StackFileReadyCandidate? Observe(StackFileObservation observation)
    {
        string path = LiveStackPath.NormalizeForComparison(observation.Path);
        if (!ShouldConsider(path))
        {
            return null;
        }
        lock (_sync)
        {
            if (_reservedPaths.Contains(path))
            {
                return null;
            }
            if (observation.FileIdentity is string observedIdentity &&
                _terminalFileIdentities.Contains(observedIdentity))
            {
                _states[path] = CandidateState.Terminal(
                    StackFileProcessingDisposition.Ignored,
                    observation.Length,
                    observation.LastWriteTimeUtc,
                    observedIdentity);
                return null;
            }
            if (!_states.TryGetValue(path, out CandidateState? state))
            {
                state = new CandidateState(observation);
                _states.Add(path, state);
                return null;
            }

            bool identityChanged = state.HasChanged(observation);
            if (state.IsTerminal)
            {
                if (state.TerminalDisposition == StackFileProcessingDisposition.Unreadable &&
                    identityChanged)
                {
                    state.ResetForChange(observation);
                }
                else if (
                    state.TerminalDisposition == StackFileProcessingDisposition.Unreadable &&
                    observation.ObservedAtUtc >= state.NextAttemptAtUtc)
                {
                    // A writer can retain an exclusive handle after its final
                    // size/timestamp update. Retry an unchanged unreadable file
                    // periodically instead of losing it forever. Preserve the
                    // failure count so repeated corrupt files remain on the
                    // bounded maximum-delay cadence.
                    state.ReopenForScheduledRetry();
                }
                return null;
            }

            if (identityChanged)
            {
                state.ResetForChange(observation);
                return null;
            }

            state.ObservationCount++;
            if (state.AwaitingDisposition ||
                state.ObservationCount < 2 ||
                observation.ObservedAtUtc - state.StableSinceUtc < _minimumStableDuration ||
                observation.ObservedAtUtc < state.NextAttemptAtUtc)
            {
                return null;
            }

            state.AwaitingDisposition = true;
            return new StackFileReadyCandidate(
                path,
                state.Length,
                state.LastWriteTimeUtc,
                state.FailedAttempts + 1,
                state.Revision,
                state.FileIdentity);
        }
    }

    public void Complete(
        StackFileReadyCandidate candidate,
        StackFileProcessingResult result,
        DateTimeOffset completedAtUtc)
    {
        string path = LiveStackPath.NormalizeForComparison(candidate.Path);
        lock (_sync)
        {
            if (!_states.TryGetValue(path, out CandidateState? state) ||
                state.IsTerminal ||
                state.Revision != candidate.Revision ||
                !state.AwaitingDisposition)
            {
                return;
            }

            state.AwaitingDisposition = false;
            if (result.Disposition != StackFileProcessingDisposition.RetryableFailure)
            {
                state.MarkTerminal(
                    result.Disposition,
                    result.Disposition == StackFileProcessingDisposition.Unreadable
                        ? completedAtUtc + _maximumRetryDelay
                        : DateTimeOffset.MaxValue);
                if (result.Disposition != StackFileProcessingDisposition.Unreadable)
                {
                    AddTerminalIdentity(state.FileIdentity);
                }
                return;
            }

            state.FailedAttempts++;
            double multiplier = Math.Pow(2, Math.Min(state.FailedAttempts - 1, 30));
            double delayTicks = Math.Min(
                _initialRetryDelay.Ticks * multiplier,
                _maximumRetryDelay.Ticks);
            state.NextAttemptAtUtc = completedAtUtc + TimeSpan.FromTicks((long)delayTicks);
        }
    }

    public void RetryNow(string path)
    {
        string fullPath = LiveStackPath.NormalizeForComparison(path);
        lock (_sync)
        {
            if (!_states.TryGetValue(fullPath, out CandidateState? state))
            {
                return;
            }
            if (state.IsTerminal &&
                state.TerminalDisposition == StackFileProcessingDisposition.Unreadable)
            {
                state.ReopenForRetry();
            }
            else if (!state.IsTerminal)
            {
                state.AwaitingDisposition = false;
                state.NextAttemptAtUtc = DateTimeOffset.MinValue;
            }
        }
    }

    public void RemovePending(string path)
    {
        string fullPath = LiveStackPath.NormalizeForComparison(path);
        lock (_sync)
        {
            if (_states.TryGetValue(fullPath, out CandidateState? state) && !state.IsTerminal)
            {
                _states.Remove(fullPath);
            }
        }
    }

    private void AddTerminalIdentity(string? fileIdentity)
    {
        if (!string.IsNullOrWhiteSpace(fileIdentity))
        {
            _terminalFileIdentities.Add(fileIdentity);
        }
    }

    private sealed class CandidateState
    {
        public CandidateState(StackFileObservation observation)
        {
            Length = observation.Length;
            LastWriteTimeUtc = observation.LastWriteTimeUtc;
            FileIdentity = observation.FileIdentity;
            StableSinceUtc = observation.ObservedAtUtc;
            ObservationCount = 1;
            Revision = 1;
        }

        private CandidateState(
            StackFileProcessingDisposition disposition,
            long length,
            DateTimeOffset lastWriteTimeUtc,
            string? fileIdentity,
            DateTimeOffset nextAttemptAtUtc)
        {
            IsTerminal = true;
            TerminalDisposition = disposition;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
            FileIdentity = fileIdentity;
            StableSinceUtc = DateTimeOffset.MinValue;
            NextAttemptAtUtc = nextAttemptAtUtc;
            ObservationCount = 2;
            Revision = 1;
        }

        public long Length { get; private set; }
        public DateTimeOffset LastWriteTimeUtc { get; private set; }
        public string? FileIdentity { get; private set; }
        public DateTimeOffset StableSinceUtc { get; private set; }
        public DateTimeOffset NextAttemptAtUtc { get; set; }
        public int ObservationCount { get; set; }
        public int FailedAttempts { get; set; }
        public long Revision { get; private set; }
        public bool AwaitingDisposition { get; set; }
        public bool IsTerminal { get; set; }
        public StackFileProcessingDisposition? TerminalDisposition { get; private set; }

        public static CandidateState Terminal(
            StackFileProcessingDisposition disposition,
            long length = 0,
            DateTimeOffset lastWriteTimeUtc = default,
            string? fileIdentity = null,
            DateTimeOffset nextAttemptAtUtc = default) =>
            new(disposition, length, lastWriteTimeUtc, fileIdentity, nextAttemptAtUtc);

        public bool HasChanged(StackFileObservation observation) =>
            Length != observation.Length ||
            LastWriteTimeUtc != observation.LastWriteTimeUtc ||
            (observation.FileIdentity is string observedIdentity &&
             !string.Equals(FileIdentity, observedIdentity, StringComparison.Ordinal));

        public void ResetForChange(StackFileObservation observation)
        {
            Length = observation.Length;
            LastWriteTimeUtc = observation.LastWriteTimeUtc;
            FileIdentity = observation.FileIdentity ?? FileIdentity;
            StableSinceUtc = observation.ObservedAtUtc;
            ObservationCount = 1;
            FailedAttempts = 0;
            NextAttemptAtUtc = DateTimeOffset.MinValue;
            AwaitingDisposition = false;
            IsTerminal = false;
            TerminalDisposition = null;
            Revision++;
        }

        public void MarkTerminal(
            StackFileProcessingDisposition disposition,
            DateTimeOffset nextAttemptAtUtc)
        {
            AwaitingDisposition = false;
            IsTerminal = true;
            TerminalDisposition = disposition;
            NextAttemptAtUtc = nextAttemptAtUtc;
        }

        public void ReopenForScheduledRetry()
        {
            IsTerminal = false;
            TerminalDisposition = null;
            AwaitingDisposition = false;
            NextAttemptAtUtc = DateTimeOffset.MinValue;
            ObservationCount = Math.Max(2, ObservationCount);
            Revision++;
        }

        public void ReopenForRetry()
        {
            IsTerminal = false;
            TerminalDisposition = null;
            AwaitingDisposition = false;
            FailedAttempts = 0;
            NextAttemptAtUtc = DateTimeOffset.MinValue;
            ObservationCount = Math.Max(2, ObservationCount);
            Revision++;
        }

        public void InvalidatePendingCandidate()
        {
            AwaitingDisposition = false;
            Revision++;
        }
    }
}

/// <summary>
/// Best-effort Windows volume/file-index identity. It is stable across rename
/// and shared by hard links. Failure is intentionally non-fatal; path plus
/// size/write-time remain the portable fallback.
/// </summary>
internal static class WindowsFileIdentity
{
    public static string? TryGet(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                return null;
            }
            ulong fileIndex = ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow;
            return $"{information.VolumeSerialNumber:X8}:{fileIndex:X16}";
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
