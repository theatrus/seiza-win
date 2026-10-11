using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal static class ParallaxJson
{
    // Invalid draft numbers must remain editable/undoable. Native requests use
    // the strict context below; only structural draft comparisons allow NaN.
    private static readonly ParallaxJsonContext DraftContext = new(new JsonSerializerOptions(ParallaxJsonContext.Default.Options)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });
    internal static string SerializeRequest(ParallaxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteProperties(writer, JsonSerializer.SerializeToElement(request.Inputs, ParallaxJsonContext.Default.ParallaxInputs));
            WriteProperties(writer, JsonSerializer.SerializeToElement(request.Scene, ParallaxJsonContext.Default.ParallaxSceneSettings));
            WriteProperties(writer, JsonSerializer.SerializeToElement(request.Video, ParallaxJsonContext.Default.ParallaxVideoSettings));
            if (request.AutoTour is not null)
            {
                writer.WritePropertyName("autoTour");
                JsonSerializer.Serialize(writer, request.AutoTour, ParallaxJsonContext.Default.ParallaxTourOptions);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    internal static string SerializeVideo(ParallaxVideoSettings video) => JsonSerializer.Serialize(video, ParallaxJsonContext.Default.ParallaxVideoSettings);
    internal static string SerializePlan(ParallaxTourPlan plan) => JsonSerializer.Serialize(plan, ParallaxJsonContext.Default.ParallaxTourPlan);
    internal static string SerializeComposition(ParallaxComposition composition) => JsonSerializer.Serialize(composition, DraftContext.ParallaxComposition);
    internal static string SerializeScene(ParallaxSceneSettings scene) => JsonSerializer.Serialize(scene, DraftContext.ParallaxSceneSettings);

    internal static ParallaxTourPlan DeserializePlan(string json)
    {
        var result = JsonSerializer.Deserialize(json, ParallaxJsonContext.Default.ParallaxTourPlan)
            ?? throw new InvalidDataException("The tour plan was empty.");
        result.Validate();
        return result;
    }

    private static void WriteProperties(Utf8JsonWriter writer, JsonElement value)
    {
        foreach (JsonProperty property in value.EnumerateObject()) property.WriteTo(writer);
    }
}

internal sealed class ParallaxWatermarkConverter : JsonConverter<ParallaxWatermark>
{
    public override ParallaxWatermark Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.False or JsonTokenType.Null => ParallaxWatermark.Disabled,
        JsonTokenType.True => ParallaxWatermark.FromText("Rendered with seiza.fyi"),
        JsonTokenType.String => ParallaxWatermark.FromText(reader.GetString()!),
        _ => throw new JsonException("A watermark must be false or text."),
    };

    public override void Write(Utf8JsonWriter writer, ParallaxWatermark value, JsonSerializerOptions options)
    {
        if (value.Text is null) writer.WriteBooleanValue(false);
        else writer.WriteStringValue(value.Text);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ParallaxInputs))]
[JsonSerializable(typeof(ParallaxSceneSettings))]
[JsonSerializable(typeof(ParallaxVideoSettings))]
[JsonSerializable(typeof(ParallaxTourOptions))]
[JsonSerializable(typeof(ParallaxTourPlan))]
[JsonSerializable(typeof(ParallaxComposition))]
[JsonSerializable(typeof(ParallaxSummary))]
[JsonSerializable(typeof(ParallaxEvent))]
internal sealed partial class ParallaxJsonContext : JsonSerializerContext;
