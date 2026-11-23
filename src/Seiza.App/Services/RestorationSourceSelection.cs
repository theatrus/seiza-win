namespace Seiza.App.Services;

/// <summary>Keeps browser source changes until an in-flight operation releases its image.</summary>
internal sealed class RestorationSourceSelection(string? source)
{
    private string? _pendingSource;
    private bool _hasPendingSource;

    internal string? Path { get; private set; } = source;
    internal bool CanProcess => Path is not null && ImageFileTypes.IsAstronomyImage(Path);

    internal bool Request(string? source, bool busy)
    {
        if (busy)
        {
            _pendingSource = source;
            _hasPendingSource = true;
            return false;
        }
        return Select(source);
    }

    internal bool ApplyPending()
    {
        if (!_hasPendingSource) return false;
        return Select(_pendingSource);
    }

    internal bool Select(string? source)
    {
        _hasPendingSource = false;
        _pendingSource = null;
        if (string.Equals(source, Path, StringComparison.OrdinalIgnoreCase)) return false;
        Path = source;
        return true;
    }
}
