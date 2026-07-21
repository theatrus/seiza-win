using System.Text.Json;

namespace Seiza.App.Services;

internal sealed record SeizaBuildInfo(string Version, string Commit, Uri Repository)
{
    private const string BuildInfoFileName = "seiza-build-info.json";

    public static SeizaBuildInfo Current { get; } = Load();

    public Uri CommitUri => Commit.Length == 40
        ? new Uri(Repository, $"commit/{Commit}")
        : Repository;

    private static SeizaBuildInfo Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, BuildInfoFileName);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            string? version = ReadString(root, "version");
            string? commit = ReadString(root, "commit");
            string? repository = ReadString(root, "repository");
            if (string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(commit) ||
                string.IsNullOrWhiteSpace(repository))
            {
                throw new JsonException("The Seiza build information is incomplete.");
            }

            return new SeizaBuildInfo(
                version,
                commit,
                new Uri(repository.TrimEnd('/') + '/', UriKind.Absolute));
        }
        catch (IOException)
        {
            return Unknown();
        }
        catch (UnauthorizedAccessException)
        {
            return Unknown();
        }
        catch (JsonException)
        {
            return Unknown();
        }
        catch (UriFormatException)
        {
            return Unknown();
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static SeizaBuildInfo Unknown() => new(
        SeizaCore.Version,
        "unknown",
        new Uri("https://github.com/theatrus/seiza/", UriKind.Absolute));
}
