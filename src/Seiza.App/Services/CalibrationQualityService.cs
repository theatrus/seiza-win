using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>Native measurement/screening only; callers must supply compatible grading peers.</summary>
internal static partial class CalibrationQualityService
{
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_measure_dark_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Measure(string request, out nint error);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_screen_bias_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ScreenBias(string request, out nint error);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_screen_darks_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ScreenDarks(string request, out nint error);

    internal static DarkQualityMeasurement MeasureFrame(DarkQualityMeasureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CalibrationCoreCapabilities.Current.RequireBiasDarkQuality();
        string json = JsonSerializer.Serialize(request, CalibrationQualityJsonContext.Default.DarkQualityMeasureRequest);
        var type = CalibrationQualityJsonContext.Default.DarkQualityMeasurement;
        nint response = Measure(json, out nint error);
        return Read(response, error, type);
    }

    internal static CalibrationQualityReport ScreenBiasFrames(DarkQualityMeasurement[] measurements) =>
        Screen(measurements, bias: true);

    internal static CalibrationQualityReport ScreenDarkFrames(DarkQualityMeasurement[] measurements) =>
        Screen(measurements, bias: false);

    private static CalibrationQualityReport Screen(DarkQualityMeasurement[] measurements, bool bias)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        CalibrationCoreCapabilities.Current.RequireBiasDarkQuality();
        string json = JsonSerializer.Serialize(new CalibrationQualityScreenRequest(measurements),
            CalibrationQualityJsonContext.Default.CalibrationQualityScreenRequest);
        var type = CalibrationQualityJsonContext.Default.CalibrationQualityReport;
        nint response = bias ? ScreenBias(json, out nint error) : ScreenDarks(json, out error);
        var report = Read(response, error, type);
        if (report.SchemaVersion != 1 || report.Frames is null || report.Frames.Length != measurements.Length ||
            report.Frames.Any(verdict => verdict is null || verdict.Grade is not ("ok" or "suspect" or "poor") ||
                verdict.Findings is null || verdict.Findings.Any(finding => finding is null ||
                    string.IsNullOrEmpty(finding.Code) || finding.Grade is not ("ok" or "suspect" or "poor"))))
            throw new SeizaCoreException("The native calibration quality report does not describe the complete peer set.");
        return report;
    }

    private static T Read<T>(nint response, nint error, JsonTypeInfo<T> type)
    {
        if (response == 0) throw NativeString.TakeError(error, "Calibration quality screening failed.");
        if (error != 0) NativeMethods.FreeString(error);
        return JsonSerializer.Deserialize(NativeString.TakeOwned(response, ""), type)
            ?? throw new SeizaCoreException("Invalid native calibration quality response.");
    }
}
