using Seiza.App.Models;

namespace Seiza.App.Services;

/// <summary>
/// Adapts a serialized native session to the generation-based checkpoint
/// store without exposing its handle or opening a large context twice.
/// </summary>
internal sealed class ImageStackCheckpointWriter : ILiveStackCheckpointWriter
{
    private readonly ImageStackSession _session;

    public ImageStackCheckpointWriter(ImageStackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public ValueTask<LiveStackNativeState> SaveContextAsync(
        string destinationPath,
        CancellationToken cancellationToken) =>
        new(_session.SaveContextAndGetStateAsync(destinationPath, cancellationToken));
}
