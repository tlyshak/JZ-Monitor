using JzMonitor.Protocol;
using JzMonitor.Recording;
using Xunit;

namespace JzMonitor.Tests.Protocol;

public class DiagnosticsTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 9, 0, 0);

    /// <summary>Кадр: оберти (сирий ×25), ОР (сирий), дросель (сирий), біти 11 і 12.</summary>
    private static ObdFrame Frame(int second, byte rpmRaw = 26, byte tps = 0, bool closedLoop = true, bool rich = false, bool idl = true)
    {
        var b = new byte[12];
        b[3] = rpmRaw;
        b[5] = 233; // ≈ 91 °C — прогрітий
        b[6] = tps;
        b[10] = (byte)(closedLoop ? 1 << 5 : 0);
        b[11] = (byte)((idl ? 1 << 1 : 0) | (rich ? 1 << 4 : 0));
        return new ObdFrame { Time = T0.AddSeconds(second), Bytes = b };
    }

    [Fact]
    public void O2StuckSeconds_CountsTimeInOneStateInClosedLoop()
    {
        // перемикання на 2-й секунді, далі LEAN 12 с
        var frames = new List<ObdFrame> { Frame(0, rich: true), Frame(1, rich: true) };
        for (int s = 2; s <= 14; s++) frames.Add(Frame(s));

        Assert.Equal(12, Diagnostics.O2StuckFor(frames, frames.Count - 1));
        Assert.Equal(1, Diagnostics.O2StuckFor(frames, 1));
    }

    [Fact]
    public void O2StuckSeconds_OpenLoopOrEngineOff_IsZero()
    {
        var frames = new List<ObdFrame> { Frame(0), Frame(1), Frame(2, closedLoop: false), Frame(3, rpmRaw: 0) };

        Assert.Equal(0, Diagnostics.O2StuckFor(frames, 2));
        Assert.Equal(0, Diagnostics.O2StuckFor(frames, 3));
    }

    [Fact]
    public void O2StuckSeconds_LeavingClosedLoopRestartsCount()
    {
        var frames = new List<ObdFrame> { Frame(0), Frame(1), Frame(2, closedLoop: false), Frame(3), Frame(4) };

        Assert.Equal(1, Diagnostics.O2StuckFor(frames, 4));
    }

    [Theory]
    [InlineData(false, 0, true)]    // концевик розімкнений, дросель рівно 0 — підозріло
    [InlineData(false, 1, false)]   // заслінка трохи відкрита (0.6 %) — нормально
    [InlineData(true, 0, false)]    // концевик замкнений — нормально
    public void IdlOpenThrottleZero(bool idlClosed, byte tps, bool expected)
    {
        Assert.Equal(expected, Diagnostics.IdlOpenThrottleZero(Frame(0, tps: tps, idl: idlClosed)));
    }

    [Fact]
    public void IdlMismatchFor_DurationOfTheRun()
    {
        // кадр 1 — поодинокий (0 с); кадри 3–6 — відрізок 3 с
        var frames = new List<ObdFrame> { Frame(0), Frame(1, idl: false), Frame(2) };
        for (int s = 3; s <= 6; s++) frames.Add(Frame(s, idl: false));
        frames.Add(Frame(7));

        Assert.Equal(0, Diagnostics.IdlMismatchFor(frames, 1));
        Assert.Equal(3, Diagnostics.IdlMismatchFor(frames, 4));
        Assert.Null(Diagnostics.IdlMismatchFor(frames, 2));
    }

    [Fact]
    public void Thresholds_ShortEventsOnlyWithSettings()
    {
        // лямбда LEAN 15 с, потім концевик розімкнений при дроселі 0 протягом 3 с
        var frames = new List<ObdFrame>();
        for (int s = 0; s <= 15; s++) frames.Add(Frame(s));
        frames.Add(Frame(16, rich: true));
        for (int s = 17; s <= 20; s++) frames.Add(Frame(s, idl: false, rich: s % 2 == 0));

        var normal = Diagnostics.Alerts(frames, new DecoderSettings());
        var all = Diagnostics.Alerts(frames, new DecoderSettings { ShowShortO2Stuck = true, ShowShortIdlMismatch = true });

        Assert.Empty(normal);                                   // 15 с < 20 с, 3 с ≤ 5 с — за замовчуванням не показується
        Assert.Equal(2, all.Count);
        Assert.All(all, a => Assert.Equal(AlertLevel.Warn, a.Level)); // лямбда ніколи не червона
    }

    [Fact]
    public void LongEvents_AlwaysShown_O2NeverRed()
    {
        var frames = new List<ObdFrame>();
        for (int s = 0; s <= 30; s++) frames.Add(Frame(s));             // лямбда 30 с
        frames.Add(Frame(31, rich: true));
        for (int s = 32; s <= 38; s++) frames.Add(Frame(s, idl: false, rich: s % 2 == 0)); // концевик 6 с

        var alerts = Diagnostics.Alerts(frames, new DecoderSettings());

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(AlertLevel.Warn, a.Level));
    }

    [Fact]
    public void IdlOpenThrottleZero_EngineOff_IsFalse()
    {
        Assert.False(Diagnostics.IdlOpenThrottleZero(Frame(0, rpmRaw: 0, idl: false)));
    }

    [Fact]
    public void Report_CountsO2StuckEventsAndIdlMismatch()
    {
        var frames = new List<ObdFrame>();
        // 25 с LEAN у зворотному зв'язку, потім нормальне перемикання, потім 7 с IDL розімкнений при дроселі 0
        for (int s = 0; s <= 25; s++) frames.Add(Frame(s));
        for (int s = 26; s <= 35; s++) frames.Add(Frame(s, rich: s % 2 == 0));
        for (int s = 36; s <= 43; s++) frames.Add(Frame(s, idl: false, rich: s % 2 == 0));

        var report = RecordingReport.Build(frames, new DecoderSettings());
        double? Value(string key) => report.Single(r => r.Key == key).Value;

        Assert.Equal(1, Value("o2_stuck"));
        Assert.Equal(25, Value("o2_stuck_max"));
        Assert.Equal(1, Value("idl_tps0"));
        Assert.Equal(650, Value("idle_rpm"));
        // де саме: лямбда — кадри 0–25 (25 с), концевик — кадри 36–43 (7 с)
        Assert.Equal([new ReportSpan(0, 25, 25)], report.Single(r => r.Key == "o2_stuck").Spans);
        Assert.Equal([new ReportSpan(36, 43, 7)], report.Single(r => r.Key == "idl_tps0").Spans);
    }
}
