using System.Globalization;
using JzMonitor.Protocol;

namespace JzMonitor.Recording;

/// <summary>Діапазон кадрів запису, індекси включно (0-based).</summary>
public readonly record struct FrameRange(int Start, int End)
{
    public int Count => End - Start + 1;
    public bool Contains(int index) => index >= Start && index <= End;

    public static FrameRange All(int frameCount) => new(0, Math.Max(0, frameCount - 1));

    /// <summary>Тривалість діапазону за часом кадрів.</summary>
    public TimeSpan Duration(IReadOnlyList<ObdFrame> frames) =>
        frames.Count == 0 ? TimeSpan.Zero : frames[End].Time - frames[Start].Time;
}

public enum RangeMode { Frames, Time }

/// <summary>
/// Розбір діапазону, введеного користувачем: номери кадрів (з 1, як показує програма) або час доби
/// «HH:mm[:ss]». Порожнє поле — від початку / до кінця запису.
/// </summary>
public static class RangeParser
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static bool TryParse(IReadOnlyList<ObdFrame> frames, RangeMode mode, string? from, string? to,
        out FrameRange range, out string error)
    {
        range = default;
        error = "";
        int n = frames.Count;
        if (n == 0) { error = "No frames"; return false; }

        int start, end;
        if (mode == RangeMode.Frames)
        {
            if (!ParseFrame(from, 1, out start) || !ParseFrame(to, n, out end))
            {
                error = "Enter frame numbers, e.g. 120 and 480";
                return false;
            }
            if (start < 1 || end > n) { error = $"Frames are numbered 1…{n}"; return false; }
            start--; end--;
        }
        else
        {
            var first = frames[0].Time;
            DateTime? tFrom = string.IsNullOrWhiteSpace(from) ? first : ToAbsolute(from, first);
            DateTime? tTo = string.IsNullOrWhiteSpace(to) ? frames[n - 1].Time : ToAbsolute(to, first);
            if (tFrom == null || tTo == null) { error = "Enter time as HH:mm or HH:mm:ss"; return false; }
            // «до» включає всю вказану секунду
            var toLimit = string.IsNullOrWhiteSpace(to) ? tTo.Value : tTo.Value.AddSeconds(1);
            start = -1;
            for (int i = 0; i < n; i++) if (frames[i].Time >= tFrom) { start = i; break; }
            end = -1;
            for (int i = n - 1; i >= 0; i--) if (frames[i].Time < toLimit || frames[i].Time == tTo) { end = i; break; }
            if (start < 0 || end < 0)
            {
                error = $"Recording covers {Format(frames, RangeMode.Time, 0)}–{Format(frames, RangeMode.Time, n - 1)}";
                return false;
            }
        }
        if (start > end) { error = "“From” must be before “To”"; return false; }
        range = new FrameRange(start, end);
        return true;

        static bool ParseFrame(string? s, int empty, out int value)
        {
            if (string.IsNullOrWhiteSpace(s)) { value = empty; return true; }
            return int.TryParse(s.Trim(), NumberStyles.Integer, Ci, out value);
        }
    }

    /// <summary>Час доби → абсолютний момент у межах запису (з урахуванням переходу через північ).</summary>
    private static DateTime? ToAbsolute(string text, DateTime first)
    {
        if (!TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"h\:mm\:ss", @"hh\:mm", @"hh\:mm\:ss"], Ci, out var tod))
            return null;
        var t = first.Date + tod;
        // Запис, що почався ввечері, а час вказано після півночі
        if (t < first - TimeSpan.FromHours(12)) t = t.AddDays(1);
        return t;
    }

    /// <summary>Текст для поля діапазону: номер кадру (з 1) або час HH:mm:ss.</summary>
    public static string Format(IReadOnlyList<ObdFrame> frames, RangeMode mode, int index)
    {
        if (frames.Count == 0) return "";
        index = Math.Clamp(index, 0, frames.Count - 1);
        return mode == RangeMode.Frames
            ? (index + 1).ToString(Ci)
            : frames[index].Time.ToString("HH:mm:ss", Ci);
    }
}

/// <summary>
/// Вирівнювання запису за часом від початку діапазону з кроком 1 с: для кожної секунди — індекс
/// останнього кадру, що вже надійшов (утримання значення), або −1 після кінця діапазону.
/// Так два записи з різною частотою кадрів кладуться на спільну вісь для порівняння.
/// </summary>
public static class Timeline
{
    public static int[] BySecond(IReadOnlyList<ObdFrame> frames, FrameRange range, int seconds)
    {
        var map = new int[seconds];
        Array.Fill(map, -1);
        if (frames.Count == 0) return map;
        var t0 = frames[range.Start].Time;
        double last = (frames[range.End].Time - t0).TotalSeconds;
        int i = range.Start;
        for (int s = 0; s < seconds; s++)
        {
            if (s > last + 0.999) break;
            while (i < range.End && (frames[i + 1].Time - t0).TotalSeconds <= s) i++;
            map[s] = i;
        }
        return map;
    }
}
