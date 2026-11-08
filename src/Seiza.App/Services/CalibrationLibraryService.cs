using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static partial class CalibrationLibraryService
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeProgress(nint phase, nuint completed, nuint total, nint context);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_files_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CallNative(string request, SafeCancelSignalHandle signal,
        nint progress, nint context, out nint error);

    public static Task<LibraryReview> PerformAsync(LibraryRequest request,
        IProgress<LibraryProgress>? progress = null, CancellationToken token = default)
    {
        // Capture caller-owned mutable JSON before queueing work.
        string json = JsonSerializer.Serialize(request, LibraryJsonContext.Default.LibraryRequest);
        return Task.Run(() =>
        {
            LibraryReview result = JsonSerializer.Deserialize(Call(json, progress, token), LibraryJsonContext.Default.LibraryReview)
                ?? throw new SeizaCoreException("No calibration review was returned.");
            result.Validate(request);
            return result;
        }, token);
    }

    public static Task<LibrarySchema> SchemaAsync(CancellationToken token = default) => Task.Run(() =>
        JsonSerializer.Deserialize(Call("{\"operation\":\"schema\"}", null, token), LibraryJsonContext.Default.LibrarySchema)
            ?? throw new SeizaCoreException("No native calibration settings were returned."), token);

    public static void Verify(LibrarySource[] sources)
    {
        string request = JsonSerializer.Serialize(new LibraryRequest { Operation = "verify", Sources = sources },
            LibraryJsonContext.Default.LibraryRequest);
        using JsonDocument result = JsonDocument.Parse(Call(request, null, default));
        if (!result.RootElement.GetProperty("valid").GetBoolean()) throw new IOException("Calibration sources changed. Analyze again.");
    }

    private static string Call(string request, IProgress<LibraryProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var signal = new SafeCancelSignalHandle(NativeMethods.CreateCancelSignal());
        if (signal.IsInvalid) throw new SeizaCoreException("Could not create the calibration cancellation signal.");
        using var registration = token.UnsafeRegister(static s => ((SafeCancelSignalHandle)s!).Cancel(), signal);
        // Never let managed callback exceptions unwind through Rust.
        NativeProgress callback = (phase, completed, total, _) =>
        {
            try { progress?.Report(new(Marshal.PtrToStringUTF8(phase) ?? "preparing", completed, total)); }
            catch { signal.Cancel(); }
        };
        nint result = CallNative(request, signal, Marshal.GetFunctionPointerForDelegate(callback), 0, out nint error);
        GC.KeepAlive(callback);
        string? response = result == 0 ? null : NativeString.TakeOwned(result, "Invalid calibration response.");
        Exception? failure = error == 0 ? null : NativeString.TakeError(error, "Calibration preparation failed.");
        token.ThrowIfCancellationRequested();
        return response ?? throw failure ?? new SeizaCoreException("Calibration preparation failed.");
    }
}
