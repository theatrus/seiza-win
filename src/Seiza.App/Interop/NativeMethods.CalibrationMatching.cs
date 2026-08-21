using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "seiza_calibration_sensor_matches")]
    internal static partial int CalibrationSensorMatches(
        in NativeFrameSignature reference,
        in NativeFrameSignature candidate,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_match_tolerances_default")]
    internal static partial void GetDefaultMatchTolerances(
        out NativeMatchTolerances tolerances);

    [LibraryImport(LibraryName, EntryPoint = "seiza_calibration_optics_match")]
    internal static partial int CalibrationOpticsMatch(
        in NativeFrameSignature reference,
        in NativeFrameSignature candidate,
        in NativeMatchTolerances tolerances,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_calibration_dark_matches")]
    internal static partial int CalibrationDarkMatches(
        in NativeFrameSignature reference,
        in NativeFrameSignature candidate,
        in NativeMatchTolerances tolerances,
        out nint error);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrameSignature
{
    public uint Known;
    public nint Camera;
    public nint Telescope;
    public nint BayerPattern;
    public nint Filter;
    public double Width;
    public double Height;
    public double Channels;
    public double BinningX;
    public double BinningY;
    public double Gain;
    public double Offset;
    public double ReadoutMode;
    public double FocalLengthMm;
    public double RotationDeg;
    public double ExposureSeconds;
    public double CameraTempC;
    public double CapturedAtUnix;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMatchTolerances
{
    public uint Known;
    public double ExposureSeconds;
    public double ExposureFraction;
    public double DarkTemperatureC;
    public double MasterTemperatureC;
    public double RotationDeg;
    public double FocalLengthMm;
    public ulong FlatSessionSeconds;
}
