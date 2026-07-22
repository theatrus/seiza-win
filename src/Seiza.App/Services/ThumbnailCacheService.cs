using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

internal static class ThumbnailCacheService
{
    public const uint MaximumDimension = 320;

    private const long MemoryLimit = 128L * 1_024 * 1_024;
    private const int MemoryCountLimit = 256;
    private const long DiskLimit = 256L * 1_024 * 1_024;
    private const long DiskTarget = 192L * 1_024 * 1_024;
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Seiza",
        "Cache",
        "Thumbnails");
    private static readonly object MemoryLock = new();
    private static readonly Dictionary<string, MemoryEntry> Memory = [];
    private static readonly LinkedList<string> MemoryOrder = [];
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> InFlight = new();
    private static long _memoryCost;
    private static int _pruneRunning;

    public static async Task<byte[]?> GetAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string key = CreateKey(path);
        if (TryGetMemory(key, out byte[]? cached))
        {
            return cached;
        }

        string cachePath = Path.Combine(CacheDirectory, $"{key}.png");
        if (File.Exists(cachePath))
        {
            try
            {
                byte[] disk = await File.ReadAllBytesAsync(cachePath, cancellationToken);
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                AddMemory(key, disk);
                return disk;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                TryDelete(cachePath);
            }
        }

        Lazy<Task<byte[]?>> job = InFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<byte[]?>>(
                () => RenderAndStoreAsync(path, key),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await job.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (job.IsValueCreated && job.Value.IsCompleted)
            {
                InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]?>>>(key, job));
            }
        }
    }

    private static async Task<byte[]?> RenderAndStoreAsync(string path, string key)
    {
        try
        {
            Models.RenderedImageData rendered = await ImageRenderService.RenderAsync(
                path,
                maxDimension: MaximumDimension);
            using var encoded = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                (uint)rendered.Width,
                (uint)rendered.Height,
                96,
                96,
                rendered.Bgra);
            await encoder.FlushAsync();
            encoded.Seek(0);

            using var memory = new MemoryStream((int)encoded.Size);
            using Stream source = encoded.AsStreamForRead();
            await source.CopyToAsync(memory);
            byte[] png = memory.ToArray();
            AddMemory(key, png);

            Directory.CreateDirectory(CacheDirectory);
            string cachePath = Path.Combine(CacheDirectory, $"{key}.png");
            string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, png);
                File.Move(temporaryPath, cachePath, true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            ScheduleDiskPrune();
            return png;
        }
        catch
        {
            return null;
        }
    }

    private static string CreateKey(string path)
    {
        var file = new FileInfo(path);
        string signature = string.Join(
            '\n',
            Path.GetFullPath(path).ToUpperInvariant(),
            file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
            $"thumbnail-v1-{MaximumDimension}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))
            .ToLowerInvariant();
    }

    private static bool TryGetMemory(string key, out byte[]? bytes)
    {
        lock (MemoryLock)
        {
            if (!Memory.TryGetValue(key, out MemoryEntry? entry))
            {
                bytes = null;
                return false;
            }

            MemoryOrder.Remove(entry.Node);
            MemoryOrder.AddLast(entry.Node);
            bytes = entry.Bytes;
            return true;
        }
    }

    private static void AddMemory(string key, byte[] bytes)
    {
        lock (MemoryLock)
        {
            if (Memory.Remove(key, out MemoryEntry? previous))
            {
                MemoryOrder.Remove(previous.Node);
                _memoryCost -= previous.Bytes.LongLength;
            }

            LinkedListNode<string> node = MemoryOrder.AddLast(key);
            Memory[key] = new MemoryEntry(bytes, node);
            _memoryCost += bytes.LongLength;
            while ((_memoryCost > MemoryLimit || Memory.Count > MemoryCountLimit) &&
                   MemoryOrder.First is not null)
            {
                string oldestKey = MemoryOrder.First.Value;
                MemoryOrder.RemoveFirst();
                if (Memory.Remove(oldestKey, out MemoryEntry? oldest))
                {
                    _memoryCost -= oldest.Bytes.LongLength;
                }
            }
        }
    }

    private static void ScheduleDiskPrune()
    {
        if (Interlocked.Exchange(ref _pruneRunning, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                PruneDiskCache();
            }
            finally
            {
                Interlocked.Exchange(ref _pruneRunning, 0);
            }
        });
    }

    private static void PruneDiskCache()
    {
        if (!Directory.Exists(CacheDirectory))
        {
            return;
        }

        FileInfo[] entries = new DirectoryInfo(CacheDirectory)
            .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();
        long total = entries.Sum(file => file.Length);
        if (total <= DiskLimit)
        {
            return;
        }

        foreach (FileInfo entry in entries)
        {
            if (total <= DiskTarget)
            {
                break;
            }

            long length = entry.Length;
            if (TryDelete(entry.FullName))
            {
                total -= length;
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record MemoryEntry(byte[] Bytes, LinkedListNode<string> Node);
}
