using System.Globalization;
using System.IO;
using JzMonitor.Protocol;

namespace JzMonitor.Recording;

/// <summary>
/// Службові поля підвалу CarTester, які JZ Monitor не використовує, але зберігає,
/// щоб відкритий .ctr після експорту лишався побайтово ідентичним.
/// </summary>
public sealed class CtrExtras
{
    /// <summary>Три списки «еталонних» кадрів (кнопки №1–№3 в оригіналі), по 25 рядків.</summary>
    public string[][] References { get; } = [new string[25], new string[25], new string[25]];
    /// <summary>10 запам'ятованих номерів кадрів (кнопки BitBtn1..10), "0" — порожньо.</summary>
    public string[] Bookmarks { get; } = Enumerable.Repeat("0", 10).ToArray();
    /// <summary>Друге Memo оригіналу.</summary>
    public List<string> Comments2 { get; } = new();
}

/// <summary>
/// Формат CarTester 1.0.0.3 (.ctr) — основний формат записів JZ Monitor (сумісний з оригінальною програмою).
///   "File for CarTester 1.0.0.3"
///   кількість кадрів
///   на кожен кадр 40 рядків + час:
///      0..9   — байти 1..10
///      10     — службове значення (в оригіналі зсув на 1: останній біт ID попереднього кадру)
///      11..22 — 12 біт ID
///      23..30 — біти 0..7 байта 11
///      31..38 — біти 0..7 байта 12
///      39     — мітка кадру
///      час H:mm:ss
///   75 рядків — три списки еталонних кадрів по 25
///   10 рядків — запам'ятовані номери кадрів
///   (Count−1) і Count рядків першого Memo; те саме для другого Memo
///   9 полів опису автомобіля
/// cp1251, CRLF. Байти 13+ і мілісекунди в цей формат не вміщуються (їх зберігає експорт CSV).
/// </summary>
public static class CtrFormat
{
    public const string Header = "File for CarTester 1.0.0.3";
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static void Save(string path, LogRecording rec)
    {
        var extras = rec.Ctr ?? new CtrExtras();
        var frames = rec.Frames.Where(f => f.Valid).ToList();
        using var w = new StreamWriter(path, false, RecordingIO.Cp1251) { NewLine = "\r\n" };
        w.WriteLine(Header);
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
            foreach (var list in extras.References)
                w.WriteLine(list[i] ?? "");
        foreach (var b in extras.Bookmarks) w.WriteLine(string.IsNullOrEmpty(b) ? "0" : b);
        WriteMemo(rec.Comments);
        WriteMemo(extras.Comments2);
        foreach (var v in rec.Vehicle.Values) w.WriteLine(v ?? "");

        void WriteMemo(List<string> lines)
        {
            w.WriteLine((lines.Count - 1).ToString(Ci));
            foreach (var l in lines) w.WriteLine(l);
        }
    }

    public static LogRecording Load(string path)
    {
        var lines = File.ReadAllLines(path, RecordingIO.Cp1251);
        if (lines.Length < 2 || !lines[0].Trim().StartsWith("File for CarTester"))
            throw new InvalidDataException("Not a CarTester file (expected \"File for CarTester ...\").");
        int.TryParse(lines[1].Trim(), out int declared);
        var rec = new LogRecording { Source = path, Ctr = new CtrExtras() };
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
                rec.Frames.Add(FromValues(vals, date + ts));
                vals.Clear();
            }
            else if (int.TryParse(l, out int v)) vals.Add(v);
        }

        try
        {
            ReadFooter(lines, pos, rec, rec.Ctr);
        }
        catch
        {
            // Старіші версії файлу можуть мати інший підвал — кадри все одно прочитані.
        }
        return rec;
    }

    private static void ReadFooter(string[] lines, int pos, LogRecording rec, CtrExtras extras)
    {
        string Next() => pos < lines.Length ? lines[pos++].TrimEnd() : throw new EndOfStreamException();
        for (int i = 0; i < 25; i++)
            for (int k = 0; k < 3; k++)
                extras.References[k][i] = Next();
        for (int i = 0; i < 10; i++) extras.Bookmarks[i] = Next();
        ReadMemo(rec.Comments);
        ReadMemo(extras.Comments2);
        for (int i = 0; i < 9 && pos < lines.Length; i++) rec.Vehicle.Values[i] = Next();

        void ReadMemo(List<string> into)
        {
            if (!int.TryParse(Next().Trim(), out int cnt)) throw new InvalidDataException();
            for (int i = 0; i <= cnt; i++) into.Add(Next());
        }
    }

    private static ObdFrame FromValues(List<int> v, DateTime time)
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
}
