using JzMonitor.Protocol;

namespace JzMonitor.Recording;

/// <summary>
/// Один показник звіту. Tolerance — допустима різниця з еталоном (у тих самих одиницях); null — не порівнювати.
/// Spans — де саме в записі це сталося (для перевірок).
/// </summary>
public sealed record ReportItem(string Group, string Key, string Name, double? Value, string Unit, string Format,
    double? Tolerance = null, IReadOnlyList<ReportSpan>? Spans = null)
{
    public string Text => Value is double v ? v.ToString(Format, System.Globalization.CultureInfo.InvariantCulture) : "—";
}

/// <summary>
/// Звіт по запису (або його діапазону): прогріті холості, прогрів, перевірки, поїздка, зв'язок.
/// Прогріті холості — оберти 400–999, ОР ≥ 80 °C, дросель закритий (IDL), авто стоїть.
/// </summary>
public static class RecordingReport
{
    public const string WarmIdle = "Warm idle", WarmUp = "Warm-up", Checks = "Checks", Trip = "Trip", Link = "Link";

    public static List<ReportItem> Build(IReadOnlyList<ObdFrame> frames, DecoderSettings s)
    {
        var items = new List<ReportItem>();
        var decoded = frames.Select(f => ParameterDecoder.Decode(f, s).ToDictionary(p => p.Key, p => p.Value)).ToList();
        double? V(int i, string key) => decoded[i].TryGetValue(key, out var v) ? v : null;

        // ── Прогріті холості ──
        var idle = Enumerable.Range(0, frames.Count).Where(i =>
            V(i, "rpm") is >= 400 and < 1000 && V(i, "ect") >= 80 && frames[i].Bit(12, 1) && V(i, "spd") is not > 0).ToList();
        double? Median(string key) => MedianOf(idle.Select(i => V(i, key)));
        items.Add(new(WarmIdle, "idle_time", "Time at warm idle", Seconds(frames, idle) / 60, "min", "0.0"));
        items.Add(new(WarmIdle, "idle_rpm", "RPM, median", Median("rpm"), "rpm", "0", 50));
        items.Add(new(WarmIdle, "idle_rpm_min", "RPM, lowest", idle.Count > 0 ? idle.Min(i => V(i, "rpm")) : null, "rpm", "0", 50));
        items.Add(new(WarmIdle, "idle_low", "Time below 600 rpm", idle.Count > 0 ? 100.0 * idle.Count(i => V(i, "rpm") < 600) / idle.Count : null, "%", "0.0", 5));
        items.Add(new(WarmIdle, "idle_iac", "ISCV, median", Median("iac"), "steps", "0", 8));
        items.Add(new(WarmIdle, "idle_inj", "Injection time, median", Median("inj"), "ms", "0.00", 0.3));
        items.Add(new(WarmIdle, "idle_ign", "Ignition advance, median", Median("ign"), "°", "0.0", 4));
        items.Add(new(WarmIdle, "idle_map", "MAP, median", Median("air"), "bar", "0.00", 0.05));
        var idleCl = idle.Where(i => Diagnostics.ClosedLoop(frames[i])).ToList();
        items.Add(new(WarmIdle, "idle_o2", "O2 rich (closed loop)", idleCl.Count > 0 ? 100.0 * idleCl.Count(i => Diagnostics.O2Rich(frames[i])) / idleCl.Count : null, "%", "0", 15));

        // ── Прогрів ── (від першого кадру з працюючим двигуном)
        int start = Enumerable.Range(0, frames.Count).FirstOrDefault(i => V(i, "rpm") > 0, -1);
        double? MinutesUntil(Func<int, bool> cond)
        {
            if (start < 0) return null;
            for (int i = start; i < frames.Count; i++)
                if (V(i, "rpm") > 0 && cond(i)) return (frames[i].Time - frames[start].Time).TotalMinutes;
            return null;
        }
        bool startedWarm = start >= 0 && V(start, "ect") >= 80;
        items.Add(new(WarmUp, "ect_start", "Coolant at start", start >= 0 ? V(start, "ect") : null, "°C", "0.0"));
        items.Add(new(WarmUp, "warm_80", "Time to 80 °C", startedWarm ? null : MinutesUntil(i => V(i, "ect") >= 80), "min", "0.0"));
        items.Add(new(WarmUp, "closed_loop", "Closed loop after", MinutesUntil(i => Diagnostics.ClosedLoop(frames[i])), "min", "0.0"));
        items.Add(new(WarmUp, "ect_max", "Coolant, highest", decoded.Select(d => d.GetValueOrDefault("ect")).Max(), "°C", "0.0", 5));

        // ── Перевірки ──
        var stuck = Diagnostics.O2StuckEvents(frames, Diagnostics.O2Threshold(s));
        items.Add(new(Checks, "o2_stuck", $"O2 stuck ≥ {Diagnostics.O2Threshold(s):0} s (closed loop)", stuck.Count, "times", "0", 0, stuck));
        items.Add(new(Checks, "o2_stuck_max", "O2 stuck, longest", stuck.Count > 0 ? stuck.Max(e => e.Seconds) : null, "s", "0"));
        var idl = Diagnostics.IdlMismatchEvents(frames, Diagnostics.IdlThreshold(s));
        items.Add(new(Checks, "idl_tps0", $"IDL open, throttle 0 % > {Diagnostics.IdlThreshold(s):0} s", idl.Count, "times", "0", 0, idl));
        var knock = Diagnostics.Runs(frames.Count, i => Diagnostics.Knock(frames[i]));
        items.Add(new(Checks, "knock", "Knock", knock.Sum(x => x.End - x.Start + 1), "frames", "0", 0, knock));
        items.Add(new(Checks, "map_max", "MAP, highest", Enumerable.Range(0, frames.Count).Where(i => V(i, "rpm") > 0).Select(i => V(i, "air")).Max(), "bar", "0.00"));
        items.Add(new(Checks, "crank", "Cranking (rpm 1–399)", Enumerable.Range(0, frames.Count).Count(i => V(i, "rpm") is > 0 and < 400), "frames", "0"));

        // ── Поїздка ──
        var trip = new TripMeter();
        foreach (var f in frames) trip.Add(f, s);
        var t = trip.At(frames.Count - 1);
        items.Add(new(Trip, "fuel", "Fuel used", frames.Count > 0 ? t.FuelL : null, "L", "0.00"));
        items.Add(new(Trip, "dist", "Distance", frames.Count > 0 ? t.DistanceKm : null, "km", "0.0"));
        items.Add(new(Trip, "avg", "Average", t.AverageL100, "L/100km", "0.0", 3));
        items.Add(new(Trip, "engine", "Engine running", t.EngineSeconds / 60, "min", "0.0"));

        // ── Зв'язок ──
        items.Add(new(Link, "frames", "Frames", frames.Count, "", "0"));
        int gaps = 0;
        for (int i = 1; i < frames.Count; i++) if ((frames[i].Time - frames[i - 1].Time).TotalSeconds > 5) gaps++;
        items.Add(new(Link, "gaps", "Gaps > 5 s", gaps, "", "0"));
        items.Add(new(Link, "other_id", "Frames with another ID", OtherIdFrames(frames), "", "0"));
        return items;
    }

    /// <summary>Кадри, чий ID відрізняється від найчастішого (ймовірні бітові помилки).</summary>
    private static int OtherIdFrames(IReadOnlyList<ObdFrame> frames)
    {
        if (frames.Count == 0) return 0;
        int main = frames.GroupBy(f => f.IdValue).OrderByDescending(g => g.Count()).First().Key;
        return frames.Count(f => f.IdValue != main);
    }

    /// <summary>Сумарний час кадрів зі списку (інтервал до наступного кадру, паузи > 5 с не рахуються).</summary>
    private static double Seconds(IReadOnlyList<ObdFrame> frames, List<int> idx)
    {
        double sum = 0;
        foreach (int i in idx)
            if (i + 1 < frames.Count && (frames[i + 1].Time - frames[i].Time).TotalSeconds is var dt and > 0 and <= 5) sum += dt;
        return sum;
    }

    private static double? MedianOf(IEnumerable<double?> values)
    {
        var l = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (l.Count == 0) return null;
        return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }
}
