namespace Seiza.App.Rendering;

internal static class StarAnalysisOverlayLifecycle
{
    /// <summary>
    /// Star measurements remain registered only when an existing source is
    /// being re-rendered for processing. A fresh load must invalidate them even
    /// when it reopens the same path, because the file contents may have changed.
    /// </summary>
    public static bool ShouldResetForLoad(
        bool sourcePathChanged,
        bool preserveSourceBoundState) =>
        sourcePathChanged || !preserveSourceBoundState;
}
