using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class AiProcessingTests
{
    [Fact]
    public void RecipeClipboardAndHistoryKeepAiSettingsButNativeStretchDoesNotReapplyThem()
    {
        var ai = new AiProcessing { Model = "noise-test", Backend = "directml", DeviceId = 1, Precision = "f16", Tile = 512, High = 0.4, Low = 0.7, Scale = 8 };
        var processing = new FitsImageProcessingConfiguration(FitsStretchStack.Default, null, ai: ai);
        var copied = FitsImageProcessingConfiguration.FromClipboardJson(processing.ToClipboardJson());
        Assert.Equal(processing, copied);
        Assert.Equal(ai, copied.Clone(true).Ai);
        using var native = JsonDocument.Parse(processing.ToJson());
        Assert.False(native.RootElement.TryGetProperty("aiNoiseReduction", out _));
        var history = new FitsImageProcessingHistory();
        history.Replace(processing);
        Assert.True(history.Undo());
        Assert.Null(history.Current.Ai);
        Assert.True(history.Redo());
        Assert.Equal(ai, history.Current.Ai);
    }

    [Fact]
    public void InvalidStrengthCannotBePastedOrSaved()
    {
        var processing = new FitsImageProcessingConfiguration(FitsStretchStack.Default, null, ai: new AiProcessing { High = double.NaN });
        Assert.Throws<InvalidOperationException>(() => processing.ToClipboardJson());
        var valid = new FitsImageProcessingConfiguration(FitsStretchStack.Default, null, ai: new AiProcessing());
        Assert.Throws<FormatException>(() => FitsImageProcessingConfiguration.FromClipboardJson(valid.ToClipboardJson().Replace("\"high\": 1", "\"high\": 2")));
    }

    [Fact]
    public void RectangleHandlesReverseDraggingAndClampsToSourceCoordinates()
    {
        Assert.Equal(new ProcessingRegion(5, 10, 15, 20), ProcessingRegion.FromDrag(20, 30, 5, 10, 100, 80));
        Assert.Equal(new ProcessingRegion(0, 0, 100, 80), ProcessingRegion.FromDrag(-30, -50, 120, 90, 100, 80));
        Assert.Equal(new ProcessingRegion(0, 0, 20, 10), ProcessingRegion.Center(20, 10));
    }

    [Fact]
    public void CacheEvictsLeastRecentlyUsedAndDisposesReplacedNativeResources()
    {
        using var cache = new ProcessingResultCache<string, Resource>(2, 100);
        var a = new Resource(); var b = new Resource(); var c = new Resource();
        Assert.True(cache.Add("a", a, 40));
        Assert.True(cache.Add("b", b, 40));
        Assert.Same(a, cache.Find("a"));
        Assert.True(cache.Add("c", c, 40));
        Assert.True(b.Disposed);
        Assert.False(a.Disposed);
        var replacement = new Resource();
        Assert.True(cache.Add("a", replacement, 70));
        Assert.True(a.Disposed);
        Assert.True(c.Disposed);
        Assert.Null(cache.Find("c"));
    }

    [Fact]
    public void OversizedResultsStayOwnedByCallerAndDoNotEvictUsefulPreview()
    {
        using var cache = new ProcessingResultCache<string, Resource>(2, 100);
        var small = new Resource(); var large = new Resource();
        cache.Add("preview", small, 20);
        Assert.False(cache.Add("full", large, 101));
        Assert.False(large.Disposed);
        Assert.Same(small, cache.Find("preview"));
        cache.Dispose();
        Assert.True(small.Disposed);
    }

    private sealed class Resource : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
