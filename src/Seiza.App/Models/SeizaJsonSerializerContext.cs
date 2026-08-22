using System.Text.Json.Serialization;

namespace Seiza.App.Models;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImageMetadata))]
[JsonSerializable(typeof(CatalogStatus))]
[JsonSerializable(typeof(CatalogSetupProgress))]
[JsonSerializable(typeof(SolveResult))]
[JsonSerializable(typeof(StackOptionsPayload))]
[JsonSerializable(typeof(ImageStackDisposition))]
[JsonSerializable(typeof(ImageStackPipelineResult))]
[JsonSerializable(typeof(LiveStackNativeState))]
[JsonSerializable(typeof(CalibrationFrameProbe))]
[JsonSerializable(typeof(CalibrationPlanRequest))]
[JsonSerializable(typeof(CalibrationPlanResult))]
[JsonSerializable(typeof(CalibrationMasterBuildRequest))]
[JsonSerializable(typeof(CalibrationMasterBuildResult))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class SeizaJsonSerializerContext : JsonSerializerContext
{
}
