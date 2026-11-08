using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Seiza.App.Tests;

internal static class CalibrationParityFixtures
{
    internal static JsonDocument Read(string name) => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "CalibrationParity", name)));

    internal static void WriteFrame(string path, int width, int height, Func<int, int, ushort> pixel,
        string kind = "LIGHT", int gain = 100, int rotation = 0, bool normalizedFlat = false, IReadOnlyList<string>? extraCards = null)
    {
        int bytes = checked(width * height * 2);
        byte[] fits = new byte[2880 + ((bytes + 2879) / 2880) * 2880];
        Array.Fill(fits, (byte)' ', 0, 2880);
        string[] cards = ["SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2",
            $"NAXIS1  = {width,20}", $"NAXIS2  = {height,20}", "BZERO   =                32768", "BSCALE  =                    1",
            $"IMAGETYP= '{kind}'", "INSTRUME= 'Parity camera'", "TELESCOP= 'Parity scope'", "XBINNING=                    1",
            "YBINNING=                    1", "EXPTIME =                   60", "CCD-TEMP=                  -10", "FOCALLEN=                  500",
            $"GAIN    = {gain,20}", $"ROTATANG= {rotation,20}", normalizedFlat ? "FLATNORM=                    T" : "COMMENT raw test image", "END"];
        cards = [.. cards[..^1], .. extraCards ?? [], "END"];
        for (int i = 0; i < cards.Length; i++) Encoding.ASCII.GetBytes(cards[i].PadRight(80)).CopyTo(fits, i * 80);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                BinaryPrimitives.WriteInt16BigEndian(fits.AsSpan(2880 + (y * width + x) * 2, 2),
                    checked((short)(pixel(x, y) - 32768)));
        File.WriteAllBytes(path, fits);
    }
}
