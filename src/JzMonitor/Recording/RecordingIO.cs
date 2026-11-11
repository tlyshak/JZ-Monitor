using System.Globalization;
using System.IO;
using System.Text;
using JzMonitor.Protocol;

namespace JzMonitor.Recording;

/// <summary>Опис автомобіля — 9 полів у кінці .ctr (порядок як в оригіналі).</summary>
public sealed class VehicleInfo
{
    public static readonly string[] Labels = ["Марка", "Двигун", "КПП", "Рік", "Пробіг", "Тип двигуна", "Циліндри", "Тип КПП", "Привід"];
    public string[] Values { get; } = new string[9];

    public VehicleInfo() { for (int i = 0; i < 9; i++) Values[i] = ""; }

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

public sealed class CtrRecording
{
    public List<ObdFrame> Frames { get; } = new();
    public VehicleInfo Vehicle { get; set; } = new();
    /// <summary>Коментарі (перше Memo оригіналу).</summary>
    public List<string> Comments { get; } = new();
    /// <summary>Друге Memo оригіналу.</summary>
    public List<string> Comments2 { get; } = new();
    /// <summary>Три списки «еталонних» кадрів (кнопки №1–№3 в оригіналі), по 25 рядків.</summary>
    public string[][] References { get; } = [new string[25], new string[25], new string[25]];
    /// <summary>10 запам'ятованих номерів кадрів (кнопки BitBtn1..10), "0" — порожньо.</summary>
    public string[] Bookmarks { get; } = Enumerable.Repeat("0", 10).ToArray();
    public string? Source { get; set; }
}

public static class RecordingIO
{
    public const string CtrHeader = "File for CarTester 1.0.0.3";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    static RecordingIO() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Cp1251
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
    }

    /*  Формат .ctr (CarTester 1.0.0.3), cp1251, CRLF:
     *    "File for CarTester 1.0.0.3"
     *    кількість кадрів
     *    на кожен кадр 40 рядків + час:
     *       0..9   — байти 1..10
     *       10     — службове значення (в оригіналі зсув на 1: останній біт ID попереднього кадру)
     *       11..22 — 12 біт ID
     *       23..30 — біти 0..7 байта 11
     *       31..38 — біти 0..7 байта 12
     *       39     — мітка кадру
     *       час H:mm:ss
     *    75 рядків — три списки еталонних кадрів по 25
     *    10 рядків — запам'ятовані номери кадрів
     *    (Count−1) і Count рядків першого Memo; те саме для другого Memo
     *    9 полів опису автомобіля
     *  Байти 13+ у цей формат не вміщуються (їх зберігає експорт CSV).
     */

    public static void SaveCtr(string path, CtrRecording rec)
    {
        var frames = rec.Frames.Where(f => f.Valid).ToList();
        using var w = new StreamWriter(path, false, Cp1251) { NewLine = "\r\n" };
        w.WriteLine(CtrHeader);
        w.WriteLine(frames.Count.ToString(Ci));
        int prevLastIdBit = 0;
        foreach (var f in frames)
        {
            for (int i = 1; i <= 10; i++) w.WriteLine((f.B(i) ?? 0).ToString(Ci));
            w.WriteLine(prevLastIdBit.ToString(Ci));
            for (int i = 0; i < 12; i++) w.WriteLine(i < f.IdBits.Length && f.IdBits[i] != 0 ? "1" : "0");
            for (int b = 0; b < 8; b++) w.WriteLine(f.Bit(11, b) ? "1" : "0");
            for (int b = 0; b < 8; b++) w.WriteLine(f.Bit(12, b) ? "1" : "0");
            w.WriteLine(f.Marked ? "1" : "0");
            w.WriteLine(f.Time.ToString("H:mm:ss", Ci));
            prevLastIdBit = f.IdBits.Length >= 12 && f.IdBits[11] != 0 ? 1 : 0;
        }
        for (int i = 0; i < 25; i++)
            foreach (var list in rec.References)
                w.WriteLine(list[i] ?? "");
        foreach (var b in rec.Bookmarks) w.WriteLine(string.IsNullOrEmpty(b) ? "0" : b);
        WriteMemo(rec.Comments);
        WriteMemo(rec.Comments2);
        foreach (var v in rec.Vehicle.Values) w.WriteLine(v ?? "");

        void WriteMemo(List<string> lines)
        {
            w.WriteLine((lines.Count - 1).ToString(Ci));
            foreach (var l in lines) w.WriteLine(l);
        }
    }

    public static CtrRecording LoadCtr(string path)
    {
        var lines = File.ReadAllLines(path, Cp1251);
        if (lines.Length < 2 || !lines[0].Trim().StartsWith("File for CarTester"))
            throw new InvalidDataException("Невідомий формат файлу (очікується «File for CarTester ...»)");
        int.TryParse(lines[1].Trim(), out int declared);
        var rec = new CtrRecording { Source = path };
        var date = File.GetLastWriteTime(path).Date;
        var vals = new List<int>(40);
        int pos = 2;
        TimeSpan prev = TimeSpan.Zero;
        while (pos < lines.Length && (declared <= 0 || rec.Frames.Count < declared))
        {
            var l = lines[pos++].Trim();
            if (l.Length == 0) { if (declared <= 0 && vals.Count == 0) break; continue; }
            if (l.Count(c => c == ':') == 2 && TimeSpan.TryParse(l, Ci, out var ts))
            {
                if (ts < prev) date = date.AddDays(1);
                prev = ts;
                rec.Frames.Add(FromCtrValues(vals, date + ts));
                vals.Clear();
            }
            else if (int.TryParse(l, out int v)) vals.Add(v);
        }

        try
        {
            ReadFooter(lines, pos, rec);
        }
        catch
        {
            // Старіші версії файлу можуть мати інший підвал — кадри все одно прочитані.
        }
        return rec;
    }

    private static void ReadFooter(string[] lines, int pos, CtrRecording rec)
    {
        string Next() => pos < lines.Length ? lines[pos++].TrimEnd() : throw new EndOfStreamException();
        for (int i = 0; i < 25; i++)
            for (int k = 0; k < 3; k++)
                rec.References[k][i] = Next();
        for (int i = 0; i < 10; i++) rec.Bookmarks[i] = Next();
        ReadMemo(rec.Comments);
        ReadMemo(rec.Comments2);
        for (int i = 0; i < 9 && pos < lines.Length; i++) rec.Vehicle.Values[i] = Next();

        void ReadMemo(List<string> into)
        {
            if (!int.TryParse(Next().Trim(), out int cnt)) throw new InvalidDataException();
            for (int i = 0; i <= cnt; i++) into.Add(Next());
        }
    }

    private static ObdFrame FromCtrValues(List<int> v, DateTime time)
    {
        int Get(int i) => i < v.Count ? v[i] : 0;
        var bytes = new byte[12];
        for (int i = 0; i < 10; i++) bytes[i] = (byte)Get(i);
        for (int b = 0; b < 8; b++)
        {
            if (Get(23 + b) != 0) bytes[10] |= (byte)(1 << b);
            if (Get(31 + b) != 0) bytes[11] |= (byte)(1 << b);
        }
        var id = new byte[12];
        for (int i = 0; i < 12; i++) id[i] = (byte)(Get(11 + i) != 0 ? 1 : 0);
        return new ObdFrame { Time = time, Bytes = bytes, IdBits = id, Marked = Get(39) != 0 };
    }

    /// <summary>Експорт у CSV для Excel: усі байти (включно з 13+), ID, біти та розраховані значення.</summary>
    public static void SaveCsv(string path, IReadOnlyList<ObdFrame> frames, DecoderSettings s)
    {
        var valid = frames.Where(f => f.Valid).ToList();
        int maxBytes = valid.Count == 0 ? 12 : valid.Max(f => f.Bytes.Length);
        var first = valid.FirstOrDefault();
        var paramKeys = first == null
            ? new List<ParamValue>()
            : ParameterDecoder.Decode(first, s).Where(p => !p.Key.StartsWith('f') && !p.Key.StartsWith('b')).ToList();

        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        var head = new List<string> { "Кадр", "Час", "ID" };
        for (int i = 1; i <= maxBytes; i++) head.Add("B" + i);
        foreach (var fl in ParameterDecoder.Flags) head.Add($"{fl.Byte}.{fl.Bit}");
        head.AddRange(paramKeys.Select(p => $"{p.Name}, {p.Unit}"));
        head.Add("Мітка");
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
