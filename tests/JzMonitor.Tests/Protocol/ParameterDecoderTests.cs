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
        var d = Decode([50, 85, 88, 57, 100, 226, 0, 0, 128, 0, 0x21, 0x8A], new DecoderSettings { Air = AirSensor.MapTurboKpa });

        Assert.Equal("6.25", d["inj"].Text);
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
    public void Decode_FlagBits_V6BitDropped_HiddenOnesMarked()
    {
        var d = Decode(new byte[12]);

        var flags = d.Values.Where(p => p.Key.StartsWith('f')).ToList();
        Assert.Equal(15, flags.Count);
        Assert.DoesNotContain(flags, f => f.Key == "f12.5");
        Assert.Equal(["f11.2", "f11.3", "f11.7", "f12.6"], flags.Where(f => f.HiddenInOriginal).Select(f => f.Key));
    }

    [Theory]
    [InlineData(36, "-0.66")]   // холості: розрідження
    [InlineData(104, "-0.00")]  // ≈ атмосфера
    [InlineData(150, "0.44")]   // наддув
    public void Decode_MapBoostBar_RelativeToAtmosphere(byte raw, string expected)
    {
        var s = new DecoderSettings { Air = AirSensor.MapTurboBoostBar };
        var bytes = new byte[12];
        bytes[4] = raw;

        var d = Decode(bytes, s);

        Assert.Equal(expected, d["air"].Text);
        Assert.Equal("bar", d["air"].Unit);
    }

    [Fact]
    public void Decode_V6RightBank_NotDecoded()
    {
        var d = Decode([0, 0, 0, 0, 0, 0, 0, 0, 128, 77, 0, 0]);

        Assert.True(d.ContainsKey("af9"));
        Assert.False(d.ContainsKey("af10"));
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

        // 3.0 мс × 750 об/хв × 440 cc/min × 6 форсунок, послідовне: 3.0×750×440×6×5e-7 = 2.97 л/год
        Assert.Equal(2.97, d["lph"].Value!.Value, 6);
        Assert.Null(d["l100km"].Value);
    }

    [Fact]
    public void Decode_FuelFlow_MatchesCarTesterFormula_WhenFlowIsPrForsTimesFour()
    {
        // CarTester: B1 × B4 × PrFors × KolFors × ChastFors × 6.25e-6, PrFors=55 → 220 cc/min
        var s = new DecoderSettings { InjectorFlowCcMin = 55 * 4, InjectorCount = 6, InjectionRate = 1 };

        var d = Decode([24, 85, 40, 30, 35, 200, 0, 0, 0, 0, 0, 0], s);

        Assert.Equal(24 * 30 * 55 * 6 * 1 * 6.25e-6, d["lph"].Value!.Value, 9);
    }
}
