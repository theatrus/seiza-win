using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>
/// Owns one filter group's live accumulator, folder discovery, telemetry, and
/// resumable checkpoints. All native mutations are serialized by
/// <see cref="_operationGate"/>; the native session never escapes this type.
/// </summary>
internal sealed class LiveStackCoordinator : IAsyncDisposable
{
    private const int MaximumAttentionItems = 50;
    private readonly LiveStackRunConfiguration _configuration;
    private readonly string _optionsJson;
    private readonly TimeProvider _timeProvider;
    private readonly LiveStackSessionStore _store;
    private readonly StackFolderMonitor _monitor;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly List<LiveStackPersistedFrame> _frames = [];
    private readonly List<LiveStackPersistedSnrSample> _snrSamples = [];
    private readonly List<LiveStackCalibrationEpoch> _calibrationHistory = [];
    private readonly List<LiveStackAttention> _attention = [];
    private readonly HashSet<string> _exportedPaths = new(StringComparer.OrdinalIgnoreCase);
    private ImageStackCalibration _calibration;
    private ImageStackSession? _session;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private LiveStackRunState _state = LiveStackRunState.Created;
    private string _statusMessage = "Ready to watch a folder.";
    private string? _currentPath;
    private string _folderMonitorStatus = "Stopped";
    private string? _folderMonitorMessage;
    private LiveStackFilterIdentity? _lockedFilter;
    private CalibrationFrameSignature? _referenceSignature;
    private RenderedImageData? _preview;
    private DateTimeOffset? _lastPreviewAtUtc;
    private DateTimeOffset? _lastCheckpointAtUtc;
    private long? _lastCheckpointGeneration;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset _createdAtUtc;
    private string _outputPath;
    private int _acceptedFrames;
    private int _rejectedFrames;
    private int _nativeRejectedFrames;
    private int _policyRejectedFrames;
    private int _ignoredFrames;
    private int _unreadableFrames;
    private int _acceptedFramesAtCheckpoint;
    private bool _checkpointDirty;
    private long _persistenceRevision;
    private bool _checkpointBlocked;
    private bool _nativeFinalized;
    private bool _initialized;
    private bool _disposed;

    public LiveStackCoordinator(
        LiveStackRunConfiguration configuration,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _createdAtUtc = _timeProvider.GetUtcNow();
        _configuration = SnapshotConfiguration(configuration);
        _calibration = CloneCalibration(_configuration.Calibration);
        _optionsJson = _configuration.Options.ToJson();
        _outputPath = FullPathOrEmpty(_configuration.OutputPath);

        string groupDirectory = Path.Combine(
            _configuration.SessionRootDirectory,
            LiveStackSessionStore.SafeGroupDirectoryName(_configuration.GroupId));
        _store = new LiveStackSessionStore(groupDirectory);

        string[] excludedPaths = _configuration.MonitorExcludedPaths();
        _monitor = new StackFolderMonitor(new StackFolderMonitorOptions
        {
            FolderPath = _configuration.WatchFolder,
            IncludeSubdirectories = _configuration.IncludeSubdirectories,
            ExcludedPaths = excludedPaths,
            ExcludedDirectories = [groupDirectory],
        }, _timeProvider);
        _monitor.StateChanged += MonitorOnStateChanged;
    }

    /// <remarks>
    /// Raised on the coordinator's worker thread. WinUI consumers must marshal
    /// to their DispatcherQueue.
    /// </remarks>
    public event EventHandler<LiveStackRunChangedEventArgs>? Changed;

    public LiveStackRunSnapshot CurrentSnapshot
    {
        get
        {
            lock (_stateSync)
            {
                return CreateSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Restores a valid current/previous generation, then starts watching.
    /// Returns after startup; use <see cref="RunAsync"/> to await the run.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool initializedDuringThisStart = false;
            ThrowIfDisposed();
            if (_state == LiveStackRunState.Completed)
            {
                throw new InvalidOperationException("The live stack has already been finalized.");
            }
            if (_nativeFinalized)
            {
                throw new InvalidOperationException(
                    "The native stack was finalized. Create a new coordinator to resume its checkpoint.");
            }
            if (_runTask is { IsCompleted: false })
            {
                return;
            }

            if (!_initialized)
            {
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
                initializedDuringThisStart = true;
            }
            if (_session is not null &&
                (_checkpointBlocked ||
                 (_checkpointDirty && _lastCheckpointAtUtc is null)))
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    try
                    {
                        await CheckpointCoreAsync(force: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        BlockOnCheckpointFailure(exception);
                        throw;
                    }
                }
                finally
                {
                    _operationGate.Release();
                }
            }

            _checkpointBlocked = false;
            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            SetState(
                _session is null
                    ? LiveStackRunState.WaitingForLight
                    : LiveStackRunState.Watching,
                _session is null
                    ? "Waiting for the first stable light frame."
                    : initializedDuringThisStart && _lastCheckpointGeneration is long generation
                        ? $"Resumed checkpoint generation {generation}; watching for new light frames."
                        : "Watching for new light frames.");
            _runTask = RunIngestionAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (_session is not null)
            {
                _initialized = true;
                SetState(
                    LiveStackRunState.Paused,
                    "Live-stack startup was cancelled; the open stack remains resumable.");
            }
            throw;
        }
        catch (Exception exception)
        {
            if (_session is not null)
            {
                _initialized = true;
            }
            if (_state != LiveStackRunState.NeedsAttention)
            {
                SetFault(exception);
            }
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Starts and waits until the run is paused, blocked, or finalized.
    /// Cancelling this method performs a pause-and-checkpoint before returning.
    /// Call this instead of StartAsync when the caller wants to await the
    /// entire run. Calling StartAsync first is harmless but redundant.
    /// A paused coordinator resumes through either method.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        Task runTask;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            runTask = _runTask ?? Task.CompletedTask;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        try
        {
            await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PauseAndSaveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Stops folder ingestion without disposing the native stack, publishes a
    /// checkpoint, and leaves this coordinator ready for StartAsync/RunAsync.
    /// </summary>
    public async Task PauseAndSaveAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state == LiveStackRunState.Completed)
            {
                return;
            }

            SetState(LiveStackRunState.Pausing, "Pausing and saving the live stack…");
            await StopIngestionCoreAsync().ConfigureAwait(false);
            // Once ingestion has stopped, completing the checkpoint is the
            // safety contract of PauseAndSave even if the caller cancels.
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_session is not null)
                {
                    await CheckpointCoreAsync(force: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                BlockOnCheckpointFailure(exception);
                throw;
            }
            finally
            {
                _operationGate.Release();
            }

            SetState(LiveStackRunState.Paused, "Live stacking is paused and resumable.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UpdateCalibrationAsync(
        ImageStackCalibration calibration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ImageStackCalibration replacement = CloneCalibration(calibration);
        if (replacement.ValidationMessage([]) is string validationError)
        {
            throw new ArgumentException(validationError, nameof(calibration));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_state == LiveStackRunState.Completed)
                {
                    throw new InvalidOperationException("The live stack has already been finalized.");
                }

                if (_session is null)
                {
                    _calibration = replacement;
                    SeedCalibrationPaths(replacement);
                    AddAttention(
                        "Calibration masters were updated and will be used for the first light frame.");
                    SetState(
                        LiveStackRunState.WaitingForLight,
                        "Calibration is ready; waiting for the first stable light frame.");
                    return;
                }

                LiveStackRunState returnState = RunningStateAfterOperation();
                SetState(LiveStackRunState.Checkpointing, "Updating calibration masters…");
                await _session.SetCalibrationAsync(replacement, cancellationToken)
                    .ConfigureAwait(false);
                _calibration = replacement;
                SeedCalibrationPaths(replacement);
                lock (_stateSync)
                {
                    _calibrationHistory.Add(CreateCalibrationEpoch(
                        replacement,
                        checked(_acceptedFrames + 1),
                        _timeProvider.GetUtcNow()));
                    MarkCheckpointDirtyLocked();
                }
                AddAttention(
                    "Calibration masters changed. New frames will use the new calibration epoch.");
                try
                {
                    // Once the atomic native swap succeeds, its epoch must be
                    // made resumable even if the caller cancels meanwhile.
                    await CheckpointCoreAsync(force: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    BlockOnCheckpointFailure(exception);
                    throw;
                }
                SetState(returnState, StatusFor(returnState));
            }
            catch (OperationCanceledException)
            {
                SetState(RunningStateAfterOperation(), "Calibration update was cancelled.");
                throw;
            }
            catch (Exception exception)
            {
                AddAttention($"Calibration could not be updated: {exception.Message}");
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LiveStackExportResult> SaveSnapshotAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string fullOutputPath = Path.GetFullPath(outputPath);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ImageStackExportSnapshot snapshot;
            int rejectedAtSnapshot;
            bool outputReserved = false;
            bool outputWritten = false;
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ImageStackSession session = RequireSession();
                EnsureSafeOutputPath(fullOutputPath);
                _monitor.ReservePath(fullOutputPath);
                outputReserved = true;
                SetState(LiveStackRunState.SavingSnapshot, "Saving a live-stack snapshot…");
                try
                {
                    snapshot = await session.ExportSnapshotAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _monitor.ReleaseReservedPath(fullOutputPath);
                    outputReserved = false;
                    SetState(RunningStateAfterOperation(), "Snapshot export was cancelled.");
                    throw;
                }
                catch (Exception exception)
                {
                    _monitor.ReleaseReservedPath(fullOutputPath);
                    outputReserved = false;
                    AddAttention(
                        $"The stack snapshot could not be prepared: {exception.Message}",
                        fullOutputPath);
                    SetState(
                        RunningStateAfterOperation(),
                        "The stack snapshot could not be prepared.");
                    throw;
                }
                lock (_stateSync)
                {
                    // Include managed policy rejections as well as native
                    // registration/integration rejections at the same boundary.
                    rejectedAtSnapshot = _rejectedFrames;
                }
            }
            finally
            {
                _operationGate.Release();
            }

            await using (snapshot)
            {
                try
                {
                    // The native snapshot owns its pixels, so folder ingestion
                    // may continue while the potentially large FITS is written.
                    await snapshot.WriteFitsAsync(fullOutputPath, cancellationToken)
                        .ConfigureAwait(false);
                    outputWritten = true;
                    _monitor.CommitReservedPath(fullOutputPath);
                    outputReserved = false;
                    lock (_stateSync)
                    {
                        _outputPath = fullOutputPath;
                        _exportedPaths.Add(fullOutputPath);
                        MarkCheckpointDirtyLocked();
                    }
                    await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        // A successful watched-tree export must be durable in
                        // the manifest before this operation reports success.
                        await CheckpointCoreAsync(force: true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        BlockOnCheckpointFailure(exception);
                        throw;
                    }
                    finally
                    {
                        _operationGate.Release();
                    }
                    SetState(
                        RunningStateAfterOperation(),
                        $"Saved {Path.GetFileName(fullOutputPath)}.");
                    return new LiveStackExportResult(
                        fullOutputPath,
                        snapshot.AcceptedFrames,
                        rejectedAtSnapshot);
                }
                catch (OperationCanceledException)
                {
                    if (outputReserved && !outputWritten)
                    {
                        _monitor.ReleaseReservedPath(fullOutputPath);
                        outputReserved = false;
                    }
                    SetState(RunningStateAfterOperation(), "Snapshot export was cancelled.");
                    throw;
                }
                catch (Exception exception)
                {
                    if (outputReserved && !outputWritten)
                    {
                        _monitor.ReleaseReservedPath(fullOutputPath);
                        outputReserved = false;
                    }
                    if (outputWritten)
                    {
                        AddAttention(
                            $"The snapshot was saved, but its resumable checkpoint failed: {exception.Message}",
                            fullOutputPath);
                        throw new IOException(
                            $"The snapshot was saved to {fullOutputPath}, but its resumable checkpoint " +
                            "could not be updated. Live ingestion has been paused.",
                            exception);
                    }
                    AddAttention(
                        $"The stack snapshot could not be saved: {exception.Message}",
                        outputPath);
                    SetState(
                        RunningStateAfterOperation(),
                        "The stack snapshot could not be saved.");
                    throw;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<LiveStackExportResult> FinishAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string fullOutputPath = Path.GetFullPath(outputPath);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state == LiveStackRunState.Completed)
            {
                throw new InvalidOperationException("The live stack has already been finalized.");
            }

            await StopIngestionCoreAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ImageStackSession session = RequireSession();
                EnsureSafeOutputPath(fullOutputPath);
                lock (_stateSync)
                {
                    _outputPath = fullOutputPath;
                    MarkCheckpointDirtyLocked();
                }
                await TryMeasureDepthAsync(
                    cancellationToken,
                    includeCurrentDepth: true).ConfigureAwait(false);
                try
                {
                    await CheckpointCoreAsync(force: true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    BlockOnCheckpointFailure(exception);
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                SetState(LiveStackRunState.Finishing, "Finalizing the live stack…");
                ImageStackSnapshot snapshot;
                try
                {
                    // Finalizing consumes the native handle. Cancellation is
                    // honored immediately before this irreversible boundary;
                    // once crossed, the saved checkpoint is the recovery path.
                    _nativeFinalized = true;
                    snapshot = await session.FinishAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _session = null;
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                await using (snapshot)
                {
                    await snapshot.WriteFitsAsync(fullOutputPath, cancellationToken)
                        .ConfigureAwait(false);
                    _monitor.SeedProcessedPaths([fullOutputPath]);
                    lock (_stateSync)
                    {
                        _exportedPaths.Add(fullOutputPath);
                    }
                    // Retirement occurs only after the final export is durable.
                    // A cancellation/write failure after native finalization leaves
                    // the pre-finalization checkpoint eligible for recovery.
                    await _store.RetireAsync(_sessionId, CancellationToken.None)
                        .ConfigureAwait(false);
                    var result = new LiveStackExportResult(
                        fullOutputPath,
                        snapshot.AcceptedFrames,
                        _rejectedFrames);
                    SetState(
                        LiveStackRunState.Completed,
                        $"Saved the completed stack to {Path.GetFileName(fullOutputPath)}.");
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                SetState(
                    _nativeFinalized
                        ? LiveStackRunState.NeedsAttention
                        : LiveStackRunState.Paused,
                    _nativeFinalized
                        ? "Export was cancelled after finalization. Reopen this run from its checkpoint."
                        : "Finalization was cancelled; the saved stack remains resumable.");
                throw;
            }
            catch (Exception exception)
            {
                AddAttention(
                    "Finalization failed. The most recent checkpoint remains available: " +
                    exception.Message,
                    fullOutputPath);
                SetState(
                    LiveStackRunState.NeedsAttention,
                    "Finalization failed; resume from the saved checkpoint.");
                throw;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopIngestionCoreAsync().ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session is not null && _checkpointDirty)
                {
                    try
                    {
                        await CheckpointCoreAsync(force: true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        AddAttention(
                            "The final automatic checkpoint failed during shutdown: " +
                            exception.Message);
                    }
                }

                if (_session is not null)
                {
                    await _session.DisposeAsync().ConfigureAwait(false);
                    _session = null;
                }
                _disposed = true;
            }
            finally
            {
                _operationGate.Release();
            }

            _monitor.StateChanged -= MonitorOnStateChanged;
            SetState(LiveStackRunState.Disposed, "The live-stack session is closed.");
            _store.Dispose();
        }
        finally
        {
            _lifecycleGate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        SetState(LiveStackRunState.Restoring, "Looking for a resumable live stack…");
        IReadOnlyList<LiveStackStoredGeneration> candidates = _configuration.ResumeExisting
            ? await _store.GetRestoreCandidatesAsync(cancellationToken).ConfigureAwait(false)
            : [];
        foreach (LiveStackStoredGeneration candidate in candidates)
        {
            ImageStackSession? candidateSession = null;
            try
            {
                if (!ManifestMatchesConfiguration(candidate.State))
                {
                    AddAttention(
                        $"Saved generation {candidate.Generation} did not match this run.",
                        candidate.ManifestPath);
                    continue;
                }
                candidateSession = await ImageStackSession
                    .ResumeAsync(candidate.ContextPath, cancellationToken)
                    .ConfigureAwait(false);
                LiveStackNativeState nativeState = await candidateSession
                    .GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!_store.TryAcceptRestoredGeneration(candidate, nativeState))
                {
                    AddAttention(
                        $"Saved generation {candidate.Generation} did not match this run.",
                        candidate.ContextPath);
                    await candidateSession.DisposeAsync().ConfigureAwait(false);
                    candidateSession = null;
                    continue;
                }

                CalibrationFrameSignature? referenceSignature =
                    await TryDeriveRestoredReferenceSignatureAsync(
                        candidate.State,
                        nativeState,
                        cancellationToken).ConfigureAwait(false);
                _session = candidateSession;
                candidateSession = null;
                RestoreState(candidate, nativeState, referenceSignature);
                SeedCalibrationPaths(_calibration);
                _monitor.SeedProcessedPaths(nativeState.InputPaths);
                _monitor.SeedProcessedFrames(candidate.State.Frames);
                _monitor.SeedProcessedPaths(candidate.State.ExportedPaths.Concat(
                    string.IsNullOrWhiteSpace(candidate.State.OutputPath)
                        ? []
                        : [candidate.State.OutputPath]));
                await TryMeasureDepthAsync(cancellationToken).ConfigureAwait(false);
                await TryRenderPreviewAsync(force: true, cancellationToken).ConfigureAwait(false);
                if (candidate.UsedPreviousGeneration)
                {
                    AddAttention(
                        "The current checkpoint was invalid; resumed the previous generation.");
                }
                break;
            }
            catch (OperationCanceledException)
            {
                if (candidateSession is not null)
                {
                    await candidateSession.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
            catch (Exception exception)
            {
                if (candidateSession is not null)
                {
                    await candidateSession.DisposeAsync().ConfigureAwait(false);
                }
                AddAttention(
                    $"Could not restore generation {candidate.Generation}: {exception.Message}",
                    candidate.ContextPath);
            }
        }

        if (_session is null && _configuration.InitialReferencePath is string referencePath)
        {
            await OpenInitialReferenceAsync(referencePath, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (_session is not null && _configuration.ApplyCalibrationOnResume)
        {
            await ApplyConfiguredCalibrationAfterRestoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        _initialized = true;
        SetState(
            _session is null
                ? LiveStackRunState.WaitingForLight
                : LiveStackRunState.Paused,
            _session is null
                ? "No checkpoint found; waiting for the first stable light frame."
                : "The saved live stack is ready to resume.");
    }

    private async Task OpenInitialReferenceAsync(
        string referencePath,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(referencePath);
        SetCurrentCandidate(fullPath, "Opening the selected reference frame…");
        CalibrationFrameProbe probe = await CalibrationService
            .ProbeAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                probe.Role.Trim(),
                CalibrationFrameRoles.Light,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The selected reference is a {probe.Role} frame, not a light frame.");
        }
        if (LiveStackCalibrationSelection.HasAnyMasters(_calibration) &&
            CalibrationLightEligibility.GetIneligibilityReason(probe) is string reason)
        {
            throw new InvalidDataException(
                $"The selected reference cannot receive calibration because {reason}.");
        }

        var file = new FileInfo(fullPath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected reference no longer exists.", fullPath);
        }

        ImageStackSession? opened = null;
        try
        {
            opened = await ImageStackSession.OpenAsync(
                fullPath,
                _configuration.Options,
                _calibration,
                cancellationToken).ConfigureAwait(false);
            ImageStackSessionCounts counts = await opened
                .GetCountsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (counts.AcceptedFrames != 1)
            {
                throw new SeizaCoreException(
                    "The native live stack did not retain exactly one reference frame.");
            }

            _session = opened;
            opened = null;
            LiveStackFilterIdentity filter = LiveStackFilterIdentity.FromProbe(probe);
            lock (_stateSync)
            {
                _lockedFilter = filter;
                _referenceSignature = probe.Signature with { };
                _calibrationHistory.Add(CreateCalibrationEpoch(
                    _calibration,
                    1,
                    _timeProvider.GetUtcNow()));
            }
            var candidate = new StackFileReadyCandidate(
                fullPath,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                Attempt: 1,
                Revision: 0,
                FileIdentity: WindowsFileIdentity.TryGet(fullPath));
            RecordTerminalFrame(
                candidate,
                LiveStackPersistedFrameDisposition.Accepted,
                "Selected reference frame",
                ValidExposure(probe.Signature.ExposureSeconds),
                counts);
            _monitor.SeedProcessedPaths([fullPath]);
            await TryMeasureDepthAsync(cancellationToken).ConfigureAwait(false);
            await TryRenderPreviewAsync(force: true, cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckpointCoreAsync(force: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                BlockOnCheckpointFailure(exception);
                throw;
            }
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ApplyConfiguredCalibrationAfterRestoreAsync(
        CancellationToken cancellationToken)
    {
        ImageStackCalibration replacement = CloneCalibration(_configuration.Calibration);
        if (LiveStackCalibrationSelection.AreEquivalent(_calibration, replacement))
        {
            return;
        }

        ImageStackSession session = RequireSession();
        SetState(LiveStackRunState.Checkpointing, "Updating restored calibration masters…");
        await session.SetCalibrationAsync(replacement, cancellationToken)
            .ConfigureAwait(false);
        _calibration = replacement;
        SeedCalibrationPaths(replacement);
        lock (_stateSync)
        {
            _calibrationHistory.Add(CreateCalibrationEpoch(
                replacement,
                checked(_acceptedFrames + 1),
                _timeProvider.GetUtcNow()));
            MarkCheckpointDirtyLocked();
        }
        AddAttention(
            "The selected calibration replaced the checkpoint calibration for new frames.");
        try
        {
            // Once the native calibration swap succeeds, persist the new epoch even
            // if startup cancellation arrives before folder watching begins.
            await CheckpointCoreAsync(force: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BlockOnCheckpointFailure(exception);
            throw;
        }
    }

    private async Task RunIngestionAsync(CancellationToken cancellationToken)
    {
        Task checkpointTimer = RunCheckpointTimerAsync(cancellationToken);
        try
        {
            await foreach (StackFileReadyCandidate candidate in
                _monitor.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_checkpointBlocked)
                    {
                        return;
                    }
                    await ProcessCandidateAsync(candidate, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _monitor.RetryNow(candidate.Path);
                    throw;
                }
                catch
                {
                    _monitor.RetryNow(candidate.Path);
                    throw;
                }
                finally
                {
                    _operationGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFault(exception);
            _runCancellation?.Cancel();
        }
        finally
        {
            try
            {
                await checkpointTimer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SetFault(exception);
            }

            lock (_stateSync)
            {
                if (_state is not (
                    LiveStackRunState.Completed or
                    LiveStackRunState.NeedsAttention or
                    LiveStackRunState.Faulted or
                    LiveStackRunState.Disposed or
                    LiveStackRunState.Pausing or
                    LiveStackRunState.Finishing))
                {
                    _state = LiveStackRunState.Paused;
                    _statusMessage = "Live stacking is paused.";
                }
            }
            PublishSnapshot();
        }
    }

    private async Task<CalibrationFrameSignature?> TryDeriveRestoredReferenceSignatureAsync(
        LiveStackPersistedState persistedState,
        LiveStackNativeState nativeState,
        CancellationToken cancellationToken)
    {
        if (nativeState.ReferenceFrame is CalibrationFrameProbe nativeReference)
        {
            if (!string.Equals(
                    nativeReference.Role.Trim(),
                    CalibrationFrameRoles.Light,
                    StringComparison.OrdinalIgnoreCase) ||
                nativeReference.IsMaster)
            {
                AddAttention(
                    "The checkpoint's native reference metadata is not a usable light frame.");
                return null;
            }

            LiveStackFilterIdentity expected =
                LiveStackFilterIdentity.FromStoredName(persistedState.FilterName);
            if (!string.IsNullOrWhiteSpace(nativeReference.Signature.Filter) &&
                !expected.Matches(LiveStackFilterIdentity.FromProbe(nativeReference)))
            {
                AddAttention(
                    "The checkpoint's native reference filter does not match the saved stack.");
                return null;
            }
            return nativeReference.Signature with { };
        }

        string? referencePath = nativeState.InputPaths.FirstOrDefault();
        LiveStackPersistedFrame? recordedReference = referencePath is null
            ? null
            : persistedState.Frames.FirstOrDefault(frame =>
                frame.Disposition == LiveStackPersistedFrameDisposition.Accepted &&
                PathsEqual(frame.Path, referencePath));
        if (referencePath is null || recordedReference is null)
        {
            AddAttention(
                "The saved stack does not contain enough reference identity to accept new lights.",
                referencePath);
            return null;
        }

        try
        {
            string persistedReferencePath = LiveStackPath.NormalizeForComparison(
                recordedReference.Path);
            var file = new FileInfo(persistedReferencePath);
            file.Refresh();
            var lastWriteTimeUtc = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            if (!file.Exists ||
                file.Length != recordedReference.Length ||
                lastWriteTimeUtc != recordedReference.LastWriteTimeUtc ||
                (recordedReference.FileIdentity is string expectedIdentity &&
                 WindowsFileIdentity.TryGet(persistedReferencePath) is string actualIdentity &&
                 !string.Equals(expectedIdentity, actualIdentity, StringComparison.Ordinal)))
            {
                AddAttention(
                    "The original reference file changed or is unavailable; new lights are blocked " +
                    "to protect calibration consistency.",
                    referencePath);
                return null;
            }

            CalibrationFrameProbe probe = await CalibrationService
                .ProbeAsync(persistedReferencePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    probe.Role.Trim(),
                    CalibrationFrameRoles.Light,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddAttention(
                    "The original reference no longer identifies as a light frame; new lights are blocked.",
                    referencePath);
                return null;
            }

            LiveStackFilterIdentity expected =
                LiveStackFilterIdentity.FromStoredName(persistedState.FilterName);
            LiveStackFilterIdentity actual = LiveStackFilterIdentity.FromProbe(probe);
            if (!expected.Matches(actual))
            {
                AddAttention(
                    "The original reference filter no longer matches the saved stack; new lights are blocked.",
                    referencePath);
                return null;
            }
            return probe.Signature with { };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddAttention(
                "The original reference identity could not be read; new lights are blocked: " +
                exception.Message,
                referencePath);
            return null;
        }
    }

    private async Task RunCheckpointTimerAsync(CancellationToken cancellationToken)
    {
        double seconds = Math.Clamp(
            _configuration.CheckpointInterval.TotalSeconds / 4,
            1,
            15);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!IsCheckpointDueByTime())
            {
                continue;
            }

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsCheckpointDueByTime() || _session is null || _checkpointBlocked)
                {
                    continue;
                }
                try
                {
                    await CheckpointCoreAsync(force: false, cancellationToken)
                        .ConfigureAwait(false);
                    SetState(RunningStateAfterOperation(), "Watching for new light frames.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    BlockOnCheckpointFailure(exception);
                    _runCancellation?.Cancel();
                    return;
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private async Task ProcessCandidateAsync(
        StackFileReadyCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!_monitor.IsCandidateCurrent(candidate))
        {
            return;
        }
        SetCurrentCandidate(candidate.Path, "Inspecting the stable image header…");
        CalibrationFrameProbe probe;
        try
        {
            probe = await CalibrationService.ProbeAsync(candidate.Path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CompleteReadFailure(candidate, exception.Message);
            return;
        }

        string role = probe.Role.Trim().ToLowerInvariant();
        if (!string.Equals(role, CalibrationFrameRoles.Light, StringComparison.Ordinal))
        {
            string reason = role switch
            {
                CalibrationFrameRoles.Bias => "Bias calibration frame",
                CalibrationFrameRoles.Dark => "Dark calibration frame",
                CalibrationFrameRoles.DarkFlat => "Dark-flat calibration frame",
                CalibrationFrameRoles.Flat => "Flat calibration frame",
                _ => $"Frame role is {role}",
            };
            RecordTerminalFrame(
                candidate,
                LiveStackPersistedFrameDisposition.Ignored,
                reason,
                ValidExposure(probe.Signature.ExposureSeconds));
            _monitor.ReportProcessingResult(
                candidate,
                new StackFileProcessingResult(StackFileProcessingDisposition.Ignored, reason));
            AddAttention($"Ignored {Path.GetFileName(candidate.Path)}: {reason}.", candidate.Path);
            SetWatchingState("Ignored a non-light frame.");
            return;
        }

        if (LiveStackCalibrationSelection.HasAnyMasters(_calibration) &&
            CalibrationLightEligibility.GetIneligibilityReason(probe) is string rawReason)
        {
            RejectForCalibrationIdentity(candidate, probe, rawReason);
            return;
        }

        LiveStackFilterIdentity candidateFilter = LiveStackFilterIdentity.FromProbe(probe);
        LiveStackFilterIdentity? lockedFilter;
        lock (_stateSync)
        {
            lockedFilter = _lockedFilter;
        }
        if (lockedFilter is not null && !lockedFilter.Matches(candidateFilter))
        {
            string reason =
                $"Filter {candidateFilter.DisplayName} does not match " +
                $"{lockedFilter.DisplayName}";
            RecordTerminalFrame(
                candidate,
                LiveStackPersistedFrameDisposition.Ignored,
                reason,
                ValidExposure(probe.Signature.ExposureSeconds));
            _monitor.ReportProcessingResult(
                candidate,
                new StackFileProcessingResult(StackFileProcessingDisposition.Ignored, reason));
            SetWatchingState($"Ignored a {candidateFilter.DisplayName} frame.");
            return;
        }

        if (_session is not null)
        {
            CalibrationFrameSignature? referenceSignature;
            lock (_stateSync)
            {
                referenceSignature = _referenceSignature;
            }
            if (referenceSignature is null)
            {
                RejectForCalibrationIdentity(
                    candidate,
                    probe,
                    "The restored reference identity is unavailable.");
                return;
            }
            if (!LiveStackCalibrationIdentity.Matches(
                    referenceSignature,
                    probe.Signature,
                    out string? mismatchReason))
            {
                RejectForCalibrationIdentity(
                    candidate,
                    probe,
                    mismatchReason ?? "Calibration identity does not match the reference.");
                return;
            }
        }

        if (_session is null)
        {
            await OpenReferenceAsync(candidate, probe, candidateFilter, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        SetCurrentCandidate(candidate.Path, "Registering and integrating the light frame…");
        ImageStackPushResult push = await _session
            .PushFrameAsync(candidate.Path, cancellationToken)
            .ConfigureAwait(false);
        if (push.NativeFailure)
        {
            CompleteReadFailure(
                candidate,
                push.Disposition.Reason ?? "The native core could not read the frame.");
            return;
        }

        // A successful native response means the mutable accumulator has
        // crossed its commit boundary, even if pause was requested while the
        // synchronous Rust call was running. Finish the matching managed
        // ledger update without cancellation so the checkpoint can never
        // describe a frame that the manifest forgot.
        ImageStackSessionCounts counts = await _session
            .GetCountsAsync(CancellationToken.None)
            .ConfigureAwait(false);
        LiveStackPersistedFrameDisposition disposition = push.Disposition.Accepted
            ? LiveStackPersistedFrameDisposition.Accepted
            : LiveStackPersistedFrameDisposition.Rejected;
        RecordTerminalFrame(
            candidate,
            disposition,
            push.Disposition.Reason,
            ValidExposure(probe.Signature.ExposureSeconds),
            counts);
        _monitor.ReportProcessingResult(
            candidate,
            new StackFileProcessingResult(
                push.Disposition.Accepted
                    ? StackFileProcessingDisposition.Accepted
                    : StackFileProcessingDisposition.Rejected,
                push.Disposition.Reason));

        if (push.Disposition.Accepted)
        {
            await AfterAcceptedFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        SetWatchingState(
            push.Disposition.Accepted
                ? $"Integrated {Path.GetFileName(candidate.Path)}."
                : $"Rejected {Path.GetFileName(candidate.Path)}: " +
                  (push.Disposition.Reason ?? "registration or acceptance failed."));
    }

    private async Task OpenReferenceAsync(
        StackFileReadyCandidate candidate,
        CalibrationFrameProbe probe,
        LiveStackFilterIdentity filter,
        CancellationToken cancellationToken)
    {
        SetCurrentCandidate(candidate.Path, "Opening the first light as the reference frame…");
        ImageStackSession? session = null;
        try
        {
            session = await ImageStackSession.OpenAsync(
                candidate.Path,
                _configuration.Options,
                _calibration,
                cancellationToken).ConfigureAwait(false);
            ImageStackSessionCounts counts = await session
                .GetCountsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (counts.AcceptedFrames != 1)
            {
                throw new SeizaCoreException(
                    "The native live stack did not retain exactly one reference frame.");
            }

            _session = session;
            session = null;
            lock (_stateSync)
            {
                _lockedFilter = filter;
                _referenceSignature = probe.Signature with { };
                _calibrationHistory.Add(CreateCalibrationEpoch(
                    _calibration,
                    1,
                    _timeProvider.GetUtcNow()));
            }
            RecordTerminalFrame(
                candidate,
                LiveStackPersistedFrameDisposition.Accepted,
                "Reference frame",
                ValidExposure(probe.Signature.ExposureSeconds),
                counts);
            _monitor.ReportProcessingResult(
                candidate,
                new StackFileProcessingResult(StackFileProcessingDisposition.Accepted));
            await AfterAcceptedFrameAsync(cancellationToken).ConfigureAwait(false);
            if (!_checkpointBlocked)
            {
                SetWatchingState(
                    $"Started the {filter.DisplayName} stack with " +
                    $"{Path.GetFileName(candidate.Path)}.");
            }
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            CompleteReadFailure(candidate, exception.Message);
        }
    }

    private async Task AfterAcceptedFrameAsync(CancellationToken cancellationToken)
    {
        await TryMeasureDepthAsync(cancellationToken).ConfigureAwait(false);
        await TryRenderPreviewAsync(force: _acceptedFrames == 1, cancellationToken)
            .ConfigureAwait(false);

        bool due;
        lock (_stateSync)
        {
            due = _acceptedFrames == 1 ||
                _acceptedFrames - _acceptedFramesAtCheckpoint >=
                    _configuration.CheckpointAcceptedFrameInterval;
        }
        if (!due)
        {
            return;
        }

        try
        {
            await CheckpointCoreAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            BlockOnCheckpointFailure(exception);
            _runCancellation?.Cancel();
        }
    }

    private async Task TryMeasureDepthAsync(
        CancellationToken cancellationToken,
        bool includeCurrentDepth = false)
    {
        ImageStackSession? session = _session;
        int accepted;
        lock (_stateSync)
        {
            accepted = _acceptedFrames;
            if (!LiveStackRunMath.IsSnrMeasurementDue(
                    accepted,
                    _snrSamples.Select(sample => sample.AcceptedFrames),
                    includeCurrentDepth))
            {
                return;
            }
        }

        try
        {
            ImageStackSnrSample? measurement = await session!
                .MeasureDepthAsync(cancellationToken)
                .ConfigureAwait(false);
            if (measurement is null || measurement.Frames != (uint)accepted)
            {
                return;
            }

            lock (_stateSync)
            {
                _snrSamples.Add(new LiveStackPersistedSnrSample
                {
                    AcceptedFrames = accepted,
                    CumulativeExposureSeconds =
                        LiveStackRunMath.CumulativeExposure(_frames),
                    Noise = measurement.Noise,
                    Background = measurement.Background,
                    Signal = measurement.Signal,
                    ChannelNoise = [.. measurement.ChannelNoise],
                    MeasuredAtUtc = _timeProvider.GetUtcNow(),
                });
                MarkCheckpointDirtyLocked();
            }
            PublishSnapshot();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddAttention($"SNR measurement was unavailable: {exception.Message}");
        }
    }

    private async Task TryRenderPreviewAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        ImageStackSession? session = _session;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_stateSync)
        {
            if (!force &&
                _lastPreviewAtUtc is DateTimeOffset last &&
                now - last < _configuration.PreviewInterval)
            {
                return;
            }
        }

        try
        {
            RenderedImageData preview = await session!.RenderPreviewAsync(
                _configuration.PreviewProcessingJson,
                _configuration.PreviewMaxDimension,
                cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                _preview = preview;
                _lastPreviewAtUtc = now;
            }
            PublishSnapshot();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddAttention($"The live preview could not be refreshed: {exception.Message}");
        }
    }

    private async Task CheckpointCoreAsync(bool force, CancellationToken cancellationToken)
    {
        ImageStackSession session = RequireSession();
        lock (_stateSync)
        {
            if (!force && !_checkpointDirty)
            {
                return;
            }
        }

        SetState(LiveStackRunState.Checkpointing, "Saving a resumable checkpoint…");
        DateTimeOffset now = _timeProvider.GetUtcNow();
        LiveStackPersistedState state;
        long capturedRevision;
        lock (_stateSync)
        {
            capturedRevision = _persistenceRevision;
            state = new LiveStackPersistedState
            {
                SessionId = _sessionId,
                GroupId = _configuration.GroupId,
                GroupTitle = _configuration.GroupTitle,
                FilterName = _lockedFilter is { Source: not LiveStackFilterSource.Unspecified }
                    ? _lockedFilter.DisplayName
                    : null,
                WatchFolder = _configuration.WatchFolder,
                IncludesSubdirectories = _configuration.IncludeSubdirectories,
                OutputPath = _outputPath,
                StackOptionsJson = _optionsJson,
                CreatedAtUtc = _createdAtUtc,
                UpdatedAtUtc = now,
                CalibrationHistory = [.. _calibrationHistory],
                ExportedPaths = [.. _exportedPaths],
                Frames = [.. _frames],
                SnrSamples = _snrSamples
                    .Select(sample => sample with { ChannelNoise = [.. sample.ChannelNoise] })
                    .ToArray(),
            };
        }

        LiveStackStoredGeneration stored = await _store.PublishAsync(
            state,
            new ImageStackCheckpointWriter(session),
            cancellationToken).ConfigureAwait(false);
        lock (_stateSync)
        {
            _lastCheckpointAtUtc = stored.State.UpdatedAtUtc;
            _lastCheckpointGeneration = stored.Generation;
            _acceptedFramesAtCheckpoint = _acceptedFrames;
            _checkpointDirty = LiveStackRunMath.CheckpointRemainsDirty(
                _persistenceRevision,
                capturedRevision);
            _checkpointBlocked = false;
        }
        PublishSnapshot();
    }

    private void CompleteReadFailure(StackFileReadyCandidate candidate, string reason)
    {
        if (candidate.Attempt < _configuration.MaximumReadAttempts)
        {
            _monitor.ReportProcessingResult(
                candidate,
                new StackFileProcessingResult(
                    StackFileProcessingDisposition.RetryableFailure,
                    reason));
            SetWatchingState(
                $"Will retry {Path.GetFileName(candidate.Path)} " +
                $"({candidate.Attempt}/{_configuration.MaximumReadAttempts}).");
            return;
        }

        RecordTerminalFrame(
            candidate,
            LiveStackPersistedFrameDisposition.Unreadable,
            reason,
            exposureSeconds: null);
        _monitor.ReportProcessingResult(
            candidate,
            new StackFileProcessingResult(StackFileProcessingDisposition.Unreadable, reason));
        AddAttention(
            $"Could not read {Path.GetFileName(candidate.Path)} after " +
            $"{candidate.Attempt} attempts: {reason}",
            candidate.Path);
        SetWatchingState($"Skipped unreadable {Path.GetFileName(candidate.Path)}.");
    }

    private void RejectForCalibrationIdentity(
        StackFileReadyCandidate candidate,
        CalibrationFrameProbe probe,
        string reason)
    {
        RecordTerminalFrame(
            candidate,
            LiveStackPersistedFrameDisposition.Rejected,
            reason,
            ValidExposure(probe.Signature.ExposureSeconds));
        _monitor.ReportProcessingResult(
            candidate,
            new StackFileProcessingResult(StackFileProcessingDisposition.Rejected, reason));
        AddAttention(
            $"Rejected {Path.GetFileName(candidate.Path)} before calibration: {reason}",
            candidate.Path);
        SetWatchingState($"Rejected calibration-incompatible {Path.GetFileName(candidate.Path)}.");
    }

    private void RecordTerminalFrame(
        StackFileReadyCandidate candidate,
        LiveStackPersistedFrameDisposition disposition,
        string? reason,
        double? exposureSeconds,
        ImageStackSessionCounts? nativeCounts = null)
    {
        lock (_stateSync)
        {
            // An unreadable file is retried after a quiet cooldown. Replace
            // that provisional history entry when the same path is observed
            // again so a recovered file is not reported forever as skipped.
            int removedUnreadable = _frames.RemoveAll(frame =>
                frame.Disposition == LiveStackPersistedFrameDisposition.Unreadable &&
                PathsEqual(frame.Path, candidate.Path));
            _unreadableFrames = Math.Max(0, _unreadableFrames - removedUnreadable);
            _frames.Add(new LiveStackPersistedFrame
            {
                Path = Path.GetFullPath(candidate.Path),
                Disposition = disposition,
                Reason = reason,
                ExposureSeconds = exposureSeconds,
                Length = candidate.Length,
                LastWriteTimeUtc = candidate.LastWriteTimeUtc,
                ProcessedAtUtc = _timeProvider.GetUtcNow(),
                FileIdentity = candidate.FileIdentity,
            });
            if (nativeCounts is not null)
            {
                _acceptedFrames = nativeCounts.AcceptedFrames;
                _nativeRejectedFrames = nativeCounts.RejectedFrames;
                _rejectedFrames = checked(_nativeRejectedFrames + _policyRejectedFrames);
            }
            else if (disposition == LiveStackPersistedFrameDisposition.Rejected)
            {
                _policyRejectedFrames++;
                _rejectedFrames = checked(_nativeRejectedFrames + _policyRejectedFrames);
            }
            if (disposition == LiveStackPersistedFrameDisposition.Ignored)
            {
                _ignoredFrames++;
            }
            else if (disposition == LiveStackPersistedFrameDisposition.Unreadable)
            {
                _unreadableFrames++;
            }
            if (_session is not null)
            {
                MarkCheckpointDirtyLocked();
            }
        }
        PublishSnapshot();
    }

    private void RestoreState(
        LiveStackStoredGeneration candidate,
        LiveStackNativeState nativeState,
        CalibrationFrameSignature? referenceSignature)
    {
        LiveStackPersistedState state = candidate.State;
        lock (_stateSync)
        {
            _sessionId = state.SessionId;
            _createdAtUtc = state.CreatedAtUtc;
            _outputPath = state.OutputPath;
            _lockedFilter = LiveStackFilterIdentity.FromStoredName(state.FilterName);
            _referenceSignature = referenceSignature;
            _frames.Clear();
            _frames.AddRange(state.Frames);
            _snrSamples.Clear();
            _snrSamples.AddRange(state.SnrSamples.Select(sample =>
                sample with { ChannelNoise = [.. sample.ChannelNoise] }));
            _calibrationHistory.Clear();
            _calibrationHistory.AddRange(state.CalibrationHistory);
            _exportedPaths.Clear();
            _exportedPaths.UnionWith(state.ExportedPaths.Select(
                LiveStackPath.NormalizeForComparison));
            if (_calibrationHistory.LastOrDefault() is LiveStackCalibrationEpoch latest)
            {
                _calibration = new ImageStackCalibration
                {
                    BiasPath = latest.BiasPath,
                    DarkPath = latest.DarkPath,
                    FlatPath = latest.FlatPath,
                    OverridesDarkExposure = latest.DarkExposureSeconds is not null,
                    DarkExposureSeconds = latest.DarkExposureSeconds ?? 300,
                };
            }
            _acceptedFrames = nativeState.AcceptedFrames;
            _nativeRejectedFrames = nativeState.RejectedFrames;
            _policyRejectedFrames = Math.Max(
                0,
                _frames.Count(frame =>
                    frame.Disposition == LiveStackPersistedFrameDisposition.Rejected) -
                _nativeRejectedFrames);
            _rejectedFrames = checked(_nativeRejectedFrames + _policyRejectedFrames);
            _ignoredFrames = _frames.Count(frame =>
                frame.Disposition == LiveStackPersistedFrameDisposition.Ignored);
            _unreadableFrames = _frames.Count(frame =>
                frame.Disposition == LiveStackPersistedFrameDisposition.Unreadable);
            _lastCheckpointAtUtc = state.UpdatedAtUtc;
            _lastCheckpointGeneration = candidate.Generation;
            _acceptedFramesAtCheckpoint = nativeState.AcceptedFrames;
            _persistenceRevision = 0;
            _checkpointDirty = false;
            _checkpointBlocked = false;
        }
        PublishSnapshot();
    }

    private bool ManifestMatchesConfiguration(LiveStackPersistedState state) =>
        string.Equals(state.GroupId, _configuration.GroupId, StringComparison.Ordinal) &&
        PathsEqual(state.WatchFolder, _configuration.WatchFolder) &&
        state.IncludesSubdirectories == _configuration.IncludeSubdirectories &&
        string.Equals(state.StackOptionsJson, _optionsJson, StringComparison.Ordinal);

    private void MarkCheckpointDirtyLocked()
    {
        _persistenceRevision = checked(_persistenceRevision + 1);
        _checkpointDirty = true;
    }

    private bool IsCheckpointDueByTime()
    {
        lock (_stateSync)
        {
            return _checkpointDirty &&
                _session is not null &&
                (_lastCheckpointAtUtc is null ||
                 _timeProvider.GetUtcNow() - _lastCheckpointAtUtc >=
                    _configuration.CheckpointInterval);
        }
    }

    private async Task StopIngestionCoreAsync()
    {
        CancellationTokenSource? cancellation = _runCancellation;
        Task? runTask = _runTask;
        if (cancellation is null && runTask is null)
        {
            return;
        }

        cancellation?.Cancel();
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation?.Dispose();
        _runCancellation = null;
        _runTask = null;
    }

    private void BlockOnCheckpointFailure(Exception exception)
    {
        lock (_stateSync)
        {
            _checkpointBlocked = true;
        }
        _runCancellation?.Cancel();
        AddAttention(
            "Checkpointing failed, so folder ingestion was paused to protect resumability: " +
            exception.Message,
            _store.GroupDirectory);
        SetState(
            LiveStackRunState.NeedsAttention,
            "Checkpoint failed. Free disk space or choose a writable session location, then resume.");
    }

    private void SetFault(Exception exception)
    {
        AddAttention($"Live stacking stopped: {exception.Message}", _currentPath);
        SetState(LiveStackRunState.Faulted, "Live stacking stopped because of an error.");
    }

    private void SetCurrentCandidate(string path, string message)
    {
        lock (_stateSync)
        {
            _state = LiveStackRunState.Processing;
            _currentPath = path;
            _statusMessage = message;
        }
        PublishSnapshot();
    }

    private void SetWatchingState(string message)
    {
        lock (_stateSync)
        {
            if (!_checkpointBlocked)
            {
                _state = _session is null
                    ? LiveStackRunState.WaitingForLight
                    : LiveStackRunState.Watching;
                _statusMessage = message;
                _currentPath = null;
            }
        }
        PublishSnapshot();
    }

    private void SetState(LiveStackRunState state, string message)
    {
        lock (_stateSync)
        {
            _state = state;
            _statusMessage = message;
            if (state is not LiveStackRunState.Processing)
            {
                _currentPath = null;
            }
        }
        PublishSnapshot();
    }

    private void AddAttention(string message, string? path = null)
    {
        lock (_stateSync)
        {
            _attention.Add(new LiveStackAttention(
                message,
                path,
                _timeProvider.GetUtcNow()));
            if (_attention.Count > MaximumAttentionItems)
            {
                _attention.RemoveRange(0, _attention.Count - MaximumAttentionItems);
            }
        }
        PublishSnapshot();
    }

    private void MonitorOnStateChanged(
        object? sender,
        StackFolderMonitorStateChangedEventArgs args)
    {
        lock (_stateSync)
        {
            _folderMonitorStatus = args.State.ToString();
            _folderMonitorMessage = args.Message;
        }
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        LiveStackRunSnapshot snapshot;
        lock (_stateSync)
        {
            snapshot = CreateSnapshotLocked();
        }

        EventHandler<LiveStackRunChangedEventArgs>? handlers = Changed;
        if (handlers is null)
        {
            return;
        }
        var args = new LiveStackRunChangedEventArgs(snapshot);
        foreach (EventHandler<LiveStackRunChangedEventArgs> handler in
            handlers.GetInvocationList().Cast<EventHandler<LiveStackRunChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A presentation subscriber cannot stop folder ingestion.
            }
        }
    }

    private LiveStackRunSnapshot CreateSnapshotLocked() => new()
    {
        State = _state,
        StatusMessage = _statusMessage,
        CurrentPath = _currentPath,
        LockedFilter = _lockedFilter,
        ReferenceSignature = _referenceSignature is null
            ? null
            : _referenceSignature with { },
        AcceptedFrames = _acceptedFrames,
        RejectedFrames = _rejectedFrames,
        IgnoredFrames = _ignoredFrames,
        UnreadableFrames = _unreadableFrames,
        FolderMonitorStatus = _folderMonitorStatus,
        FolderMonitorMessage = _folderMonitorMessage,
        ObservedAtUtc = _timeProvider.GetUtcNow(),
        LastCheckpointAtUtc = _lastCheckpointAtUtc,
        LastCheckpointGeneration = _lastCheckpointGeneration,
        CalibrationHistory = [.. _calibrationHistory],
        Frames = [.. _frames],
        SnrSamples = _snrSamples
            .Select(sample => sample with { ChannelNoise = [.. sample.ChannelNoise] })
            .ToArray(),
        SnrPlot = LiveStackRunMath.CreateSnrPlot(_snrSamples),
        Preview = _preview,
        Attention = [.. _attention],
        RequiresReopenToResume = _nativeFinalized,
    };

    private LiveStackRunState RunningStateAfterOperation()
    {
        lock (_stateSync)
        {
            if (_checkpointBlocked)
            {
                return LiveStackRunState.NeedsAttention;
            }
            if (_state is LiveStackRunState.Faulted or
                LiveStackRunState.Completed or
                LiveStackRunState.Disposed)
            {
                return _state;
            }
            return _runTask is { IsCompleted: false }
                ? _session is null
                    ? LiveStackRunState.WaitingForLight
                    : LiveStackRunState.Watching
                : LiveStackRunState.Paused;
        }
    }

    private static string StatusFor(LiveStackRunState state) => state switch
    {
        LiveStackRunState.WaitingForLight => "Waiting for the first stable light frame.",
        LiveStackRunState.Watching => "Watching for new light frames.",
        LiveStackRunState.Paused => "Live stacking is paused and resumable.",
        _ => "Live stack updated.",
    };

    private ImageStackSession RequireSession() => _session ??
        throw new InvalidOperationException("No light frame has started this live stack yet.");

    private void EnsureSafeOutputPath(string outputPath)
    {
        string[] sources;
        lock (_stateSync)
        {
            sources = _frames.Select(frame => frame.Path)
                .Concat(new[]
                {
                    _configuration.InitialReferencePath,
                    _calibration.BiasPath,
                    _calibration.DarkPath,
                    _calibration.FlatPath,
                }.OfType<string>())
                .Concat(_calibrationHistory.SelectMany(epoch => new[]
                {
                    epoch.BiasPath,
                    epoch.DarkPath,
                    epoch.FlatPath,
                }.OfType<string>()))
                .ToArray();
        }
        if (sources.Any(source => PathsEqual(source, outputPath)) ||
            LiveStackPath.IsWithinDirectory(outputPath, _store.GroupDirectory))
        {
            throw new ArgumentException(
                "The stack output cannot overwrite an input, calibration frame, or checkpoint.",
                nameof(outputPath));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static LiveStackCalibrationEpoch CreateCalibrationEpoch(
        ImageStackCalibration calibration,
        int startsAtAcceptedFrame,
        DateTimeOffset selectedAtUtc) => new()
        {
            StartsAtAcceptedFrame = startsAtAcceptedFrame,
            BiasPath = FullPathOrNull(calibration.BiasPath),
            DarkPath = FullPathOrNull(calibration.DarkPath),
            FlatPath = FullPathOrNull(calibration.FlatPath),
            DarkExposureSeconds = calibration.OverridesDarkExposure
            ? calibration.DarkExposureSeconds
            : null,
            SelectedAtUtc = selectedAtUtc,
        };

    private static LiveStackRunConfiguration SnapshotConfiguration(
        LiveStackRunConfiguration source) => source with
        {
            WatchFolder = Path.GetFullPath(source.WatchFolder),
            SessionRootDirectory = Path.GetFullPath(source.SessionRootDirectory),
            OutputPath = string.IsNullOrWhiteSpace(source.OutputPath)
            ? null
            : Path.GetFullPath(source.OutputPath),
            InitialReferencePath = string.IsNullOrWhiteSpace(source.InitialReferencePath)
            ? null
            : Path.GetFullPath(source.InitialReferencePath),
            Options = CloneOptions(source.Options),
            Calibration = CloneCalibration(source.Calibration),
        };

    private static ImageStackOptions CloneOptions(ImageStackOptions source) => new()
    {
        Normalization = source.Normalization,
        LocalTileSize = source.LocalTileSize,
        Rejection = source.Rejection,
        SigmaLow = source.SigmaLow,
        SigmaHigh = source.SigmaHigh,
        RejectionWarmup = source.RejectionWarmup,
        MaximumRegistrationRms = source.MaximumRegistrationRms,
        MaximumDriftPixels = source.MaximumDriftPixels,
        MaximumDriftFraction = source.MaximumDriftFraction,
        MinimumOverlap = source.MinimumOverlap,
    };

    private static ImageStackCalibration CloneCalibration(ImageStackCalibration source) => new()
    {
        BiasPath = FullPathOrNull(source.BiasPath),
        DarkPath = FullPathOrNull(source.DarkPath),
        FlatPath = FullPathOrNull(source.FlatPath),
        OverridesDarkExposure = source.OverridesDarkExposure,
        DarkExposureSeconds = source.DarkExposureSeconds,
    };

    private void SeedCalibrationPaths(ImageStackCalibration calibration) =>
        _monitor.SeedProcessedPaths(new[]
        {
            calibration.BiasPath,
            calibration.DarkPath,
            calibration.FlatPath,
        }.OfType<string>());

    private static double? ValidExposure(double? exposure) =>
        exposure is double value && double.IsFinite(value) && value > 0
            ? value
            : null;

    private static string FullPathOrEmpty(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static string? FullPathOrNull(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static bool PathsEqual(string left, string right) =>
        LiveStackPath.Equals(left, right);

}
