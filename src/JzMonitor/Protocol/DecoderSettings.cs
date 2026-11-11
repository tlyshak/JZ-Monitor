using System.IO;
using System.Text;
using System.Text.Json;

namespace JzMonitor.Protocol;

/// <summary>Тип датчика повітря (CarTester.ini: Vozduh).</summary>
public enum AirSensor { Raw = 0, MapKpa = 1, MapMmHg = 2, MapTurboKpa = 3, MapTurboMmHg = 4, MafGs = 5, VafVolt = 6 }
/// <summary>Клапан ХХ (KHH).</summary>
public enum IacMode { Raw = 0, DutyPercent = 1, Step = 2 }
/// <summary>Дросель (DZ).</summary>
public enum ThrottleMode { Raw = 0, Degrees = 1, Percent = 2 }
/// <summary>Байти 9/10 (Korr).</summary>
public enum AfMode { Raw = 0, Volts = 1, Percent = 2 }

public sealed class DecoderSettings
{
    public AirSensor Air { get; set; } = AirSensor.MapTurboKpa;
    /// <summary>true — пряма шкала температури ОР (Temperature=1), false — інверсна (255−x).</summary>
    public bool CoolantDirect { get; set; } = true;
    public IacMode Iac { get; set; } = IacMode.Step;
    public ThrottleMode Throttle { get; set; } = ThrottleMode.Percent;
    public AfMode Af { get; set; } = AfMode.Volts;

    /// <summary>Продуктивність форсунки (PrFors).</summary>
    public int InjectorFlow { get; set; } = 55;
    /// <summary>Кількість форсунок (KolFors).</summary>
    public int InjectorCount { get; set; } = 6;
    /// <summary>Частота впорскування (ChastFors).</summary>
    public int InjectionRate { get; set; } = 1;

    // Параметри лінії / протоколу
    public string? Port { get; set; }
    public InputLine Line { get; set; } = InputLine.Dsr;
    public bool Invert { get; set; }
    public bool Dtr { get; set; }
    public bool Rts { get; set; }
    public double BitTimeMs { get; set; } = 8.2;
    public int GapBits { get; set; } = 16;
    public int MinDataBytes { get; set; } = 12;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "JzMonitor.settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static DecoderSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<DecoderSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); } catch { }
    }

    /// <summary>Імпорт секції [Param] з CarTester.ini.</summary>
    public void ImportCarTesterIni(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string section = "";
        foreach (var raw in File.ReadAllLines(path, Encoding.GetEncoding(1251)))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            if (section != "Param") continue;
            int eq = line.IndexOf('=');
            if (eq < 0 || !int.TryParse(line[(eq + 1)..], out int v)) continue;
            switch (line[..eq])
            {
                case "Vozduh": if (v is >= 0 and <= 6) Air = (AirSensor)v; break;
                case "Temperature": CoolantDirect = v == 1; break;
                case "KHH": if (v is >= 0 and <= 2) Iac = (IacMode)v; break;
                case "DZ": if (v is >= 0 and <= 2) Throttle = (ThrottleMode)v; break;
                case "Korr": if (v is >= 0 and <= 2) Af = (AfMode)v; break;
                case "PrFors": InjectorFlow = v; break;
                case "KolFors": InjectorCount = v; break;
                case "ChastFors": InjectionRate = v; break;
                case "DTR": Dtr = v != 0; break;
            }
        }
    }
}
