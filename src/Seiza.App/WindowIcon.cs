using Microsoft.UI.Windowing;

namespace Seiza.App;

internal static class WindowIcon
{
    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    internal static void Apply(AppWindow window) => window.SetIcon(IconPath);
}
