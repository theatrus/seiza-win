using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    private const string LibraryName = "seiza_cabi";

    [LibraryImport(LibraryName, EntryPoint = "seiza_core_version")]
    internal static partial nint GetCoreVersion();

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_catalog_status_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetCatalogStatusJson(string? catalogDirectory, out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_solve_image_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SolveImageJson(
        string path,
        string? catalogDirectory,
        double minimumScaleArcsecPerPixel,
        double maximumScaleArcsecPerPixel,
        byte sipOrder,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_catalog_setup",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool SetupCatalog(
        string? catalogDirectory,
        uint preset,
        delegate* unmanaged[Cdecl]<nint, nint, void> progress,
        nint context,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_rendered_image_open_with_rgb_stretch",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenRenderedImage(
        string path,
        double targetMedian,
        double shadowsClip,
        uint maxDimension,
        uint rgbStretchMode,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_rendered_image_open_with_stretch_config",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenRenderedImageWithStretchConfiguration(
        string path,
        string configurationJson,
        uint maxDimension,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_width")]
    internal static partial uint GetRenderedImageWidth(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_height")]
    internal static partial uint GetRenderedImageHeight(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_bgra")]
    internal static partial nint GetRenderedImageBgra(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_bgra_length")]
    internal static partial nuint GetRenderedImageBgraLength(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_metadata_json")]
    internal static partial nint GetRenderedImageMetadataJson(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image_free")]
    internal static partial void FreeRenderedImage(nint image);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_rendered_image16_open_with_stretch_config",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenRenderedImage16WithStretchConfiguration(
        string path,
        string configurationJson,
        uint maxDimension,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_width")]
    internal static partial uint GetRenderedImage16Width(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_height")]
    internal static partial uint GetRenderedImage16Height(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_rgba")]
    internal static partial nint GetRenderedImage16Rgba(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_rgba_length")]
    internal static partial nuint GetRenderedImage16RgbaLength(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_metadata_json")]
    internal static partial nint GetRenderedImage16MetadataJson(nint image);

    [LibraryImport(LibraryName, EntryPoint = "seiza_rendered_image16_free")]
    internal static partial void FreeRenderedImage16(nint image);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_open_fits",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenLiveStacker(
        string referencePath,
        string? biasPath,
        string? darkPath,
        string? flatPath,
        double darkExposureSeconds,
        string optionsJson,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_open_context",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint OpenLiveStackerContext(string contextPath, out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_save_context",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SaveLiveStackerContext(
        nint stacker,
        string contextPath,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_state_json")]
    internal static partial nint GetLiveStackerStateJson(nint stacker, out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_set_calibration_fits",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SetLiveStackerCalibration(
        nint stacker,
        string? biasPath,
        string? darkPath,
        string? flatPath,
        double darkExposureSeconds,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_push_fits_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint PushLiveStackerFrameJson(
        nint stacker,
        string path,
        out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_push_fits_pipelined_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint PushLiveStackerFramesJson(
        nint stacker,
        string pathsJson,
        nuint workers,
        nuint maxInFlightBytes,
        float normalizedFullScale,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_width")]
    internal static partial nuint GetLiveStackerWidth(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_height")]
    internal static partial nuint GetLiveStackerHeight(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_channels")]
    internal static partial nuint GetLiveStackerChannels(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_data_length")]
    internal static partial nuint GetLiveStackerDataLength(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_accepted_frames")]
    internal static partial uint GetLiveStackerAcceptedFrames(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_rejected_frames")]
    internal static partial uint GetLiveStackerRejectedFrames(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_mean")]
    internal static partial nint GetLiveStackerMean(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_coverage")]
    internal static partial nint GetLiveStackerCoverage(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_rejected_samples")]
    internal static partial nint GetLiveStackerRejectedSamples(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_measure_depth")]
    internal static unsafe partial int MeasureLiveStackerDepth(
        nint stacker,
        NativeSnrSample* sample,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_checkpoint_depths")]
    internal static unsafe partial nuint GetStackSnrCheckpointDepths(
        nuint total,
        nuint* output,
        nuint outputLength);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_snapshot")]
    internal static partial nint SnapshotLiveStacker(nint stacker, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_export_snapshot")]
    internal static partial nint ExportLiveStackerSnapshot(nint stacker, out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_live_stacker_render_preview",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint RenderLiveStackerPreview(
        nint stacker,
        string configurationJson,
        uint maxDimension,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_finish")]
    internal static partial nint FinishLiveStacker(ref nint stacker, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_live_stacker_free")]
    internal static partial void FreeLiveStacker(nint stacker);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_accepted_frames")]
    internal static partial uint GetStackSnapshotAcceptedFrames(nint snapshot);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_rejected_frames")]
    internal static partial uint GetStackSnapshotRejectedFrames(nint snapshot);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_stack_snapshot_write_fits",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool WriteStackSnapshotFits(
        nint snapshot,
        string path,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_snapshot_free")]
    internal static partial void FreeStackSnapshot(nint snapshot);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_stack_export_snapshot_write_fits",
        StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool WriteStackExportSnapshotFits(
        nint snapshot,
        string path,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_stack_export_snapshot_free")]
    internal static partial void FreeStackExportSnapshot(nint snapshot);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_probe_frame_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ProbeFrameJson(string path, out nint error);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_calibration_plan_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint PlanCalibrationJson(string requestJson, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_cancel_signal_create")]
    internal static partial nint CreateCancelSignal();

    [LibraryImport(LibraryName, EntryPoint = "seiza_cancel_signal_cancel")]
    internal static partial void CancelSignal(nint signal);

    [LibraryImport(LibraryName, EntryPoint = "seiza_cancel_signal_free")]
    internal static partial void FreeCancelSignal(nint signal);

    [LibraryImport(
        LibraryName,
        EntryPoint = "seiza_calibration_build_master_json",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint BuildCalibrationMasterJson(
        string requestJson,
        nint cancelSignal,
        out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_string_free")]
    internal static partial void FreeString(nint value);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSnrSample
{
    internal const int MaximumChannels = 3;

    public uint Frames;
    public double Noise;
    public double Background;
    public double Signal;
    public double Snr;
    public nuint ChannelCount;
    public fixed double ChannelNoise[MaximumChannels];
}
