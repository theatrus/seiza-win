using System.Runtime.InteropServices;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal readonly record struct CalibrationMatchTolerances(
    double ExposureSeconds,
    double ExposureFraction,
    double DarkTemperatureC,
    double MasterTemperatureC,
    double RotationDeg,
    double FocalLengthMm,
    ulong FlatSessionSeconds);

/// <summary>
/// The managed entry point for Seiza's calibration matching policy. Keep
/// calibration decisions here instead of translating its tolerances or
/// asymmetric unknown-field rules into C#.
/// </summary>
internal static class CalibrationMatchingService
{
    private const uint FrameHasWidth = 1 << 0;
    private const uint FrameHasHeight = 1 << 1;
    private const uint FrameHasChannels = 1 << 2;
    private const uint FrameHasBinningX = 1 << 3;
    private const uint FrameHasBinningY = 1 << 4;
    private const uint FrameHasGain = 1 << 5;
    private const uint FrameHasOffset = 1 << 6;
    private const uint FrameHasReadoutMode = 1 << 7;
    private const uint FrameHasFocalLength = 1 << 8;
    private const uint FrameHasRotation = 1 << 9;
    private const uint FrameHasExposure = 1 << 10;
    private const uint FrameHasCameraTemperature = 1 << 11;
    private const uint FrameHasCapturedAt = 1 << 12;

    private const uint ToleranceHasExposure = 1 << 0;
    private const uint ToleranceHasDarkTemperature = 1 << 1;
    private const uint ToleranceHasMasterTemperature = 1 << 2;
    private const uint ToleranceHasRotation = 1 << 3;
    private const uint ToleranceHasFocalLength = 1 << 4;
    private const uint ToleranceHasFlatSession = 1 << 5;
    private const uint ToleranceHasExposureFraction = 1 << 6;
    private const uint AllToleranceFields =
        ToleranceHasExposure |
        ToleranceHasDarkTemperature |
        ToleranceHasMasterTemperature |
        ToleranceHasRotation |
        ToleranceHasFocalLength |
        ToleranceHasFlatSession |
        ToleranceHasExposureFraction;

    public static CalibrationMatchTolerances GetDefaultTolerances()
    {
        NativeMethods.GetDefaultMatchTolerances(out NativeMatchTolerances native);
        return FromNative(native);
    }

    public static bool SensorMatches(
        CalibrationFrameSignature reference,
        CalibrationFrameSignature candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        using var nativeReference = new NativeFrameSignatureLease(reference);
        using var nativeCandidate = new NativeFrameSignatureLease(candidate);
        nint error = 0;
        int result = NativeMethods.CalibrationSensorMatches(
            nativeReference.Value,
            nativeCandidate.Value,
            out error);
        return ReadMatchResult(
            result,
            error,
            "The Seiza core could not compare calibration sensor settings.");
    }

    public static bool OpticsMatch(
        CalibrationFrameSignature reference,
        CalibrationFrameSignature candidate,
        CalibrationMatchTolerances? tolerances = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        using var nativeReference = new NativeFrameSignatureLease(reference);
        using var nativeCandidate = new NativeFrameSignatureLease(candidate);
        NativeMatchTolerances nativeTolerances = ToNative(
            tolerances ?? GetDefaultTolerances());
        nint error = 0;
        int result = NativeMethods.CalibrationOpticsMatch(
            nativeReference.Value,
            nativeCandidate.Value,
            nativeTolerances,
            out error);
        return ReadMatchResult(
            result,
            error,
            "The Seiza core could not compare calibration optics.");
    }

    public static bool DarkMatches(
        CalibrationFrameSignature reference,
        CalibrationFrameSignature candidate,
        CalibrationMatchTolerances? tolerances = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        using var nativeReference = new NativeFrameSignatureLease(reference);
        using var nativeCandidate = new NativeFrameSignatureLease(candidate);
        NativeMatchTolerances nativeTolerances = ToNative(
            tolerances ?? GetDefaultTolerances());
        nint error = 0;
        int result = NativeMethods.CalibrationDarkMatches(
            nativeReference.Value,
            nativeCandidate.Value,
            nativeTolerances,
            out error);
        return ReadMatchResult(
            result,
            error,
            "The Seiza core could not compare dark exposure and temperature.");
    }

    public static string DescribeSensorMismatch(
        CalibrationFrameSignature reference,
        CalibrationFrameSignature candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        using var nativeReference = new NativeFrameSignatureLease(reference);
        using var nativeCandidate = new NativeFrameSignatureLease(candidate);
        nint error = 0;
        nint description = NativeMethods.CalibrationDescribeSensorMismatch(
            nativeReference.Value,
            nativeCandidate.Value,
            out error);
        return ReadDescription(
            description,
            error,
            "The Seiza core could not describe the sensor mismatch.");
    }

    public static string DescribeOpticsMismatch(
        CalibrationFrameSignature reference,
        CalibrationFrameSignature candidate,
        CalibrationMatchTolerances? tolerances = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        using var nativeReference = new NativeFrameSignatureLease(reference);
        using var nativeCandidate = new NativeFrameSignatureLease(candidate);
        NativeMatchTolerances nativeTolerances = ToNative(
            tolerances ?? GetDefaultTolerances());
        nint error = 0;
        nint description = NativeMethods.CalibrationDescribeOpticsMismatch(
            nativeReference.Value,
            nativeCandidate.Value,
            nativeTolerances,
            out error);
        return ReadDescription(
            description,
            error,
            "The Seiza core could not describe the optics mismatch.");
    }

    private static bool ReadMatchResult(int result, nint error, string fallback)
    {
        if (result < 0)
        {
            throw NativeString.TakeError(error, fallback);
        }
        if (error != 0)
        {
            _ = NativeString.TakeOwned(error, string.Empty);
        }
        return result switch
        {
            0 => false,
            1 => true,
            _ => throw new SeizaCoreException(
                "The Seiza core returned an invalid calibration match result."),
        };
    }

    private static string ReadDescription(nint description, nint error, string fallback)
    {
        if (description == 0)
        {
            throw NativeString.TakeError(error, fallback);
        }

        string value = NativeString.TakeOwned(description, fallback);
        if (error != 0)
        {
            throw NativeString.TakeError(error, value);
        }
        return value;
    }

    private static CalibrationMatchTolerances FromNative(NativeMatchTolerances value) =>
        new(
            value.ExposureSeconds,
            value.ExposureFraction,
            value.DarkTemperatureC,
            value.MasterTemperatureC,
            value.RotationDeg,
            value.FocalLengthMm,
            value.FlatSessionSeconds);

    private static NativeMatchTolerances ToNative(CalibrationMatchTolerances value) => new()
    {
        Known = AllToleranceFields,
        ExposureSeconds = value.ExposureSeconds,
        ExposureFraction = value.ExposureFraction,
        DarkTemperatureC = value.DarkTemperatureC,
        MasterTemperatureC = value.MasterTemperatureC,
        RotationDeg = value.RotationDeg,
        FocalLengthMm = value.FocalLengthMm,
        FlatSessionSeconds = value.FlatSessionSeconds,
    };

    private sealed class NativeFrameSignatureLease : IDisposable
    {
        private readonly nint[] _strings = new nint[4];

        public NativeFrameSignatureLease(CalibrationFrameSignature signature)
        {
            try
            {
                _strings[0] = Utf8(signature.Camera);
                _strings[1] = Utf8(signature.Telescope);
                _strings[2] = Utf8(signature.BayerPattern);
                _strings[3] = Utf8(signature.Filter);

                var value = new NativeFrameSignature
                {
                    Camera = _strings[0],
                    Telescope = _strings[1],
                    BayerPattern = _strings[2],
                    Filter = _strings[3],
                };
                Set(signature.Width, FrameHasWidth, ref value.Known, ref value.Width);
                Set(signature.Height, FrameHasHeight, ref value.Known, ref value.Height);
                Set(signature.Channels, FrameHasChannels, ref value.Known, ref value.Channels);
                Set(signature.BinningX, FrameHasBinningX, ref value.Known, ref value.BinningX);
                Set(signature.BinningY, FrameHasBinningY, ref value.Known, ref value.BinningY);
                Set(signature.Gain, FrameHasGain, ref value.Known, ref value.Gain);
                Set(signature.Offset, FrameHasOffset, ref value.Known, ref value.Offset);
                Set(
                    signature.ReadoutMode,
                    FrameHasReadoutMode,
                    ref value.Known,
                    ref value.ReadoutMode);
                Set(
                    signature.FocalLengthMm,
                    FrameHasFocalLength,
                    ref value.Known,
                    ref value.FocalLengthMm);
                Set(
                    signature.RotationDeg,
                    FrameHasRotation,
                    ref value.Known,
                    ref value.RotationDeg);
                Set(
                    signature.ExposureSeconds,
                    FrameHasExposure,
                    ref value.Known,
                    ref value.ExposureSeconds);
                Set(
                    signature.CameraTempC,
                    FrameHasCameraTemperature,
                    ref value.Known,
                    ref value.CameraTempC);
                Set(
                    signature.CapturedAtUnix,
                    FrameHasCapturedAt,
                    ref value.Known,
                    ref value.CapturedAtUnix);
                Value = value;
            }
            catch
            {
                FreeStrings();
                throw;
            }
        }

        public NativeFrameSignature Value { get; }

        public void Dispose() => FreeStrings();

        private void FreeStrings()
        {
            for (int index = 0; index < _strings.Length; index++)
            {
                nint value = _strings[index];
                if (value != 0)
                {
                    Marshal.FreeCoTaskMem(value);
                    _strings[index] = 0;
                }
            }
        }

        private static nint Utf8(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? 0
                : Marshal.StringToCoTaskMemUTF8(value);

        private static void Set(
            long? source,
            uint flag,
            ref uint known,
            ref double target)
        {
            if (source is not long value)
            {
                return;
            }
            known |= flag;
            target = value;
        }

        private static void Set(
            double? source,
            uint flag,
            ref uint known,
            ref double target)
        {
            if (source is not double value || !double.IsFinite(value))
            {
                return;
            }
            known |= flag;
            target = value;
        }
    }
}
