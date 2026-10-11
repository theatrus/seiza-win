using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class WindowLifetimeRegistryTests
{
    [Fact]
    public void ClosingLastDocumentWhileParallaxRemainsDoesNotDisposeServices()
    {
        int disposals = 0;
        var windows = new WindowLifetimeRegistry<object>(() => disposals++);
        object document = new(), parallax = new(), reopenedDocument = new();
        windows.Add(document);
        windows.Add(parallax);

        Assert.True(windows.Remove(document));
        Assert.Equal(0, disposals);
        windows.Add(reopenedDocument);
        Assert.True(windows.Remove(parallax));
        Assert.Equal(0, disposals);
        Assert.True(windows.Remove(reopenedDocument));
        Assert.Equal(1, disposals);
        Assert.Equal(0, windows.Count);
    }

    [Fact]
    public void CatalogueWindowAlsoRetainsServicesUntilItCloses()
    {
        int disposals = 0;
        var windows = new WindowLifetimeRegistry<object>(() => disposals++);
        object document = new(), catalogue = new();
        windows.Add(document);
        windows.Add(catalogue);
        windows.Remove(document);
        Assert.Equal(0, disposals);
        windows.Remove(catalogue);
        Assert.Equal(1, disposals);
    }

    [Fact]
    public void DuplicateRegistrationAndCloseDoNotDisposeTwice()
    {
        int disposals = 0;
        var windows = new WindowLifetimeRegistry<object>(() => disposals++);
        object window = new();
        Assert.True(windows.Add(window));
        Assert.False(windows.Add(window));
        Assert.Equal(1, windows.Count);
        Assert.False(windows.Remove(new object()));
        Assert.True(windows.Remove(window));
        Assert.False(windows.Remove(window));
        Assert.Equal(1, disposals);
    }

    [Fact]
    public void TracksWindowIdentityRatherThanValueEquality()
    {
        int disposals = 0;
        var windows = new WindowLifetimeRegistry<EqualWindow>(() => disposals++);
        var first = new EqualWindow(1);
        var second = new EqualWindow(1);
        windows.Add(first);
        windows.Add(second);
        Assert.Equal(2, windows.Count);
        windows.Remove(first);
        Assert.Equal(0, disposals);
        windows.Remove(second);
        Assert.Equal(1, disposals);
    }

    private sealed record EqualWindow(int Value);
}
