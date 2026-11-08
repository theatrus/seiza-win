using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class ImageSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaImageSource-").FullName;
    private string Frame(string name, string filter)
    {
        string path = Path.Combine(_root, name + ".fits");
        CalibrationParityFixtures.WriteFrame(path, 8, 4, (x, y) => (ushort)(40000 + 8 * y + x), extraCards:
            [$"FILTER  = '{filter}'", "SEIZAPCC= 'GAIA'", "SEIZATRF= 'DISPLAY'", "HISTORY Source history", "COMMENT Source comment"]);
        return path;
    }

    [Fact]
    public void HeaderReaderKeepsCommentaryMarkersAndInvalidatesChangedFiles()
    {
        string path = Frame("image_Ha", "OIII"); byte[] original = File.ReadAllBytes(path);
        ImageSourceHeader header = ImageSourceService.ReadHeader(path);
        Assert.Equal("OIII", header.Text("FILTER")); Assert.Equal("GAIA", header.Text("SEIZAPCC"));
        Assert.Contains(header.History, h => h.Contains("Source history"));
        Assert.Contains(header.Comments, h => h.Contains("Source comment"));
        Assert.Same(header, ImageSourceService.ReadHeader(path));
        Assert.True(ColorWorkflowService.HasDisplayTransfer(path));
        Assert.Equal(original, File.ReadAllBytes(path));
        Frame("image_Ha", "SII"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        ImageSourceHeader changed = ImageSourceService.ReadHeader(path);
        Assert.NotSame(header, changed); Assert.Equal("SII", changed.Text("FILTER")); Assert.Equal("OIII", header.Text("FILTER"));
    }

    [Fact]
    public async Task BrowserBatchAndLiveUseTheSameHeaderFirstFilterAndFilenameFallback()
    {
        string a = Frame("misleading_Ha", "OIII"), b = Frame("other_OIII", "Ha"), c = Frame("fallback_SII", "");
        string[] paths = [a, b, c];
        var filters = await ImageSourceService.ReadFiltersAsync(paths);
        var routing = await StackInputRouting.RouteAsync(paths);
        var browser = ImageBrowserGroupingModel.Create(paths, _root, ImageBrowserGrouping.Filter, filters: filters);
        var batch = ImageStackGrouping.Groups(paths, true, routing.Filters);
        foreach (string path in paths)
        {
            var live = LiveStackFilterIdentity.FromProbe(await CalibrationService.ProbeAsync(path));
            Assert.Equal(live.Key, Assert.Single(browser.Groups, g => g.Paths.Contains(path)).Key);
            Assert.Equal(live.Key, Assert.Single(batch, g => g.Inputs.Contains(path)).Id);
        }
        Assert.Equal("oxygen-iii", Assert.Single(batch, g => g.Inputs.Contains(a)).Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageSourceService.ReadFiltersAsync(paths, new(true)));
    }

    [Fact]
    public async Task WindowsWindowReadsPhysicalUnsignedSamplesAndClipsAtEdges()
    {
        string path = Frame("samples", "L"); byte[] before = File.ReadAllBytes(path);
        var window = await ImageSourceService.ReadWindowAsync(path, 6, 2, 4, 4);
        Assert.Equal((8, 4, 1, 6, 2, 2, 2), (window.ImageWidth, window.ImageHeight, window.Planes, window.X, window.Y, window.Width, window.Height));
        Assert.Equal(new float[] { 40022, 40023, 40030, 40031 }, window.Values);
        Assert.Empty((await ImageSourceService.ReadWindowAsync(path, 100, 100, 1, 1)).Values);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageSourceService.ReadWindow(path, 0, 0, 1025, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageSourceService.ReadWindow(path, 0, 0, 1, 1, float.NaN));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageSourceService.ReadWindowAsync(path, 0, 0, 1, 1, token: new(true)));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void NormalizedXisfWindowsPreserveStoredUnitsOrUseRequestedScaleAndCarryPixInsightWcs()
    {
        string Vector(string id, string dimensions, params double[] values) =>
            $"<Property id=\"AstrometricSolution:{id}\" type=\"{(dimensions.StartsWith("rows", StringComparison.Ordinal) ? "F64Matrix" : "F64Vector")}\" {dimensions} location=\"inline:hex\">{Convert.ToHexString(values.SelectMany(BitConverter.GetBytes).ToArray())}</Property>";
        string properties = "<Property id=\"AstrometricSolution:Version\" type=\"String\">1.3</Property>" +
            "<Property id=\"AstrometricSolution:ProjectionSystem\" type=\"String\">Gnomonic</Property>" +
            Vector("ReferenceCelestialCoordinates", "length=\"2\"", 83.8, -5.4) +
            Vector("ReferenceImageCoordinates", "length=\"2\"", 100.5, 80.5) +
            Vector("LinearTransformationMatrix", "rows=\"2\" columns=\"2\"", -0.0002, 0, 0, 0.0002);
        string path = Xisf(false, properties);
        Assert.Equal(new float[] { 0, .25f, .5f, .75f }, ImageSourceService.ReadWindow(path, 0, 0, 2, 2).Values);
        Assert.Equal(new float[] { 0, .25f * 65535, .5f * 65535, .75f * 65535 }, ImageSourceService.ReadWindow(path, 0, 0, 2, 2, 65535).Values);
        var header = ImageSourceService.ReadHeader(path);
        Assert.Equal(83.8, header.Headers["CRVAL1"].GetDouble()); Assert.Equal(101, header.Headers["CRPIX1"].GetDouble());
        Assert.Empty(header.History); Assert.Empty(header.Comments);
        string compressed = Xisf(true, "");
        Assert.Throws<SeizaCoreException>(() => ImageSourceService.ReadWindow(compressed, 0, 0, 1, 1));
    }

    [Fact]
    public void RgbWindowsKeepPlaneOrderWhenReadingOnlyPartOfTheImage()
    {
        var window = ImageSourceService.ReadWindow(Xisf(false, "", 3), 1, 0, 1, 2);
        Assert.Equal(3, window.Planes);
        float[] expected = [.25f, .75f, 1.25f, 1.75f, 2.25f, 2.75f];
        Assert.Equal(expected, window.Values);
    }

    private string Xisf(bool compressed, string properties, int planes = 1)
    {
        byte[] pixels = Enumerable.Range(0, planes * 4).Select(i => i / 4f).SelectMany(BitConverter.GetBytes).ToArray();
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var zipper = new ZLibStream(output, CompressionLevel.Fastest, true)) zipper.Write(pixels);
            pixels = output.ToArray();
        }
        int offset = 0; byte[] header;
        while (true)
        {
            header = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\"><Image geometry=\"2:2:{planes}\" sampleFormat=\"Float32\" colorSpace=\"{(planes == 3 ? "RGB" : "Gray")}\" bounds=\"0:1\" {(compressed ? "compression=\"zlib:16\"" : "")} location=\"attachment:{offset}:{pixels.Length}\">{properties}</Image></xisf>");
            if (offset == header.Length + 16) break;
            offset = header.Length + 16;
        }
        string path = Path.Combine(_root, Guid.NewGuid() + ".xisf");
        using var file = File.Create(path); file.Write("XISF0100"u8);
        byte[] preamble = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(preamble, (uint)header.Length);
        file.Write(preamble); file.Write(header); file.Write(pixels); return path;
    }

    [WindowsStackSourceIdentityFact]
    public void WindowsReadsSmallWindowAtFarEdgeOfFortyThousandPixelSparseMosaic()
    {
        string path = Path.Combine(_root, "mosaic.fits");
        const int edge = 40000;
        using (var file = File.Create(path))
        {
            Assert.True(Sparse(file.SafeFileHandle, 0x000900C4, 0, 0, 0, 0, out _, 0), new Win32Exception(Marshal.GetLastWin32Error()).Message);
            string[] cards = ["SIMPLE  =                    T", "BITPIX  =                  -32", "NAXIS   =                    2", "NAXIS1  =                40000", "NAXIS2  =                40000", "END"];
            byte[] header = Enumerable.Repeat((byte)' ', 2880).ToArray();
            for (int i = 0; i < cards.Length; i++) Encoding.ASCII.GetBytes(cards[i].PadRight(80)).CopyTo(header, i * 80);
            file.Write(header); file.SetLength(2880L + edge * (long)edge * 4);
            byte[] value = new byte[4];
            for (int row = 0; row < 2; row++) for (int col = 0; col < 2; col++)
            {
                file.Position = 2880L + ((edge - 2L + row) * edge + edge - 2 + col) * 4;
                BinaryPrimitives.WriteSingleBigEndian(value, 100 + 2 * row + col); file.Write(value);
            }
        }
        var window = ImageSourceService.ReadWindow(path, edge - 2, edge - 2, 2, 2);
        Assert.Equal((edge, edge), (window.ImageWidth, window.ImageHeight));
        Assert.Equal(new float[] { 100, 101, 102, 103 }, window.Values);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Sparse(SafeFileHandle handle, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    public void Dispose() => Directory.Delete(_root, true);
}
