namespace Seiza.App.Models;

public enum RgbStretchMode : uint
{
    Auto = 0,
    LinkedAuto = 1,
    Linear = 2,
}

public static class RgbStretchModeExtensions
{
    public static string Title(this RgbStretchMode mode) => mode switch
    {
        RgbStretchMode.Auto => "Auto",
        RgbStretchMode.LinkedAuto => "Linked Auto",
        RgbStretchMode.Linear => "Linear",
        _ => mode.ToString(),
    };

    public static string Help(this RgbStretchMode mode) => mode switch
    {
        RgbStretchMode.Auto => "Stretch each RGB channel independently.",
        RgbStretchMode.LinkedAuto => "Use one shared automatic stretch for all RGB channels.",
        RgbStretchMode.Linear => "Map the native 16-bit RGB range directly to the display.",
        _ => string.Empty,
    };
}
