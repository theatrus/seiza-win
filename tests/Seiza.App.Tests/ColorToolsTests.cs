using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ColorToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seiza-color-tools-" + Guid.NewGuid().ToString("N"));
    public ColorToolsTests() => Directory.CreateDirectory(_root);
    private string Image(string name, int channels, params float[] samples)
    {
        string path = Path.Combine(_root, name + ".fits");
        int width = samples.Length / channels;
        byte[] fits = new byte[2880 + ((samples.Length * 4 + 2879) / 2880) * 2880];
        Array.Fill(fits, (byte)' ', 0, 2880);
        string[] cards = ["SIMPLE  =                    T", "BITPIX  =                  -32", $"NAXIS   = { (channels == 1 ? 2 : 3),20}",
            $"NAXIS1  = {width,20}", "NAXIS2  =                    1", $"NAXIS3  = {channels,20}", "END"];
        for (int i = 0; i < cards.Length; i++) Encoding.ASCII.GetBytes(cards[i].PadRight(80)).CopyTo(fits, i * 80);
        for (int channel = 0; channel < channels; channel++)
            for (int x = 0; x < width; x++)
                BinaryPrimitives.WriteSingleBigEndian(fits.AsSpan(2880 + (channel * width + x) * 4, 4), samples[x * channels + channel]);
        File.WriteAllBytes(path, fits); return path;
    }
    private ColorToolsRequest Continuum() => new() { Tool = ColorTool.Continuum, Aligned = true, LinearConfirmed = true,
        Inputs = [Image("nb", 1, 1, 3, float.NaN, -2), Image("reference", 1, 2, 1, 1, 2)],
        Saturations = [null, null], ClippingMasks = [null, null], ManualCoefficients = [.5], Output = Path.Combine(_root, "residual.fits") };

    [Fact]
    public async Task ContinuumPlanRoundTripsAndRejectsChangedSourcesWithoutReplacingDestination()
    {
        var request = Continuum();
        byte[] original = File.ReadAllBytes(request.Inputs[0]);
        var result = await ColorToolsService.RunAsync(request);
        Assert.Equal("linear", result.Report.GetProperty("transfer").GetString());
        Assert.True(result.Report.GetProperty("signedResidual").GetBoolean());
        Assert.Equal(original, File.ReadAllBytes(request.Inputs[0]));
        string reportPath = Path.Combine(_root, "plan.json");
        await ColorToolsService.SaveReportAsync(result, request, reportPath);
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        var replay = request with { ManualCoefficients = null, Plan = document.RootElement.GetProperty("plan"), PlanPath = reportPath };
        await ColorToolsService.RunAsync(replay);
        byte[] saved = File.ReadAllBytes(request.Output!);
        Image("reference", 1, 3, 1, 1, 2);
        await Assert.ThrowsAsync<SeizaCoreException>(() => ColorToolsService.RunAsync(replay));
        Assert.Equal(saved, File.ReadAllBytes(request.Output!));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".seiza-color-tools-*"));
    }

    [Fact]
    public async Task SkippedAutomaticFitDoesNotPublishOrSilentlyUseManualWeights()
    {
        var request = Continuum() with { ManualCoefficients = null };
        File.WriteAllText(request.Output!, "keep");
        var result = await ColorToolsService.RunAsync(request, analyze: true);
        Assert.Equal("skipped", result.Status); Assert.Null(result.Output);
        Assert.Contains("No output", result.Message);
        Assert.Equal("keep", File.ReadAllText(request.Output!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ColorToolsService.RunAsync(request));
    }

    [Theory]
    [InlineData(".fits")]
    [InlineData(".xisf")]
    public async Task DisplayOutputsReopenWithIdentityAndUnitDomainForPreviewAndExport(string extension)
    {
        var request = new ColorToolsRequest { Tool = ColorTool.RestorePreparedStars, Aligned = true,
            Inputs = [Image("stars", 3, .2f, .4f, .6f), Image("nebula", 3, .1f, .2f, .3f)], Output = Path.Combine(_root, "display" + extension) };
        var built = await ColorToolsService.RunAsync(request);
        var initial = ColorWorkflowService.InitialProcessingFor(request.Output!);
        Assert.True(initial.PreserveDisplaySamples);
        Assert.All(initial.StretchStack.Stages, stage => Assert.Equal(FitsStretchType.Identity, stage.Type));
        Assert.True(initial.Clone().PreserveDisplaySamples);
        Assert.True(FitsImageProcessingConfiguration.FromClipboardJson(initial.ToClipboardJson()).PreserveDisplaySamples);
        var preview = SeizaCore.Render(request.Output!);
        Assert.InRange(preview.Bgra[0], (byte)182, (byte)185);
        Assert.InRange(preview.Bgra[1], (byte)131, (byte)134);
        Assert.InRange(preview.Bgra[2], (byte)70, (byte)73);
        var exported = SeizaCore.Render16(request.Output!);
        Assert.Equal(1, exported.Width);
        Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(exported.RgbaBytes), (ushort)18348, (ushort)18352);
        Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(exported.RgbaBytes.AsSpan(2)), (ushort)34076, (ushort)34080);
        Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(exported.RgbaBytes.AsSpan(4)), (ushort)47183, (ushort)47187);
        Assert.False(ColorWorkflowService.InitialProcessingFor(request.Inputs[0]).PreserveDisplaySamples);
        string savedCopy = Path.Combine(_root, "saved" + (extension == ".fits" ? ".xisf" : ".fits"));
        await ColorToolsService.SaveImageAsync(built, request, savedCopy);
        Assert.True(ColorWorkflowService.HasDisplayTransfer(savedCopy));
        Assert.Equal(preview.Bgra, SeizaCore.Render(savedCopy).Bgra);
    }

    [Fact]
    public async Task ValidationCancellationAndNativeErrorsPreserveExistingOutputsAndSources()
    {
        var request = Continuum();
        Assert.Throws<InvalidOperationException>(() => (request with { Aligned = false }).Validate());
        Assert.Throws<InvalidOperationException>(() => (request with { LinearConfirmed = false }).Validate());
        Assert.Throws<InvalidOperationException>(() => (request with { Output = request.Inputs[0] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (request with { ClippingMasks = [request.Inputs[1], null], Output = request.Inputs[1] }).Validate());
        File.WriteAllText(request.Output!, "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ColorToolsService.RunAsync(request, token: new CancellationToken(true)));
        await Assert.ThrowsAsync<SeizaCoreException>(() => ColorToolsService.RunAsync(request with { Inputs = [request.Inputs[0], Image("wrong-grid", 1, 1)] }));
        Assert.Equal("keep", File.ReadAllText(request.Output!));
        var result = await ColorToolsService.RunAsync(request);
        string planPath = Path.Combine(_root, "protected-plan.json");
        File.WriteAllText(planPath, "keep plan");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ColorToolsService.SaveReportAsync(result, request with { PlanPath = planPath }, planPath));
        Assert.Equal("keep plan", File.ReadAllText(planPath));
    }

    [Fact]
    public void PreparedLayersOmitUnusedStretchAndContinuumFields()
    {
        var request = new ColorToolsRequest { Tool = ColorTool.RestorePreparedStars, Inputs = ["stars.fits", "nebula.fits"], StarAmount = double.NaN, TargetMedian = double.NaN };
        using var json = JsonDocument.Parse(request.ToNativeJson("output.fits", false));
        Assert.Equal("restore-prepared-stars", json.RootElement.GetProperty("operation").GetString());
        Assert.False(json.RootElement.TryGetProperty("starAmount", out _));
        Assert.False(json.RootElement.TryGetProperty("targetMedian", out _));
        Assert.False(json.RootElement.TryGetProperty("saturations", out _));
    }

    [Fact]
    public async Task SavingPreviewCopiesBuiltSamplesAcrossFormatsAndDoesNotRecomputeChangedSources()
    {
        var request = Continuum();
        var preview = await ColorToolsService.RunAsync(request);
        string copy = Path.Combine(_root, "reviewed.xisf");
        Image("nb", 1, 100, 300, 5, -200);
        await ColorToolsService.SaveImageAsync(preview, request, copy);
        Assert.Equal("XISF0100", Encoding.ASCII.GetString(File.ReadAllBytes(copy), 0, 8));
        Assert.False(ColorWorkflowService.HasDisplayTransfer(copy));
        Assert.Equal("LINEAR", SeizaCore.Render(copy).Metadata.Headers["SEIZATRF"].GetString());
        string roundTrip = Path.Combine(_root, "reviewed.fits");
        await ColorToolsService.SaveImageAsync(preview with { Output = copy }, request, roundTrip);
        var original = File.ReadAllBytes(preview.Output!);
        var saved = File.ReadAllBytes(roundTrip);
        // Both files have complete FITS headers in the first 2880-byte block.
        Assert.Equal(original.AsSpan(2880).ToArray(), saved.AsSpan(2880).ToArray());
        File.WriteAllText(copy, "keep");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ColorToolsService.SaveImageAsync(preview, request, copy, new CancellationToken(true)));
        Assert.Equal("keep", File.ReadAllText(copy));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ColorToolsService.SaveImageAsync(preview, request, request.Inputs[0]));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".seiza-color-save-*"));
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
