using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Models;

namespace Seiza.App.Services;

// This small platform adapter uses seiza-download directly; no C ABI implementation is forked.
internal static unsafe partial class ParallaxCatalogService
{
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_distance_catalog_status_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint StatusJson(string? directory, out nint error);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_catalog_setup_parallax", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool Setup(string? directory, delegate* unmanaged[Cdecl]<nint, nint, void> callback, nint context, out nint error);
    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_string_free")]
    private static partial void Free(nint value);

    public static (string Directory, string Description) Describe(string? directory, double magnitude)
    {
        nint value = StatusJson(directory, out nint error);
        if (value == 0) throw ReadError(error);
        try
        {
            using JsonDocument json = JsonDocument.Parse(Marshal.PtrToStringUTF8(value)!);
            return ParallaxCatalogStatusPresentation.Format(json.RootElement, magnitude);
        }
        finally { Free(value); }
    }

    public static void Install(string? directory, Action<CatalogSetupProgress> progress)
    {
        GCHandle context = GCHandle.Alloc(progress);
        try
        {
            if (!Setup(directory, &Callback, GCHandle.ToIntPtr(context), out nint error)) throw ReadError(error);
        }
        finally { context.Free(); }
    }
    private static SeizaCoreException ReadError(nint error)
    {
        try { return new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Offline distance catalog operation failed."); }
        finally { if (error != 0) Free(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(nint json, nint context)
    {
        try
        {
            var progress = JsonSerializer.Deserialize(Marshal.PtrToStringUTF8(json)!, SeizaJsonSerializerContext.Default.CatalogSetupProgress);
            if (progress is not null && GCHandle.FromIntPtr(context).Target is Action<CatalogSetupProgress> action) action(progress);
        }
        catch { /* Never unwind across native callbacks. */ }
    }
}
