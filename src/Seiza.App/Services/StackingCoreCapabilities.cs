using System.Runtime.InteropServices;
using Seiza.App.Interop;

namespace Seiza.App.Services;

/// <summary>Probe actual exports so compatibility does not depend on version strings.</summary>
internal sealed record StackingCoreCapabilities(
    bool SupportsReferenceImage,
    bool SupportsReferenceImageCandidates,
    bool SupportsExpandedCanvas = false,
    bool SupportsGpuResume = false)
{
    private static readonly Lazy<StackingCoreCapabilities> Detected = new(Detect);

    public static StackingCoreCapabilities Current => Detected.Value;

    public void RequireGpuResume()
    {
        if (!SupportsGpuResume)
            throw new SeizaCoreException("Resuming with current GPU settings needs Seiza core 0.42.2 or later. Update Seiza to continue this saved stack.");
    }

    public void RequireExpandedCanvas()
    {
        if (!SupportsExpandedCanvas)
            throw new SeizaCoreException("The installed Seiza core does not support expanded mosaic canvases. Update Seiza to core 0.40.1 or later.");
    }

    public void RequireReferenceImage(bool needsFallback = false)
    {
        if ((!SupportsReferenceImage && !SupportsReferenceImageCandidates) ||
            (needsFallback && !SupportsReferenceImageCandidates))
        {
            throw new SeizaCoreException(
                needsFallback
                    ? "The installed Seiza core does not support automatic alignment-reference fallback. " +
                      "Update Seiza to use published core 0.27.0 or later."
                    : "The installed Seiza core does not support a shared alignment reference image. " +
                      "Update Seiza to use published core 0.27.0 or later.");
        }
    }

    private static StackingCoreCapabilities Detect()
    {
        // Use the same assembly-aware lookup as LibraryImport, rather than
        // inspecting a separately named DLL or inferring from a crate pin.
        if (!NativeLibrary.TryLoad("seiza_cabi", typeof(NativeMethods).Assembly, null, out nint library))
        {
            return new(false, false);
        }
        try
        {
            return new(
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_open_fits_with_reference_image", out _),
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_open_fits_with_reference_image_candidates", out _),
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_open_fits_expanding", out _) &&
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_expansion_planning_notes_json", out _) &&
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_expanded_lens_json", out _),
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_open_context_with_gpu", out _));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
