using System.Text.Json.Serialization;

namespace Seiza.App.Models;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ImageMetadata))]
[JsonSerializable(typeof(CatalogStatus))]
[JsonSerializable(typeof(CatalogSetupProgress))]
[JsonSerializable(typeof(SolveResult))]
[JsonSerializable(typeof(StackOptionsPayload))]
[JsonSerializable(typeof(ImageStackDisposition))]
internal sealed partial class SeizaJsonSerializerContext : JsonSerializerContext
{
}
