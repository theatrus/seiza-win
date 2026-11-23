using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class AiProcessingIntegrationFactAttribute : FactAttribute
{
    public AiProcessingIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SEIZA_TEST_AI_IMAGE") is null ||
            Environment.GetEnvironmentVariable("SEIZA_TEST_AI_MODEL") is null)
            Skip = "Set SEIZA_TEST_AI_IMAGE and SEIZA_TEST_AI_MODEL to exercise an installed model and native runtime.";
    }
}

public sealed class AiProcessingIntegrationTests
{
    [AiProcessingIntegrationFact]
    public async Task StrengthAndStretchChangesReuseInferenceWhileSourceAndRegionChangesInvalidateIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "seiza-ai-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source" + Path.GetExtension(Environment.GetEnvironmentVariable("SEIZA_TEST_AI_IMAGE")));
        File.Copy(Environment.GetEnvironmentVariable("SEIZA_TEST_AI_IMAGE")!, source);
        try
        {
            var settings = new AiProcessing { Model = Environment.GetEnvironmentVariable("SEIZA_TEST_AI_MODEL"), Backend = "directml", DeviceId = 1 };
            var region = new ProcessingRegion(32, 32, 128, 96);
            var messages = new List<string>();
            var progress = new InlineProgress<RestorationProgress>(step => messages.Add(step.Message));
            async Task Render(AiProcessing ai, ProcessingRegion area, bool identity = false) => await ImageRenderService.RenderAsync(source,
                new FitsImageProcessingConfiguration(identity ? new FitsStretchStack([FitsStretchConfiguration.CreateIdentity()]) : FitsStretchStack.Default, null, ai: ai), maxDimension: 256,
                region: area, progress: progress);

            await Render(settings, region);
            Assert.Contains("Opening the model…", messages);
            messages.Clear();
            await Render(settings with { High = 0.4, Low = 0.6 }, region);
            Assert.DoesNotContain("Opening the model…", messages);
            Assert.Contains(messages, text => text.StartsWith("Using cached inference", StringComparison.Ordinal));
            messages.Clear();
            await Render(settings with { High = 0.4, Low = 0.6 }, region, identity: true);
            Assert.Contains("Using cached AI result", messages);

            messages.Clear();
            await Render(settings, region with { X = 64 });
            Assert.Contains("Opening the model…", messages);
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
            messages.Clear();
            await Render(settings, region);
            Assert.Contains("Opening the model…", messages);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [AiProcessingIntegrationFact]
    public async Task LinearSaveUsesFullGeometryAndRejectsReplacingTheSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "seiza-ai-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Seiza.slnx"))) root = root.Parent;
            string source = Path.Combine(root!.FullName, "tests", "fixtures", "noise-model", "tiny-input.fits");
            string destination = Path.Combine(directory, "full.fits");
            var settings = new AiProcessing { Model = Environment.GetEnvironmentVariable("SEIZA_TEST_AI_MODEL"), Backend = "directml", DeviceId = 1 };
            var progress = new InlineProgress<RestorationProgress>(_ => { });
            await ImageRenderService.RenderAsync(source,
                new FitsImageProcessingConfiguration(FitsStretchStack.Default, null, ai: settings),
                region: new ProcessingRegion(10, 10, 64, 64), progress: progress);
            await ImageRenderService.SaveLinearAiAsync(source, destination, settings, progress, CancellationToken.None);
            var header = ImageSourceService.ReadHeader(destination);
            Assert.Equal(128, header.Headers["NAXIS1"].GetInt32());
            Assert.Equal(96, header.Headers["NAXIS2"].GetInt32());
            Assert.False(header.Headers.ContainsKey("MDPREV"));
            Assert.Equal("LINEAR", header.Text("SEIZATRF"));
            await Assert.ThrowsAsync<IOException>(() => ImageRenderService.SaveLinearAiAsync(source, source, settings, progress, CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageRenderService.SaveLinearAiAsync(source, Path.Combine(directory, "cancelled.fits"), settings, progress, cancelled.Token));
            Assert.False(File.Exists(Path.Combine(directory, "cancelled.fits")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

}
