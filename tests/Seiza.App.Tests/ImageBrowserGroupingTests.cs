using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageBrowserGroupingTests
{
    [Fact]
    public void ImportedListsRetainInputOrderUnderNoneWhileFolderViewsStillSort()
    {
        string[] paths = [Image("night2", "frame10.fits"), Image("night1", "frame2.fits"), Image("night2", "frame1.fits")];
        var list = ImageBrowserGroupingModel.Create(paths, null, ImageBrowserGrouping.None, preserveInputOrder: true);
        Assert.Equal(paths, list.OrderedPaths);
        var folder = ImageBrowserGroupingModel.Create(paths, null, ImageBrowserGrouping.None);
        Assert.Equal(new[] { paths[2], paths[1], paths[0] }, folder.OrderedPaths);
        var grouped = ImageBrowserGroupingModel.Create(paths, null, ImageBrowserGrouping.Folder, preserveInputOrder: true);
        Assert.Equal(new[] { paths[1], paths[2], paths[0] }, grouped.OrderedPaths);
    }

    [Theory]
    [InlineData((int)ImageBrowserGrouping.None)]
    [InlineData((int)ImageBrowserGrouping.Folder)]
    [InlineData((int)ImageBrowserGrouping.FileType)]
    [InlineData((int)ImageBrowserGrouping.Filter)]
    public void EmptyInputHasNoGroupsOrNavigation(int grouping)
    {
        ImageBrowserGroupingResult result = Create([], (ImageBrowserGrouping)grouping);

        Assert.Empty(result.Groups);
        Assert.Empty(result.OrderedPaths);
    }

    [Fact]
    public void FolderGroupingRetainsFullNestedPathsAndPlacesRootFirst()
    {
        string[] paths =
        [
            Image("night10", "LIGHT", "frame10.fits"),
            Image("night2", "LIGHT", "frame10.fits"),
            Image("root2.fits"),
            Image("night2", "LIGHT", "frame2.fits"),
            Image("root1.fits"),
            Image("night2", "DARK", "frame1.fits"),
        ];

        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.Folder);

        Assert.Equal(
            ["(root)", Path.Combine("night2", "DARK"), Path.Combine("night2", "LIGHT"), Path.Combine("night10", "LIGHT")],
            result.Groups.Select(group => group.Label));
        Assert.Equal([Image("root1.fits"), Image("root2.fits")], result.Groups[0].Paths);
        Assert.Equal(
            [Image("night2", "LIGHT", "frame2.fits"), Image("night2", "LIGHT", "frame10.fits")],
            result.Groups[2].Paths);
        Assert.Equal(result.Groups.SelectMany(group => group.Paths), result.OrderedPaths);
        Assert.Equal(6, result.OrderedPaths.Count);
    }

    [Fact]
    public void FlatGroupingSortsFileNamesThenRelativePathWithoutLosingDuplicateNames()
    {
        string[] paths =
        [
            Image("night10", "frame2.fits"),
            Image("frame10.fits"),
            Image("night2", "frame2.fits"),
            Image("frame1.fits"),
        ];

        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.None);

        Assert.Single(result.Groups);
        Assert.Empty(result.Groups[0].Label);
        Assert.Equal(
            [Image("frame1.fits"), Image("night2", "frame2.fits"), Image("night10", "frame2.fits"), Image("frame10.fits")],
            result.OrderedPaths);
        Assert.Equal(result.Groups[0].Paths, result.OrderedPaths);
    }

    [Fact]
    public void TypeGroupingCoalescesAliasesAndUsesTheSameOrderForNavigation()
    {
        string[] paths =
        [
            Image("x.tiff"), Image("image2.fts"), Image("image10.fits"), Image("image1.fit"),
            Image("b.jpeg"), Image("c.jfif"), Image("a.jpg"), Image("w.tif"),
            Image("d.PNG"), Image("z.XiSf"),
        ];

        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.FileType);

        Assert.Equal(["FITS", "JPEG", "PNG", "TIFF", "XISF"], result.Groups.Select(group => group.Label));
        Assert.Equal([Image("image1.fit"), Image("image2.fts"), Image("image10.fits")], result.Groups[0].Paths);
        Assert.Equal([Image("a.jpg"), Image("b.jpeg"), Image("c.jfif")], result.Groups[1].Paths);
        Assert.Equal(result.Groups.SelectMany(group => group.Paths), result.OrderedPaths);
        Assert.Equal(paths.Length, result.OrderedPaths.Count);
    }

    [Fact]
    public void UnknownExtensionsAndExtensionlessInputsAreNotLost()
    {
        ImageBrowserGroupingResult result = Create(
            [Image("x.bmp"), Image("image"), Image("y.BMP")],
            ImageBrowserGrouping.FileType);

        Assert.Equal(["BMP", "Other"], result.Groups.Select(group => group.Label));
        Assert.Equal(2, result.Groups[0].Paths.Count);
        Assert.Equal(3, result.OrderedPaths.Count);
    }

    [Theory]
    [InlineData((int)ImageBrowserGrouping.None)]
    [InlineData((int)ImageBrowserGrouping.Folder)]
    [InlineData((int)ImageBrowserGrouping.FileType)]
    public void DuplicatePathsAndAliasesAreRemovedCaseInsensitivelyAndDeterministically(int grouping)
    {
        string canonical = Image("LIGHT", "FRAME1.FITS");
        string lower = canonical.ToLowerInvariant();
        string dots = Path.Combine(Root, "LIGHT", "..", "LIGHT", "FRAME1.FITS");
        string distinct = Image("LIGHT2", "FRAME1.FITS");
        string[] paths = [lower, canonical, dots, distinct, canonical];

        ImageBrowserGroupingResult forward = Create(paths, (ImageBrowserGrouping)grouping);
        ImageBrowserGroupingResult reversed = Create(paths.Reverse(), (ImageBrowserGrouping)grouping);

        Assert.Equal(2, forward.OrderedPaths.Count);
        Assert.Equal(forward.OrderedPaths, reversed.OrderedPaths);
        Assert.Equal(forward.Groups.Select(group => group.Key), reversed.Groups.Select(group => group.Key));
        Assert.Equal(forward.Groups.Select(group => group.Label), reversed.Groups.Select(group => group.Label));
        Assert.Contains(distinct, forward.OrderedPaths);
    }

    [Fact]
    public void NoRootKeepsMultipleInputDirectoriesUnambiguous()
    {
        string first = Path.Combine(Path.GetPathRoot(Root)!, "session2", "LIGHT", "same.fits");
        string second = Path.Combine(Path.GetPathRoot(Root)!, "session10", "LIGHT", "same.fits");

        ImageBrowserGroupingResult result = ImageBrowserGroupingModel.Create(
            [second, first], null, ImageBrowserGrouping.Folder);

        Assert.Equal([Path.GetDirectoryName(first), Path.GetDirectoryName(second)], result.Groups.Select(group => group.Label));
        Assert.Equal([first, second], result.OrderedPaths);
    }

    [Fact]
    public void OutsideRootCannotBeMislabelledAsAnInsideDirectoryWithTheSamePrefix()
    {
        string outside = Path.Combine($"{Root}-other", "LIGHT", "frame.fits");
        ImageBrowserGroupingResult result = Create([outside, Image("frame.fits")], ImageBrowserGrouping.Folder);

        Assert.Equal("(root)", result.Groups[0].Label);
        Assert.Equal(Path.GetDirectoryName(outside), result.Groups[1].Label);
        Assert.Equal([Image("frame.fits"), outside], result.OrderedPaths);
    }

    [Fact]
    public void RelativeRootAndTrailingSeparatorsStillRecognizeRootImages()
    {
        string image = Path.Combine("browser-images", "frame.fits");

        ImageBrowserGroupingResult result = ImageBrowserGroupingModel.Create(
            [image], $"browser-images{Path.DirectorySeparatorChar}", ImageBrowserGrouping.Folder);

        Assert.Equal("(root)", Assert.Single(result.Groups).Label);
        Assert.Equal(image, Assert.Single(result.OrderedPaths));
    }

    [Fact]
    public void UnicodeNamesAndVeryLongNumericRunsHaveDeterministicNaturalOrder()
    {
        string[] paths =
        [
            Image("星雲10", "星2.fits"),
            Image("星雲2", "星10.fits"),
            Image("星雲2", "星2.fits"),
            Image("星雲2", "星10000000000000000000000000000000000000000000000000.fits"),
        ];

        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.Folder);

        Assert.Equal(["星雲2", "星雲10"], result.Groups.Select(group => group.Label));
        Assert.Equal([paths[2], paths[1], paths[3], paths[0]], result.OrderedPaths);
    }

    [Fact]
    public void CaseEquivalentNaturalNamesUseADeterministicTieBreak()
    {
        string[] paths = [Image("b", "FRAME1.fits"), Image("a", "frame1.fits"), Image("c", "frame01.fits")];

        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.None);
        ImageBrowserGroupingResult reversed = Create(paths.Reverse(), ImageBrowserGrouping.None);

        Assert.Equal(result.OrderedPaths, reversed.OrderedPaths);
        Assert.Equal([paths[1], paths[0], paths[2]], result.OrderedPaths);
    }

    [Fact]
    public void CaseEquivalentDirectoriesHaveStableLabelsAndOneGroup()
    {
        string[] paths = [Image("light", "frame1.fits"), Image("LIGHT", "frame2.fits")];

        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.Folder);
        ImageBrowserGroupingResult reversed = Create(paths.Reverse(), ImageBrowserGrouping.Folder);

        Assert.Equal("LIGHT", Assert.Single(result.Groups).Label);
        Assert.Equal(result.Groups[0].Key, reversed.Groups[0].Key);
        Assert.Equal(result.Groups[0].Label, reversed.Groups[0].Label);
        Assert.Equal(paths, result.OrderedPaths);
    }

    [Fact]
    public void FlatGroupingWithoutRootStillRetainsIdenticalNamesInDifferentFolders()
    {
        string first = Image("night2", "same.fits");
        string second = Image("night10", "same.fits");

        ImageBrowserGroupingResult result = ImageBrowserGroupingModel.Create(
            [second, first], null, ImageBrowserGrouping.None);

        Assert.Equal([first, second], result.OrderedPaths);
    }

    [Fact]
    public void RootPathCasingAndAlternateSeparatorsDoNotCreateExtraFolderGroups()
    {
        string image = Image("frame.fits");
        string alias = image.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        ImageBrowserGroupingResult result = ImageBrowserGroupingModel.Create(
            [image, alias], Root.ToUpperInvariant(), ImageBrowserGrouping.Folder);

        Assert.Equal("(root)", Assert.Single(result.Groups).Label);
        Assert.Single(result.OrderedPaths);
    }

    [Fact]
    public void EveryGroupingIsIndependentOfInputEnumerationOrder()
    {
        string[] paths =
        [
            Image("night10", "light", "frame10.fits"),
            Image("night2", "LIGHT", "frame2.fits"),
            Image("night2", "LIGHT", "FRAME1.fits"),
            Image("night2", "light", "frame01.fits"),
            Image("frame2.png"),
            Image("frame10.jpg"),
            Image("night2", "LIGHT", "frame2.fits").ToUpperInvariant(),
        ];

        foreach (ImageBrowserGrouping grouping in Enum.GetValues<ImageBrowserGrouping>())
        {
            ImageBrowserGroupingResult expected = Create(paths, grouping);
            for (int offset = 0; offset < paths.Length; offset++)
            {
                IEnumerable<string> rotated = paths.Skip(offset).Concat(paths.Take(offset));
                ImageBrowserGroupingResult actual = Create(rotated.Reverse(), grouping);
                Assert.Equal(expected.OrderedPaths, actual.OrderedPaths);
                Assert.Equal(expected.Groups.Select(group => group.Key), actual.Groups.Select(group => group.Key));
                Assert.Equal(expected.Groups.Select(group => group.Label), actual.Groups.Select(group => group.Label));
                Assert.Equal(actual.Groups.SelectMany(group => group.Paths), actual.OrderedPaths);
            }
        }
    }

    [Theory]
    [InlineData((int)ImageBrowserGrouping.None)]
    [InlineData((int)ImageBrowserGrouping.Folder)]
    [InlineData((int)ImageBrowserGrouping.FileType)]
    [InlineData((int)ImageBrowserGrouping.Filter)]
    public void SingleImageWorksWithoutAnyFilesystemAccess(int grouping)
    {
        string missingImage = Image("nonexistent-folder", "not-on-disk.fits");

        ImageBrowserGroupingResult result = Create([missingImage], (ImageBrowserGrouping)grouping);

        Assert.Single(result.Groups);
        Assert.Equal(missingImage, Assert.Single(result.OrderedPaths));
    }

    [Fact]
    public void GroupingNeverMutatesTheInputOrder()
    {
        string[] paths = [Image("image10.fits"), Image("image2.fits")];
        string[] original = paths.ToArray();

        _ = Create(paths, ImageBrowserGrouping.Folder);

        Assert.Equal(original, paths);
    }

    [Fact]
    public void BlankPathsAreIgnored()
    {
        ImageBrowserGroupingResult result = Create(["", " ", null!, Image("image.fits")], ImageBrowserGrouping.None);

        Assert.Equal(Image("image.fits"), Assert.Single(result.OrderedPaths));
    }

    [Fact]
    public void InvalidGroupingIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create([], (ImageBrowserGrouping)99));
    }

    [Fact]
    public void NullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Create(null!, ImageBrowserGrouping.None));
    }

    [Fact]
    public void FilterGroupingCoalescesAliasesAcrossFoldersAndKeepsUnknownImages()
    {
        string[] paths =
        [
            Image("night2", "light_Ha_10.fits"), Image("night1", "light_H-alpha_2.xisf"),
            Image("light_OIII_2.fits"), Image("light_oxygen-iii_1.fit"),
            Image("light_filter_Custom_1.png"), Image("light_filter_CUSTOM_2.jpg"),
            Image("unrecognized.png"),
        ];
        ImageBrowserGroupingResult result = Create(paths, ImageBrowserGrouping.Filter);

        Assert.Equal(["CUSTOM", "H-alpha", "OIII", "Other"], result.Groups.Select(group => group.Label));
        Assert.Equal([2, 2, 2, 1], result.Groups.Select(group => group.Paths.Count));
        Assert.Equal(result.Groups.SelectMany(group => group.Paths), result.OrderedPaths);
        Assert.Equal(paths.Length, result.OrderedPaths.Count);
        Assert.Equal(result.Groups.Select(group => group.Key), Create(paths.Reverse(), ImageBrowserGrouping.Filter).Groups.Select(group => group.Key));
    }

    [Fact]
    public void NamedOtherFilterDoesNotMergeWithUnrecognizedFiles()
    {
        ImageBrowserGroupingResult result = Create(
            [Image("light_filter_Other_1.fits"), Image("unrecognized.fits")], ImageBrowserGrouping.Filter);

        Assert.Equal(2, result.Groups.Count);
        Assert.All(result.Groups, group => Assert.Equal("Other", group.Label));
        Assert.Equal(2, result.Groups.Select(group => group.Key).Distinct().Count());
    }

    [Theory]
    [InlineData(0, -1, -1)]
    [InlineData(0, 1, 1)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 1, 2)]
    [InlineData(2, -1, 1)]
    [InlineData(2, 1, 3)]
    [InlineData(3, -1, 2)]
    [InlineData(3, 1, 4)]
    [InlineData(4, -1, 3)]
    [InlineData(4, 1, -1)]
    [InlineData(-1, 1, 0)]
    [InlineData(-1, -1, 4)]
    public void NavigationVisitsEveryItemInGroupedOrderInBothDirections(int selected, int direction, int expected)
    {
        string[] paths = ["a", "b", "c", "d", "e"];
        Assert.Equal(expected, ImageBrowserGroupingModel.FindAdjacentIndex(paths, selected, direction));
    }

    [Fact]
    public void EmptyCollectionsHaveNoNavigationTarget()
    {
        foreach (int direction in new[] { -1, 1 })
        {
            Assert.Equal(-1, ImageBrowserGroupingModel.FindAdjacentIndex([], -1, direction));
        }
    }

    [Theory]
    [InlineData((int)ImageBrowserGrouping.Folder)]
    [InlineData((int)ImageBrowserGrouping.Filter)]
    [InlineData((int)ImageBrowserGrouping.FileType)]
    [InlineData((int)ImageBrowserGrouping.None)]
    public void NavigationTraversesTheEntireGroupingLayoutWithoutOmissions(int grouping)
    {
        ImageBrowserGroupingResult layout = Create(
            [Image("z", "light_OIII_2.fits"), Image("light_Ha_1.fits"), Image("a", "light_Ha_1.fits"), Image("preview.png")],
            (ImageBrowserGrouping)grouping);
        List<string> forward = [];
        for (int index = ImageBrowserGroupingModel.FindAdjacentIndex(layout.OrderedPaths, -1, 1); index >= 0;
            index = ImageBrowserGroupingModel.FindAdjacentIndex(layout.OrderedPaths, index, 1))
        {
            forward.Add(layout.OrderedPaths[index]);
        }
        List<string> backward = [];
        for (int index = ImageBrowserGroupingModel.FindAdjacentIndex(layout.OrderedPaths, -1, -1); index >= 0;
            index = ImageBrowserGroupingModel.FindAdjacentIndex(layout.OrderedPaths, index, -1))
        {
            backward.Add(layout.OrderedPaths[index]);
        }
        Assert.Equal(layout.OrderedPaths, forward);
        Assert.Equal(layout.OrderedPaths.Reverse(), backward);
        Assert.Equal(4, forward.Count);
    }

    [Fact]
    public void NavigationRejectsInvalidDirectionAndSelection()
    {
        string[] paths = ["a"];
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBrowserGroupingModel.FindAdjacentIndex(paths, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBrowserGroupingModel.FindAdjacentIndex(paths, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBrowserGroupingModel.FindAdjacentIndex(paths, -2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBrowserGroupingModel.FindAdjacentIndex(paths, 1, 1));
    }

    private static string Root { get; } = Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, "image-browser-test-root");

    private static string Image(params string[] parts) => Path.Combine([Root, .. parts]);

    private static ImageBrowserGroupingResult Create(IEnumerable<string> paths, ImageBrowserGrouping grouping) =>
        ImageBrowserGroupingModel.Create(paths, Root, grouping);
}
