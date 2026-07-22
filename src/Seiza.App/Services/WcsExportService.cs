using System.Globalization;
using System.Text;
using Seiza.App.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Seiza.App.Services;

internal static class WcsExportService
{
    private const int FitsCardLength = 80;
    private const int FitsBlockLength = 2880;

    public static async Task<StorageFile?> PickAndSaveAsync(
        string sourcePath,
        WcsResult wcs)
    {
        Validate(wcs);

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath),
        };
        picker.FileTypeChoices.Add("FITS WCS solution", [".wcs"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        await FileIO.WriteBytesAsync(file, BuildFile(wcs));
        return file;
    }

    internal static byte[] BuildFile(WcsResult wcs)
    {
        Validate(wcs);

        var header = new StringBuilder();
        AppendCard(header, "SIMPLE", "T");
        AppendCard(header, "BITPIX", "8");
        AppendCard(header, "NAXIS", "0");

        bool hasSip = wcs.Sip is not null;
        AppendTextCard(header, "CTYPE1", hasSip ? "RA---TAN-SIP" : "RA---TAN");
        AppendTextCard(header, "CTYPE2", hasSip ? "DEC--TAN-SIP" : "DEC--TAN");
        AppendTextCard(header, "CUNIT1", "deg");
        AppendTextCard(header, "CUNIT2", "deg");
        AppendNumberCard(header, "EQUINOX", 2000.0);
        AppendNumberCard(header, "CRVAL1", wcs.Crval[0]);
        AppendNumberCard(header, "CRVAL2", wcs.Crval[1]);
        AppendNumberCard(header, "CRPIX1", wcs.Crpix[0] + 1.0);
        AppendNumberCard(header, "CRPIX2", wcs.Crpix[1] + 1.0);
        AppendNumberCard(header, "CD1_1", wcs.Cd[0][0]);
        AppendNumberCard(header, "CD1_2", wcs.Cd[0][1]);
        AppendNumberCard(header, "CD2_1", wcs.Cd[1][0]);
        AppendNumberCard(header, "CD2_2", wcs.Cd[1][1]);

        if (wcs.Sip is { } sip)
        {
            AppendCard(header, "A_ORDER", sip.Order.ToString(CultureInfo.InvariantCulture));
            AppendCard(header, "B_ORDER", sip.Order.ToString(CultureInfo.InvariantCulture));
            AppendSipCoefficients(header, "A", sip.A, sip.Order, minimumTotal: 2);
            AppendSipCoefficients(header, "B", sip.B, sip.Order, minimumTotal: 2);
            AppendCard(header, "AP_ORDER", sip.Order.ToString(CultureInfo.InvariantCulture));
            AppendCard(header, "BP_ORDER", sip.Order.ToString(CultureInfo.InvariantCulture));
            AppendSipCoefficients(header, "AP", sip.Ap, sip.Order, minimumTotal: 0);
            AppendSipCoefficients(header, "BP", sip.Bp, sip.Order, minimumTotal: 0);
        }

        header.Append("END".PadRight(FitsCardLength));
        int padding = (FitsBlockLength - header.Length % FitsBlockLength) % FitsBlockLength;
        header.Append(' ', padding);
        return Encoding.ASCII.GetBytes(header.ToString());
    }

    private static void AppendSipCoefficients(
        StringBuilder header,
        string prefix,
        double[] values,
        int order,
        int minimumTotal)
    {
        int index = 0;
        for (int p = 0; p <= order && index < values.Length; p++)
        {
            for (int q = 0; q <= order - p && index < values.Length; q++)
            {
                if (p + q < minimumTotal)
                {
                    continue;
                }

                AppendNumberCard(header, $"{prefix}_{p}_{q}", values[index]);
                index++;
            }
        }
    }

    private static void AppendTextCard(StringBuilder header, string keyword, string value) =>
        AppendCard(header, keyword, $"'{value}'");

    private static void AppendNumberCard(StringBuilder header, string keyword, double value) =>
        AppendCard(header, keyword, value.ToString("E13", CultureInfo.InvariantCulture));

    private static void AppendCard(StringBuilder header, string keyword, string value)
    {
        string card = $"{keyword,-8}= {value,20}";
        if (card.Length > FitsCardLength)
        {
            throw new InvalidDataException($"FITS card {keyword} is too long.");
        }

        header.Append(card);
        header.Append(' ', FitsCardLength - card.Length);
    }

    private static void Validate(WcsResult wcs)
    {
        if (wcs.Crval.Length != 2 || wcs.Crpix.Length != 2 ||
            wcs.Cd.Length != 2 || wcs.Cd.Any(row => row.Length != 2))
        {
            throw new InvalidDataException("The plate solution does not contain a complete WCS transform.");
        }

        IEnumerable<double> values = wcs.Crval
            .Concat(wcs.Crpix)
            .Concat(wcs.Cd.SelectMany(row => row));
        if (wcs.Sip is { } sip)
        {
            if (sip.Order is < 2 or > 9)
            {
                throw new InvalidDataException("The plate solution contains an unsupported SIP order.");
            }

            values = values
                .Concat(sip.A)
                .Concat(sip.B)
                .Concat(sip.Ap)
                .Concat(sip.Bp);
        }

        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException("The plate solution contains a non-finite WCS value.");
        }
    }
}
