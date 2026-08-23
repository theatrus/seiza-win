using Seiza.App.Rendering;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StarAnalysisOverlayLifecycleTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void OnlySameSourceProcessingReloadPreservesAnalysis(
        bool sourcePathChanged,
        bool preserveSourceBoundState,
        bool expectedReset)
    {
        bool reset = StarAnalysisOverlayLifecycle.ShouldResetForLoad(
            sourcePathChanged,
            preserveSourceBoundState);

        Assert.Equal(expectedReset, reset);
    }
}
