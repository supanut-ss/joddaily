using Microsoft.EntityFrameworkCore;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using JodDaily.Services;

namespace JodDaily.Tests;

public class SlipLocalTests : ChatTestBase
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    /// <summary>A slip QR payload: 00{ 00 version, 01 bank, 02 reference } 51 country, 91 CRC.</summary>
    private static string Payload(string bank, string reference)
    {
        var inner = "0006000001" + "01" + bank.Length.ToString("00") + bank + "02" + reference.Length.ToString("00") + reference;
        var body = "00" + inner.Length.ToString("00") + inner + "5102TH" + "9104";
        return body + SlipQr.Crc16(body);
    }

    [Fact]
    public void A_slip_qr_gives_the_bank_the_reference_and_the_day_in_it()
    {
        var qr = SlipQr.Parse(Payload("004", "202610200123456789AB"), Today);
        Assert.Equal(("004", "202610200123456789AB", new DateOnly(2026, 10, 20)), (qr!.Bank, qr.Ref, qr.Date));
    }

    [Fact]
    public void A_reference_that_does_not_start_with_a_plausible_day_has_no_date()
    {
        Assert.Null(SlipQr.Parse(Payload("014", "0123456789ABCDEFGH"), Today)!.Date);
    }

    [Fact]
    public void A_qr_with_a_wrong_crc_or_another_structure_is_not_a_slip()
    {
        var p = Payload("004", "202610200123456789AB");
        Assert.Null(SlipQr.Parse(p[..^4] + "0000", Today));
        Assert.Null(SlipQr.Parse("https://example.com", Today));
    }

    [Fact]
    public void The_qr_is_found_in_an_image()
    {
        var payload = Payload("004", "202610200123456789AB");
        var writer = new ZXing.BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = new EncodingOptions { Width = 300, Height = 300, Margin = 4 } };
        var pixels = writer.Write(payload);

        Assert.Equal("202610200123456789AB", SlipQr.Read(Bmp(pixels.Pixels, pixels.Width, pixels.Height))!.Ref);
    }

    /// <summary>A top-down 32-bit BMP of BGRA pixels.</summary>
    private static byte[] Bmp(byte[] bgra, int w, int h)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + bgra.Length); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(bgra.Length); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        bw.Write(bgra);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("Transfer\nAmount\n1,500.00 THB\nFee 0.00", 150000L)]
    [InlineData("Amount 65.50\nFee 0.00", 6550L)]
    [InlineData("Balance 20,000.00\nTransferred 450.00\nFee 0.00", 2000000L)]   // no label: the largest figure
    [InlineData("Fee 0.00", null)]
    [InlineData("no numbers", null)]
    public void The_amount_is_the_labelled_figure_else_the_largest_and_never_zero(string text, long? satang) =>
        Assert.Equal(satang, SlipOcr.PickAmount(text));

    [Fact]
    public async Task The_same_slip_is_not_recorded_twice()
    {
        var slip = new SlipData(150000, null, null, "REF1");
        await Chat.SaveSlipAsync(Me, slip);
        var second = Json(await Chat.SaveSlipAsync(Me, slip));

        Assert.Equal(1, await Db.LedgerEntries.CountAsync());
        Assert.Contains("บันทึกไปแล้ว", second);
    }
}
