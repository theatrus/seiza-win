using Seiza.App.Services;

namespace Seiza.App.Models;

internal enum ImageBrowserGrouping
{
    None,
    Folder,
    FileType,
    Filter,
}

internal sealed record ImageBrowserPathGroup(
    string Key,
    string Label,
    IReadOnlyList<string> Paths);

internal sealed record ImageBrowserGroupingResult(
    IReadOnlyList<ImageBrowserPathGroup> Groups,
    IReadOnlyList<string> OrderedPaths);

/// <summary>
/// Builds the visible browser groups and navigation order without reading any
/// files. Paths, rather than file names, remain the identity of each image.
/// </summary>
internal static class ImageBrowserGroupingModel
{
    public static ImageBrowserGroupingResult Create(
        IEnumerable<string> paths,
        string? rootFolder,
        ImageBrowserGrouping grouping,
        bool preserveInputOrder = false, IReadOnlyDictionary<string, string?>? filters = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Enum.IsDefined(grouping))
        {
            throw new ArgumentOutOfRangeException(nameof(grouping));
        }

        string? root = string.IsNullOrWhiteSpace(rootFolder)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
        var uniquePaths = new Dictionary<string, PathEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var entry = new PathEntry(path, root);
            if (!uniquePaths.TryGetValue(entry.FullPath, out PathEntry? previous)
                || StringComparer.Ordinal.Compare(path, previous.Path) < 0)
            {
                uniquePaths[entry.FullPath] = entry;
            }
        }

        PathEntry[] entries = uniquePaths.Values.ToArray();
        if (!preserveInputOrder || grouping != ImageBrowserGrouping.None)
            Array.Sort(entries, ComparePaths);
        if (entries.Length == 0)
        {
            return new ImageBrowserGroupingResult([], []);
        }

        if (grouping == ImageBrowserGrouping.None)
        {
            IReadOnlyList<string> ordered = Array.AsReadOnly(entries.Select(entry => entry.Path).ToArray());
            return new ImageBrowserGroupingResult(
                [new ImageBrowserPathGroup(string.Empty, string.Empty, ordered)],
                ordered);
        }

        var groups = new Dictionary<string, GroupBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (PathEntry entry in entries)
        {
            ImageFilenameFilter? filter = grouping == ImageBrowserGrouping.Filter
                ? ImageFilenameFilter.Resolve(entry.FullPath, filters) : null;
            string key = grouping switch
            {
                ImageBrowserGrouping.Folder => entry.Directory,
                ImageBrowserGrouping.Filter => filter?.Id ?? "unknown",
                _ => FileTypeLabel(entry.FullPath),
            };
            string label = grouping switch
            {
                ImageBrowserGrouping.Folder => entry.DirectoryLabel,
                ImageBrowserGrouping.Filter => filter?.Title ?? "Other",
                _ => key,
            };
            if (!groups.TryGetValue(key, out GroupBuilder? group))
            {
                group = new GroupBuilder(key, label, entry.IsRootDirectory);
                groups.Add(key, group);
            }
            else if (StringComparer.Ordinal.Compare(label, group.Label) < 0)
            {
                // The same Windows directory may arrive with different casing.
                // Its chosen key and label must not depend on enumeration order.
                group.Key = key;
                group.Label = label;
            }

            group.Paths.Add(entry.Path);
        }

        GroupBuilder[] orderedGroups = groups.Values.ToArray();
        Array.Sort(orderedGroups, (left, right) =>
        {
            if (grouping == ImageBrowserGrouping.Folder && left.IsRoot != right.IsRoot)
            {
                return left.IsRoot ? -1 : 1;
            }

            int result = CompareNatural(left.Label, right.Label);
            return result != 0 ? result : CompareNatural(left.Key, right.Key);
        });

        ImageBrowserPathGroup[] resultGroups = orderedGroups
            .Select(group => new ImageBrowserPathGroup(
                group.Key,
                group.Label,
                group.Paths.AsReadOnly()))
            .ToArray();
        return new ImageBrowserGroupingResult(
            Array.AsReadOnly(resultGroups),
            Array.AsReadOnly(resultGroups.SelectMany(group => group.Paths).ToArray()));
    }

    public static int FindAdjacentIndex(
        IReadOnlyList<string> orderedPaths,
        int selectedIndex,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(orderedPaths);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (selectedIndex < -1 || selectedIndex >= orderedPaths.Count)
            throw new ArgumentOutOfRangeException(nameof(selectedIndex));

        int start = selectedIndex < 0 ? (direction > 0 ? 0 : orderedPaths.Count - 1) : selectedIndex + direction;
        return start >= 0 && start < orderedPaths.Count ? start : -1;
    }

    private static int ComparePaths(PathEntry left, PathEntry right)
    {
        int result = NaturalStringComparer.Instance.Compare(left.FileName, right.FileName);
        if (result == 0)
        {
            result = CompareNatural(left.RelativePath, right.RelativePath);
        }

        if (result == 0)
        {
            result = StringComparer.Ordinal.Compare(left.FileName, right.FileName);
        }

        return result != 0 ? result : StringComparer.Ordinal.Compare(left.Path, right.Path);
    }

    private static int CompareNatural(string left, string right)
    {
        int result = NaturalStringComparer.Instance.Compare(left, right);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left, right);
    }

    private static string FileTypeLabel(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".FIT" or ".FTS" or ".FITS" => "FITS",
        ".JPG" or ".JPEG" or ".JFIF" => "JPEG",
        ".TIF" or ".TIFF" => "TIFF",
        ".PNG" => "PNG",
        ".XISF" => "XISF",
        "" => "Other",
        string extension => extension[1..],
    };

    private sealed class PathEntry
    {
        public PathEntry(string path, string? root)
        {
            Path = path;
            FullPath = System.IO.Path.GetFullPath(path);
            FileName = System.IO.Path.GetFileName(FullPath);
            Directory = System.IO.Path.GetDirectoryName(FullPath) ?? FullPath;
            RelativePath = root is null ? FullPath : System.IO.Path.GetRelativePath(root, FullPath);
            IsRootDirectory = root is not null
                && StringComparer.OrdinalIgnoreCase.Equals(root, Directory);
            if (IsRootDirectory)
            {
                DirectoryLabel = "(root)";
            }
            else if (root is not null)
            {
                string relativeDirectory = System.IO.Path.GetRelativePath(root, Directory);
                DirectoryLabel = IsWithinRoot(relativeDirectory) ? relativeDirectory : Directory;
            }
            else
            {
                DirectoryLabel = Directory;
            }
        }

        public string Path { get; }

        public string FullPath { get; }

        public string FileName { get; }

        public string Directory { get; }

        public string DirectoryLabel { get; }

        public string RelativePath { get; }

        public bool IsRootDirectory { get; }

        private static bool IsWithinRoot(string path) =>
            !System.IO.Path.IsPathRooted(path)
            && path != ".."
            && !path.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.StartsWith($"..{System.IO.Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class GroupBuilder(string key, string label, bool isRoot)
    {
        public string Key { get; set; } = key;

        public string Label { get; set; } = label;

        public bool IsRoot { get; } = isRoot;

        public List<string> Paths { get; } = [];
    }
}
