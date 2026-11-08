using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

/// <summary>Published-ABI integration/replay evidence, not a multi-session batch executor.</summary>
public sealed class CalibrationSessionBoundaryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaSessionBoundary-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void SessionsAndMeasuredRotationSurviveCheckpointAndPixelReplay(bool expanded)
    {
        Assert.True(CalibrationCoreCapabilities.Current.SupportsSessionGates);
        const int width = 160, height = 128, globalWidth = 200;
        ushort[] signal = StarField(globalWidth, height);
        string Light(string name, int gain, int bias, int rotation) => Write(name, "LIGHT", gain, rotation,
            (x, y) => checked((ushort)(signal[y * globalWidth + x + (gain == 200 ? 20 : 0)] + bias)));
        string Write(string name, string kind, int gain, int rotation, Func<int, int, ushort> pixel)
        {
            string path = Path.Combine(_root, name + ".fits");
            CalibrationParityFixtures.WriteFrame(path, width, height, pixel, kind, gain, rotation, normalizedFlat: kind == "FLAT");
            return path;
        }
        string a = Light("a", 100, 500, 0), b = Light("b", 200, 1500, 40), again = Light("a-again", 100, 500, 0);
        string wrong = Light("wrong-gain", 200, 1500, 40), wrongRotation = Light("wrong-rotation", 200, 1500, 40);
        string biasA = Write("bias-a", "BIAS", 100, 0, (_, _) => 500), biasB = Write("bias-b", "BIAS", 200, 90, (_, _) => 1500);
        string flatA = Write("flat-a", "FLAT", 100, 0, (_, _) => 1), flatB = Write("flat-b", "FLAT", 200, 90, (_, _) => 1);
        string[] sources = [a, b, again, wrong, wrongRotation, biasA, biasB, flatA, flatB];
        byte[][] originals = sources.Select(File.ReadAllBytes).ToArray();
        var options = new ImageStackOptions { UseGpu = false, Normalization = StackNormalizationMode.None, Rejection = StackRejectionMode.None };
        nint error;
        nint raw;
        if (expanded)
        {
            using var cancel = new SafeCancelSignalHandle(NativeMethods.CreateCancelSignal());
            // Deliberately uncalibrated planning isolates the available integration/replay boundary.
            // Switching masters afterward cannot fix references planned under the wrong session.
            string paths = JsonSerializer.Serialize(new[] { a, b, again }, SeizaJsonSerializerContext.Default.StringArray);
            raw = NativeMethods.OpenLiveStackerExpanding(paths, null, null, null, 0, options.ToJson(), 1, cancel, out error);
        }
        else raw = NativeMethods.OpenLiveStacker(a, biasA, null, flatA, 0, options.ToJson(), out error);
        Ensure(raw != 0, error);
        using var stacker = new SafeLiveStackerHandle(raw);

        void Apply(string bias, string flat, double? measured = null)
        {
            // All operations in this test have exclusive ownership of the native handle.
            // Setters are separate transactions: abort the push if any setter fails.
            Ensure(NativeMethods.SetLiveStackerMatchTolerances(raw, null, out nint failure), failure);
            Ensure(NativeMethods.SetLiveStackerLightRotation(raw, measured is null ? 0u : 1u, measured ?? 0, out failure), failure);
            Ensure(NativeMethods.SetLiveStackerCalibration(raw, bias, null, flat, 0, out failure), failure);
        }
        if (expanded)
        {
            Assert.Equal(0, State(raw).AcceptedFrames);
            Apply(biasA, flatA);
            Assert.True(Push(raw, a).Accepted);
        }
        Apply(biasB, flatB);
        string recordedFingerprint = State(raw).ConfigurationFingerprint;
        var refusedRotation = Push(raw, wrongRotation);
        Assert.False(refusedRotation.Accepted);
        Assert.Contains("rotation", refusedRotation.Reason!, StringComparison.OrdinalIgnoreCase);
        Apply(biasB, flatB, measured: 90);
        Assert.NotEqual(recordedFingerprint, State(raw).ConfigurationFingerprint);
        Assert.True(Push(raw, b).Accepted);
        Apply(biasA, flatA);
        Assert.True(Push(raw, again).Accepted);
        LiveStackNativeState integrated = State(raw);
        Assert.Equal(3, integrated.AcceptedFrames);
        Assert.Null(integrated.ReintegrationUnavailable);
        foreach (string master in new[] { biasA, biasB, flatA, flatB })
            Assert.Single(integrated.InputPaths, path => LiveStackPath.Equals(path, master));
        using var onlineSnapshot = Snapshot(raw);
        float[] online = Pixels(onlineSnapshot);
        if (expanded) Assert.True(NativeMethods.GetStackSnapshotWidth(onlineSnapshot.DangerousGetHandle()) > width);

        // A measured rotation cannot bypass the hard sensor/gain gate.
        var refusedGain = Push(raw, wrong);
        Assert.False(refusedGain.Accepted);
        Assert.Contains("gain", refusedGain.Reason!, StringComparison.OrdinalIgnoreCase);
        string context = Path.Combine(_root, "stack.context");
        Ensure(NativeMethods.SaveLiveStackerContext(raw, context, out error), error);
        nint restoredRaw = NativeMethods.OpenLiveStackerContext(context, out error);
        Ensure(restoredRaw != 0, error);
        using var restored = new SafeLiveStackerHandle(restoredRaw);
        Assert.True(State(raw).DescribesSameCheckpoint(State(restoredRaw)));
        nint replayRaw = NativeMethods.ReintegrateLiveStacker(restoredRaw, 1000, 1000, 0, null, 0, out error);
        Ensure(replayRaw != 0, error);
        using var replay = new SafeStackSnapshotHandle(replayRaw);
        Assert.Equal(3u, NativeMethods.GetStackSnapshotAcceptedFrames(replayRaw));
        float[] reproduced = Pixels(replay);
        Assert.Equal(online.Length, reproduced.Length);
        Assert.Equal(online.Select(float.IsFinite), reproduced.Select(float.IsFinite));
        float[] differences = online.Zip(reproduced).Where(pair => float.IsFinite(pair.First) && float.IsFinite(pair.Second))
            .Select(pair => Math.Abs(pair.First - pair.Second)).ToArray();
        Assert.True(differences.Length > width * height / 2);
        Assert.True(differences.Max() < 0.01f, $"Maximum replay difference: {differences.Max()} ADU");
        float[] background = online.Where(value => float.IsFinite(value) && value > 0).Order().ToArray();
        Assert.InRange(background[background.Length / 2], 90, 110);

        // B remains necessary after switching back to A and reopening the checkpoint.
        string hidden = Path.Combine(_root, "bias-b-away.fits");
        File.Move(biasB, hidden);
        try
        {
            nint unavailable = NativeMethods.ReintegrateLiveStacker(restoredRaw, 1000, 1000, 0, null, 0, out error);
            using var unexpected = new SafeStackSnapshotHandle(unavailable);
            string message = error == 0 ? string.Empty : NativeString.TakeOwned(error, "");
            Assert.Equal(0, unavailable);
            Assert.Contains("bias-b.fits", message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Move(hidden, biasB); }
        for (int i = 0; i < sources.Length; i++) Assert.Equal(SHA256.HashData(originals[i]), SHA256.HashData(File.ReadAllBytes(sources[i])));
    }

    [Fact]
    public void FailedMasterReplacementLeavesThePreviousSetAndCheckpointIdentityIntact()
    {
        string light = Path.Combine(_root, "light.fits"), next = Path.Combine(_root, "next.fits");
        string bias = Path.Combine(_root, "bias.fits"), wrong = Path.Combine(_root, "wrong-size.fits");
        ushort[] signal = StarField(160, 128);
        CalibrationParityFixtures.WriteFrame(light, 160, 128, (x, y) => checked((ushort)(signal[y * 160 + x] + 500)));
        File.Copy(light, next);
        CalibrationParityFixtures.WriteFrame(bias, 160, 128, (_, _) => 500, "BIAS");
        CalibrationParityFixtures.WriteFrame(wrong, 32, 32, (_, _) => 1500, "BIAS");
        string options = new ImageStackOptions { UseGpu = false, Normalization = StackNormalizationMode.None, Rejection = StackRejectionMode.None }.ToJson();
        nint raw = NativeMethods.OpenLiveStacker(light, bias, null, null, 0, options, out nint error);
        Ensure(raw != 0, error);
        using var stacker = new SafeLiveStackerHandle(raw);
        var before = State(raw);
        Assert.False(NativeMethods.SetLiveStackerCalibration(raw, wrong, null, null, 0, out error));
        Assert.NotEmpty(NativeString.TakeOwned(error, ""));
        Assert.True(before.DescribesSameCheckpoint(State(raw)));
        Assert.True(Push(raw, next).Accepted);
        using var snapshot = Snapshot(raw);
        float[] values = Pixels(snapshot).Where(float.IsFinite).Order().ToArray();
        Assert.InRange(values[values.Length / 2], 90, 110);
    }

    [Fact]
    public unsafe void PreparedInputCannotStandInForSessionAwareRawPlanning()
    {
        string path = Path.Combine(_root, "prepared.fits");
        ushort[] signal = StarField(160, 128);
        CalibrationParityFixtures.WriteFrame(path, 160, 128, (x, y) => signal[y * 160 + x]);
        nint raw = NativeMethods.OpenPreparedLiveStacker(path, null, out nint error);
        Ensure(raw != 0, error);
        using var stacker = new SafeLiveStackerHandle(raw);
        Assert.Equal("prepared-only", State(raw).InputMode);
        Assert.False(NativeMethods.SetLiveStackerCalibration(raw, null, null, null, 0, out error));
        Assert.NotEmpty(NativeString.TakeOwned(error, ""));
        Assert.False(NativeMethods.SetLiveStackerMatchTolerances(raw, null, out error));
        Assert.NotEmpty(NativeString.TakeOwned(error, ""));
        Assert.False(NativeMethods.SetLiveStackerLightRotation(raw, 1, 90, out error));
        Assert.NotEmpty(NativeString.TakeOwned(error, ""));
    }

    private static void Ensure(bool success, nint error)
    {
        if (!success) throw NativeString.TakeError(error, "Native calibration boundary call failed.");
        if (error != 0) NativeMethods.FreeString(error);
    }

    private static LiveStackNativeState State(nint stacker)
    {
        nint json = NativeMethods.GetLiveStackerStateJson(stacker, out nint error);
        Ensure(json != 0, error);
        return JsonSerializer.Deserialize(NativeString.TakeOwned(json, ""), SeizaJsonSerializerContext.Default.LiveStackNativeState)!;
    }

    private static ImageStackDisposition Push(nint stacker, string path)
    {
        nint json = NativeMethods.PushLiveStackerFrameJson(stacker, path, out nint error);
        Ensure(json != 0, error);
        return JsonSerializer.Deserialize(NativeString.TakeOwned(json, ""), SeizaJsonSerializerContext.Default.ImageStackDisposition)!;
    }

    private static SafeStackSnapshotHandle Snapshot(nint stacker)
    {
        nint raw = NativeMethods.SnapshotLiveStacker(stacker, out nint error);
        Ensure(raw != 0, error);
        return new(raw);
    }

    private static float[] Pixels(SafeStackSnapshotHandle snapshot)
    {
        nint raw = snapshot.DangerousGetHandle();
        var values = new float[checked((int)NativeMethods.GetStackSnapshotDataLength(raw))];
        Marshal.Copy(NativeMethods.GetStackSnapshotImage(raw), values, 0, values.Length);
        return values;
    }

    private static ushort[] StarField(int width, int height)
    {
        var random = new Random(1729);
        var stars = Enumerable.Range(0, 35).Select(_ => (X: 10 + random.NextDouble() * (width - 20),
            Y: 10 + random.NextDouble() * (height - 20), Peak: 2000 + random.NextDouble() * 4000)).ToArray();
        ushort[] result = new ushort[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double value = 100 + random.NextDouble() * 6 - 3;
                foreach (var star in stars)
                {
                    double dx = x - star.X, dy = y - star.Y;
                    value += star.Peak * Math.Exp(-(dx * dx + dy * dy) / 5.12);
                }
                result[y * width + x] = checked((ushort)Math.Round(value));
            }
        return result;
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(_root)) File.Delete(path);
        Directory.Delete(_root);
    }
}
