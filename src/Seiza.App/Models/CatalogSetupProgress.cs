namespace Seiza.App.Models;

public enum CatalogSetupPreset : uint
{
    StandardBlind = 0,
    DeepestBlind = 1,
    All = 2,
}

public sealed class CatalogSetupProgress
{
    public required string Phase { get; init; }

    public required string Message { get; init; }

    public string? FileName { get; init; }

    public int FilesCompleted { get; init; }

    public int FilesTotal { get; init; }

    public ulong? BytesCompleted { get; init; }

    public ulong? BytesTotal { get; init; }

    public ulong? WrittenBytes { get; init; }
}
