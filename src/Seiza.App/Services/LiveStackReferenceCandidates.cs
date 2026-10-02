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
