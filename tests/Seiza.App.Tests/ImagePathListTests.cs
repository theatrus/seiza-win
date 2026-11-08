using Seiza.App.Services;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImagePathListTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaPathList-").FullName;

    [Fact]
    public void CsvIssuesRetainSourceAndPhysicalLinesAcrossBlankAndMultilineRows()
    {
        string image = Write("frame.fits", "");
        string list = Write("issues.csv", "\npath,note\n\nframe.fits,\"first\nsecond\"\n\nmissing.fits,no\nnotes.txt,unsupported\n,empty\n");
        var result = ImagePathList.Read(list);
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(new long[] { 7, 8, 9 }, result.Issues.Select(issue => issue.Line));
        Assert.All(result.Issues, issue => Assert.Equal(list, issue.ListPath));
        Assert.Equal("missing.fits", result.Issues[0].Value);
        Assert.Contains("Unsupported", result.Issues[1].Reason);
        Assert.Contains("Missing image path", result.Issues[2].Reason);
    }

    [Fact]
    public void TextIssuesKeepOriginalLinesAndIgnoreQuotedEmptyRows()
    {
        string list = Write("issues.txt", "\n\"\"\nmissing.fits\n\nnotes.txt\n");
        var result = ImagePathList.Read(list);
        Assert.Equal(new long[] { 3, 5 }, result.Issues.Select(issue => issue.Line));
    }

    [Theory]
    [InlineData("images.txt", "second.fits\nfirst.fits\n")]
    [InlineData("images.csv", "File\nsecond.fits\nfirst.fits\n")]
    public void Utf16BomListsRetainTheirDeclaredOrder(string name, string text)
    {
        string first = Write("first.fits", "");
        string second = Write("second.fits", "");
        string list = Path.Combine(_root, name);
        File.WriteAllText(list, text, Encoding.Unicode);
        Assert.Equal(new[] { second, first }, ImagePathList.Read(list).Paths);
    }

    [Fact]
    public void MissingHeaderlessImageStillPreventsAColumnSwitch()
    {
        string image = Write("frame.fits", "");
        var result = ImagePathList.Read(Write("images.csv", "missing.fits,note\nframe.fits,File\n"));
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void CommandLineAcceptsListsWithoutTreatingThemAsImages()
    {
        string image = Write("frame.fits", "");
        string csv = Write("selected.CSV", "path\nframe.fits\n");
        string txt = Write("selected.txt", "frame.fits\n");
        string other = Write("notes.json", "{}");
        Assert.Equal(new[] { csv, txt, image }, CommandLineActivationRelay.GetSupportedPaths(
            new[] { $"\"{csv}\"", txt, image, other, Path.Combine(_root, "missing.csv") }));
        Assert.False(ImageFileTypes.IsSupportedImage(csv));
        Assert.False(ImageFileTypes.IsSupportedImage(txt));
        Assert.True(ImageFileTypes.IsOpenableFile(csv));
    }

    [Fact]
    public void HardLinkedImagesAreDeduplicatedByFileIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        string image = Write("frame.fits", "");
        string alias = Path.Combine(_root, "alias.fits");
        Assert.True(CreateHardLink(alias, image, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        var result = ImagePathList.Read(Write("images.txt", "alias.fits\nframe.fits\n"));
        Assert.Equal(alias, Assert.Single(result.Paths));
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void WindowsShortcutsUseTheirSavedTargetsAndReportBrokenLinks()
    {
        if (!OperatingSystem.IsWindows()) return;
        string image = Write("frame.fits", "");
        CreateShortcut(Path.Combine(_root, "frame.lnk"), image);
        CreateShortcut(Path.Combine(_root, "missing.lnk"), Path.Combine(_root, "missing.fits"));
        var result = ImagePathList.Read(Write("images.txt", "frame.lnk\nframe.fits\nmissing.lnk\n"));
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(3, Assert.Single(result.Issues).Line);
        Assert.Contains("Shortcut target", result.Issues[0].Reason);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CreateShortcut(string path, string target)
    {
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
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, nint securityAttributes);

    [Fact]
    public void TextSupportsQuotedAbsoluteAndRelativePathsAndDeduplicates()
    {
        string first = Write("first frame.fits", "");
        string second = Write("second.xisf", "");
        string list = Write("images.txt", $"\n\"{first}\"\nsecond.xisf\n./second.xisf\nmissing.fits\nnotes.txt\n");
        ImagePathListResult result = ImagePathList.Read(list);
        Assert.Equal(new[] { first, second }, result.Paths);
        Assert.Equal(2, result.SkippedCount);
    }

    [Fact]
    public void CsvFindsNamedPathColumnAndHandlesQuotedCommas()
    {
        string image = Write("frame, one.FITS", "");
        string list = Write("images.csv", "rating,filepath,note\r\n5,\"frame, one.FITS\",\"a \"\"quoted\"\" note\"\r\n4,missing.fits,no\r\n3\r\n");
        ImagePathListResult result = ImagePathList.Read(list);
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(2, result.SkippedCount);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("file_path")]
    [InlineData("FilePath")]
    [InlineData("file")]
    [InlineData("FileName")]
    [InlineData(" FILE ")]
    [InlineData(" File_Path ")]
    public void CsvRecognizesPathHeaders(string header)
    {
        string image = Write("frame.fit", "");
        var result = ImagePathList.Read(Write("images.csv", $"rating,{header}\n5,frame.fit\n"));
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void HeaderlessCsvUsesFirstColumnAndIgnoresMetadata()
    {
        string image = Write("first.png", "");
        Write("other.fits", "");
        var result = ImagePathList.Read(Write("images.csv", "first.png,other.fits\nfirst.png,duplicate\n"));
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void SubframeSelectorPreambleIsNotCountedAsImageData()
    {
        string first = Write("approved.fits", "");
        string second = Write("rejected.fits", "");
        string list = Write("subframes.csv",
            "SubframeSelector 1.9\nSubframe Scale,1.25000\nCamera Gain,0.70000\n" +
            "Approval expression,\"FWHM < 5\"\n\nIndex,Approved,Locked,File,Weight\n" +
            $"1,true,false,\"{first}\",1\n2,false,true,\"{second}\",0.5\n3,true,false,missing.fits,0.1\n");
        var result = ImagePathList.Read(list);
        Assert.Equal(new[] { first, second }, result.Paths);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void HeaderlessDataCannotSwitchColumnsAfterFirstImage()
    {
        string first = Write("first.fits", "");
        string second = Write("second.fits", "");
        var result = ImagePathList.Read(Write("images.csv", "first.fits,note\nsecond.fits,File\n"));
        Assert.Equal(new[] { first, second }, result.Paths);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void ParentRelativePathsResolveFromListDirectory()
    {
        string image = Write("frame.fits", "");
        string nested = Directory.CreateDirectory(Path.Combine(_root, "lists")).FullName;
        string list = Path.Combine(nested, "images.txt");
        try
        {
            File.WriteAllText(list, "../frame.fits\n");
            Assert.Equal(image, Assert.Single(ImagePathList.Read(list).Paths));
        }
        finally
        {
            File.Delete(list);
            Directory.Delete(nested);
        }
    }

    [Theory]
    [InlineData("\"unterminated\n")]
    [InlineData("\"first.fits\"trailing\n")]
    [InlineData("fir\"st.fits\n")]
    [InlineData("\"first\" \".fits\"\n")]
    public void MalformedCsvReportsLineInsteadOfReturningPartialCollection(string malformed)
    {
        Write("first.fits", "");
        string list = Write("images.csv", "path\nfirst.fits\n" + malformed);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ImagePathList.Read(list));
        Assert.Contains("line 3", error.Message);
        Assert.Contains(list, error.Message);
    }

    [Theory]
    [InlineData("images.txt")]
    [InlineData("images.csv")]
    public void InvalidEncodingFailsRatherThanSubstitutingPathCharacters(string name)
    {
        string list = Path.Combine(_root, name);
        File.WriteAllBytes(list, [0xff, 0xff]);
        Assert.Throws<InvalidDataException>(() => ImagePathList.Read(list));
        File.WriteAllBytes(list, [0xff, 0xfe, 0x61]);
        Assert.Throws<InvalidDataException>(() => ImagePathList.Read(list));
    }

    [Fact]
    public void EmptyListAndMissingListAreDistinct()
    {
        Assert.Empty(ImagePathList.Read(Write("empty.txt", "\n  \n")).Paths);
        Assert.Throws<FileNotFoundException>(() => ImagePathList.Read(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public void CancellationPrecedesReadingTheFile()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ImagePathList.Read(Path.Combine(_root, "missing.txt"), cancellation.Token));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        // Only remove files created directly in this fixture; never recurse into listed targets.
        foreach (string path in Directory.EnumerateFiles(_root)) File.Delete(path);
        Directory.Delete(_root);
    }
}
