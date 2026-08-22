using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackSessionPathsTests
{
    [Fact]
    public void WatchFolderIdentityIgnoresWindowsCaseAndTrailingSeparators()
    {
        string root = Path.Combine(Path.GetTempPath(), "M 101 captures");

        string first = LiveStackSessionPaths.ForWatchFolder(root);
        string second = LiveStackSessionPaths.ForWatchFolder(
            root.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Equal(first, second);
        Assert.Equal("LiveStacks", Directory.GetParent(first)!.Name);
        Assert.StartsWith("M-101-CAPTURES-", Path.GetFileName(first), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentWatchFoldersUseDifferentSessionDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "Seiza-Live-Stack");

        Assert.NotEqual(
            LiveStackSessionPaths.ForWatchFolder(Path.Combine(root, "one")),
            LiveStackSessionPaths.ForWatchFolder(Path.Combine(root, "two")));
    }
}
