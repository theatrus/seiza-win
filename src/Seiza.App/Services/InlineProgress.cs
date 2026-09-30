namespace Seiza.App.Services;

/// <summary>
/// Reports on the calling thread. Native callbacks arrive on a worker
/// thread; the receiving action decides how to marshal the update.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
