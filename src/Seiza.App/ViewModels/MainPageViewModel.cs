using CommunityToolkit.Mvvm.ComponentModel;
using Seiza.App.Services;

namespace Seiza.App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    public string CoreVersionText { get; } = $"Seiza Rust core {SeizaCore.Version}";

    [ObservableProperty]
    public partial string FileName { get; set; } = "No image open";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Open an image or a folder to begin.";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasImage { get; set; }

    [ObservableProperty]
    public partial bool CanNavigatePrevious { get; set; }

    [ObservableProperty]
    public partial bool CanNavigateNext { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = string.Empty;

    public void BeginLoading(string path)
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusText = $"Opening {Path.GetFileName(path)}…";
    }

    public void CompleteLoading(string path, Models.ImageMetadata metadata)
    {
        IsLoading = false;
        HasImage = true;
        FileName = Path.GetFileName(path);

        string color = metadata.ColorKind switch
        {
            "planar-rgb" => "RGB",
            "bayer" => "Bayer",
            "mono" => "monochrome",
            _ => metadata.ColorKind,
        };
        StatusText = $"{metadata.Width:N0} × {metadata.Height:N0} · {metadata.Format} · {color}";
    }

    public void FailLoading(string message)
    {
        IsLoading = false;
        ErrorMessage = message;
        StatusText = HasImage ? StatusText : message;
    }
}
