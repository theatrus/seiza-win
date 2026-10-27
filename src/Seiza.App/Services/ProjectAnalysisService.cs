using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Seiza.App.Interop;

namespace Seiza.App.Services;

internal sealed record ProjectAnalysisReport(string HtmlPath, int MeasuredFrames, int TotalFrames);

internal static class ProjectAnalysisService
{
    internal static Task<ProjectAnalysisReport> AnalyzeAsync(
        IReadOnlyList<string> inputs, string outputDirectory,
        IProgress<ProjectAnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string[] snapshot = inputs.ToArray();
        if (snapshot.Length == 0)
            throw new ArgumentException("Open a folder or image list containing FITS or XISF images first.", nameof(inputs));

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string request = JsonSerializer.Serialize(new { inputs = snapshot });
            Exception? callbackFailure = null;
            NativeMethods.ProjectAnalysisCallback callback = (pointer, _) =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested) return 0;
                    progress?.Report(ProjectAnalysisProgress.Parse(Marshal.PtrToStringUTF8(pointer)!));
                    return cancellationToken.IsCancellationRequested ? 0 : 1;
                }
                catch (Exception exception)
                {
                    callbackFailure = exception;
                    return 0; // Never let managed exceptions unwind through Rust.
                }
            };
            string json;
            nint value = 0, error = 0;
            try
            {
                value = NativeMethods.AnalyzeProjectWithProgress(request, callback, 0, out error);
                cancellationToken.ThrowIfCancellationRequested();
                if (callbackFailure is not null) throw new InvalidOperationException("Could not read analysis progress.", callbackFailure);
                if (value == 0) throw new SeizaCoreException(Marshal.PtrToStringUTF8(error) ?? "Project analysis failed.");
                json = Marshal.PtrToStringUTF8(value) ?? throw new InvalidDataException("Empty analysis result.");
            }
            finally
            {
                GC.KeepAlive(callback);
                if (value != 0) NativeMethods.FreeProjectAnalysisString(value);
                if (error != 0) NativeMethods.FreeProjectAnalysisString(error);
            }
            using JsonDocument document = JsonDocument.Parse(json);
            int measured = document.RootElement.GetProperty("measured").GetInt32();
            int total = document.RootElement.GetProperty("total").GetInt32();
            string html = document.RootElement.GetProperty("html").GetString()!;
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            string path = Path.Combine(outputDirectory, $"seiza-analysis-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");
            AtomicOutputFile.Write(path,
                staging => File.WriteAllText(staging, html, new UTF8Encoding(false)),
                cancellationToken);
            return new ProjectAnalysisReport(path, measured, total);
        }, cancellationToken);
    }
}
