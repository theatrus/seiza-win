using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>Publishes a standalone master only after the native build has been verified.</summary>
internal static class CalibrationMasterExport
{
    public static async Task<CalibrationMasterBuildResult> BuildAsync(
        CalibrationMasterBuildRequest request, IReadOnlyList<string> protectedPaths,
        Func<CalibrationMasterBuildRequest, CancellationToken, Task<CalibrationMasterBuildResult>>? build = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string output = Path.GetFullPath(request.Output);
        string[] sources = protectedPaths.Concat(request.Inputs)
            .Concat(new[] { request.Bias, request.Dark }.OfType<string>()).ToArray();
        void ProtectSources()
        {
            if (sources.Any(path => StackSourceIdentity.RefersToSameFile(output, path)))
                throw new InvalidOperationException("The master output cannot replace an input image or calibration master.");
        }
        ProtectSources();
        string staging = Path.Combine(Path.GetDirectoryName(output)!, $".seiza-master-{Guid.NewGuid():N}.fits");
        try
        {
            CalibrationMasterBuildRequest staged = request with { Output = staging };
            CalibrationMasterBuildResult result = await (build ?? CalibrationService.BuildMasterAsync)(staged, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CalibrationPreparationService.ValidateBuildResult(result, staged, staging, request.Inputs.Count, 2);
            ProtectSources();
            token.ThrowIfCancellationRequested();
            File.Move(staging, output, overwrite: true);
            return result with { Output = output };
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

