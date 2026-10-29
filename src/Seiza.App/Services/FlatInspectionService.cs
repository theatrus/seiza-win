using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed record FlatInspection(CalibrationFrameProbe Probe, FlatQualityMeasurement Measurement, FlatVerdict? Verdict);
internal static class FlatInspectionService
{
    public static async Task<FlatInspection> InspectAsync(string path, FlatQualityOptions options, CancellationToken token = default)
    {
        options = options with { Enabled = true };
        options.Validate();
        return await Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var before = Identity(path);
            CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(path, token).ConfigureAwait(false);
            if (probe.Role != CalibrationFrameRoles.Flat)
                throw new ArgumentException("Flat inspection requires a flat identified by FITS/XISF metadata.");
            FlatQualityMeasurement measured = FlatQualityService.MeasureFrame(new(path, options.FullScale, true,
                options.UseOptics ? options.FocalRatio : null, options.UseOptics ? options.PixelSizeUm : null,
                options.UseOptics ? options.Obstruction : null));
            token.ThrowIfCancellationRequested();
            bool raw = !probe.IsMaster && !probe.CalibrationState.BiasSubtracted &&
                !probe.CalibrationState.DarkSubtracted && !probe.CalibrationState.FlatNormalized;
            FlatVerdict? verdict = raw ? FlatQualityService.ScreenFrames([measured]).Frames[0] : null;
            if (before != Identity(path)) throw new IOException("The flat changed during inspection. Inspect it again.");
            return new FlatInspection(probe, measured, verdict);
        }, token).ConfigureAwait(false);
    }
    private static (long, DateTime, string?) Identity(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc, WindowsFileIdentity.TryGet(path));
    }
}
