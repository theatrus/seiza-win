namespace Seiza.App.Services;

/// <summary>Bounded LRU for expensive native results. Its owner serializes access.</summary>
internal sealed class ProcessingResultCache<TKey, TValue>(int capacity, long budget) : IDisposable
    where TKey : notnull where TValue : class, IDisposable
{
    private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> _entries = new();
    private long _bytes;

    internal TValue? Find(TKey key)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (!EqualityComparer<TKey>.Default.Equals(node.Value.Key, key)) continue;
            _entries.Remove(node);
            _entries.AddFirst(node);
            return node.Value.Value;
        }
        return null;
    }

    // False leaves ownership with the caller, including oversized full-image runs.
    internal bool Add(TKey key, TValue value, long bytes)
    {
        if (bytes <= 0 || bytes > budget || capacity <= 0) return false;
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (EqualityComparer<TKey>.Default.Equals(node.Value.Key, key)) Remove(node);
            node = next;
        }
        while (_entries.Count >= capacity || _bytes > budget - bytes) Remove(_entries.Last!);
        _entries.AddFirst((key, value, bytes));
        _bytes += bytes;
        return true;
    }

    private void Remove(LinkedListNode<(TKey Key, TValue Value, long Bytes)> node)
    {
        _entries.Remove(node);
        _bytes -= node.Value.Bytes;
        node.Value.Value.Dispose();
    }

    public void Dispose()
    {
        while (_entries.Last is { } last) Remove(last);
    }
}
