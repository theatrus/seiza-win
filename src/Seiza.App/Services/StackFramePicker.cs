using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Seiza.App.Services;

internal static class StackFramePicker
{
    public static DropDownButton CreateButton(string accessibleName, Func<string, Task> addAsync)
    {
        var menu = new MenuFlyout();
        foreach (var (label, source) in new[] { ("Choose images…", "images"), ("Choose folder…", "folder"), ("Choose file list…", "list") })
        {
            var item = new MenuFlyoutItem { Text = label };
            if (source == "folder") ToolTipService.SetToolTip(item, "Add FITS/XISF files directly inside the chosen folder");
            item.Click += async (_, _) => await addAsync(source);
            menu.Items.Add(item);
        }
        var button = new DropDownButton { Content = "Add frames", Flyout = menu };
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    public static FileOpenPicker OpenPicker(nint owner)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        foreach (string extension in new[] { ".fits", ".fit", ".fts", ".xisf" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
        return picker;
    }

    public static async Task<StackFrameImportResult?> PickAsync(string source, nint owner, CancellationToken token)
    {
        if (source == "images")
        {
            var files = await OpenPicker(owner).PickMultipleFilesAsync();
            token.ThrowIfCancellationRequested();
            return files.Count == 0 ? null : new(files.Select(file => file.Path).ToArray(), []);
        }
        string? path = source == "folder"
            ? await ImageFileService.PickFolderAsync(owner)
            : await ImageFileService.PickImageListAsync(owner);
        if (path is null) return null;
        return await Task.Run(() => StackFrameImport.Read(path, source == "folder", token), token);
    }
}
