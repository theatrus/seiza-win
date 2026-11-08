using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageDirectoryScannerTests
{
    [Fact]
    public async Task FolderOfWindowsShortcutsLoadsTargetsOnWorkerThread()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Fixture source = new();
        using Fixture links = new();
        string fits = source.File("images/frame.FITS");
        string xisf = source.File("frame.xisf");
        CreateShortcut(links.PathFor("first.lnk"), fits);
        CreateShortcut(links.PathFor("duplicate.LNK"), fits);
        CreateShortcut(links.PathFor("nested/second.lnk"), xisf);
        CreateShortcut(links.PathFor("missing.lnk"), source.PathFor("missing.fits"));
        CreateShortcut(links.PathFor("text.lnk"), source.File("notes.txt"));
        CreateShortcut(links.PathFor("folder.lnk"), source.Root);
        links.File("corrupt.lnk");

        ImageDirectoryScanResult top = await Task.Run(() => ImageDirectoryScanner.Scan(links.Root, false));
        Assert.Equal(fits, Assert.Single(top.Paths));
        ImageDirectoryScanResult recursive = await Task.Run(() => ImageDirectoryScanner.Scan(links.Root, true));
        Assert.Equal(2, recursive.Paths.Count);
        Assert.Contains(fits, recursive.Paths);
        Assert.Contains(xisf, recursive.Paths);
    }

    [Fact]
    public void ImageBesideItsShortcutAppearsOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Fixture fixture = new();
        string fits = fixture.File("frame.fit");
        CreateShortcut(fixture.PathFor("frame.lnk"), fits);
        Assert.Equal(fits, Assert.Single(ImageDirectoryScanner.Scan(fixture.Root, false).Paths));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            ((dynamic)shortcut).TargetPath = target;
            ((dynamic)shortcut).Save();
        }
        finally
        {
            if (shortcut is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static readonly string[] ExpectedRecursivePaths =
    [
        "cover.png", "night2/light1.fits", "night2/narrowband/light2.xisf",
        "night2/narrowband/light10.xisf", "night10/light1.fits", "夜空2/星.fits",
    ];
    private static readonly string[] ExpectedTopLevelPaths = ["frame2.fits", "frame02.fits", "frame10.fits"];
    private static readonly string[] ExpectedAccessiblePaths = ["readable/light.fits", "root.fits"];

    [Fact]
    public void RecursiveScanKeepsDuplicateNamesAndUnicodePaths()
    {
        using Fixture fixture = new();
        fixture.File("夜空2/星.fits");
        fixture.File("night10/light1.fits");
        fixture.File("night2/light1.fits");
        fixture.File("night2/narrowband/light10.xisf");
        fixture.File("night2/narrowband/light2.xisf");
        fixture.File("cover.png");
        fixture.File("night2/notes.txt");

        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(fixture.Root, true);

        Assert.Equal(fixture.Root, result.RootPath);
        Assert.Equal(ExpectedRecursivePaths, fixture.RelativePaths(result));
        Assert.Equal(0, result.SkippedFolderCount);
        Assert.Equal(result.Paths, ImageDirectoryScanner.Scan(fixture.Root, true).Paths);
    }

    [Fact]
    public void TopOnlyScanLeavesNestedImagesForExplicitFolderBrowsing()
    {
        using Fixture fixture = new();
        fixture.File("frame10.fits");
        fixture.File("frame2.fits");
        fixture.File("frame02.fits");
        fixture.File("nested/frame1.fits");

        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(fixture.Root, false);

        Assert.Equal(ExpectedTopLevelPaths, fixture.RelativePaths(result));
        Assert.Equal(0, result.SkippedFolderCount);
    }

    [Fact]
    public void SupportedExtensionsAreTheSameAcrossFolderScanningAndPickers()
    {
        using Fixture fixture = new();
        foreach (string extension in ImageFileTypes.SupportedExtensions)
        {
            fixture.File("frame" + extension.ToUpperInvariant());
            Assert.True(ImageFileTypes.IsSupportedImage("frame" + extension));
            Assert.True(ImageFileTypes.IsSupportedExtension(extension.ToUpperInvariant()));
        }
        fixture.File("frame.raw");
        fixture.File("frame.fits.backup");
        fixture.File("no-extension");

        Assert.Equal(ImageFileTypes.SupportedExtensions.Count,
            ImageDirectoryScanner.Scan(fixture.Root, true).Paths.Count);
        Assert.False(ImageFileTypes.IsSupportedExtension(null));
        Assert.False(ImageFileTypes.IsSupportedExtension(""));
        Assert.False(ImageFileTypes.IsSupportedExtension("fits"));
        Assert.True(ImageFileTypes.IsAstronomyImage("star.XISF"));
        Assert.False(ImageFileTypes.IsAstronomyImage("star.PNG"));
    }

    [Fact]
    public void EmptyFolderReturnsAnEmptyStableSnapshot()
    {
        using Fixture fixture = new();
        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(fixture.Root, true);
        Assert.Empty(result.Paths);
        Assert.Equal(0, result.SkippedFolderCount);
        fixture.File("later.fits");
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void RelativeRootIsNormalizedAndTrailingSeparatorRemoved()
    {
        using Fixture fixture = new();
        fixture.File("first.fits");
        string root = Path.GetRelativePath(Environment.CurrentDirectory, fixture.Root) + Path.DirectorySeparatorChar;
        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(root, false);
        Assert.Equal(fixture.Root, result.RootPath);
        Assert.Equal(fixture.PathFor("first.fits"), Assert.Single(result.Paths));
    }

    [Fact]
    public void AlreadyCanceledScanDoesNotAccessAMissingRoot()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() =>
            ImageDirectoryScanner.Scan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                true, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void CancellationDuringEnumerationDoesNotPublishPartialResults()
    {
        using Fixture fixture = new();
        FileInfo first = new(fixture.File("first.fits"));
        FileInfo second = new(fixture.File("second.fits"));
        using CancellationTokenSource cancellation = new();
        IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo _)
        {
            yield return first;
            cancellation.Cancel();
            yield return second;
        }

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(() =>
            ImageDirectoryScanner.Scan(fixture.Root, true, Enumerate, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InaccessibleOrVanishedDescendantsDoNotHideReadableFolders(int errorKind)
    {
        using Fixture fixture = new();
        fixture.File("denied/hidden.fits");
        fixture.File("readable/light.fits");
        fixture.File("root.fits");
        IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo folder) =>
            folder.Name == "denied"
                ? throw (errorKind == 0
                    ? new UnauthorizedAccessException("Fixture access denied.")
                    : new DirectoryNotFoundException("Fixture directory disappeared."))
                : folder.EnumerateFileSystemInfos();

        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(
            fixture.Root, true, Enumerate, CancellationToken.None);

        Assert.Equal(ExpectedAccessiblePaths, fixture.RelativePaths(result));
        Assert.Equal(1, result.InaccessibleFolderCount);
        Assert.Equal(1, result.SkippedFolderCount);
        Assert.Equal(0, result.SkippedReparseFolderCount);
    }

    [Fact]
    public void RootPermissionErrorsAreNotTurnedIntoAnEmptySuccessfulScan()
    {
        using Fixture fixture = new();
        UnauthorizedAccessException failure = new("Fixture root denied: " + fixture.Root);
        UnauthorizedAccessException actual = Assert.Throws<UnauthorizedAccessException>(() =>
            ImageDirectoryScanner.Scan(fixture.Root, true, _ => throw failure, CancellationToken.None));
        Assert.Same(failure, actual);
    }

    [Fact]
    public void MissingRootAndFileRootReportErrors()
    {
        using Fixture fixture = new();
        Assert.Throws<DirectoryNotFoundException>(() =>
            ImageDirectoryScanner.Scan(fixture.PathFor("missing"), true));
        string file = fixture.File("not-a-folder.fits");
        Assert.ThrowsAny<IOException>(() => ImageDirectoryScanner.Scan(file, true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyRootIsRejected(string? root)
    {
        Assert.ThrowsAny<ArgumentException>(() => ImageDirectoryScanner.Scan(root!, true));
    }

    [Fact]
    public void DescendantDirectoryLinksAreSkippedIncludingCycles()
    {
        using Fixture fixture = new();
        fixture.File("scan/visible.fits");
        fixture.File("scan/real/nested.fits");
        fixture.File("external/hidden.fits");
        // Unprivileged symlink creation is supported by Unix, Windows Developer
        // Mode and the elevated Windows CI runner. If unavailable, the injected
        // error tests above still cover safe descendant failure handling.
        if (!fixture.TryDirectoryLink("scan/outside", "external") ||
            !fixture.TryDirectoryLink("scan/loop", "scan"))
        {
            return;
        }

        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(fixture.PathFor("scan"), true);

        Assert.Equal(new[] { fixture.PathFor("scan/real/nested.fits"), fixture.PathFor("scan/visible.fits") }, result.Paths);
        Assert.Equal(2, result.SkippedReparseFolderCount);
        Assert.Equal(2, result.SkippedFolderCount);
        Assert.Equal(0, result.InaccessibleFolderCount);
    }

    [Fact]
    public void AnExplicitlyChosenDirectoryLinkCanBeTheRoot()
    {
        using Fixture fixture = new();
        fixture.File("real/visible.fits");
        fixture.File("real/subdir/nested.fits");
        if (!fixture.TryDirectoryLink("selected", "real"))
        {
            return;
        }

        ImageDirectoryScanResult result = ImageDirectoryScanner.Scan(fixture.PathFor("selected"), true);

        Assert.Equal(new[] { fixture.PathFor("selected/subdir/nested.fits"), fixture.PathFor("selected/visible.fits") }, result.Paths);
        Assert.Equal(0, result.SkippedFolderCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<string> _directoryLinks = [];

        public string Root { get; } = Directory.CreateTempSubdirectory("SeizaDirectoryScanner-").FullName;

        public string PathFor(string relative) => Path.Combine(Root,
            relative.Replace('/', Path.DirectorySeparatorChar));

        public string File(string relative)
        {
            string path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, []);
            return path;
        }

        public string[] RelativePaths(ImageDirectoryScanResult result) =>
            result.Paths.Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/')).ToArray();

        public bool TryDirectoryLink(string relative, string target)
        {
            string link = PathFor(relative);
            try
            {
                Directory.CreateSymbolicLink(link, PathFor(target));
                _directoryLinks.Add(link);
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            // Unlink explicitly before recursively removing only this fixture's
            // validated owned temporary directory, never any link target.
            string relative = Path.GetRelativePath(Path.GetTempPath(), Path.GetFullPath(Root));
            if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) ||
                !Path.GetFileName(Root).StartsWith("SeizaDirectoryScanner-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the scanner test fixture.");
            }

            foreach (string link in _directoryLinks)
            {
                Directory.Delete(link);
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
