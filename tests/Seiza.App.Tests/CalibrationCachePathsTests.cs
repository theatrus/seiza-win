using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class CalibrationCachePathsTests
{
    [Fact]
    public void LibraryIdentityIgnoresWindowsCaseAndTrailingSeparators()
    {
        string root = Path.Combine(Path.GetTempPath(), "Seiza-Calibration-Library");

        string first = CalibrationCachePaths.ForLibrary(root);
        string second = CalibrationCachePaths.ForLibrary(
            root.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.Equal("CalibrationMasters", Directory.GetParent(first)!.Name);
    }

    [Fact]
    public void DifferentLibrariesUseDifferentCacheDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "Seiza-Calibration-Library");

        Assert.NotEqual(
            CalibrationCachePaths.ForLibrary(Path.Combine(root, "one")),
            CalibrationCachePaths.ForLibrary(Path.Combine(root, "two")));
    }
}
