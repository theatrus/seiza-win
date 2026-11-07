namespace Seiza.App.Services;

internal sealed record StackFrameImportResult(string[] Paths, string[] Notes);

internal static class StackFrameImport
{
    // The viewer and stacking share list parsing, relative paths, and directory scanning.
    public static StackFrameImportResult Read(string source, bool folder, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<string> paths;
        var notes = new List<string>();
        if (folder)
        {
            ImageDirectoryScanResult scan = ImageDirectoryScanner.Scan(source, includeSubfolders: false, token);
            paths = scan.Paths;
            if (scan.SkippedFolderCount > 0) notes.Add($"{scan.SkippedFolderCount} folders could not be read.");
        }
        else
        {
            ImagePathListResult list = ImagePathList.Read(source, token);
            paths = list.Paths;
            notes.AddRange(list.Issues.Select(issue => $"Line {issue.Line}: {issue.Value} — {issue.Reason}"));
        }
        string[] stackable = paths.Where(ImageFileTypes.IsAstronomyImage).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int unsupported = paths.Count(path => !ImageFileTypes.IsAstronomyImage(path));
        if (unsupported > 0) notes.Add($"Skipped {unsupported} images: stacking accepts FITS/XISF files only.");
        token.ThrowIfCancellationRequested();
        return new(stackable, notes.ToArray());
    }
}
