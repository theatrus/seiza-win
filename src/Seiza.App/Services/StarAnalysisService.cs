using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed class StarAnalysisSourceChangedException(string path) : IOException(
    $"The image changed while its stars were being analyzed: {path}")
{
}

internal interface IStarAnalysisNativeClient
{
    string CoreVersion { get; }

    string DetectPath(string path, string optionsJson);
}

internal sealed class NativeStarAnalysisClient : IStarAnalysisNativeClient
{
    public string CoreVersion => SeizaCore.Version;

    public string DetectPath(string path, string optionsJson)
    {
        nint error = 0;
        nint json = NativeMethods.DetectStarsPathJson(path, optionsJson, out error);
        if (json == 0)
        {
            throw NativeString.TakeError(
                error,
                "The Seiza native core could not analyze stars in the image.");
        }

        if (error != 0)
        {
            NativeMethods.FreeString(error);
        }

        return NativeString.TakeOwned(
            json,
            "The Seiza native core returned an invalid star analysis response.");
    }
}

internal sealed class StarAnalysisService
{
    private const int DefaultCacheCapacity = 4;
    private static readonly SemaphoreSlim NativeGate = new(1, 1);

    private readonly IStarAnalysisNativeClient _nativeClient;
    private readonly SemaphoreSlim _nativeGate;
    private readonly int _cacheCapacity;
    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _lru = [];
    private readonly Dictionary<CacheKey, InflightEntry> _inflight = [];

    internal static StarAnalysisService Shared { get; } = new();

    internal StarAnalysisService()
        : this(new NativeStarAnalysisClient(), DefaultCacheCapacity, NativeGate)
    {
    }

    internal StarAnalysisService(
        IStarAnalysisNativeClient nativeClient,
        int cacheCapacity = DefaultCacheCapacity,
        SemaphoreSlim? nativeGate = null)
    {
        ArgumentNullException.ThrowIfNull(nativeClient);
        if (cacheCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheCapacity),
                "Cache capacity must be positive.");
        }

        _nativeClient = nativeClient;
        _cacheCapacity = cacheCapacity;
        _nativeGate = nativeGate ?? new SemaphoreSlim(1, 1);
    }

    internal async Task<StarAnalysisResult> AnalyzeAsync(
        string path,
        StarAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStamp source = ReadFileStamp(path);
        EnsureSupportedPath(source.FullPath);
        StarAnalysisOptions effectiveOptions = options ?? new StarAnalysisOptions();
        string optionsJson = effectiveOptions.ToJson();
        string coreVersion = _nativeClient.CoreVersion;
        if (string.IsNullOrWhiteSpace(coreVersion))
        {
            coreVersion = "unknown";
        }

        CacheKey key = CacheKey.Create(source, coreVersion, optionsJson);
        if (TryGetCached(key, out StarAnalysisResult? cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnchanged(source);
            return cached!;
        }

        cancellationToken.ThrowIfCancellationRequested();
        InflightEntry inflight = GetOrStartAnalysis(
            key,
            source,
            optionsJson,
            effectiveOptions.TriangleAngleDegrees);

        try
        {
            // WaitAsync cancels only this caller's wait. Once the native call
            // has begun it owns and frees its buffers synchronously on its
            // worker; the abandoned operation is allowed to finish without
            // unsafe interruption.
            StarAnalysisResult result = await inflight.Operation
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnchanged(source);
            return result;
        }
        finally
        {
            ReleaseWaiter(key, inflight);
        }
    }

    private InflightEntry GetOrStartAnalysis(
        CacheKey key,
        FileStamp source,
        string optionsJson,
        double? triangleAngleDegrees)
    {
        lock (_cacheLock)
        {
            if (_inflight.TryGetValue(key, out InflightEntry? existing))
            {
                existing.WaiterCount++;
                return existing;
            }

            var entry = new InflightEntry { WaiterCount = 1 };
            entry.Operation = AnalyzeAndCacheAsync(
                key,
                source,
                optionsJson,
                triangleAngleDegrees,
                entry);
            _inflight.Add(key, entry);
            _ = entry.Operation.ContinueWith(
                completed => CompleteInflight(key, entry, completed),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return entry;
        }
    }

    private async Task<StarAnalysisResult> AnalyzeAndCacheAsync(
        CacheKey key,
        FileStamp source,
        string optionsJson,
        double? triangleAngleDegrees,
        InflightEntry entry)
    {
        bool gateAcquired = false;
        try
        {
            await _nativeGate
                .WaitAsync(entry.QueueCancellation.Token)
                .ConfigureAwait(false);
            gateAcquired = true;

            lock (_cacheLock)
            {
                // A cancellation can race the semaphore grant. Linearize the
                // decision here: with no waiters, skip before native starts;
                // otherwise no later waiter cancellation may interrupt it.
                if (entry.QueueAbandoned)
                {
                    throw new OperationCanceledException(entry.QueueCancellation.Token);
                }

                entry.NativeStarted = true;
            }

            EnsureUnchanged(source);
            StarAnalysisResult result = await Task.Run(
                    () => DetectAndValidate(
                        source,
                        optionsJson,
                        triangleAngleDegrees))
                .ConfigureAwait(false);
            EnsureUnchanged(source);
            AddCached(key, result);
            return result;
        }
        finally
        {
            if (gateAcquired)
            {
                _nativeGate.Release();
            }
        }
    }

    private void CompleteInflight(
        CacheKey key,
        InflightEntry entry,
        Task<StarAnalysisResult> completed)
    {
        lock (_cacheLock)
        {
            if (_inflight.TryGetValue(key, out InflightEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _inflight.Remove(key);
            }
        }

        // A canceled UI wait does not cancel the native worker. Observe any
        // eventual failure so an abandoned operation cannot raise an
        // UnobservedTaskException later.
        _ = completed.Exception;
        entry.QueueCancellation.Dispose();
    }

    private void ReleaseWaiter(CacheKey key, InflightEntry entry)
    {
        lock (_cacheLock)
        {
            if (entry.WaiterCount <= 0)
            {
                return;
            }

            entry.WaiterCount--;
            if (entry.WaiterCount == 0 &&
                !entry.NativeStarted &&
                !entry.Operation.IsCompleted)
            {
                if (_inflight.TryGetValue(key, out InflightEntry? current) &&
                    ReferenceEquals(current, entry))
                {
                    _inflight.Remove(key);
                }

                entry.QueueAbandoned = true;
                // The token is observed only by SemaphoreSlim.WaitAsync. It
                // is never passed to native code or to the worker after the
                // gate. The completion cleanup is scheduled asynchronously,
                // so it cannot dispose this source during Cancel().
                entry.QueueCancellation.Cancel();
            }
        }
    }

    private StarAnalysisResult DetectAndValidate(
        FileStamp source,
        string optionsJson,
        double? triangleAngleDegrees)
    {
        string json = _nativeClient.DetectPath(source.FullPath, optionsJson);
        StarAnalysisResult result;
        try
        {
            result = JsonSerializer.Deserialize(
                json,
                SeizaJsonSerializerContext.Default.StarAnalysisResult)
                ?? throw new JsonException("The response was JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Seiza core returned malformed star analysis JSON.",
                exception);
        }

        result.Validate();
        StarAnalysisValidator.ValidateTriangleRequest(result, triangleAngleDegrees);
        EnsureUnchanged(source);
        return result;
    }

    private bool TryGetCached(CacheKey key, out StarAnalysisResult? result)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                result = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            result = node.Value.Result;
            return true;
        }
    }

    private void AddCached(CacheKey key, StarAnalysisResult result)
    {
        lock (_cacheLock)
        {
            if (_cache.Remove(key, out LinkedListNode<CacheEntry>? existing))
            {
                _lru.Remove(existing);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, result));
            _lru.AddFirst(node);
            _cache.Add(key, node);
            while (_cache.Count > _cacheCapacity)
            {
                LinkedListNode<CacheEntry> oldest = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Key);
            }
        }
    }

    private static FileStamp ReadFileStamp(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The image to analyze does not exist.", fullPath);
        }

        return new FileStamp(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static void EnsureUnchanged(FileStamp expected)
    {
        FileStamp actual;
        try
        {
            actual = ReadFileStamp(expected.FullPath);
        }
        catch (FileNotFoundException)
        {
            throw new StarAnalysisSourceChangedException(expected.FullPath);
        }

        if (actual.Length != expected.Length ||
            actual.LastWriteUtcTicks != expected.LastWriteUtcTicks)
        {
            throw new StarAnalysisSourceChangedException(expected.FullPath);
        }
    }

    private static void EnsureSupportedPath(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not
            (".fits" or ".fit" or ".fts" or ".xisf"))
        {
            throw new NotSupportedException(
                "Star analysis currently supports FITS and XISF images.");
        }
    }

    private readonly record struct FileStamp(
        string FullPath,
        long Length,
        long LastWriteUtcTicks);

    private readonly record struct CacheKey(
        string NormalizedPath,
        long Length,
        long LastWriteUtcTicks,
        string CoreVersion,
        string OptionsJson)
    {
        internal static CacheKey Create(
            FileStamp source,
            string coreVersion,
            string optionsJson) => new(
                source.FullPath.ToUpperInvariant(),
                source.Length,
                source.LastWriteUtcTicks,
                coreVersion,
                optionsJson);
    }

    private sealed record CacheEntry(CacheKey Key, StarAnalysisResult Result);

    private sealed class InflightEntry
    {
        internal CancellationTokenSource QueueCancellation { get; } = new();

        internal Task<StarAnalysisResult> Operation { get; set; } = null!;

        internal int WaiterCount { get; set; }

        internal bool NativeStarted { get; set; }

        internal bool QueueAbandoned { get; set; }
    }
}
