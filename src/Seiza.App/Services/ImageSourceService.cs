using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>Source-unit reads only. Display processing and calibration policy stay with their owners.</summary>
internal static class ImageSourceService
{
    private sealed record Stamp(long Length, long Modified, string? Identity);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (Stamp Stamp, ImageSourceHeader Header)> Headers = new(StringComparer.OrdinalIgnoreCase);

    private static Stamp Identity(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc.Ticks, WindowsFileIdentity.TryGet(path));
    }

    public static ImageSourceHeader ReadHeader(string path)
    {
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            Stamp before = Identity(path);
            if (Headers.TryGetValue(path, out var cached) && cached.Stamp == before) return cached.Header;
            nint result = NativeMethods.ReadImageHeaderJson(path, out nint error);
            if (result == 0) throw NativeString.TakeError(error, "Could not read the source header.");
            if (error != 0) NativeMethods.FreeString(error);
            var header = JsonSerializer.Deserialize(NativeString.TakeOwned(result, "Invalid image header."), ImageIoJsonContext.Default.ImageSourceHeader)
                ?? throw new SeizaCoreException("No source header was returned.");
            if (before != Identity(path)) throw new IOException("The image changed while its header was read. Read it again.");
            if (Headers.Count >= 256) Headers.Remove(Headers.Keys.First());
            Headers[path] = (before, header);
            return header;
        }
    }

    internal static ImageSourceHeader? TryReadHeader(string path)
    {
        if (!ImageFileTypes.IsAstronomyImage(path)) return null;
        try { return ReadHeader(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SeizaCoreException or JsonException) { return null; }
    }

    public static Task<IReadOnlyDictionary<string, string?>> ReadFiltersAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        string[] snapshot = paths.ToArray();
        return Task.Run<IReadOnlyDictionary<string, string?>>(() =>
        {
            var filters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in snapshot)
            {
                token.ThrowIfCancellationRequested();
                filters[path] = TryReadHeader(path)?.Text("FILTER");
            }
            token.ThrowIfCancellationRequested();
            return filters;
        }, token);
    }

    public static Task<ImageSourceWindow> ReadWindowAsync(string path, int x, int y, int width, int height,
        float normalizedFullScale = 0, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        ImageSourceWindow result = ReadWindow(path, x, y, width, height, normalizedFullScale);
        token.ThrowIfCancellationRequested();
        return result;
    }, token);

    internal static unsafe ImageSourceWindow ReadWindow(string path, int x, int y, int width, int height, float normalizedFullScale = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x); ArgumentOutOfRangeException.ThrowIfNegative(y);
        if (width is < 0 or > 1024 || height is < 0 or > 1024) throw new ArgumentOutOfRangeException(nameof(width), "A source window may be at most 1024 pixels on each side.");
        if (!float.IsFinite(normalizedFullScale) || normalizedFullScale < 0) throw new ArgumentOutOfRangeException(nameof(normalizedFullScale));
        var values = new float[checked(3 * width * height)];
        NativeImageWindow window;
        fixed (float* buffer = values)
        {
            if (!NativeMethods.ReadImageWindow(path, (nuint)x, (nuint)y, (nuint)width, (nuint)height,
                normalizedFullScale, buffer, (nuint)values.Length, out window, out nint error))
                throw NativeString.TakeError(error, "Source samples are unavailable without decoding the whole image.");
            if (error != 0) NativeMethods.FreeString(error);
        }
        int length = checked((int)(window.Planes * window.Width * window.Height));
        if (length > values.Length || window.Width > (nuint)width || window.Height > (nuint)height || window.Planes > 3)
            throw new SeizaCoreException("The source reader returned an invalid window.");
        Array.Resize(ref values, length);
        return new(checked((int)window.ImageWidth), checked((int)window.ImageHeight), checked((int)window.Planes),
            checked((int)window.X), checked((int)window.Y), checked((int)window.Width), checked((int)window.Height), values);
    }
}
