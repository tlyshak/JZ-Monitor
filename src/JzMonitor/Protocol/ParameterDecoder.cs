using System.Globalization;

namespace JzMonitor.Protocol;

public sealed record ParamValue(string Key, string Name, double? Value, string Text, string Unit, string Raw, bool HiddenInOriginal = false);

/// <summary>
/// Перетворення байтів кадру у фізичні величини. Формули взято з дизасемблювання CarTester (функція MemoDraw).
/// Дані правого ряду V6 (байт 10, біт 12.5) не декодуються — цільовий двигун рядний (2JZ-GTE).
/// </summary>
public static class ParameterDecoder
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    /// <summary>Нормальний атмосферний тиск, кПа (нуль шкали наддуву).</summary>
    public const double StdAtmKpa = 101.325;

    public sealed record FlagDef(int Byte, int Bit, string Name, string OnText, string OffText, bool Hidden);

    /// <summary>Біти байтів 11 і 12. Hidden = оригінал їх не показував.</summary>
    public static readonly FlagDef[] Flags =
    [
        new(11, 0, "Post-start enrichment", "ON", "OFF", false),
        new(11, 1, "Warm-up enrichment", "ON", "OFF", false),
        new(11, 2, "Bit 11.2 (unknown)", "1", "0", true),
        new(11, 3, "Bit 11.3 (unknown)", "1", "0", true),
        new(11, 4, "Knock", "YES", "NO", false),
        new(11, 5, "Closed loop (O2 feedback)", "ON", "OFF", false),
        new(11, 6, "Extra enrichment", "ON", "OFF", false),
        new(11, 7, "Bit 11.7 (unknown)", "1", "0", true),
        new(12, 0, "Starter", "ON", "OFF", false),
        new(12, 1, "Idle switch (IDL)", "CLOSED", "OPEN", false),
        new(12, 2, "A/C", "ON", "OFF", false),
        new(12, 3, "Park / Neutral", "ON", "OFF", false),
        new(12, 4, "O2 sensor", "RICH", "LEAN", false),
        new(12, 6, "Bit 12.6 (unknown)", "1", "0", true),
        new(12, 7, "TE1 → E1", "YES", "NO", false),
    ];

    public static List<ParamValue> Decode(ObdFrame f, DecoderSettings s)
    {
        var list = new List<ParamValue>(32);

        Add("inj", "Injection time", f.B(1), b => b * 0.125, "ms", "0.00");
        Add("ign", "Ignition advance", f.B(2), b => b * 0.47 - 30, "°", "0.0");
        switch (s.Iac)
        {
            case IacMode.DutyPercent: Add("iac", "ISCV", f.B(3), b => b / 255.0 * 100, "%", "0.0"); break;
            case IacMode.Step: Add("iac", "ISCV", f.B(3), b => b, "steps", "0"); break;
            default: Add("iac", "ISCV", f.B(3), b => b, "raw", "0"); break;
        }
        // Оберти передаються одним байтом ×25 — крок 25 об/хв задає сам протокол
        Add("rpm", "RPM", f.B(4), b => b * 25, "rpm", "0");
        switch (s.Air)
        {
            case AirSensor.MapKpa: Add("air", "MAP", f.B(5), b => b * 0.6515, "kPa", "0.0"); break;
            case AirSensor.MapMmHg: Add("air", "MAP", f.B(5), b => b * 4.886, "mmHg", "0"); break;
            case AirSensor.MapTurboKpa: Add("air", "MAP", f.B(5), b => b * 0.97, "kPa", "0.0"); break;
            case AirSensor.MapTurboMmHg: Add("air", "MAP", f.B(5), b => b * 7.732, "mmHg", "0"); break;
            // Наддув відносно атмосфери: формула MAP-T (×0.97 кПа) мінус 101.3 кПа, у барах
            case AirSensor.MapTurboBoostBar: Add("air", "MAP", f.B(5), b => (b * 0.97 - StdAtmKpa) / 100, "bar", "0.00"); break;
            case AirSensor.MafGs: Add("air", "MAF", f.B(5), b => b * 2.0, "g/s", "0.0"); break;
            case AirSensor.VafVolt: Add("air", "VAF", f.B(5), b => b / 255.0 * 5, "V", "0.00"); break;
            default: Add("air", "Air sensor", f.B(5), b => b, "raw", "0"); break;
        }
        Add("ect", "Coolant temp" + (s.CoolantDirect ? "" : " (inv.)"), f.B(6), b => Coolant(b, s.CoolantDirect), "°C", "0.0");
        switch (s.Throttle)
        {
            case ThrottleMode.Degrees: Add("tps", "Throttle", f.B(7), b => b / 2.0, "°", "0.0"); break;
            case ThrottleMode.Percent: Add("tps", "Throttle", f.B(7), b => b / 1.8, "%", "0.0"); break;
            default: Add("tps", "Throttle", f.B(7), b => b, "raw", "0"); break;
        }
        Add("spd", "Vehicle speed", f.B(8), b => b, "km/h", "0");
        switch (s.Af)
        {
            case AfMode.Volts: Add("af9", "Target A/F", f.B(9), b => b * 5 / 256.0, "V", "0.00"); break;
            case AfMode.Percent: Add("af9", "Target A/F", f.B(9), b => b * 20 / 128.0 - 20, "%", "0.0"); break;
            default: Add("af9", "Target A/F", f.B(9), b => b, "raw", "0"); break;
        }

        foreach (var fl in Flags)
        {
            if (f.B(fl.Byte) is not byte raw) continue;
            bool on = ((raw >> fl.Bit) & 1) != 0;
            list.Add(new ParamValue($"f{fl.Byte}.{fl.Bit}", fl.Name, on ? 1 : 0,
                on ? fl.OnText : fl.OffText, "", on ? "1" : "0", fl.Hidden));
        }

        // Витрата палива (оцінка, як в оригіналі; без затримки відкриття форсунок):
        //   л/год = t_впорск(мс) × об/хв × cc/min × к-сть × впорсків_за_2_оберти × 5e-7
        //   = B1 × B4 × cc/min × к-сть × впорсків × 1.5625e-6   (B1×0.125 мс, B4×25 об/хв)
        // Збігається з CarTester (B1 × B4 × PrFors × KolFors × ChastFors × 6.25e-6) при PrFors = cc/min ÷ 4.
        if (f.B(1) is byte inj && f.B(4) is byte rpm)
        {
            double lh = inj * rpm * (double)s.InjectorFlowCcMin * s.InjectorCount * s.InjectionRate * 1.5625e-6;
            list.Add(new ParamValue("lph", "Fuel flow", lh, lh.ToString("0.00", Ci), "L/h", ""));
            if (f.B(8) is byte spd && spd > 0)
            {
                double l100 = lh * 100 / spd;
                list.Add(new ParamValue("l100km", "Fuel economy", l100, l100.ToString("0.0", Ci), "L/100km", ""));
            }
            else list.Add(new ParamValue("l100km", "Fuel economy", null, "—", "L/100km", ""));
        }

        // Байти, яких оригінал не обробляв зовсім
        for (int n = 13; n <= f.Bytes.Length; n++)
        {
            byte b = f.Bytes[n - 1];
            list.Add(new ParamValue("b" + n, $"Byte {n} (dropped by CarTester)", b, b.ToString(Ci), "raw",
                $"{b} · 0x{b:X2} · {Convert.ToString(b, 2).PadLeft(8, '0')}", true));
        }
        return list;

        void Add(string key, string name, byte? raw, Func<int, double> conv, string unit, string fmt)
        {
            if (raw is not byte b) return;
            double v = conv(b);
            list.Add(new ParamValue(key, name, v, v.ToString(fmt, Ci), unit, $"{b} · 0x{b:X2}"));
        }
    }

    /// <summary>Кусково-лінійна характеристика датчика температури ОР (10 відрізків, як в оригіналі).</summary>
    public static double Coolant(int raw, bool direct)
    {
        int t = direct ? raw : 255 - raw;
        return t switch
        {
            < 15 => (t - 5) * 2 - 60,
            < 39 => (t - 15) * 0.83 - 40,
            < 82 => (t - 39) * 0.47 - 20,
            < 135 => (t - 82) * 0.38,
            < 180 => (t - 135) * 0.44 + 20,
            < 210 => (t - 180) * 0.67 + 40,
            < 228 => (t - 210) * 1.11 + 60,
            < 237 => (t - 228) * 2.11 + 80,
            < 243 => (t - 237) * 3.83 + 99,
            _ => (t - 243) * 9.8 + 122,
        };
    }
}
