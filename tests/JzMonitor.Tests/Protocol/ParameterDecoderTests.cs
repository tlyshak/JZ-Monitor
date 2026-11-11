using JzMonitor.Protocol;

namespace JzMonitor.Tests.Protocol;

/// <summary>
/// Еталонні точки: формули з дизасемблювання CarTester (docs/PROTOCOL.md), перевірені
/// на скріншотах CarTester для 2JZ-GTE (кут, ОР, оберти, впорскування, MAP).
/// </summary>
public class ParameterDecoderTests
{
    private static Dictionary<string, ParamValue> Decode(byte[] bytes, DecoderSettings? s = null) =>
        ParameterDecoder.Decode(new ObdFrame { Bytes = bytes }, s ?? new DecoderSettings()).ToDictionary(p => p.Key);

    [Fact]
    public void Decode_VerifiedScreenshotPoints_MatchCarTester()
    {
        var d = Decode([50, 85, 88, 57, 100, 226, 0, 0, 128, 0, 0x21, 0x8A]);

        Assert.Equal("6.250", d["inj"].Text);
        Assert.Equal("10.0", d["ign"].Text);
        Assert.Equal("88", d["iac"].Text);
        Assert.Equal("1425", d["rpm"].Text);
        Assert.Equal("97.0", d["air"].Text);
        Assert.Equal("77.8", d["ect"].Text);
    }

    [Theory]
    [InlineData(113, 11.8)]
    [InlineData(226, 77.8)]
    [InlineData(14, -42.0)]
    [InlineData(15, -40.0)]
    [InlineData(82, 0.0)]
    [InlineData(243, 122.0)]
    public void Coolant_DirectScale_FollowsPiecewiseTable(int raw, double expected)
    {
        Assert.Equal(expected, ParameterDecoder.Coolant(raw, direct: true), 1);
    }

    [Fact]
    public void Coolant_InverseScale_Uses255MinusRaw()
    {
        Assert.Equal(ParameterDecoder.Coolant(255 - 113, direct: true), ParameterDecoder.Coolant(113, direct: false));
    }

    [Fact]
    public void Decode_FlagBits_AllSixteenPresent_HiddenOnesMarked()
    {
        var d = Decode(new byte[12]);

        var flags = d.Values.Where(p => p.Key.StartsWith('f')).ToList();
        Assert.Equal(16, flags.Count);
        Assert.Equal(["f11.2", "f11.3", "f11.7", "f12.6"], flags.Where(f => f.HiddenInOriginal).Select(f => f.Key));
    }

    [Fact]
    public void Decode_ExtraBytes_ReportedAsHidden()
    {
        var d = Decode(Enumerable.Range(0, 14).Select(i => (byte)i).ToArray());

        Assert.True(d["b13"].HiddenInOriginal);
        Assert.Equal(13.0, d["b14"].Value);
    }

    [Fact]
    public void Decode_FuelFlow_ZeroSpeed_NoPer100km()
    {
        var d = Decode([24, 85, 40, 30, 35, 200, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(24 * 30 * 55 * 6 * 1 * 6.25e-6, d["lph"].Value!.Value, 6);
        Assert.Null(d["l100km"].Value);
    }
}
