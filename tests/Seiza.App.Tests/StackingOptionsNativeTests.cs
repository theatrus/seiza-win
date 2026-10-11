using System.Buffers.Binary;
using System.Text;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

/// <summary>
/// Exercises the application JSON against the shipped DLL, not a mocked native contract.
/// Like the other native tests, the DLL is required; CI builds and copies it first.
/// </summary>
public sealed class StackingOptionsNativeTests
{
    private static readonly int[] ExpectedReintegrationPasses = [0, 2];
    private static readonly int[] ExpectedReintegrationFrameIndices = [0, 1, 2, 3];

    [Fact]
    public async Task LiveCoordinatorSnapshotsEveryAdvancedOptionBeforeOpeningTheNativeStack()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string captureDirectory = Path.Combine(directory, "captures");
            Directory.CreateDirectory(captureDirectory);
            string reference = Path.Combine(directory, "reference.fits");
            WriteStarField(reference, 0, bayer: true);
            var options = new ImageStackOptions
            {
                RegistrationModel = StackRegistrationModel.Quadratic,
                Normalization = StackNormalizationMode.LocalBackground,
                LocalTileSize = 64,
                Weighting = StackWeightingMode.InverseNoiseVariance,
                MinimumWeight = 0.125,
                MaximumWeight = 8,
                Interpolation = StackInterpolation.Lanczos3,
                Demosaic = StackDemosaic.Mhc,
                CfaIntegration = StackCfaIntegration.BayerDrizzle,
                SuppressHotPixels = true,
                CosmeticLowSigma = 12,
                CosmeticHighSigma = 14,
            };
            ImageStackOptions expectedOptions = options.Copy();
            string expectedJson = expectedOptions.ToJson();
            var configuration = new LiveStackRunConfiguration
            {
                WatchFolder = captureDirectory,
                SessionRootDirectory = Path.Combine(directory, "sessions"),
                InitialReferencePath = reference,
                Options = options,
                ResumeExisting = false,
                PreviewMaxDimension = 128,
            };
            await using (var coordinator = new LiveStackCoordinator(configuration))
            {
                // Editing the source controls after construction must neither
                // overwrite the captured recipe nor alter its native identity.
                options.RegistrationModel = StackRegistrationModel.Similarity;
                options.Normalization = StackNormalizationMode.Global;
                options.LocalTileSize = 256;
                options.Weighting = StackWeightingMode.Equal;
                options.MinimumWeight = 0.05;
                options.MaximumWeight = 20;
                options.Interpolation = StackInterpolation.Bilinear;
                options.Demosaic = StackDemosaic.Vng;
                options.CfaIntegration = StackCfaIntegration.Demosaic;
                options.SuppressHotPixels = false;
                options.CosmeticLowSigma = 16;
                options.CosmeticHighSigma = 16;
                Assert.NotEqual(expectedJson, options.ToJson());

                await coordinator.StartAsync();
                Assert.Equal(1, coordinator.CurrentSnapshot.AcceptedFrames);
                await coordinator.PauseAndSaveAsync();
                Assert.Equal(LiveStackRunState.Paused, coordinator.CurrentSnapshot.State);
            }

            string groupDirectory = Path.Combine(configuration.SessionRootDirectory,
                LiveStackSessionStore.SafeGroupDirectoryName(configuration.GroupId));
            using var store = new LiveStackSessionStore(groupDirectory);
            LiveStackStoredGeneration generation = (await store.GetRestoreCandidatesAsync())[0];
            Assert.Equal(expectedJson, generation.State.StackOptionsJson);
            await using ImageStackSession expected = await ImageStackSession.OpenAsync(
                reference, expectedOptions, new ImageStackCalibration());
            LiveStackNativeState expectedState = await expected.GetStateAsync();
            Assert.Equal(expectedState.ConfigurationFingerprint, generation.ExpectedNativeState.ConfigurationFingerprint);
            await using ImageStackSession restored = await ImageStackSession.ResumeAsync(generation.ContextPath);
            Assert.Equal(expectedState.ConfigurationFingerprint, (await restored.GetStateAsync()).ConfigurationFingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, true)]
    public async Task OptionsSurviveNativePushCheckpointResumeAndReintegration(
        bool advanced,
        int model,
        bool bayerDrizzle)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] frames = Enumerable.Range(0, 4)
                .Select(index => Path.Combine(directory, $"frame-{index}.fits"))
                .ToArray();
            for (int index = 0; index < frames.Length; index++)
            {
                WriteStarField(frames[index], index, bayerDrizzle);
            }

            var options = new ImageStackOptions
            {
                RegistrationModel = (StackRegistrationModel)model,
            };
            if (advanced)
            {
                options.Normalization = StackNormalizationMode.LocalBackground;
                options.LocalTileSize = 64;
                options.Weighting = StackWeightingMode.InverseNoiseVariance;
                options.MinimumWeight = 0.125;
                options.MaximumWeight = 8;
                options.Interpolation = StackInterpolation.Lanczos3;
                options.Demosaic = StackDemosaic.Mhc;
                options.SuppressHotPixels = true;
                options.CosmeticLowSigma = 12;
                options.CosmeticHighSigma = 14;
            }
            if (bayerDrizzle)
            {
                options.CfaIntegration = StackCfaIntegration.BayerDrizzle;
            }

            string checkpoint = Path.Combine(directory, "stack.context");
            LiveStackNativeState saved;
            ImageStackLiveView liveView;
            await using (ImageStackSession session = await ImageStackSession.OpenAsync(
                frames[0], options, new ImageStackCalibration()))
            {
                foreach (string frame in frames.Skip(1))
                {
                    ImageStackPushResult result = await session.PushFrameAsync(frame);
                    Assert.False(result.NativeFailure, result.Disposition.Reason);
                    Assert.True(result.Disposition.Accepted, result.Disposition.Reason);
                }
                saved = await session.SaveContextAndGetStateAsync(checkpoint);
                liveView = await session.CopyLiveViewAsync();
                Assert.Equal(4, saved.AcceptedFrames);
                Assert.Equal(0, saved.RejectedFrames);
                Assert.Equal(256, saved.Width);
                Assert.Equal(256, saved.Height);
                Assert.Equal(bayerDrizzle ? 3 : 1, saved.Channels);
                Assert.Null(saved.ReintegrationUnavailable);
                Assert.True(saved.InputPaths.Zip(frames).All(pair => LiveStackPath.Equals(pair.First, pair.Second)));
                Assert.Equal(frames.Length, saved.InputPaths.Length);
                Assert.All(liveView.Mean, value => Assert.True(float.IsFinite(value)));
                if (bayerDrizzle)
                {
                    // Drizzle retains the sparse CFA coverage instead of
                    // fabricating four samples for each color at every pixel.
                    Assert.Contains(liveView.Coverage, value => value > 0);
                    Assert.All(liveView.Coverage, value => Assert.InRange(value, 0U, 4U));
                }
                else
                {
                    Assert.Contains(liveView.Coverage, value => value == 4);
                }
            }

            await using ImageStackSession resumed = await ImageStackSession.ResumeAsync(checkpoint);
            LiveStackNativeState restored = await resumed.GetStateAsync();
            Assert.True(saved.DescribesSameCheckpoint(restored));
            Assert.Equal(saved.ConfigurationFingerprint, restored.ConfigurationFingerprint);
            ImageStackLiveView restoredView = await resumed.CopyLiveViewAsync();
            Assert.Equal(liveView.Mean, restoredView.Mean);
            Assert.Equal(liveView.Coverage, restoredView.Coverage);

            var reports = new List<ImageStackReintegrationProgress>();
            await using ImageStackSnapshot cleaned = await resumed.ReintegrateAsync(
                options.TransientLowSigma,
                options.TransientHighSigma,
                new ImmediateProgress(reports));
            Assert.Equal(4, cleaned.AcceptedFrames);
            Assert.Equal(0, cleaned.RejectedFrames);
            // Seiza 0.25 integrates retained scratch frames band by band. All
            // three rejection passes still run, but the ABI reports pass 0
            // for normalization reads and pass 2 across the bands; pass 1 is
            // announced separately only on the whole-frame fallback path.
            Assert.Equal(ExpectedReintegrationPasses, reports.Select(report => report.Pass).Distinct().ToArray());
            foreach (int pass in ExpectedReintegrationPasses)
            {
                Assert.Equal(ExpectedReintegrationFrameIndices,
                    reports.Where(report => report.Pass == pass).Select(report => report.Index).ToArray());
            }
            Assert.All(reports, report => Assert.Equal(4, report.Count));
            string output = Path.Combine(directory, "cleaned.fits");
            await cleaned.WriteFitsAsync(output);
            Assert.True(new FileInfo(output).Length > 2880);
            Assert.Equal("SIMPLE", Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 6));

            // Reintegration returns an independent snapshot; it must not mutate the live accumulator.
            ImageStackLiveView afterReintegration = await resumed.CopyLiveViewAsync();
            Assert.Equal(liveView.Mean, afterReintegration.Mean);
            Assert.Equal(liveView.Coverage, afterReintegration.Coverage);
            Assert.Equal(saved.ConfigurationFingerprint, (await resumed.GetStateAsync()).ConfigurationFingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ImmediateProgress(List<ImageStackReintegrationProgress> reports)
        : IProgress<ImageStackReintegrationProgress>
    {
        public void Report(ImageStackReintegrationProgress value) => reports.Add(value);
    }

    internal static void WriteStarField(string path, int frame, bool bayer, int side = 256, double noiseRange = 40)
    {
        const int headerBytes = 2880;
        int dataBytes = side * side * sizeof(float);
        byte[] fits = new byte[headerBytes + ((dataBytes + 2879) / 2880) * 2880];
        Array.Fill(fits, (byte)' ', 0, headerBytes);
        string[] cards =
        [
            "SIMPLE  =                    T",
            "BITPIX  =                  -32",
            "NAXIS   =                    2",
            $"NAXIS1  = {side,20}",
            $"NAXIS2  = {side,20}",
            "EXPTIME =                   60",
            "IMAGETYP= 'Light Frame'",
            bayer ? "BAYERPAT= 'RGGB'" : "COMMENT monochrome synthetic field",
            "END",
        ];
        for (int index = 0; index < cards.Length; index++)
        {
            Encoding.ASCII.GetBytes(cards[index].PadRight(80)).CopyTo(fits, index * 80);
        }

        // Fixed irregular star positions prevent an ambiguous grid match. A small
        // subpixel translation and independently seeded noise exercise resampling.
        var stars = new (double X, double Y, double Peak)[48];
        var positions = new Random(1729);
        for (int index = 0; index < stars.Length; index++)
        {
            stars[index] = (20 + positions.NextDouble() * (side - 40),
                20 + positions.NextDouble() * (side - 40),
                3000 + positions.NextDouble() * 5000);
        }
        var noise = new Random(811 + frame);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                double value = 1000 + x * 0.05 + y * 0.03 + (noise.NextDouble() - 0.5) * noiseRange;
                foreach ((double starX, double starY, double peak) in stars)
                {
                    double dx = x - starX - frame * 0.35;
                    double dy = y - starY + frame * 0.2;
                    value += peak * Math.Exp(-(dx * dx + dy * dy) / 5.12);
                }
                if (bayer)
                {
                    value *= y % 2 == 0 && x % 2 == 0 ? 0.8 :
                        y % 2 != 0 && x % 2 != 0 ? 0.6 : 1;
                }
                BinaryPrimitives.WriteSingleBigEndian(
                    fits.AsSpan(headerBytes + (y * side + x) * sizeof(float), sizeof(float)),
                    (float)value);
            }
        }
        File.WriteAllBytes(path, fits);
    }
}
