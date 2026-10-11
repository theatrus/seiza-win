namespace Seiza.App.Services;

// Called on the UI thread. All independent windows retain process-wide services,
// not only document windows that participate in file-activation routing.
internal sealed class WindowLifetimeRegistry<T>(Action lastWindowClosed) where T : class
{
    private readonly HashSet<T> _windows = new(ReferenceEqualityComparer.Instance);

    public int Count => _windows.Count;

    public bool Add(T window) => _windows.Add(window);

    public bool Remove(T window)
    {
        if (!_windows.Remove(window)) return false;
        if (_windows.Count == 0) lastWindowClosed();
        return true;
    }
}
