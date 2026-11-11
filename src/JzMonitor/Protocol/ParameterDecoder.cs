using System.Globalization;

namespace JzMonitor.Protocol;

public sealed record ParamValue(string Key, string Name, double? Value, string Text, string Unit, string Raw, bool HiddenInOriginal = false);

/// <summary>
/// Перетворення байтів кадру у фізичні величини. Формули взято з дизасемблювання CarTester (функція MemoDraw).
/// </summary>
public static class ParameterDecoder
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public sealed record FlagDef(int Byte, int Bit, string Name, string OnText, string OffText, bool Hidden);

    /// <summary>Біти байтів 11 і 12. Hidden = оригінал їх не показував (але зберігав у файл).</summary>
    public static readonly FlagDef[] Flags =
    [
        new(11, 0, "Збагачення після пуску", "Так", "Ні", false),
        new(11, 1, "Збагачення на прогріві", "Так", "Ні", false),
        new(11, 2, "Біт 11.2 (невідомо)", "1", "0", true),
        new(11, 3, "Біт 11.3 (невідомо)", "1", "0", true),
        new(11, 4, "Датчик детонації", "Так", "Ні", false),
        new(11, 5, "Зворотний зв'язок (λ-регулювання)", "Увімк", "Вимк", false),
        new(11, 6, "Додаткове збагачення", "Увімк", "Вимк", false),
        new(11, 7, "Біт 11.7 (невідомо)", "1", "0", true),
        new(12, 0, "Стартер", "Увімк", "Вимк", false),
        new(12, 1, "Концевик ХХ (IDL)", "Закрито", "Відкрито", false),
        new(12, 2, "Кондиціонер", "Увімк", "Вимк", false),
        new(12, 3, "Park/Neutral", "Увімк", "Вимк", false),
        new(12, 4, "Лямбда-зонд (лівий)", "Багата", "Бідна", false),
        new(12, 5, "Лямбда-зонд (правий, V6)", "Багата", "Бідна", false),
        new(12, 6, "Біт 12.6 (невідомо)", "1", "0", true),
        new(12, 7, "TE1→E1", "Так", "Ні", false),
    ];

    public static List<ParamValue> Decode(ObdFrame f, DecoderSettings s)
    {
        var list = new List<ParamValue>(40);

        Add("inj", "1. Час впорскування", f.B(1), b => b * 0.125, "мс", "0.000");
        Add("ign", "2. Кут випередження", f.B(2), b => b * 0.47 - 30, "°", "0.0");
        switch (s.Iac)
        {
            case IacMode.DutyPercent: Add("iac", "3. Клапан ХХ (IAC)", f.B(3), b => b / 255.0 * 100, "%", "0.0"); break;
            case IacMode.Step: Add("iac", "3. Клапан ХХ (IAC)", f.B(3), b => b, "крок", "0"); break;
            default: Add("iac", "3. Клапан ХХ (IAC)", f.B(3), b => b, "", "0"); break;
        }
        Add("rpm", "4. Оберти двигуна", f.B(4), b => b * 25, "об/хв", "0");
        switch (s.Air)
        {
            case AirSensor.MapKpa: Add("air", "5. MAP", f.B(5), b => b * 0.6515, "кПа", "0.0"); break;
            case AirSensor.MapMmHg: Add("air", "5. MAP", f.B(5), b => b * 4.886, "мм рт.ст.", "0"); break;
            case AirSensor.MapTurboKpa: Add("air", "5. MAP (турбо)", f.B(5), b => b * 0.97, "кПа", "0.0"); break;
            case AirSensor.MapTurboMmHg: Add("air", "5. MAP (турбо)", f.B(5), b => b * 7.732, "мм рт.ст.", "0"); break;
            case AirSensor.MafGs: Add("air", "5. MAF", f.B(5), b => b * 2.0, "г/с", "0.0"); break;
            case AirSensor.VafVolt: Add("air", "5. VAF", f.B(5), b => b / 255.0 * 5, "В", "0.00"); break;
            default: Add("air", "5. Датчик повітря", f.B(5), b => b, "", "0"); break;
        }
        Add("ect", "6. Температура ОР" + (s.CoolantDirect ? "" : " (інв.)"), f.B(6), b => Coolant(b, s.CoolantDirect), "°C", "0.0");
        switch (s.Throttle)
        {
            case ThrottleMode.Degrees: Add("tps", "7. Дросель/педаль", f.B(7), b => b / 2.0, "°", "0.0"); break;
            case ThrottleMode.Percent: Add("tps", "7. Дросель/педаль", f.B(7), b => b / 1.8, "%", "0.0"); break;
            default: Add("tps", "7. Дросель/педаль", f.B(7), b => b, "", "0"); break;
        }
        Add("spd", "8. Швидкість", f.B(8), b => b, "км/год", "0");
        for (int n = 9; n <= 10; n++)
        {
            string name = n == 9 ? "9. Корекція/λ (лівий)" : "10. Корекція/λ (правий, V6)";
            switch (s.Af)
            {
                case AfMode.Volts: Add("af" + n, name, f.B(n), b => b * 5 / 256.0, "В", "0.00"); break;
                case AfMode.Percent: Add("af" + n, name, f.B(n), b => b * 20 / 128.0 - 20, "%", "0.0"); break;
                default: Add("af" + n, name, f.B(n), b => b, "", "0"); break;
            }
        }

        foreach (var fl in Flags)
        {
            if (f.B(fl.Byte) is not byte raw) continue;
            bool on = ((raw >> fl.Bit) & 1) != 0;
            list.Add(new ParamValue($"f{fl.Byte}.{fl.Bit}", $"{fl.Byte}.{fl.Bit}  {fl.Name}", on ? 1 : 0,
                on ? fl.OnText : fl.OffText, "", on ? "1" : "0", fl.Hidden));
        }

        // Витрата палива: B1 × B4 × PrFors × KolFors × ChastFors × 6.25e-6 (як в оригіналі)
        if (f.B(1) is byte inj && f.B(4) is byte rpm)
        {
            double lh = inj * rpm * (double)s.InjectorFlow * s.InjectorCount * s.InjectionRate * 6.25e-6;
            list.Add(new ParamValue("lph", "Витрата палива (прибл.)", lh, lh.ToString("0.00", Ci), "л/год", ""));
            if (f.B(8) is byte spd && spd > 0)
            {
                double l100 = lh * 100 / spd;
                list.Add(new ParamValue("l100km", "Витрата палива (прибл.)", l100, l100.ToString("0.0", Ci), "л/100км", ""));
            }
            else list.Add(new ParamValue("l100km", "Витрата палива (прибл.)", null, "—", "л/100км", ""));
        }

        // Байти, яких оригінал не обробляв зовсім
        for (int n = 13; n <= f.Bytes.Length; n++)
        {
            byte b = f.Bytes[n - 1];
            list.Add(new ParamValue("b" + n, $"{n}. Додатковий байт (оригінал відкидав)", b, b.ToString(Ci), "",
                $"{b} / 0x{b:X2} / {Convert.ToString(b, 2).PadLeft(8, '0')}", true));
        }
        return list;

        void Add(string key, string name, byte? raw, Func<int, double> conv, string unit, string fmt)
        {
            if (raw is not byte b) return;
            double v = conv(b);
            list.Add(new ParamValue(key, name, v, v.ToString(fmt, Ci), unit, $"{b} / 0x{b:X2}"));
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
