using System.Text.Json;
using System.Text.Json.Nodes;
using Seiza.App.Services;

namespace Seiza.App.Models;

internal enum ColorTool { Narrowband, Continuum, StretchStars, RestoreStars, RestorePreparedStars }

internal sealed record ColorToolsRequest
{
    public ColorTool Tool { get; init; }
    public string[] Inputs { get; init; } = [];
    public string? Output { get; init; }
    public bool Aligned { get; init; }
    public bool LinearConfirmed { get; init; }
    public bool InputDisplay { get; init; }
    public bool NebulaDisplay { get; init; } = true;
    public string Palette { get; init; } = "hoo";
    public double TargetMedian { get; init; } = 0.2;
    public bool Balance { get; init; } = true;
    public double ShadowProtection { get; init; } = 1;
    public double OiiiBoost { get; init; } = 1;
    public double SiiBoost { get; init; } = 1;
    public bool ExcludeStars { get; init; }
    public double GreenReduction { get; init; }
    public string? Structure { get; init; }
    public double StructureAmount { get; init; }
    public double?[] Saturations { get; init; } = [];
    public string?[] ClippingMasks { get; init; } = [];
    public bool PsfMatching { get; init; }
    public double[]? ManualCoefficients { get; init; }
    public JsonElement? Plan { get; init; }
    public string? PlanPath { get; init; }
    public double StarAmount { get; init; } = 5;
    public double StarReduction { get; init; }

    public IEnumerable<string> ProtectedInputs => Inputs.Concat(ClippingMasks)
        .Append(Structure).Append(PlanPath).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!));

    public void Validate(bool analyze = false, bool requireOutput = true)
    {
        if (!Enum.IsDefined(Tool)) throw new ArgumentException("Choose a supported color tool.");
        if (!Aligned) throw new InvalidOperationException("Confirm that all inputs share the same aligned pixel grid. Equal dimensions alone do not establish alignment.");
        if ((Tool == ColorTool.Continuum || Tool is ColorTool.StretchStars or ColorTool.RestoreStars ||
            (Tool == ColorTool.Narrowband && !InputDisplay) || (Tool == ColorTool.RestorePreparedStars && !NebulaDisplay)) && !LinearConfirmed)
            throw new InvalidOperationException("Confirm that scientific and star-extraction sources are linear, on their original common photometric scale.");
        bool count = Tool switch
        {
            ColorTool.Narrowband => (Palette == "hoo" && Inputs.Length == 2) || (Palette == "sho" && Inputs.Length == 3),
            ColorTool.Continuum => Inputs.Length is >= 2 and <= 4,
            ColorTool.RestoreStars => Inputs.Length == 3,
            _ => Inputs.Length == 2,
        };
        if (!count || Inputs.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Choose every required input image for this operation.");
        if (analyze && Tool != ColorTool.Continuum) throw new ArgumentException("Stellar analysis applies to continuum subtraction.");
        foreach (string path in ProtectedInputs)
            if (!File.Exists(path)) throw new FileNotFoundException("A selected input, clipping mask or plan is missing.", path);
        if (Tool == ColorTool.Continuum)
        {
            if (Saturations.Length != Inputs.Length || ClippingMasks.Length != Inputs.Length)
                throw new ArgumentException("Each continuum input needs its own saturation / clipping-mask choice.");
            if (Saturations.Any(value => value is double v && (!double.IsFinite(v) || v <= 0)))
                throw new ArgumentException("Saturation must be positive in the prepared image's physical units, or unspecified. Never use the observed maximum.");
            if (!analyze && (Plan is null) == (ManualCoefficients is null))
                throw new InvalidOperationException("Analyze and review a fitted plan, load a saved exact-source plan, or explicitly choose manual coefficients.");
            if (ManualCoefficients is not null && (ManualCoefficients.Length != Inputs.Length - 1 ||
                ManualCoefficients.Any(value => !double.IsFinite(value) || value < 0)))
                throw new ArgumentException("Choose one finite nonnegative coefficient per continuum reference.");
        }
        static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        bool restore = Tool is ColorTool.RestoreStars or ColorTool.RestorePreparedStars;
        if (((Tool == ColorTool.Narrowband && !InputDisplay) || (restore && !NebulaDisplay)) &&
            (!double.IsFinite(TargetMedian) || TargetMedian <= 0 || TargetMedian >= 1))
            throw new ArgumentException("Choose a display target median between zero and one.");
        if (Tool == ColorTool.Narrowband && Balance && (!Range(ShadowProtection, 0, 1) ||
            !double.IsFinite(OiiiBoost) || OiiiBoost <= 0 || !double.IsFinite(SiiBoost) || SiiBoost <= 0))
            throw new ArgumentException("Shadow protection must be 0–1 and channel boosts must be positive.");
        if ((Tool == ColorTool.Narrowband || restore) && !Range(GreenReduction, 0, 1))
            throw new ArgumentException("Green reduction must be 0–1.");
        if (Tool == ColorTool.Narrowband && !string.IsNullOrWhiteSpace(Structure) && !Range(StructureAmount, 0, 1))
            throw new ArgumentException("Broadband structure mix must be 0–1.");
        if (Tool is ColorTool.StretchStars or ColorTool.RestoreStars && !Range(StarAmount, 0, 8))
            throw new ArgumentException("RGB star stretch amount must be 0–8.");
        if (restore && !Range(StarReduction, 0, 1)) throw new ArgumentException("Star reduction must be 0–1.");
        if (!analyze && requireOutput) ProtectDestination(Output ?? throw new ArgumentException("Choose a separate output image."));
    }

    public void ProtectDestination(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".fits" or ".fit" or ".fts" or ".xisf"))
            throw new ArgumentException("Choose a FITS or XISF output to preserve float samples and transfer semantics.");
        if (ProtectedInputs.Any(input => StackSourceIdentity.RefersToSameFile(input, path)))
            throw new InvalidOperationException("An output cannot replace any input image, clipping mask or saved plan.");
    }

    public string ToNativeJson(string? stagingOutput, bool analyze)
    {
        static JsonArray ArrayOf<T>(IEnumerable<T> values)
        {
            var array = new JsonArray();
            foreach (T value in values)
                array.Add(value switch { string s => JsonValue.Create(s), double d => JsonValue.Create(d), null => null,
                    _ => throw new ArgumentException("Unsupported native array element.") });
            return array;
        }
        string operation = Tool switch
        {
            ColorTool.Narrowband => "narrowband", ColorTool.Continuum => analyze ? "analyze-continuum" : "subtract-continuum",
            ColorTool.StretchStars => "stretch-stars", ColorTool.RestoreStars => "restore-stars",
            _ => "restore-prepared-stars",
        };
        var root = new JsonObject
        {
            ["operation"] = operation, ["inputs"] = ArrayOf(Inputs.Select(Path.GetFullPath)), ["output"] = stagingOutput,
        };
        if (Tool == ColorTool.Narrowband)
        {
            root["inputDisplay"] = InputDisplay; root["palette"] = Palette; root["balance"] = Balance;
            if (Balance) root["balanceOptions"] = new JsonObject { ["shadow_protection"] = ShadowProtection,
                ["oiii_boost"] = OiiiBoost, ["sii_boost"] = SiiBoost,
                ["star_mask_radius"] = ExcludeStars ? 5.0 : null };
            if (!string.IsNullOrWhiteSpace(Structure)) { root["structure"] = Path.GetFullPath(Structure); root["structureAmount"] = StructureAmount; }
            if (!InputDisplay) root["targetMedian"] = TargetMedian;
        }
        if (Tool == ColorTool.Continuum)
        {
            root["saturations"] = ArrayOf(Saturations);
            root["clippingMasks"] = ArrayOf(ClippingMasks.Select(path => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path)));
            root["psfMatching"] = PsfMatching;
            root["manualCoefficients"] = ManualCoefficients is null ? null : ArrayOf(ManualCoefficients);
            root["plan"] = Plan is JsonElement plan ? JsonNode.Parse(plan.GetRawText()) : null;
        }
        if (Tool is ColorTool.StretchStars or ColorTool.RestoreStars) root["starAmount"] = StarAmount;
        if (Tool is ColorTool.RestoreStars or ColorTool.RestorePreparedStars)
        {
            root["starReduction"] = StarReduction; root["nebulaDisplay"] = NebulaDisplay;
            if (!NebulaDisplay) root["targetMedian"] = TargetMedian;
        }
        if (Tool is ColorTool.Narrowband or ColorTool.RestoreStars or ColorTool.RestorePreparedStars) root["greenReduction"] = GreenReduction;
        return root.ToJsonString();
    }
}

internal sealed record ColorToolsResult(string? Output, string Status, string Message, JsonElement Report);
