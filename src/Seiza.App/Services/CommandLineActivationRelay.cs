using System.IO.Pipes;
using System.Text;

namespace Seiza.App.Services;

/// <summary>
/// Relays ordinary unpackaged command-line file activations to the process that
/// owns the Windows App SDK instance key. AppInstance redirects WinRT activation
/// data, but an MSI file association's quoted %1 argument is process-local.
/// </summary>
internal static class CommandLineActivationRelay
{
    private const string PipePrefix = "Seiza.Activation";

    public static string[] GetSupportedPaths() => Environment.GetCommandLineArgs()
        .Skip(1)
        .Select(argument => argument.Trim().Trim('"'))
        .Where(path => File.Exists(path) && ImageFileService.IsSupportedImage(path))
        .Select(Path.GetFullPath)
        .ToArray();

    public static async Task SendAsync(uint processId, IReadOnlyList<string> paths)
    {
        using NamedPipeClientStream client = new(
            ".",
            PipeName(processId),
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);

        await using StreamWriter writer = new(
            client,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false);
        foreach (string path in paths)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
            await writer.WriteLineAsync(encoded);
        }
    }

    public static async Task ListenAsync(uint processId, Action<string[]> received)
    {
        while (true)
        {
            try
            {
                using NamedPipeServerStream server = new(
                    PipeName(processId),
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();

                using StreamReader reader = new(server, Encoding.UTF8);
                List<string> paths = [];
                while (await reader.ReadLineAsync() is { } encoded)
                {
                    try
                    {
                        string path = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                        if (File.Exists(path) && ImageFileService.IsSupportedImage(path))
                        {
                            paths.Add(Path.GetFullPath(path));
                        }
                    }
                    catch (FormatException)
                    {
                        // Ignore malformed messages from unrelated local processes.
                    }
                }

                if (paths.Count > 0)
                {
                    received([.. paths]);
                }
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(250);
            }
        }
    }

    private static string PipeName(uint processId) => $"{PipePrefix}.{processId}";
}
