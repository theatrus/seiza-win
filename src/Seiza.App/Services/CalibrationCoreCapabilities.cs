using System.Runtime.InteropServices;
using Seiza.App.Interop;

namespace Seiza.App.Services;

/// <summary>Probe the loaded ABI; an in-progress upstream PR is not a runtime capability.</summary>
internal sealed record CalibrationCoreCapabilities(
    bool SupportsBiasDarkQuality, bool SupportsSessionGates, bool SupportsFlatGroupPlanning)
{
    private static readonly Lazy<CalibrationCoreCapabilities> Detected = new(Detect);
    public static CalibrationCoreCapabilities Current => Detected.Value;

    public void RequireBiasDarkQuality()
    {
        if (!SupportsBiasDarkQuality)
            throw new SeizaCoreException("This Seiza core does not support native bias/dark quality screening.");
    }

    public void RequireFlatGroupPlanning()
    {
        if (!SupportsFlatGroupPlanning)
            throw new SeizaCoreException("Shared measured flat grouping requires a published C ABI with Seiza #385 support. " +
                "The current automatic calibration workflow still prepares one compatible set per output.");
    }

    private static CalibrationCoreCapabilities Detect()
    {
        if (!NativeLibrary.TryLoad("seiza_cabi", typeof(NativeMethods).Assembly, null, out nint library))
            return new(false, false, false);
        try
        {
            bool Has(string name) => NativeLibrary.TryGetExport(library, name, out _);
            return new(
                Has("seiza_calibration_measure_dark_json") && Has("seiza_calibration_screen_bias_json") &&
                    Has("seiza_calibration_screen_darks_json"),
                Has("seiza_live_stacker_set_match_tolerances") && Has("seiza_live_stacker_set_light_rotation"),
                Has("seiza_flat_group_plan_json"));
        }
        finally { NativeLibrary.Free(library); }
    }
}
