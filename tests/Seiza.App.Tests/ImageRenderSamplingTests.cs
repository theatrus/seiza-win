using System.Buffers.Binary;
using System.Text;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImageRenderSamplingTests
{
    [Fact]
    public void PublishedCoreAveragesReducedFits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"seiza-sampling-{Guid.NewGuid():N}.fits");
        try
        {
            byte[] fits = new byte[5760];
            Array.Fill(fits, (byte)' ', 0, 2880);
            string[] cards = [
                "SIMPLE  =                    T",
                "BITPIX  =                   16",
                "NAXIS   =                    2",
                "NAXIS1  =                    8",
                "NAXIS2  =                    8",
                "BZERO   =                32768",
                "END",
            ];
            for (int index = 0; index < cards.Length; index++)
            {
                Encoding.ASCII.GetBytes(cards[index].PadRight(80)).CopyTo(fits, index * 80);
            }
            for (int index = 0; index < 64; index++)
            {
                short value = (index / 8 + index % 8) % 2 == 0 ? short.MinValue : short.MaxValue;
                BinaryPrimitives.WriteInt16BigEndian(fits.AsSpan(2880 + index * 2, 2), value);
            }
            File.WriteAllBytes(path, fits);
            var full = SeizaCore.Render(path);
            var reduced = SeizaCore.Render(path, maxDimension: 2);
            Assert.Equal(2, reduced.Width);
            Assert.Equal(2, reduced.Height);
            Assert.NotEqual(full.Bgra[0], full.Bgra[4]);
            for (int channel = 0; channel < 3; channel++)
            {
                byte expected = (byte)((full.Bgra[channel] + full.Bgra[4 + channel] + 1) / 2);
                for (int pixel = 0; pixel < 4; pixel++)
                {
                    Assert.Equal(expected, reduced.Bgra[pixel * 4 + channel]);
                    Assert.Equal(255, reduced.Bgra[pixel * 4 + 3]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
