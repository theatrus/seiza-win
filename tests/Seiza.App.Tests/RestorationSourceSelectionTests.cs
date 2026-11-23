using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class RestorationSourceSelectionTests
{
    [Theory]
    [InlineData("stack.fits")]
    [InlineData("stack.FIT")]
    [InlineData("stack.fts")]
    [InlineData("stack.XISF")]
    public void StartsWithTheBrowserImage(string path)
    {
        var selection = new RestorationSourceSelection(path);
        Assert.Equal(path, selection.Path);
        Assert.True(selection.CanProcess);
    }

    [Fact]
    public void BrowserImageArrivingDuringModelSetupIsSelectedWhenSetupFinishes()
    {
        var selection = new RestorationSourceSelection(null);
        Assert.False(selection.Request("stack.fits", busy: true));
        Assert.Null(selection.Path);
        Assert.True(selection.ApplyPending());
        Assert.Equal("stack.fits", selection.Path);
        Assert.True(selection.CanProcess);
        Assert.False(selection.ApplyPending());
    }

    [Fact]
    public void KeepsProcessingSourceStableAndUsesLatestBrowserRequestAfterwards()
    {
        var selection = new RestorationSourceSelection("first.fits");
        selection.Request("second.fits", busy: true);
        selection.Request("third.xisf", busy: true);
        Assert.Equal("first.fits", selection.Path);
        Assert.True(selection.ApplyPending());
        Assert.Equal("third.xisf", selection.Path);
    }

    [Fact]
    public void ReturningToCurrentImageSupersedesAnOlderQueuedRequestWithoutInvalidation()
    {
        var selection = new RestorationSourceSelection("first.fits");
        selection.Request("second.fits", busy: true);
        selection.Request("FIRST.FITS", busy: true);
        Assert.False(selection.ApplyPending());
        Assert.Equal("first.fits", selection.Path);
    }

    [Fact]
    public void EmptyBrowserDoesNotLeaveAnotherDocumentsImageSelected()
    {
        var selection = new RestorationSourceSelection("first.fits");
        selection.Request(null, busy: true);
        Assert.True(selection.ApplyPending());
        Assert.Null(selection.Path);
        Assert.False(selection.CanProcess);
    }

    [Fact]
    public void IdleBrowserRequestChangesSourceImmediately()
    {
        var selection = new RestorationSourceSelection("first.fits");
        Assert.True(selection.Request("second.xisf", busy: false));
        Assert.Equal("second.xisf", selection.Path);
        Assert.False(selection.ApplyPending());
    }

    [Fact]
    public void ExplicitPickerSelectionSupersedesEarlierBrowserRequest()
    {
        var selection = new RestorationSourceSelection("first.fits");
        selection.Request("second.fits", busy: true);
        Assert.True(selection.Select("picked.xisf"));
        Assert.False(selection.ApplyPending());
        Assert.Equal("picked.xisf", selection.Path);
    }

    [Theory]
    [InlineData("display.png")]
    [InlineData("display.tiff")]
    [InlineData("display.jpg")]
    public void UnsupportedBrowserImageRemainsVisibleWithoutEnablingProcessing(string path)
    {
        var selection = new RestorationSourceSelection("first.fits");
        Assert.True(selection.Request(path, busy: false));
        Assert.Equal(path, selection.Path);
        Assert.False(selection.CanProcess);
    }
}
