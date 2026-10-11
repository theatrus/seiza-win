using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Seiza.App.Models;

public enum ParallaxMotion { FlyIn, Tour }

public sealed record ParallaxStop
{
    [JsonIgnore] public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; }
    public double[]? Focus { get; set; }
    public double Dolly { get; set; }
    public double Zoom { get; set; } = 1;
    public double RotateDegrees { get; set; }
    public double Pan { get; set; }
    public double Travel { get; set; } = 4;
    public double Hold { get; set; } = 1;
    public double SpinDegrees { get; set; }
    public double Push { get; set; }
    public string? Title { get; set; }
    [JsonIgnore] public string DisplayName => Name ?? (Focus is null ? "Whole Image" : "Custom Point");
    public ParallaxStop DeepClone() => this with { Focus = Focus?.ToArray() };
}

public sealed record ParallaxTourOptions
{
    public int? Targets { get; set; } = 5;
    public double Hold { get; set; } = 1.5;
    public double Motion { get; set; } = 1;
}

public sealed record ParallaxTourPlan
{
    public int SchemaVersion { get; set; } = 1;
    public double[] Focus { get; set; } = [];
    public string FocusName { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public List<ParallaxStop> Tour { get; set; } = [];
    // Optional for upstream plans and tours saved before these controls existed.
    public double? TourGlide { get; set; }
    public bool? TourTitles { get; set; }
    public bool? TourLoop { get; set; }
    public string ToJson() => ParallaxJson.SerializePlan(this);
    public static ParallaxTourPlan FromJson(string json) => ParallaxJson.DeserializePlan(json);

    public void Validate()
    {
        if (SchemaVersion != 1 || !ParallaxComposition.ValidPoint(Focus) || Focus.Length != 2 || Tour is null || Tour.Count < 2)
            throw new InvalidDataException("The tour plan has an unsupported schema or invalid stops.");
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, Stops = Tour };
        composition.Scene.DistanceFocus = Focus;
        composition.TourGlide = TourGlide ?? composition.TourGlide;
        composition.TourLoop = TourLoop ?? composition.TourLoop;
        if (composition.ValidationMessage is string message)
            throw new InvalidDataException($"Invalid tour plan: {message}");
    }
}

public sealed record ParallaxSceneSettings
{
    public double[]? DistanceFocus { get; set; }
    public double? DistanceParsecs { get; set; }
    public double? UnmatchedDistanceParsecs { get; set; }
    public bool Online { get; set; }
    public double GaiaMaxMagnitude { get; set; } = 16;
    public int? MaxStars { get; set; }
    public string SmallStars { get; set; } = "field";
    public bool KeepGalaxies { get; set; }
    public bool Dust { get; set; } = true;
    public double DustOpacity { get; set; } = 3;
    public double MinimumScaleArcsecPerPixel { get; set; } = 0.1;
    public double MaximumScaleArcsecPerPixel { get; set; } = 20;
    public ParallaxSceneSettings DeepClone() => this with { DistanceFocus = DistanceFocus?.ToArray() };
    public bool ContentEquals(ParallaxSceneSettings? other) => other is not null &&
        ParallaxJson.SerializeScene(this) == ParallaxJson.SerializeScene(other);
}

public sealed record ParallaxLabel
{
    [JsonIgnore] public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; }
    public string Text { get; set; } = "Label";
}

[JsonConverter(typeof(ParallaxWatermarkConverter))]
public sealed record ParallaxWatermark(string? Text)
{
    public static ParallaxWatermark Disabled { get; } = new((string?)null);
    public static ParallaxWatermark FromText(string text) => new(text);
}

// Reconfigure takes COMPLETE camera/output settings; omitted fields take native
// defaults, not values from the old handle. Never put scene fields in this type.
public sealed record ParallaxVideoSettings
{
    public double[]? Focus { get; set; }
    public string Start { get; set; } = "whole";
    public double Dolly { get; set; } = 0.4;
    public double Truck { get; set; }
    public double TruckAngleDegrees { get; set; }
    public double Pan { get; set; }
    public double Zoom { get; set; } = 1;
    public double ZoomEnd { get; set; } = 1;
    public double[] RotateDegrees { get; set; } = [0, 0];
    public string Easing { get; set; } = "inOut";
    public string Quality { get; set; } = "standard";
    public double GrowthLimit { get; set; } = 4;
    public double FadeFrom { get; set; } = 6;
    public List<ParallaxStop> Tour { get; set; } = [];
    public double TourGlide { get; set; } = 0.2;
    public bool TourTitles { get; set; }
    public bool TourLoop { get; set; }
    public double Seconds { get; set; } = 8;
    public int Fps { get; set; } = 30;
    public string Size { get; set; } = "1920x1080";
    public bool Overlay { get; set; }
    public double OverlayDensity { get; set; } = 0.6;
    public List<ParallaxLabel> Labels { get; set; } = [];
    public string LabelColor { get; set; } = "#F0F4F8";
    public ParallaxWatermark Watermark { get; set; } = ParallaxWatermark.Disabled;
    public string ToJson() => ParallaxJson.SerializeVideo(this);
}

public sealed record ParallaxComposition
{
    public ParallaxMotion Motion { get; set; }
    public List<ParallaxStop> Stops { get; set; } = [new() { Travel = 0 }, new() { Name = "Destination", Dolly = 0.4, Hold = 2 }];
    public ParallaxSceneSettings Scene { get; set; } = new();
    public string DistanceName { get; set; } = "Image center";
    public double[]? Focus { get; set; }
    public string FocusName { get; set; } = "Image center";
    public double Seconds { get; set; } = 8;
    public double Dolly { get; set; } = 0.4;
    public double Pan { get; set; }
    public double Rotation { get; set; }
    public double RotationStart { get; set; }
    public string Start { get; set; } = "whole";
    public double Truck { get; set; }
    public double TruckAngleDegrees { get; set; }
    public double Zoom { get; set; } = 1;
    public double ZoomEnd { get; set; } = 1;
    public string Easing { get; set; } = "inOut";
    public double TourGlide { get; set; } = 0.2;
    public bool TourTitles { get; set; }
    public bool TourLoop { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public string Quality { get; set; } = "standard";
    public double GrowthLimit { get; set; } = 4;
    public double FadeFrom { get; set; } = 6;
    public bool Overlay { get; set; }
    public double OverlayDensity { get; set; } = 0.6;
    public List<ParallaxLabel> Labels { get; set; } = [];
    public string LabelColor { get; set; } = "#F0F4F8";
    public bool CreditEnabled { get; set; }
    public string Credit { get; set; } = "Rendered with seiza.fyi";

    public ParallaxComposition DeepClone() => this with
    {
        Focus = Focus?.ToArray(), Scene = Scene.DeepClone(),
        Stops = Stops.Select(stop => stop.DeepClone()).ToList(),
        Labels = Labels.Select(label => label with { }).ToList(),
    };

    // Record equality is shallow for lists; compare values for undo/debounce.
    public bool ContentEquals(ParallaxComposition? other) => other is not null &&
        ParallaxJson.SerializeComposition(this) == ParallaxJson.SerializeComposition(other);

    [JsonIgnore] public double Duration => Motion == ParallaxMotion.Tour
        ? Stops.Select((stop, index) => stop.Hold + (index == 0 ? 0 : stop.Travel)).Sum() + LoopClosingDuration : Seconds;

    [JsonIgnore] public double LoopClosingDuration
    {
        get
        {
            if (Motion != ParallaxMotion.Tour || !TourLoop || Stops.Count < 2) return 0;
            ParallaxStop first = Stops[0], last = Stops[^1];
            if (PointsEqual(first.Focus, last.Focus) && first.Dolly == last.Dolly && first.Zoom == last.Zoom && first.Pan == last.Pan) return 0;
            return Stops.Skip(1).Average(stop => stop.Travel) + Math.Max(first.Hold * 3, 3);
        }
    }

    public double ArrivalAt(int index) => index < 0 || index >= Stops.Count ? 0 :
        Stops.Take(index).Sum(stop => stop.Hold) + Stops.Take(index + 1).Skip(1).Sum(stop => stop.Travel);

    public void SetSpin(int index, bool clockwise)
    {
        if (index < 0 || index >= Stops.Count) return;
        Stops[index].SpinDegrees = clockwise ? -360 : 360;
        if (Stops[index].Hold == 0) Stops[index].Hold = 4;
    }

    public void SetTravelTurn(int index, bool clockwise)
    {
        if (index <= 0 || index >= Stops.Count) return;
        Stops[index].RotateDegrees = Stops[index - 1].RotateDegrees + (clockwise ? -360 : 360);
    }

    [JsonIgnore] public string? ValidationMessage
    {
        get
        {
            if (Width is < 16 or > 3840 || Height is < 16 or > 3840 || Width % 2 != 0 || Height % 2 != 0 || Fps is not (24 or 30 or 60))
                return "Choose even dimensions between 16 and 3840 pixels and 24, 30 or 60 fps.";
            if (Stops is null || Stops.Any(stop => stop is null) || Scene is null || Labels is null) return "The composition is incomplete.";
            if (!double.IsFinite(Duration) || Duration <= 0 || Duration > 600) return "The video must be longer than zero and at most 10 minutes.";
            if (new[] { Scene.DistanceParsecs, Scene.UnmatchedDistanceParsecs }.Any(value => value is double distance && (!double.IsFinite(distance) || distance <= 0)))
                return "Distances must be positive, finite parsec values.";
            if (!ValidPoint(Scene.DistanceFocus) || !double.IsFinite(Scene.GaiaMaxMagnitude) || Scene.GaiaMaxMagnitude is < 1 or > 25 ||
                !double.IsFinite(Scene.DustOpacity) || Scene.DustOpacity < 0 || Scene.MaxStars is < 0 || Scene.SmallStars is not ("field" or "drop") ||
                !double.IsFinite(Scene.MinimumScaleArcsecPerPixel) || !double.IsFinite(Scene.MaximumScaleArcsecPerPixel) ||
                Scene.MinimumScaleArcsecPerPixel <= 0 || Scene.MaximumScaleArcsecPerPixel < Scene.MinimumScaleArcsecPerPixel)
                return "Check the scene's distance reference, star limit, dust and solve settings.";
            if (!ValidPoint(Focus) || !Finite(Dolly, Pan, Rotation, RotationStart, Truck, TruckAngleDegrees, Zoom, ZoomEnd, TourGlide, GrowthLimit, FadeFrom) ||
                Dolly is < 0 or >= 1 || Pan is < 0 or > 1 || Zoom < 1 || ZoomEnd < 1 || TourGlide < 0 || GrowthLimit < 1 || FadeFrom <= 0 ||
                Start is not ("whole" or "focus") || Quality is not ("standard" or "high") || Easing is not ("inOut" or "linear"))
                return "Check the camera position, zoom, roll, glide and star appearance settings.";
            if (!double.IsFinite(OverlayDensity) || OverlayDensity is < 0 or > 1 || !Regex.IsMatch(LabelColor ?? string.Empty, "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant) ||
                Labels.Any(label => label is null || !Finite(label.X, label.Y, label.Radius) || label.Radius < 0 || string.IsNullOrEmpty(label.Text)))
                return "Labels need valid positions, nonnegative radii and a color in #RRGGBB format.";
            if (Motion == ParallaxMotion.Tour)
            {
                if (Stops.Count < 2) return "A tour needs at least two stops.";
                if (Stops.Any(stop => !ValidPoint(stop.Focus) || !Finite(stop.Dolly, stop.Pan, stop.Zoom, stop.RotateDegrees, stop.Travel, stop.Hold, stop.SpinDegrees, stop.Push) ||
                    stop.Dolly is < 0 or >= 1 || stop.Pan is < 0 or > 1 || stop.Zoom <= 0 || stop.Push is < 0 or >= 1 || stop.Travel < 0 || stop.Hold < 0))
                    return "Every stop needs valid framing, spin, push and nonnegative times.";
            }
            return null;
        }
    }

    public ParallaxVideoSettings VideoSettings(bool preview)
    {
        double scale = preview ? Math.Min(1, 960.0 / Math.Max(Width, Height)) : 1;
        int width = Math.Max(16, (int)(Width * scale) / 2 * 2), height = Math.Max(16, (int)(Height * scale) / 2 * 2);
        return new()
        {
            Focus = Focus?.ToArray(), Start = Start, Dolly = Dolly, Truck = Truck, TruckAngleDegrees = TruckAngleDegrees,
            Pan = Pan, Zoom = Zoom, ZoomEnd = ZoomEnd, RotateDegrees = [RotationStart, Rotation], Easing = Easing,
            Quality = preview ? "standard" : Quality, GrowthLimit = GrowthLimit, FadeFrom = FadeFrom,
            Tour = Motion == ParallaxMotion.Tour ? Stops.Select(stop => stop.DeepClone()).ToList() : [], TourGlide = TourGlide,
            TourTitles = Motion == ParallaxMotion.Tour && TourTitles, TourLoop = Motion == ParallaxMotion.Tour && TourLoop,
            Seconds = Duration, Fps = Fps, Size = FormattableString.Invariant($"{width}x{height}"), Overlay = Overlay,
            OverlayDensity = OverlayDensity, Labels = Labels.Select(label => label with { }).ToList(), LabelColor = LabelColor,
            Watermark = CreditEnabled ? ParallaxWatermark.FromText(Credit) : ParallaxWatermark.Disabled,
        };
    }

    internal static bool ValidPoint(double[]? point) => point is null || point.Length == 2 && point.All(double.IsFinite);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static bool PointsEqual(double[]? first, double[]? second) => first is null ? second is null : second is not null && first.SequenceEqual(second);
}

public sealed record ParallaxInputs
{
    public string Image { get; set; } = string.Empty;
    public string? Starless { get; set; }
    public string? Stars { get; set; }
    public WcsResult? Wcs { get; set; }
    public string? CatalogDirectory { get; set; }
    public string? GaiaCache { get; set; }
    public string? Objects { get; set; }
    public string? ObjectDistances { get; set; }
    public string? StarDistances { get; set; }
    public string? RcAstroExecutable { get; set; }
    public string? RcAstroHost { get; set; }
}

public sealed record ParallaxRequest
{
    public ParallaxInputs Inputs { get; set; } = new();
    public ParallaxSceneSettings Scene { get; set; } = new();
    public ParallaxVideoSettings Video { get; set; } = new();
    public ParallaxTourOptions? AutoTour { get; set; }
    public string ToJson() => ParallaxJson.SerializeRequest(this);
}

public sealed record ParallaxFitValue
{
    public double Asked { get; init; }
    public double Used { get; init; }
    [JsonIgnore] public bool Changed => Math.Abs(Asked - Used) > 0.0001;
}
public sealed record ParallaxStopFit
{
    public ParallaxFitValue Zoom { get; init; } = new();
    public ParallaxFitValue Pan { get; init; } = new();
}
public sealed record ParallaxFit
{
    public ParallaxFitValue Zoom { get; init; } = new();
    public ParallaxFitValue Pan { get; init; } = new();
    public ParallaxFitValue Lead { get; init; } = new();
    public ParallaxFitValue Truck { get; init; } = new();
    public ParallaxStopFit[] Stops { get; init; } = [];
    public bool Inside { get; init; }
    [JsonIgnore] public string? EdgeWarning => Inside ? null : "Some frames extend beyond the image. Move the focus or tour stops inward, or reduce roll and pan.";
    [JsonIgnore] public IReadOnlyList<string> Adjustments
    {
        get
        {
            var result = new List<string>();
            void Add(string name, ParallaxFitValue value)
            {
                if (value.Changed) result.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {value.Asked:F2} → {value.Used:F2}"));
            }
            Add("Zoom", Zoom); Add("Pan", Pan); Add("Sideways lead", Lead); Add("Sideways motion", Truck);
            for (int i = 0; i < Stops.Length; i++) { Add($"Stop {i + 1} zoom", Stops[i].Zoom); Add($"Stop {i + 1} pan", Stops[i].Pan); }
            return result;
        }
    }
}
public sealed record ParallaxSummary
{
    public int SchemaVersion { get; init; }
    public int Frames { get; init; }
    public int Fps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int DetectedStars { get; init; }
    public int WithDistance { get; init; }
    public double BackgroundDistanceParsecs { get; init; }
    public string BackgroundBasis { get; init; } = string.Empty;
    public double[] BackgroundFocus { get; init; } = [];
    public ParallaxFit Fit { get; init; } = new();
    public WcsResult Wcs { get; init; } = new();
}
public sealed record ParallaxEvent
{
    public string Kind { get; init; } = string.Empty;
    public string? Message { get; init; }
    public double? Fraction { get; init; }
}

public sealed record ParallaxActivityEntry(Guid Id, TimeSpan Elapsed, string Message, bool Warning);

public sealed class ParallaxActivity
{
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset LastEventAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? NetworkPhase { get; private set; }
    public int? Regions { get; private set; }
    public int Downloaded { get; private set; }
    public bool CancellationRequested { get; set; }
    public List<ParallaxActivityEntry> Entries { get; } = [];

    public ParallaxActivity(string message, DateTimeOffset? now = null)
    {
        StartedAt = LastEventAt = now ?? DateTimeOffset.UtcNow;
        Entries.Add(new(Guid.NewGuid(), TimeSpan.Zero, message, false));
    }

    public void Receive(ParallaxEvent update, DateTimeOffset? now = null)
    {
        DateTimeOffset time = now ?? DateTimeOffset.UtcNow;
        LastEventAt = time;
        if (update.Message is not string raw) return;
        string message = raw.Trim();
        Entries.Add(new(Guid.NewGuid(), time - StartedAt, message, update.Kind == "warning"));
        if (Entries.Count > 100) Entries.RemoveAt(0);
        if (message.StartsWith("fetching Gaia DR3 distances", StringComparison.Ordinal))
        {
            NetworkPhase = "Looking up Gaia star distances…"; Downloaded = 0;
            Match match = Regex.Match(message, @"\d+(?= cone\(s\)$)", RegexOptions.CultureInvariant);
            Regions = match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
        }
        else if (message.EndsWith("cone(s) fetched", StringComparison.Ordinal) && NetworkPhase is not null)
        {
            if (int.TryParse(message.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int count)) Downloaded = count;
        }
        else if (Regex.IsMatch(message, @"^\d+ Gaia stars(?: from .*)?$", RegexOptions.CultureInvariant)) NetworkPhase = "Checking bright-star distances (Hipparcos)…";
        else if (message.Contains("matched to Gaia", StringComparison.Ordinal) || message.Contains("no Hipparcos distances", StringComparison.Ordinal) || update.Kind != "warning") NetworkPhase = null;
    }

    public string Detail(DateTimeOffset now)
    {
        string elapsed = FormatTime((FinishedAt ?? now) - StartedAt);
        if (CancellationRequested) return $"{elapsed} elapsed · Cancel requested. Waiting for the current operation to return.";
        return Regions is int regions && NetworkPhase?.StartsWith("Looking up Gaia", StringComparison.Ordinal) == true
            ? $"{elapsed} elapsed · {Downloaded} new sky regions downloaded · {regions} regions cover this image" : $"{elapsed} elapsed";
    }

    public string? WaitingMessage(DateTimeOffset now) => FinishedAt is null && !CancellationRequested && NetworkPhase is not null && (now - LastEventAt).TotalSeconds >= 15
        ? $"Waiting for the catalogue service: {FormatTime(now - LastEventAt)} since its last update. Queries can take several minutes; completed Gaia regions are cached." : null;

    private static string FormatTime(TimeSpan duration)
    {
        int seconds = Math.Max(0, (int)duration.TotalSeconds);
        return FormattableString.Invariant($"{seconds / 60}:{seconds % 60:00}");
    }
}
