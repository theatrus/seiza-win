using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackFrameImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaStackImport-").FullName;

    [Fact]
    public void FileListResolvesRelativePathsDeduplicatesAndReportsSkippedEntries()
    {
        string light = Write("light.fits", "");
        Write("preview.png", "");
        string list = Write("frames.csv", "path\nlight.fits\nlight.fits\npreview.png\nmissing.fits\n");
        StackFrameImportResult result = StackFrameImport.Read(list, folder: false);
        Assert.Equal(light, Assert.Single(result.Paths));
        Assert.Contains(result.Notes, note => note.Contains("missing.fits", StringComparison.Ordinal));
        Assert.Contains(result.Notes, note => note.Contains("FITS/XISF", StringComparison.Ordinal));
    }

    [Fact]
    public void FolderImportsOnlyDirectAstronomyImages()
    {
        string bias = Write("bias.xisf", "");
        Write("preview.png", "");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        Write("nested/dark.fits", "");
        StackFrameImportResult result = StackFrameImport.Read(_root, folder: true);
        Assert.Equal(bias, Assert.Single(result.Paths));
        Assert.Single(result.Notes);
    }

    [Fact]
    public void MalformedListDoesNotReturnPartialInputs()
    {
        Write("light.fits", "");
        string list = Write("bad.csv", "path\nlight.fits\n\"unfinished\n");
        Assert.Throws<InvalidDataException>(() => StackFrameImport.Read(list, folder: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreCancelledImportDoesNotReadSources(bool folder)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StackFrameImport.Read("missing", folder, cancellation.Token));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
