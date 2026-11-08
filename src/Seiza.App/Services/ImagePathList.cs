using System.Text;

namespace Seiza.App.Services;

internal sealed record ImagePathListIssue(string ListPath, long Line, string Value, string Reason);
internal sealed record ImagePathListResult(IReadOnlyList<string> Paths, IReadOnlyList<ImagePathListIssue> Issues)
{
    public int SkippedCount => Issues.Count;
}

internal static class ImagePathList
{
    public static bool IsSupported(string path) => ImageFileTypes.IsSupportedImageList(path);

    public static ImagePathListResult Read(string listPath, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(listPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        List<string> paths = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenIdentities = new(StringComparer.Ordinal);
        List<ImagePathListIssue> issues = [];

        void AddPath(string value, long line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = value.Trim();
            bool isImage = false;
            try
            {
                if (value.Length == 0)
                {
                    issues.Add(new(fullPath, line, value, "Missing image path."));
                    return;
                }
                string path = Path.GetFullPath(value, directory);
                bool shortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
                isImage = shortcut || ImageFileTypes.IsSupportedImage(path);
                if (shortcut)
                {
                    path = WindowsShortcutTarget.TryGetImagePath(path)
                        ?? throw new IOException("Shortcut target is missing or is not a supported image.");
                }
                if (!isImage)
                {
                    issues.Add(new(fullPath, line, value, "Unsupported image format."));
                    return;
                }
                if (Directory.Exists(path)) throw new IOException("List entries must name image files, not folders.");
                if (seen.Contains(path)) return;
                // Verify access to the bytes, rather than treating every failed
                // File.Exists check as a missing file. Opening never changes it.
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                string? identity = WindowsFileIdentity.TryGet(path);
                if (identity is not null && !seenIdentities.Add(identity)) return;
                seen.Add(path);
                paths.Add(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or
                NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add(new(fullPath, line, value, exception.Message));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        string text = ReadText(fullPath, cancellationToken);
        if (Path.GetExtension(fullPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            int pathColumn = 0;
            bool lookingForHeader = true;
            var entries = new List<(string Value, long Line)>();
            ReadCsv(text, fullPath, (fields, line) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lookingForHeader)
                {
                    int header = Array.FindIndex(fields, field =>
                        field.Trim().ToLowerInvariant() is "path" or "filepath" or "file_path" or "file" or "filename");
                    if (header >= 0)
                    {
                        pathColumn = header;
                        lookingForHeader = false;
                        // SubframeSelector exports settings before the table.
                        // Those rows are metadata, not skipped image entries.
                        entries.Clear();
                        return;
                    }
                }
                string value = fields.Length > pathColumn ? fields[pathColumn] : string.Empty;
                entries.Add((value, line));
                // Once headerless image data begins, never reinterpret a later
                // metadata cell named "file" as a new column header.
                if (ImageFileTypes.IsSupportedImage(value) || Path.GetExtension(value).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    lookingForHeader = false;
            }, cancellationToken);
            foreach (var (value, line) in entries) AddPath(value, line);
        }
        else
        {
            using var reader = new StringReader(text);
            long lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                string value = line.Trim();
                if (value.Length == 0) continue;
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                if (string.IsNullOrWhiteSpace(value)) continue;
                AddPath(value, lineNumber);
            }
        }
        return new ImagePathListResult(paths.AsReadOnly(), issues.AsReadOnly());
    }

    private static string ReadText(string path, CancellationToken token)
    {
        byte[] data = File.ReadAllBytes(path);
        token.ThrowIfCancellationRequested();
        Encoding encoding = new UTF8Encoding(false, true);
        int preamble = 0;
        if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe)
        {
            encoding = new UnicodeEncoding(false, true, true);
            preamble = 2;
        }
        else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff)
        {
            encoding = new UnicodeEncoding(true, true, true);
            preamble = 2;
        }
        else if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) preamble = 3;
        try
        {
            string text = encoding.GetString(data, preamble, data.Length - preamble);
            if (text.Length > 0 && text[0] == '\ufeff') text = text[1..];
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Image list {path} must use valid UTF-8 or UTF-16 text.", exception);
        }
    }

    private enum CsvState { Unquoted, Quoted, AfterQuote, AfterQuoteWhitespace }

    // Match the Mac importer: quotes must enclose a complete field. Keep
    // physical record starts while parsing embedded newlines, and validate
    // the whole CSV before opening any listed images or publishing a result.
    private static void ReadCsv(string text, string path, Action<string[], long> row, CancellationToken token)
    {
        List<string> fields = [];
        var field = new StringBuilder();
        CsvState state = CsvState.Unquoted;
        long line = 1, rowLine = 1;
        bool hasContent = false;

        InvalidDataException InvalidCsv() => new($"Invalid CSV in {path} at line {rowLine}. Check the field quoting.");
        void FinishRow()
        {
            if (hasContent || fields.Count > 0 || !string.IsNullOrWhiteSpace(field.ToString()))
            {
                fields.Add(field.ToString().Trim());
                row(fields.ToArray(), rowLine);
            }
            fields.Clear();
            field.Clear();
            hasContent = false;
            state = CsvState.Unquoted;
        }

        for (int offset = 0; offset < text.Length; offset++)
        {
            if ((offset & 1023) == 0) token.ThrowIfCancellationRequested();
            char character = text[offset];
            if (state == CsvState.Quoted)
            {
                if (character == '"') state = CsvState.AfterQuote;
                else field.Append(character);
                if (character == '\n') line++;
                continue;
            }
            switch (character)
            {
                case '"':
                    if (state == CsvState.AfterQuote) { field.Append('"'); state = CsvState.Quoted; }
                    else if (state == CsvState.Unquoted && string.IsNullOrWhiteSpace(field.ToString()))
                    {
                        field.Clear(); state = CsvState.Quoted; hasContent = true;
                    }
                    else throw InvalidCsv();
                    break;
                case ',':
                    fields.Add(field.ToString().Trim()); field.Clear(); hasContent = true; state = CsvState.Unquoted;
                    break;
                case '\n':
                    FinishRow(); line++; rowLine = line;
                    break;
                default:
                    if (state is CsvState.AfterQuote or CsvState.AfterQuoteWhitespace)
                    {
                        if (character is not (' ' or '\t')) throw InvalidCsv();
                        state = CsvState.AfterQuoteWhitespace;
                    }
                    else field.Append(character);
                    break;
            }
        }
        token.ThrowIfCancellationRequested();
        if (state == CsvState.Quoted) throw InvalidCsv();
        FinishRow();
    }
}
