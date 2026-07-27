using System.Text.Json.Serialization;

namespace Seiza.App.Models;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StackOptionsPayload))]
[JsonSerializable(typeof(ImageStackDisposition))]
internal sealed partial class SeizaJsonSerializerContext : JsonSerializerContext
{
}
