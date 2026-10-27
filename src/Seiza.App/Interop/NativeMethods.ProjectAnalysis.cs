using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProjectAnalysisCallback(nint json, nint context);

    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_analyze_project_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AnalyzeProjectWithProgress(
        string requestJson,
        ProjectAnalysisCallback callback, nint context, out nint error);

    [LibraryImport("seiza_platform", EntryPoint = "seiza_win_string_free")]
    internal static partial void FreeProjectAnalysisString(nint value);
    [LibraryImport(LibraryName, EntryPoint = "seiza_analyze_project_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AnalyzeProjectJson(string requestJson, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_analysis_html", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AnalysisHtml(string reportJson, out nint error);
}
