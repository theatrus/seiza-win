namespace Seiza.App.Services;

internal static class AtomicOutputFile
{
    public static void Write(
        string destinationPath,
        Action<string> writeStagingFile,
        CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The output path needs a parent directory.", nameof(destinationPath));
        string staging = Path.Combine(directory, $".seiza-stack-{Guid.NewGuid():N}.fits");
        try
        {
            writeStagingFile(staging);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch
            {
                // Never mask a stacking, cancellation, or publication failure.
            }
        }
    }
}
