using System.Text.Json.Serialization;

namespace Seiza.App.Models;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ImageMetadata))]
[JsonSerializable(typeof(CatalogStatus))]
[JsonSerializable(typeof(CatalogSetupProgress))]
internal sealed partial class SeizaJsonSerializerContext : JsonSerializerContext
{
}
