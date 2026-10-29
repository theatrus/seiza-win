using System.Text.Json;
using System.Text.Json.Nodes;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class FlatGroupPolicyFactAttribute : FactAttribute
{
    public FlatGroupPolicyFactAttribute()
    {
        if (!CalibrationCoreCapabilities.Current.SupportsFlatGroupPlanning)
            Skip = "The pinned published C ABI has no seiza_flat_group_plan_json; awaiting the released Seiza #385 contract.";
    }
}

public sealed class FlatGroupPolicyTests
{
    [Fact]
    public async Task UnavailablePolicyHasAnExplicitCapabilityBoundary()
    {
        var unavailable = new CalibrationCoreCapabilities(false, false, false);
        Assert.Throws<SeizaCoreException>(unavailable.RequireBiasDarkQuality);
        Assert.Contains("#385", Assert.Throws<SeizaCoreException>(unavailable.RequireFlatGroupPlanning).Message);
        new CalibrationCoreCapabilities(true, true, true).RequireFlatGroupPlanning();
        using var request = JsonDocument.Parse("{}");
        if (!CalibrationCoreCapabilities.Current.SupportsFlatGroupPlanning)
            await Assert.ThrowsAsync<SeizaCoreException>(() => FlatGroupPolicyService.EvaluateAsync(request.RootElement));
    }

    [Fact]
    public async Task CancellationPrecedesAnyNativePolicyCall()
    {
        using var request = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<OperationCanceledException>(() => FlatGroupPolicyService.EvaluateAsync(request.RootElement, new CancellationToken(true)));
    }

    [FlatGroupPolicyFact]
    public async Task SharedSnapshotKeepsCapturedFamiliesEventsAndUncertainty()
    {
        using var input = CalibrationParityFixtures.Read("flat-groups-request.json");
        using var expected = CalibrationParityFixtures.Read("flat-groups-expected.json");
        // Only the test converts the exact Rust fixture's record field spelling to the C wire format.
        // Opaque identifiers and enum values remain untouched. Production forwards C JSON verbatim.
        using var request = JsonDocument.Parse(CamelFields(input.RootElement)!.ToJsonString());
        using var wireExpected = JsonDocument.Parse(CamelFields(expected.RootElement)!.ToJsonString());
        JsonElement result = await FlatGroupPolicyService.EvaluateAsync(request.RootElement);
        foreach (string field in new[] { "families", "boundaries", "events" })
            Assert.True(JsonElement.DeepEquals(result.GetProperty(field), wireExpected.RootElement.GetProperty(field)), field);
        string[] positions = ["before", "during", "after"];
        for (int i = 0; i < positions.Length; i++)
            Assert.True(JsonElement.DeepEquals(result.GetProperty("lights")[i].GetProperty("events")[0].GetProperty("placement"),
                wireExpected.RootElement.GetProperty("placements")[0].GetProperty(positions[i])));
    }

    private static JsonNode? CamelFields(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject().Select(property =>
            KeyValuePair.Create(ToCamel(property.Name), CamelFields(property.Value)))),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(CamelFields).ToArray()),
        _ => JsonNode.Parse(element.GetRawText()),
    };

    private static string ToCamel(string name)
    {
        string[] words = name.Split('_');
        return words[0] + string.Concat(words.Skip(1).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
