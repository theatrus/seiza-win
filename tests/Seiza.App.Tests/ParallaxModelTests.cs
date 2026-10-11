using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxModelTests
{
    [Fact]
    public void PlannerRoundTripPreservesUnwrappedRollAndIncomingTiming()
    {
        const string json = """{"schemaVersion":1,"focus":[100,80],"focusName":"Target","seconds":7,"tour":[{"name":"Whole Image","dolly":0,"zoom":1,"rotateDegrees":20,"pan":0,"travel":99,"hold":1},{"name":"Target","focus":[100,80],"dolly":0.4,"zoom":1.2,"rotateDegrees":380,"pan":0.6,"travel":4,"hold":2}]}""";
        ParallaxTourPlan plan = ParallaxTourPlan.FromJson(json);
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, Stops = plan.Tour };
        composition.Scene.DistanceFocus = plan.Focus;
        Assert.Equal(7, composition.Duration);
        Assert.Equal(5, composition.ArrivalAt(1));
        composition.Stops[1].Name = "Renamed";
        var request = new ParallaxRequest { Inputs = new() { Image = "source.png" }, Scene = composition.Scene, Video = composition.VideoSettings(false) };
        using var document = JsonDocument.Parse(request.ToJson());
        JsonElement root = document.RootElement;
        Assert.Equal(380, root.GetProperty("tour")[1].GetProperty("rotateDegrees").GetDouble());
        Assert.Equal(0.6, root.GetProperty("tour")[1].GetProperty("pan").GetDouble());
        Assert.False(root.GetProperty("tour")[1].TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("autoTour", out _));
        Assert.False(root.TryGetProperty("inputs", out _));
        Assert.Equal(100, root.GetProperty("distanceFocus")[0].GetDouble());
        composition.SetTravelTurn(1, clockwise: true);
        Assert.Equal(-340, composition.Stops[1].RotateDegrees);
        composition.SetTravelTurn(1, clockwise: false);
        Assert.Equal(380, composition.Stops[1].RotateDegrees);
        composition.Stops[1].Hold = 0;
        Assert.Equal(5, composition.Duration);
    }

    [Fact]
    public void SpinPushTitlesAndLoopRoundTripWithoutPersistingUiIdentity()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, TourGlide = 0.35, TourTitles = true, TourLoop = true };
        composition.Stops[1].RotateDegrees = 45;
        composition.Stops[1].Push = 0.6;
        composition.Stops[1].Title = "The Iris Nebula";
        composition.SetSpin(1, clockwise: true);
        Assert.Equal(45, composition.Stops[1].RotateDegrees);
        var plan = new ParallaxTourPlan
        {
            Focus = [20, 30], FocusName = "Scene", Seconds = composition.Duration, Tour = composition.Stops,
            TourGlide = composition.TourGlide, TourTitles = composition.TourTitles, TourLoop = composition.TourLoop,
        };
        ParallaxTourPlan decoded = ParallaxTourPlan.FromJson(plan.ToJson());
        Assert.Equal(-360, decoded.Tour[1].SpinDegrees);
        Assert.Equal(0.6, decoded.Tour[1].Push);
        Assert.Equal(0.35, decoded.TourGlide);
        Assert.Equal(45, decoded.Tour[1].RotateDegrees);
        Assert.Equal("The Iris Nebula", decoded.Tour[1].Title);
        Assert.True(decoded.TourTitles); Assert.True(decoded.TourLoop);
        Assert.NotEqual(composition.Stops[1].Id, decoded.Tour[1].Id);
        ParallaxStop old = JsonSerializer.Deserialize("""{"rotateDegrees":720,"hold":2}""", ParallaxJsonContext.Default.ParallaxStop)!;
        Assert.Equal(720, old.RotateDegrees);
        Assert.Equal(0, old.SpinDegrees); Assert.Equal(0, old.Push); Assert.Null(old.Title);
        Assert.Equal(4, old.Travel); Assert.Equal(1, old.Zoom);
        ParallaxStop unnamed = JsonSerializer.Deserialize("""{"focus":[20,30]}""", ParallaxJsonContext.Default.ParallaxStop)!;
        Assert.Equal("Custom Point", unnamed.DisplayName);
    }

    [Fact]
    public void LoopClosureUsesIncomingTravelAndExistingReturnIsNotDuplicated()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, TourLoop = true };
        composition.Stops[0].Hold = 0.1;
        composition.Stops[1].Travel = 0.3;
        Assert.Equal(3.3, composition.LoopClosingDuration, precision: 8);
        ParallaxStop closing = composition.Stops[0].DeepClone();
        closing.Id = Guid.NewGuid(); closing.Travel = 0.7; closing.RotateDegrees = 720;
        composition.Stops.Add(closing);
        Assert.Equal(0, composition.LoopClosingDuration);
        composition.Motion = ParallaxMotion.FlyIn;
        composition.TourTitles = true;
        Assert.False(composition.VideoSettings(false).TourLoop);
        Assert.False(composition.VideoSettings(false).TourTitles);
    }

    [Fact]
    public void CompleteReconfigurationNeverContainsSceneFields()
    {
        var composition = new ParallaxComposition();
        using var document = JsonDocument.Parse(composition.VideoSettings(false).ToJson());
        foreach (string key in new[] { "start", "dolly", "truck", "truckAngleDegrees", "pan", "zoom", "zoomEnd", "rotateDegrees", "easing", "quality", "growthLimit", "fadeFrom", "tour", "tourGlide", "tourTitles", "tourLoop", "seconds", "fps", "size", "overlay", "overlayDensity", "labels", "labelColor", "watermark" })
            Assert.True(document.RootElement.TryGetProperty(key, out _), key);
        foreach (string key in new[] { "image", "wcs", "distanceFocus", "distanceParsecs", "smallStars", "online", "catalogDirectory", "autoTour" })
            Assert.False(document.RootElement.TryGetProperty(key, out _), key);
        Assert.False(document.RootElement.GetProperty("watermark").GetBoolean());
        composition.CreditEnabled = true;
        using var enabled = JsonDocument.Parse(composition.VideoSettings(false).ToJson());
        Assert.Equal(composition.Credit, enabled.RootElement.GetProperty("watermark").GetString());
    }

    [Theory]
    [InlineData(1920, 1080, "960x540")]
    [InlineData(1080, 1920, "540x960")]
    [InlineData(3840, 2160, "960x540")]
    [InlineData(100, 98, "100x98")]
    public void PreviewBoundsPreservePresetAspectRatio(int width, int height, string expected)
    {
        var composition = new ParallaxComposition { Width = width, Height = height, Quality = "high" };
        Assert.Equal(expected, composition.VideoSettings(true).Size);
        Assert.Equal("standard", composition.VideoSettings(true).Quality);
        Assert.Equal("high", composition.VideoSettings(false).Quality);
    }

    [Theory]
    [InlineData(16, 3840, "16x3840")]
    [InlineData(3840, 16, "3840x16")]
    [InlineData(32, 3840, "16x1920")]
    [InlineData(3840, 32, "1920x16")]
    [InlineData(18, 3840, "16x3414")]
    [InlineData(3840, 18, "3414x16")]
    [InlineData(66, 3840, "16x930")]
    [InlineData(3840, 66, "930x16")]
    [InlineData(68, 3840, "16x904")]
    [InlineData(3840, 68, "904x16")]
    [InlineData(1922, 1080, "958x538")]
    [InlineData(1080, 1922, "538x958")]
    public void CustomPreviewUsesOneScaleWithOnlyAnEvenPixelRoundingDifference(int width, int height, string expected)
    {
        var composition = new ParallaxComposition { Width = width, Height = height };
        Assert.Null(composition.ValidationMessage);
        Assert.Equal(expected, composition.VideoSettings(true).Size);
        Assert.Equal($"{width}x{height}", composition.VideoSettings(false).Size);
        int[] preview = composition.VideoSettings(true).Size.Split('x').Select(int.Parse).ToArray();
        Assert.InRange(preview[0], 16, width);
        Assert.InRange(preview[1], 16, height);
        Assert.Equal(0, preview[0] % 2);
        Assert.Equal(0, preview[1] % 2);
        Assert.InRange(preview[0] * preview[1], 16 * 16, 960 * 960);
        int shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        int previewShort = Math.Min(preview[0], preview[1]), previewLong = Math.Max(preview[0], preview[1]);
        double scaledLong = (double)longSide * previewShort / shortSide;
        Assert.InRange(Math.Abs(previewLong - scaledLong), 0, 1.000001);
        if (previewShort > 16) Assert.InRange(previewLong, 16, 960);
    }

    [Fact]
    public void HistoryDeepCloneKeepsEditableCollectionsAndArraysIndependent()
    {
        var original = new ParallaxComposition { Focus = [1, 2] };
        original.Scene.DistanceFocus = [3, 4]; original.Stops[1].Focus = [5, 6];
        original.Labels.Add(new() { Text = "Before" });
        var snapshot = original.DeepClone();
        Assert.True(original.ContentEquals(snapshot));
        Assert.True(original.Scene.ContentEquals(snapshot.Scene));
        Assert.Equal(original.Stops[1].Id, snapshot.Stops[1].Id);
        original.Focus[0] = 7; original.Scene.DistanceFocus[0] = 8; original.Stops[1].Focus![0] = 9;
        original.Labels[0].Text = "After";
        Assert.Equal(1, snapshot.Focus![0]); Assert.Equal(3, snapshot.Scene.DistanceFocus![0]);
        Assert.Equal(5, snapshot.Stops[1].Focus![0]); Assert.Equal("Before", snapshot.Labels[0].Text);
        Assert.False(original.ContentEquals(snapshot)); Assert.False(original.Scene.ContentEquals(snapshot.Scene));
    }

    [Fact]
    public void ValidationRejectsBadNativeBoundsAndNonfiniteValues()
    {
        Assert.Null(new ParallaxComposition().ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Zoom = 0.5 }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { GrowthLimit = 0.5 }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Width = 1921 }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Seconds = double.NaN }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Focus = [1] }.ValidationMessage);
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour };
        composition.Stops[1].Pan = double.NaN;
        Assert.NotNull(composition.ValidationMessage);
        composition.Stops[1].Pan = 0; composition.Scene.DistanceParsecs = 0;
        Assert.NotNull(composition.ValidationMessage);
    }

    [Fact]
    public void InvalidDraftNumbersRemainComparableForUndoButNeverBecomeNativeJson()
    {
        var composition = new ParallaxComposition { Dolly = double.NaN };
        composition.Scene.DistanceParsecs = double.PositiveInfinity;
        Assert.True(composition.ContentEquals(composition.DeepClone()));
        Assert.True(composition.Scene.ContentEquals(composition.Scene.DeepClone()));
        Assert.False(new ParallaxComposition().ContentEquals(composition));
        Assert.Throws<ArgumentException>(() => composition.VideoSettings(false).ToJson());
    }

    [Fact]
    public void ActivityReportsNetworkWaitsWithoutInventingAnOverallPercentage()
    {
        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(1000);
        var activity = new ParallaxActivity("Loading images…", start);
        activity.Receive(new() { Kind = "note", Message = "fetching Gaia DR3 distances within 1.61 deg of (315.1375, +67.7775) in 4 cone(s)" }, start.AddSeconds(5));
        Assert.Equal(4, activity.Regions);
        Assert.Null(activity.WaitingMessage(start.AddSeconds(10)));
        Assert.Contains("0:20 since its last update", activity.WaitingMessage(start.AddSeconds(25)));
        activity.Receive(new() { Kind = "note", Message = "  1 cone(s) fetched" }, start.AddSeconds(30));
        Assert.Equal(1, activity.Downloaded);
        Assert.Contains("1 new sky regions downloaded", activity.Detail(start.AddSeconds(35)));
        activity.Receive(new() { Kind = "note", Message = "10574 Gaia stars" }, start.AddSeconds(40));
        Assert.Contains("Hipparcos", activity.NetworkPhase);
        activity.Receive(new() { Kind = "note", Message = "5384 matched to Gaia, 5339 with a distance" }, start.AddSeconds(50));
        Assert.Null(activity.NetworkPhase); Assert.Null(activity.WaitingMessage(start.AddSeconds(80)));
        activity.Receive(new() { Kind = "warning", Message = "A catalogue warning" }, start.AddSeconds(60));
        Assert.True(activity.Entries[^1].Warning);
        activity.CancellationRequested = true;
        Assert.Contains("Cancel requested", activity.Detail(start.AddSeconds(70)));
        activity.FinishedAt = start.AddSeconds(71);
        Assert.Null(activity.WaitingMessage(start.AddSeconds(100)));
    }

    [Fact]
    public void ActivityRetainsOnlyTheLastHundredEvents()
    {
        var activity = new ParallaxActivity("Start");
        for (int index = 0; index < 150; index++) activity.Receive(new() { Kind = "note", Message = $"Step {index}" });
        Assert.Equal(100, activity.Entries.Count);
        Assert.Equal("Step 50", activity.Entries[0].Message);
        Assert.Equal("Step 149", activity.Entries[^1].Message);
    }

    [Fact]
    public void TourPlanRejectsUnsupportedSchemaAndBrokenCoordinates()
    {
        Assert.Throws<InvalidDataException>(() => ParallaxTourPlan.FromJson("""{"schemaVersion":2,"focus":[1,2],"tour":[{},{}]}"""));
        Assert.Throws<InvalidDataException>(() => ParallaxTourPlan.FromJson("""{"focus":[1],"tour":[{},{}]}"""));
    }
}
