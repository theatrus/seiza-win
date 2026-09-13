using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageSamplingTests
{
    [Theory]
    [InlineData(1000, 500, 1, true)]
    [InlineData(1000, 500, 2, true)]
    [InlineData(2000, 1000, 2, false)]
    [InlineData(4000, 2000, 1, false)]
    [InlineData(4000, 2000, 2, false)]
    [InlineData(4000, 500, 1, true)]
    public void UsesPhysicalPixelsForFullResolutionImages(
        double width, double height, double displayScale, bool expected)
    {
        Assert.Equal(expected, ImageSampling.NeedsFiltering(
            4000, 2000, 4000, 2000, width, height, displayScale));
    }

    [Fact]
    public void EnlargedResponsivePreviewStaysFiltered()
    {
        Assert.True(ImageSampling.NeedsFiltering(
            2048, 1024, 8000, 4000, 4000, 2000, 2));
    }
}
