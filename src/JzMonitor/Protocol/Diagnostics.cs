namespace JzMonitor.Protocol;

/// <summary>Відрізок кадрів, де спрацювала перевірка (індекси в переданому списку кадрів, включно).</summary>
public readonly record struct ReportSpan(int Start, int End, double? Seconds = null);

public enum AlertLevel { Warn, Alarm }

/// <summary>Спрацювала перевірка на відрізку кадрів: рівень і пояснення.</summary>
public readonly record struct Alert(ReportSpan Span, AlertLevel Level, string Text);

/// <summary>
/// Перевірки стану по кадрах (без WPF). Пороги — з поведінки еталонного 2JZ-GTE у записах, не з протоколу.
/// Короткі спрацювання (лямбда 10–20 с, концевик ХХ 2–5 с) показуються лише за налаштуванням — у записах
/// справного авто вони трапляються на перехідних режимах і створюють шум.
/// </summary>
public static class Diagnostics
{
    /// <summary>Лямбда в одному стані в зворотному зв'язку: завжди від 20 с, з налаштуванням — від 10 с.</summary>
    public const double O2StuckSeconds = 20, O2StuckShortSeconds = 10;
    /// <summary>Концевик ХХ розімкнений при дроселі 0: завжди понад 5 с, з налаштуванням — понад 2 с.</summary>
    public const double IdlMismatchSeconds = 5, IdlMismatchShortSeconds = 2;

    public static double O2Threshold(DecoderSettings s) => s.ShowShortO2Stuck ? O2StuckShortSeconds : O2StuckSeconds;
    public static double IdlThreshold(DecoderSettings s) => s.ShowShortIdlMismatch ? IdlMismatchShortSeconds : IdlMismatchSeconds;

    /// <summary>Розрив між кадрами, довший за цей, перериває відлік (запис зупиняли).</summary>
    private const double MaxGapSeconds = 5;

    private static bool Running(ObdFrame f) => f.B(4) is > 0;
    /// <summary>Біт 11.5 — зворотний зв'язок за лямбдою (closed loop).</summary>
    public static bool ClosedLoop(ObdFrame f) => f.Bit(11, 5);
    /// <summary>Біт 12.4 — лямбда: 1 = RICH, 0 = LEAN.</summary>
    public static bool O2Rich(ObdFrame f) => f.Bit(12, 4);
    /// <summary>Детонація (біт 11.4) при працюючому двигуні.</summary>
    public static bool Knock(ObdFrame f) => Running(f) && f.Bit(11, 4);

    /// <summary>
    /// Скільки секунд до кадру index (включно) лямбда не змінює стан, поки двигун працює і зворотний зв'язок
    /// увімкнено. 0 — кадр не в зворотному зв'язку. Справна лямбда в зворотному зв'язку перемикається
    /// кілька разів на секунду: довгий один стан — суміш, яку ECU не може виправити, або датчик.
    /// </summary>
    public static double O2StuckFor(IReadOnlyList<ObdFrame> frames, int index)
    {
        if (index < 0 || index >= frames.Count) return 0;
        var f = frames[index];
        if (!Running(f) || !ClosedLoop(f)) return 0;
        bool state = O2Rich(f);
        int start = index;
        while (start > 0)
        {
            var p = frames[start - 1];
            if (!Running(p) || !ClosedLoop(p) || O2Rich(p) != state) break;
            if ((frames[start].Time - p.Time).TotalSeconds > MaxGapSeconds) break;
            start--;
        }
        return (f.Time - frames[start].Time).TotalSeconds;
    }

    /// <summary>
    /// Концевик ХХ (IDL, біт 12.1) розімкнений, а дросель рівно 0 (сирий байт 7 = 0) при працюючому двигуні.
    /// Концевик має розмикатися лише коли заслінка вже трохи відкрита — інакше він або датчик дроселя
    /// відрегульовані неправильно. Будь-яке відкриття (байт ≥ 1) — умова не виконується.
    /// </summary>
    public static bool IdlOpenThrottleZero(ObdFrame f) => Running(f) && !f.Bit(12, 1) && f.B(7) is 0;

    /// <summary>
    /// Скільки секунд триває відрізок «концевик розімкнений при дроселі 0», якому належить кадр index
    /// (від першого до останнього кадру відрізка; поодинокий кадр — 0). Не на відрізку — null.
    /// </summary>
    public static double? IdlMismatchFor(IReadOnlyList<ObdFrame> frames, int index)
    {
        if (index < 0 || index >= frames.Count || !IdlOpenThrottleZero(frames[index])) return null;
        int a = index, b = index;
        while (a > 0 && IdlOpenThrottleZero(frames[a - 1]) && (frames[a].Time - frames[a - 1].Time).TotalSeconds <= MaxGapSeconds) a--;
        while (b + 1 < frames.Count && IdlOpenThrottleZero(frames[b + 1]) && (frames[b + 1].Time - frames[b].Time).TotalSeconds <= MaxGapSeconds) b++;
        return (frames[b].Time - frames[a].Time).TotalSeconds;
    }

    /// <summary>Епізоди, коли лямбда в зворотному зв'язку стояла в одному стані щонайменше threshold секунд.</summary>
    public static List<ReportSpan> O2StuckEvents(IReadOnlyList<ObdFrame> frames, double threshold)
    {
        var d = Enumerable.Range(0, frames.Count).Select(i => O2StuckFor(frames, i)).ToArray();
        var events = new List<ReportSpan>();
        for (int i = 0; i < d.Length; i++)
        {
            // Кінець епізоду — останній кадр або наступний кадр почав новий відлік
            bool end = i == d.Length - 1 || d[i + 1] < d[i] || d[i + 1] == 0;
            if (!end || d[i] < threshold || d[i] == 0) continue;
            int start = i;
            while (start > 0 && d[start] > 0) start--;
            events.Add(new ReportSpan(start, i, d[i]));
        }
        return events;
    }

    /// <summary>Відрізки «концевик розімкнений при дроселі 0», довші за threshold секунд.</summary>
    public static List<ReportSpan> IdlMismatchEvents(IReadOnlyList<ObdFrame> frames, double threshold)
    {
        var events = new List<ReportSpan>();
        foreach (var sp in Runs(frames.Count, i => IdlOpenThrottleZero(frames[i])))
        {
            // Пауза в записі ділить відрізок — тривалість рахуємо по кожному кадру відрізка
            double sec = IdlMismatchFor(frames, sp.End) ?? 0;
            if (sec > threshold) events.Add(sp with { Seconds = sec });
        }
        return events;
    }

    /// <summary>Відрізки поспіль кадрів, де умова виконується.</summary>
    public static List<ReportSpan> Runs(int count, Func<int, bool> cond)
    {
        var runs = new List<ReportSpan>();
        for (int i = 0; i < count; i++)
        {
            if (!cond(i)) continue;
            int start = i;
            while (i + 1 < count && cond(i + 1)) i++;
            runs.Add(new ReportSpan(start, i));
        }
        return runs;
    }

    /// <summary>
    /// Усі спрацювання перевірок у записі — для знаків «!» на графіку: лямбда застрягла і концевик ХХ при дроселі 0
    /// (попередження, з порогами з налаштувань), детонація (тривога).
    /// </summary>
    public static List<Alert> Alerts(IReadOnlyList<ObdFrame> frames, DecoderSettings s)
    {
        var alerts = new List<Alert>();
        foreach (var sp in O2StuckEvents(frames, O2Threshold(s)))
            alerts.Add(new(sp, AlertLevel.Warn, $"O2 stuck {(O2Rich(frames[sp.End]) ? "RICH" : "LEAN")} for {sp.Seconds:0} s in closed loop"));
        foreach (var sp in IdlMismatchEvents(frames, IdlThreshold(s)))
            alerts.Add(new(sp, AlertLevel.Warn, $"Idle switch open with throttle 0 % for {sp.Seconds:0} s"));
        foreach (var sp in Runs(frames.Count, i => Knock(frames[i])))
            alerts.Add(new(sp, AlertLevel.Alarm, "Knock"));
        return alerts;
    }
}
