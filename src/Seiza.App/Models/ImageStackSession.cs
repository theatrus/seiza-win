using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal sealed record ImageStackPushResult(
    ImageStackDisposition Disposition,
    bool NativeFailure);

internal sealed record ImageStackPipelineResult(
    [property: JsonPropertyName("frames")]
    IReadOnlyList<ImageStackDisposition> Frames,
    [property: JsonPropertyName("integrated")]
    int Integrated,
    [property: JsonPropertyName("rejected")]
    int Rejected,
    [property: JsonPropertyName("failed")]
    int Failed);

internal sealed record ImageStackSessionCounts(
    int AcceptedFrames,
    int RejectedFrames);

internal sealed record ImageStackSnrSample(
    uint Frames,
    double Noise,
    double Background,
    double Signal,
    double Snr,
    IReadOnlyList<double> ChannelNoise)
{
    /// <summary>
    /// Compares this depth against one common signal, normally the deepest
    /// measured sample. Using each depth's own signal flatters shallow stacks.
    /// </summary>
    public double RelativeSnr(double commonSignal) =>
        double.IsFinite(commonSignal) && commonSignal >= 0 &&
        double.IsFinite(Noise) && Noise > 0
            ? commonSignal / Noise
            : 0;
}

/// <summary>
/// An owned copy of the native accumulator view. Native borrowed pointers are
/// copied while the session is serialized and never escape the service.
/// </summary>
internal sealed record ImageStackLiveView(
    int Width,
    int Height,
    int Channels,
    int AcceptedFrames,
    int RejectedFrames,
    float[] Mean,
    uint[] Coverage,
    uint[] RejectedSamples);
