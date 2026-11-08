namespace Seiza.App.Services;

internal sealed record ImageDirectoryScanResult(
    string RootPath,
    IReadOnlyList<string> Paths,
    int SkippedReparseFolderCount,
    int InaccessibleFolderCount)
{
    public int SkippedFolderCount => SkippedReparseFolderCount + InaccessibleFolderCount;
}

internal static class ImageDirectoryScanner
{
    public static ImageDirectoryScanResult Scan(
        string folderPath,
        bool includeSubfolders,
        CancellationToken cancellationToken = default) =>
        Scan(folderPath, includeSubfolders, EnumerateEntries, cancellationToken);

    // A narrow enumeration seam keeps error and mid-enumeration cancellation
    // behavior testable without changing permissions on real user directories.
    internal static ImageDirectoryScanResult Scan(
        string folderPath,
        bool includeSubfolders,
        Func<DirectoryInfo, IEnumerable<FileSystemInfo>> enumerateEntries,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(enumerateEntries);
        cancellationToken.ThrowIfCancellationRequested();

        string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        DirectoryInfo root = new(rootPath);
        List<string> paths = [];
        Stack<DirectoryInfo> pendingFolders = new();
        pendingFolders.Push(root);
        int skippedReparseFolderCount = 0;
        int inaccessibleFolderCount = 0;

        while (pendingFolders.TryPop(out DirectoryInfo? folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isRoot = ReferenceEquals(folder, root);
            List<string> folderImages = [];
            List<DirectoryInfo> childFolders = [];
            int folderReparseCount = 0;

            try
            {
                // The explicitly selected root may itself be a junction, but
                // never follow a descendant reparse point. Recheck here as well
                // as during discovery in case a queued folder has changed.
                if (!isRoot)
                {
                    FileAttributes folderAttributes = folder.Attributes;
                    if (folderAttributes == (FileAttributes)(-1))
                    {
                        throw new DirectoryNotFoundException($"Image subfolder no longer exists: {folder.FullName}");
                    }
                    if ((folderAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedReparseFolderCount++;
                        continue;
                    }
                }

                foreach (FileSystemInfo entry in enumerateEntries(folder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = entry.Attributes;
                    if (attributes == (FileAttributes)(-1))
                    {
                        // An individual entry can disappear while an acquisition
                        // folder is being scanned. It is no longer a candidate.
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!includeSubfolders)
                        {
                            continue;
                        }

                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            folderReparseCount++;
                        }
                        else
                        {
                            childFolders.Add(new DirectoryInfo(entry.FullName));
                        }
                    }
                    else if (ImageFileTypes.IsSupportedImage(entry.FullName))
                    {
                        folderImages.Add(entry.FullName);
                    }
                    else if (WindowsShortcutTarget.TryGetImagePath(entry.FullName) is { } targetPath)
                    {
                        folderImages.Add(targetPath);
                    }
                }
            }
            catch (Exception exception) when (
                !isRoot && exception is UnauthorizedAccessException or IOException)
            {
                // A vanished, offline, or inaccessible descendant must not hide
                // the readable images in the rest of the selected directory.
                inaccessibleFolderCount++;
                continue;
            }

            paths.AddRange(folderImages);
            skippedReparseFolderCount += folderReparseCount;
            foreach (DirectoryInfo child in childFolders)
            {
                pendingFolders.Push(child);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Multiple shortcuts (or an image beside its shortcut) represent one image.
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Compare relative paths, not only basenames: duplicate names in
        // different folders have a stable place in both grouped and flat views.
        // The ordinal tie-break also stabilizes case-sensitive shares.
        try
        {
            paths.Sort((left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string leftRelative = Path.GetRelativePath(rootPath, left);
                string rightRelative = Path.GetRelativePath(rootPath, right);
                int natural = NaturalStringComparer.Instance.Compare(leftRelative, rightRelative);
                return natural != 0 ? natural : StringComparer.Ordinal.Compare(leftRelative, rightRelative);
            });
        }
        catch (InvalidOperationException exception) when (
            exception.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // List.Sort wraps comparer exceptions; preserve cancellation rather
            // than reporting a sort failure when a large folder is superseded.
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();

        return new ImageDirectoryScanResult(
            rootPath,
            paths.AsReadOnly(),
            skippedReparseFolderCount,
            inaccessibleFolderCount);
    }

    private static IEnumerable<FileSystemInfo> EnumerateEntries(DirectoryInfo folder) =>
        folder.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        });
}
