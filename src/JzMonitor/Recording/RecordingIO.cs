using System.Globalization;
using System.IO;
using System.Text;
using JzMonitor.Protocol;

namespace JzMonitor.Recording;

/// <summary>Опис автомобіля — 9 полів (як у CarTester).</summary>
public sealed class VehicleInfo
{
    public static readonly string[] Keys = ["Make", "Engine", "Transmission", "Year", "Mileage", "EngineType", "Cylinders", "GearboxType", "Drive"];
    public static readonly string[] Labels = ["Make / model", "Engine", "Transmission", "Year", "Mileage", "Engine type", "Cylinders", "Gearbox type", "Drive"];
    public string[] Values { get; } = Enumerable.Repeat("", 9).ToArray();

    /// <summary>Значення за замовчуванням із секції [Koment] CarTester.ini.</summary>
    public static VehicleInfo FromIni(string path)
    {
        var v = new VehicleInfo();
        string[] keys = ["Marka", "DVS", "Transm", "Year", "Mileage", "TypeD", "Cylinders", "TypeT", "Weels"];
        string section = "";
        foreach (var raw in File.ReadAllLines(path, RecordingIO.Cp1251))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            int eq = line.IndexOf('=');
            if (section != "Koment" || eq < 0) continue;
            int idx = Array.IndexOf(keys, line[..eq]);
            if (idx >= 0) v.Values[idx] = line[(eq + 1)..];
        }
        return v;
    }
}

public sealed class LogRecording
{
    public List<ObdFrame> Frames { get; } = new();
    public VehicleInfo Vehicle { get; set; } = new();
    public List<string> Comments { get; } = new();
    public string? Source { get; set; }
    /// <summary>Службові дані підвалу CarTester (зберігаються, щоб .ctr лишався побайтово ідентичним).</summary>
    public CtrExtras? Ctr { get; set; }
}

public static class RecordingIO
{
    public static Encoding Cp1251
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
    }

    /// <summary>Експорт у CSV для Excel: усі байти, ID, біти та розраховані значення.</summary>
    public static void SaveCsv(string path, IReadOnlyList<ObdFrame> frames, DecoderSettings s)
    {
        var valid = frames.Where(f => f.Valid).ToList();
        int maxBytes = valid.Count == 0 ? 12 : valid.Max(f => f.Bytes.Length);
        var first = valid.FirstOrDefault();
        var paramKeys = first == null
            ? new List<ParamValue>()
            : ParameterDecoder.Decode(first, s).Where(p => !p.Key.StartsWith('f') && !p.Key.StartsWith('b')).ToList();

        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        var head = new List<string> { "Frame", "Time", "ID" };
        for (int i = 1; i <= maxBytes; i++) head.Add("B" + i);
        foreach (var fl in ParameterDecoder.Flags) head.Add($"{fl.Byte}.{fl.Bit} {fl.Name}");
        head.AddRange(paramKeys.Select(p => p.Unit.Length > 0 ? $"{p.Name}, {p.Unit}" : p.Name));
        head.Add("Mark");
        w.WriteLine(string.Join(";", head));

        int n = 0;
        foreach (var f in valid)
        {
            n++;
            var row = new List<string> { n.ToString(), f.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"), f.IdBitString };
            for (int i = 1; i <= maxBytes; i++) row.Add(f.B(i)?.ToString() ?? "");
            foreach (var fl in ParameterDecoder.Flags) row.Add(f.B(fl.Byte) is null ? "" : f.Bit(fl.Byte, fl.Bit) ? "1" : "0");
            var vals = ParameterDecoder.Decode(f, s).ToDictionary(p => p.Key);
            foreach (var p in paramKeys)
                row.Add(vals.TryGetValue(p.Key, out var v) && v.Value is double d ? d.ToString("0.###", CultureInfo.CurrentCulture) : "");
            row.Add(f.Marked ? "1" : "0");
            w.WriteLine(string.Join(";", row));
        }
    }
}

/// <summary>Запис сирих відрізків рівня у файл — для аналізу невідомих варіантів протоколу.</summary>
public sealed class EdgeLogger : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();

    public EdgeLogger(string path)
    {
        _w = new StreamWriter(path, false, new UTF8Encoding(false));
        _w.WriteLine("level;duration_ms");
    }

    public void Write(Edge e)
    {
        lock (_lock) _w.WriteLine($"{e.Level};{e.DurationMs.ToString("0.000", CultureInfo.InvariantCulture)}");
    }

    public static IEnumerable<Edge> Read(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(';');
            if (p.Length == 2 && int.TryParse(p[0], out int lvl) &&
                double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                yield return new Edge(lvl, d);
        }
    }

    public void Dispose()
    {
        lock (_lock) _w.Dispose();
    }
}
