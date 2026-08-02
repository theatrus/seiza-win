using System.Text.Json;

namespace Seiza.App.Models;

internal enum FitsBackgroundCorrectionMode
{
    Subtract,
    Divide,
}

internal static class FitsBackgroundCorrectionModeExtensions
{
    public static string Title(this FitsBackgroundCorrectionMode mode) => mode switch
    {
        FitsBackgroundCorrectionMode.Subtract => "Subtract Gradient",
        FitsBackgroundCorrectionMode.Divide => "Correct Illumination",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string Help(this FitsBackgroundCorrectionMode mode) => mode switch
    {
        FitsBackgroundCorrectionMode.Subtract =>
            "Remove an additive glow or gradient while keeping the background level.",
        FitsBackgroundCorrectionMode.Divide =>
            "Correct a multiplicative field response while keeping the image scale.",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static string JsonName(this FitsBackgroundCorrectionMode mode) => mode switch
    {
        FitsBackgroundCorrectionMode.Subtract => "subtract",
        FitsBackgroundCorrectionMode.Divide => "divide",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

internal enum FitsBackgroundModelType
{
    Automatic,
    Polynomial,
    RadialBasis,
}

internal static class FitsBackgroundModelTypeExtensions
{
    public static string Title(this FitsBackgroundModelType model) => model switch
    {
        FitsBackgroundModelType.Automatic => "Automatic",
        FitsBackgroundModelType.Polynomial => "Polynomial",
        FitsBackgroundModelType.RadialBasis => "Radial Basis",
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    public static string Help(this FitsBackgroundModelType model) => model switch
    {
        FitsBackgroundModelType.Automatic =>
            "Choose a conservative surface from held-out background samples.",
        FitsBackgroundModelType.Polynomial =>
            "Fit a smooth polynomial surface with a fixed degree.",
        FitsBackgroundModelType.RadialBasis =>
            "Fit a flexible thin-plate surface. Inspect extended objects for lost detail.",
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    public static string JsonName(this FitsBackgroundModelType model) => model switch
    {
        FitsBackgroundModelType.Automatic => "automatic",
        FitsBackgroundModelType.Polynomial => "polynomial",
        FitsBackgroundModelType.RadialBasis => "radial_basis",
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };
}

internal sealed class FitsBackgroundConfiguration : IEquatable<FitsBackgroundConfiguration>
{
    public FitsBackgroundCorrectionMode Mode { get; set; } = FitsBackgroundCorrectionMode.Subtract;
    public double Strength { get; set; } = 1.0;
    public FitsBackgroundModelType ModelType { get; set; } = FitsBackgroundModelType.Automatic;
    public int AutomaticMaxDegree { get; set; } = 2;
    public int PolynomialDegree { get; set; } = 2;
    public double Ridge { get; set; } = 1.0e-8;
    public double RbfSmoothing { get; set; } = 0.01;
    public int MaxControlPoints { get; set; } = 192;
    public bool AllowRadialBasisInAutomatic { get; set; }
    public double MinimumImprovement { get; set; } = 0.12;

    public static FitsBackgroundConfiguration CreateLegacyDefault() => new()
    {
        ModelType = FitsBackgroundModelType.Polynomial,
    };

    public FitsBackgroundConfiguration Clone() =>
        (FitsBackgroundConfiguration)MemberwiseClone();

    public string Summary =>
        $"{ModelType.Title()}, {Strength:P0} " +
        (Mode == FitsBackgroundCorrectionMode.Subtract ? "subtract" : "divide");

    public string? ValidationMessage
    {
        get
        {
            if (!double.IsFinite(Strength) || Strength is < 0 or > 1)
            {
                return "Background correction strength must be between 0 and 1.";
            }
            if ((ModelType is FitsBackgroundModelType.Automatic or FitsBackgroundModelType.Polynomial) &&
                (!double.IsFinite(Ridge) || Ridge < 0))
            {
                return "Background ridge must be a non-negative number.";
            }

            switch (ModelType)
            {
                case FitsBackgroundModelType.Automatic:
                    if (AutomaticMaxDegree is < 0 or > 4)
                    {
                        return "Automatic maximum degree must be between 0 and 4.";
                    }
                    if (!double.IsFinite(MinimumImprovement) ||
                        MinimumImprovement is < 0 or > 0.75)
                    {
                        return "Minimum improvement must be between 0 and 0.75.";
                    }
                    return AllowRadialBasisInAutomatic
                        ? RadialBasisValidationMessage
                        : null;
                case FitsBackgroundModelType.Polynomial:
                    return PolynomialDegree is < 0 or > 4
                        ? "Polynomial degree must be between 0 and 4."
                        : null;
                case FitsBackgroundModelType.RadialBasis:
                    return RadialBasisValidationMessage;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported background model '{ModelType}'.");
            }
        }
    }

    private string? RadialBasisValidationMessage
    {
        get
        {
            if (!double.IsFinite(RbfSmoothing) || RbfSmoothing < 0)
            {
                return "Radial-basis smoothing must be a non-negative number.";
            }
            return MaxControlPoints is < 16 or > 512
                ? "Radial-basis control points must be between 16 and 512."
                : null;
        }
    }

    public bool Equals(FitsBackgroundConfiguration? other) => other is not null &&
        Mode == other.Mode &&
        Strength == other.Strength &&
        ModelType == other.ModelType &&
        AutomaticMaxDegree == other.AutomaticMaxDegree &&
        PolynomialDegree == other.PolynomialDegree &&
        Ridge == other.Ridge &&
        RbfSmoothing == other.RbfSmoothing &&
        MaxControlPoints == other.MaxControlPoints &&
        AllowRadialBasisInAutomatic == other.AllowRadialBasisInAutomatic &&
        MinimumImprovement == other.MinimumImprovement;

    public override bool Equals(object? obj) => Equals(obj as FitsBackgroundConfiguration);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Mode);
        hash.Add(Strength);
        hash.Add(ModelType);
        hash.Add(AutomaticMaxDegree);
        hash.Add(PolynomialDegree);
        hash.Add(Ridge);
        hash.Add(RbfSmoothing);
        hash.Add(MaxControlPoints);
        hash.Add(AllowRadialBasisInAutomatic);
        hash.Add(MinimumImprovement);
        return hash.ToHashCode();
    }

    internal void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("mode", Mode.JsonName());
        writer.WriteNumber("strength", Strength);
        writer.WritePropertyName("config");
        writer.WriteStartObject();
        writer.WritePropertyName("model");
        writer.WriteStartObject();
        writer.WriteString("kind", ModelType.JsonName());
        switch (ModelType)
        {
            case FitsBackgroundModelType.Automatic:
                writer.WriteNumber("max_degree", AutomaticMaxDegree);
                writer.WriteNumber("ridge", Ridge);
                writer.WriteNumber("rbf_smoothing", RbfSmoothing);
                writer.WriteNumber("max_control_points", MaxControlPoints);
                writer.WriteBoolean("allow_radial_basis", AllowRadialBasisInAutomatic);
                writer.WriteNumber("minimum_improvement", MinimumImprovement);
                break;
            case FitsBackgroundModelType.Polynomial:
                writer.WriteNumber("degree", PolynomialDegree);
                writer.WriteNumber("ridge", Ridge);
                break;
            case FitsBackgroundModelType.RadialBasis:
                writer.WriteNumber("smoothing", RbfSmoothing);
                writer.WriteNumber("max_control_points", MaxControlPoints);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported background model '{ModelType}'.");
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    internal static FitsBackgroundConfiguration FromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The background-processing settings are invalid.");
        }

        FitsBackgroundCorrectionMode mode = element.TryGetProperty("mode", out JsonElement modeElement)
            ? ParseMode(modeElement.GetString())
            : FitsBackgroundCorrectionMode.Subtract;
        double strength = ReadDouble(element, "strength", 1.0);

        FitsBackgroundConfiguration result;
        if (!element.TryGetProperty("config", out JsonElement configElement))
        {
            result = CreateLegacyDefault();
        }
        else
        {
            if (configElement.ValueKind != JsonValueKind.Object ||
                !configElement.TryGetProperty("model", out JsonElement modelElement) ||
                modelElement.ValueKind != JsonValueKind.Object ||
                !modelElement.TryGetProperty("kind", out JsonElement kindElement))
            {
                throw new FormatException("The background model settings are invalid.");
            }

            FitsBackgroundModelType modelType = ParseModelType(kindElement.GetString());
            result = new FitsBackgroundConfiguration
            {
                ModelType = modelType,
            };
            switch (modelType)
            {
                case FitsBackgroundModelType.Automatic:
                    result.AutomaticMaxDegree = ReadInt(modelElement, "max_degree", 2);
                    result.Ridge = ReadDouble(modelElement, "ridge", 1.0e-8);
                    result.RbfSmoothing = ReadDouble(modelElement, "rbf_smoothing", 0.01);
                    result.MaxControlPoints = ReadInt(modelElement, "max_control_points", 192);
                    result.AllowRadialBasisInAutomatic =
                        ReadBool(modelElement, "allow_radial_basis", false);
                    result.MinimumImprovement =
                        ReadDouble(modelElement, "minimum_improvement", 0.12);
                    break;
                case FitsBackgroundModelType.Polynomial:
                    result.PolynomialDegree = ReadRequiredInt(modelElement, "degree");
                    result.Ridge = ReadRequiredDouble(modelElement, "ridge");
                    break;
                case FitsBackgroundModelType.RadialBasis:
                    result.RbfSmoothing = ReadDouble(modelElement, "smoothing", 0.01);
                    result.MaxControlPoints = ReadInt(modelElement, "max_control_points", 192);
                    break;
                default:
                    throw new FormatException($"Unknown background model '{modelType}'.");
            }
        }

        result.Mode = mode;
        result.Strength = strength;
        if (result.ValidationMessage is { } message)
        {
            throw new FormatException(message);
        }
        return result;
    }

    private static FitsBackgroundCorrectionMode ParseMode(string? value) => value switch
    {
        "subtract" => FitsBackgroundCorrectionMode.Subtract,
        "divide" => FitsBackgroundCorrectionMode.Divide,
        _ => throw new FormatException($"Unknown background correction mode '{value}'."),
    };

    private static FitsBackgroundModelType ParseModelType(string? value) => value switch
    {
        "automatic" => FitsBackgroundModelType.Automatic,
        "polynomial" => FitsBackgroundModelType.Polynomial,
        "radial_basis" => FitsBackgroundModelType.RadialBasis,
        _ => throw new FormatException($"Unknown background model '{value}'."),
    };

    private static double ReadDouble(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetDouble() : fallback;

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : fallback;

    private static bool ReadBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : fallback;

    private static double ReadRequiredDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.GetDouble()
            : throw new FormatException($"The background model is missing '{name}'.");

    private static int ReadRequiredInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.GetInt32()
            : throw new FormatException($"The background model is missing '{name}'.");
}
