using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class CalibrationService
{
    public static Task<CalibrationFrameProbe> ProbeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return Task.Run(() =>
        {
            nint error = 0;
            nint response = NativeMethods.ProbeFrameJson(fullPath, out error);
            return ReadJson(
                response,
                error,
                SeizaJsonSerializerContext.Default.CalibrationFrameProbe,
                "The Seiza core could not inspect the frame header.");
        }, cancellationToken);
    }

    public static Task<CalibrationPlanResult> PlanAsync(
        CalibrationPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Minimum < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A calibration plan needs at least one selected frame.");
        }

        string requestJson = JsonSerializer.Serialize(
            request,
            SeizaJsonSerializerContext.Default.CalibrationPlanRequest);
        return Task.Run(() =>
        {
            nint error = 0;
            nint response = NativeMethods.PlanCalibrationJson(requestJson, out error);
            return ReadJson(
                response,
                error,
                SeizaJsonSerializerContext.Default.CalibrationPlanResult,
                "The Seiza core could not plan the calibration master.");
        }, cancellationToken);
    }

    public static async Task<CalibrationMasterBuildResult> BuildMasterAsync(
        CalibrationMasterBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBuildRequest(request);
        string requestJson = JsonSerializer.Serialize(
            request,
            SeizaJsonSerializerContext.Default.CalibrationMasterBuildRequest);

        nint rawSignal = NativeMethods.CreateCancelSignal();
        if (rawSignal == 0)
        {
            throw new SeizaCoreException(
                "The Seiza core could not create a calibration cancellation signal.");
        }
        using var signal = new SafeCancelSignalHandle(rawSignal);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((SafeCancelSignalHandle)state!).Cancel(),
            signal);

        return await Task.Run(() =>
        {
            nint error = 0;
            nint response = NativeMethods.BuildCalibrationMasterJson(
                requestJson,
                signal.DangerousGetHandle(),
                out error);
            if (response == 0 && cancellationToken.IsCancellationRequested)
            {
                if (error != 0)
                {
                    _ = NativeString.TakeOwned(error, string.Empty);
                }
                throw new OperationCanceledException(cancellationToken);
            }
            return ReadJson(
                response,
                error,
                SeizaJsonSerializerContext.Default.CalibrationMasterBuildResult,
                "The Seiza core could not build the calibration master.");
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static T ReadJson<T>(
        nint response,
        nint error,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        string fallback)
    {
        if (response == 0)
        {
            throw NativeString.TakeError(error, fallback);
        }
        if (error != 0)
        {
            _ = NativeString.TakeOwned(error, string.Empty);
        }

        string json = NativeString.TakeOwned(response, string.Empty);
        return JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new SeizaCoreException("The Seiza core returned invalid calibration JSON.");
    }

    private static void ValidateBuildRequest(CalibrationMasterBuildRequest request)
    {
        if (request.Kind is not ("bias" or "dark" or "flat"))
        {
            throw new ArgumentException("Choose a bias, dark, or flat master kind.", nameof(request));
        }
        if (request.Inputs.Count < 2)
        {
            throw new ArgumentException(
                "At least two calibration frames are required.",
                nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Output))
        {
            throw new ArgumentException("A master output path is required.", nameof(request));
        }

        string[] inputs = request.Inputs.Select(Path.GetFullPath).ToArray();
        if (inputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length)
        {
            throw new ArgumentException("Calibration inputs must be unique.", nameof(request));
        }
        string output = Path.GetFullPath(request.Output);
        if (inputs.Contains(output, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The master output cannot replace one of its inputs.",
                nameof(request));
        }
    }
}
