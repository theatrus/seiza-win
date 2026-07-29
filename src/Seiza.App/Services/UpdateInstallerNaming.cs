namespace Seiza.App.Services;

internal static class UpdateInstallerNaming
{
    public static string FromDownloadLink(string? downloadLink)
    {
        if (!Uri.TryCreate(downloadLink, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update feed did not provide a valid HTTPS installer URL.");
        }

        string fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetExtension(fileName), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update feed did not provide a Windows Installer (.msi) file.");
        }

        return fileName;
    }
}
