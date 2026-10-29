using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Seiza.App.Services;

namespace Seiza.App.Models;

// The native coordinator owns policy. Preserve its records and setting keys verbatim.
internal sealed record CalibrationLibraryPreferences
{
    public Dictionary<string, JsonNode?> Settings { get; init; } = [];
    public Dictionary<string, Dictionary<string, JsonNode?>> Rigs { get; init; } = [];
    public List<LibraryChoice> Choices { get; init; } = [];
    public List<JsonObject> GroupOverrides { get; init; } = [];
    public List<LibraryLightChoice> LightChoices { get; init; } = [];
    public string Incomplete { get; set; } = "stop";
    public string FlatAssociation { get; set; } = "automatic";
    public string DarkAssociation { get; set; } = "automatic";
    public bool FlatStarMasking { get; set; }
    public JsonObject FlatStability { get; init; } = new()
    {
        ["enabled"] = true, ["trimUnstable"] = true,
        ["tryNextSession"] = true, ["retainUnstable"] = true,
    };

    public CalibrationLibraryPreferences Copy() => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(this, LibraryJsonContext.Default.CalibrationLibraryPreferences),
        LibraryJsonContext.Default.CalibrationLibraryPreferences)!;
}

internal sealed record LibraryChoice(string Path, string Fingerprint, string Choice);
internal sealed record LibraryLightChoice(string Path, string Fingerprint, JsonObject Row);
internal sealed record LibrarySource(string Path, string Fingerprint);
internal sealed record LibrarySetting(string Key, JsonNode? Value);
internal sealed record LibraryRigSettings(string RigUuid, LibrarySetting[] Settings);
internal sealed record LibraryRig(string Id, string Name);
internal sealed record LibraryFrame(string Path, string Fingerprint, string Id, string Kind,
    bool IsMaster, string? Grade, string? Reason, string MeasurementState, string? Error);
internal sealed record LibraryField(string Key, string Group, string Label, string Help,
    JsonObject Kind, JsonNode? Default);
internal sealed record LibrarySchema(LibraryField[] Settings);
internal sealed record LibraryProgress(string Phase, ulong Completed, ulong Total);

internal sealed record LibraryRequest
{
    public required string Operation { get; init; }
    public string? Library { get; init; }
    public string[] Lights { get; init; } = [];
    public string[] Provided { get; init; } = [];
    public string Cache { get; init; } = "";
    public LibrarySetting[] Settings { get; init; } = [];
    public LibraryRigSettings[] Rigs { get; init; } = [];
    public LibraryChoice[] Choices { get; init; } = [];
    public JsonObject[] GroupOverrides { get; init; } = [];
    public LibraryLightChoice[] LightChoices { get; init; } = [];
    public string? ReviewKey { get; init; }
    public string Incomplete { get; init; } = "stop";
    public bool FlatStarMasking { get; init; }
    public JsonObject? FlatStability { get; init; }
    public string FlatAssociation { get; init; } = "automatic";
    public string DarkAssociation { get; init; } = "automatic";
    public LibrarySource[] Sources { get; init; } = [];

    public static LibraryRequest Create(string operation, string? library,
        IEnumerable<string> lights, IEnumerable<string> provided,
        CalibrationLibraryPreferences preferences, string? reviewKey = null) => new()
    {
        Operation = operation, Library = library, Lights = lights.Select(System.IO.Path.GetFullPath).ToArray(),
        Provided = provided.Select(System.IO.Path.GetFullPath).ToArray(),
        Cache = System.IO.Path.Combine(CalibrationCachePaths.ForLibrary(library ?? "provided-calibration-frames"), "graded-library"),
        Settings = Rows(preferences.Settings),
        Rigs = preferences.Rigs.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new LibraryRigSettings(p.Key, Rows(p.Value))).ToArray(),
        Choices = [.. preferences.Choices], GroupOverrides = preferences.GroupOverrides.Select(r => (JsonObject)r.DeepClone()).ToArray(),
        LightChoices = preferences.LightChoices.Select(r => r with { Row = (JsonObject)r.Row.DeepClone() }).ToArray(),
        ReviewKey = reviewKey, Incomplete = preferences.Incomplete,
        FlatStarMasking = preferences.FlatStarMasking, FlatStability = (JsonObject)preferences.FlatStability.DeepClone(),
        FlatAssociation = preferences.FlatAssociation, DarkAssociation = preferences.DarkAssociation,
    };
    private static LibrarySetting[] Rows(Dictionary<string, JsonNode?> values) => values
        .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new LibrarySetting(p.Key, p.Value?.DeepClone())).ToArray();
}

internal sealed record LibraryFileSession(string? Bias, string? Dark, string? Flat,
    double? DarkExposureSeconds, double? Pedestal, double? NormalizedFullScale, JsonObject Tolerances)
{
    [JsonIgnore] public ImageStackCalibration Calibration => new()
    {
        BiasPath = Bias, DarkPath = Dark, FlatPath = Flat,
        OverridesDarkExposure = Dark is not null && DarkExposureSeconds is not null,
        DarkExposureSeconds = DarkExposureSeconds ?? 0,
    };

    // StackOptions has an older snake_case subdocument, limited to application gates.
    public string OptionsJson(ImageStackOptions options)
    {
        if (Pedestal is not null || NormalizedFullScale is not null)
            throw new InvalidOperationException("This session requires the session-aware opener.");
        var root = JsonNode.Parse(options.ToJson())!.AsObject();
        var gate = new JsonObject();
        foreach (var (native, wire) in new[] {
            ("allow_flat_gain_offset_mismatch", "allowFlatGainOffsetMismatch"),
            ("exposure_seconds", "exposureSeconds"), ("exposure_fraction", "exposureFraction"),
            ("dark_temperature_c", "darkTemperatureC"), ("rotation_deg", "rotationDeg"),
            ("focal_length_mm", "focalLengthMm") })
            if (Tolerances[wire] is { } value) gate[native] = value.DeepClone();
        if (Tolerances["lightRotation"] is { } rotation) gate["light_rotation"] = rotation.DeepClone();
        root["match_tolerances"] = gate;
        return root.ToJsonString();
    }
}

internal sealed record LibraryReview(string ReviewKey, LibraryFrame[] Frames, LibraryRig[] Rigs,
    JsonObject Snapshot, JsonObject Grouping, JsonObject FlatWork, JsonObject[] LightOptions,
    string[] Warnings, bool[] Missing, string[] Paths, string[] LightPaths,
    LibraryFileSession[] FileSessions, LibrarySource[] Sources)
{
    [JsonIgnore] public int[] Assignments => Snapshot["assignments"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
    [JsonIgnore] public int SessionCount => Snapshot["sessions"]!.AsArray().Count;
    public void Validate(LibraryRequest request)
    {
        int count = request.Lights.Length;
        if (string.IsNullOrWhiteSpace(ReviewKey) || Paths.Length != count || LightPaths.Length != count ||
            LightOptions.Length != count || Missing.Length != count || Assignments.Length != count ||
            Snapshot["selections"]!.AsArray().Count != count ||
            Assignments.Any(i => i < 0 || i >= SessionCount) ||
            (request.Operation == "execute" && FileSessions.Length != SessionCount))
            throw new SeizaCoreException("The native calibration assignment plan is incomplete.");
    }
}

internal sealed class LibraryPreparedPlan
{
    private readonly Dictionary<string, int> _indices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibrarySource> _sources = new(StringComparer.OrdinalIgnoreCase);
    public LibraryReview Review { get; }
    public string[] OriginalPaths { get; }
    public int[] Assignments { get; }
    public LibraryPreparedPlan(LibraryReview review, IReadOnlyList<string> originals)
    {
        Review = review; OriginalPaths = originals.ToArray(); Assignments = review.Assignments;
        if (OriginalPaths.Length != review.Paths.Length || Assignments.Length != OriginalPaths.Length || review.FileSessions.Length != review.SessionCount)
            throw new InvalidOperationException("The prepared calibration plan is incomplete.");
        for (int i = 0; i < OriginalPaths.Length; i++)
            foreach (string path in new[] { OriginalPaths[i], review.Paths[i], review.LightPaths[i] })
                _indices[LiveStackPath.NormalizeForComparison(path)] = i;
        foreach (LibrarySource source in review.Sources) _sources[LiveStackPath.NormalizeForComparison(source.Path)] = source;
    }
    public int Index(string path)
    {
        if (_indices.TryGetValue(LiveStackPath.NormalizeForComparison(path), out int index)) return index;
        for (int i = 0; i < OriginalPaths.Length; i++)
            if (StackSourceIdentity.RefersToSameFile(path, Review.LightPaths[i])) return i;
        throw new InvalidOperationException("A light is missing from the reviewed calibration plan.");
    }
    public LibraryFileSession Session(string path) => Review.FileSessions[Assignments[Index(path)]];
    public string[] Inputs(IEnumerable<string> inputs, bool exclude) => inputs
        .Where(p => !exclude || !Review.Missing[Index(p)]).Select(p => Review.Paths[Index(p)]).ToArray();
    public void VerifyActive(string path)
    {
        int i = Index(path);
        LibraryFileSession session = Session(path);
        string[] active = new[] { OriginalPaths[i], Review.Paths[i], Review.LightPaths[i], session.Bias, session.Dark, session.Flat }
            .OfType<string>().ToArray();
        CalibrationLibraryService.Verify(active.Select(p => _sources.GetValueOrDefault(LiveStackPath.NormalizeForComparison(p)))
            .OfType<LibrarySource>().Distinct().ToArray());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CalibrationLibraryPreferences))]
[JsonSerializable(typeof(LibraryRequest))]
[JsonSerializable(typeof(LibraryReview))]
[JsonSerializable(typeof(LibrarySchema))]
[JsonSerializable(typeof(LibraryFileSession[]))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;
