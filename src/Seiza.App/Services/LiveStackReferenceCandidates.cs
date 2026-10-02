using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>
/// Observes startup reference candidates with exactly the watcher's stability
/// and exclusion rules. This private tracker never marks a capture processed.
/// </summary>
internal sealed class LiveStackReferenceCandidates
{
    private readonly StackFolderMonitorOptions _options;
    private readonly StackFileCandidateTracker _tracker;

    public LiveStackReferenceCandidates(StackFolderMonitorOptions options)
    {
        _options = options;
        _tracker = new StackFileCandidateTracker(options);
    }

    public bool HasPendingCandidates => _tracker.PendingPaths.Count > 0;

    public async Task<IReadOnlyList<StackFileReadyCandidate>> ObserveStableExistingAsync(
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        _ = await Task.Run(() => ObserveExisting(timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasPendingCandidates)
        {
            return [];
        }

        // Timer completion is only a wakeup, not proof that the tracker's UTC
        // stability interval elapsed. Windows timers can fire just before the
        // requested boundary; recheck instead of silently dropping every frame.
        // Wait after enumeration finishes, not after its pre-scan timestamp:
        // a large capture folder can itself take the full interval to scan.
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset stableAfter = now + _options.MinimumStableDuration;
        long waitStarted = timeProvider.GetTimestamp();
        TimeSpan maximumWait = _options.MinimumStableDuration + _options.ObservationInterval;
        while (now < stableAfter)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remainingBudget = maximumWait - timeProvider.GetElapsedTime(waitStarted);
            if (remainingBudget <= TimeSpan.Zero)
            {
                // A backward wall-clock adjustment must not hold startup open
                // indefinitely. The watcher will establish a fresh window.
                return [];
            }
            TimeSpan remaining = stableAfter - now;
            if (remaining > remainingBudget)
            {
                remaining = remainingBudget;
            }
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(1)
                    ? TimeSpan.FromMilliseconds(1)
                    : remaining,
                timeProvider, cancellationToken).ConfigureAwait(false);
            now = timeProvider.GetUtcNow();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ObserveExisting(now);
    }

    public IReadOnlyList<StackFileReadyCandidate> ObserveExisting(DateTimeOffset now)
    {
        var ready = new List<StackFileReadyCandidate>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = _options.IncludeSubdirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string path in Directory.EnumerateFiles(_options.FolderPath, "*", enumeration)
                     .Where(_tracker.ShouldConsider))
        {
            try
            {
                var file = new FileInfo(path);
                file.Refresh();
                if (!file.Exists || file.Length <= 0)
                {
                    continue;
                }
                StackFileReadyCandidate? candidate = _tracker.Observe(new StackFileObservation(
                    file.FullName, file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), now,
                    WindowsFileIdentity.TryGet(file.FullName)));
                if (candidate is not null)
                {
                    ready.Add(candidate);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An active writer or disappearing file belongs to the watcher.
            }
        }
        return ready.OrderBy(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsUnchanged(StackFileReadyCandidate candidate)
    {
        try
        {
            var file = new FileInfo(candidate.Path);
            file.Refresh();
            return file.Exists && file.Length == candidate.Length &&
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) == candidate.LastWriteTimeUtc &&
                string.Equals(WindowsFileIdentity.TryGet(candidate.Path), candidate.FileIdentity,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
