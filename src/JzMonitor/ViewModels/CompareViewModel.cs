using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using JzMonitor.Controls;
using JzMonitor.Protocol;
using JzMonitor.Recording;
using Microsoft.Win32;

namespace JzMonitor.ViewModels;

/// <summary>Один файл у порівнянні: запис, діапазон і розраховані значення.</summary>
public sealed class CompareSide : ObservableObject
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private readonly DecoderSettings _settings;
    private readonly Action _changed;

    public string Label { get; }
    public Brush Badge { get; }
    public LogRecording? Recording { get; private set; }
    /// <summary>Значення параметрів по кадрах (null — немає або двигун стоїть для inj/ign).</summary>
    public Dictionary<string, double?[]> Values { get; } = new();
    public FrameRange Range { get; private set; }
    public TripPoint Trip { get; private set; }

    public CompareSide(string label, Brush badge, DecoderSettings settings, Action changed)
    {
        Label = label;
        Badge = badge;
        _settings = settings;
        _changed = changed;
        OpenCommand = new RelayCommand(Open);
        ApplyCommand = new RelayCommand(Apply, () => HasData);
        FullCommand = new RelayCommand(() => SetRange(FrameRange.All(Recording!.Frames.Count)), () => HasData);
    }

    public RelayCommand OpenCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand FullCommand { get; }

    public bool HasData => Recording is { Frames.Count: > 0 };
    public string FileName => Recording?.Source is string s ? Path.GetFileName(s) : HasData ? "Current recording" : "No file — click Open";

    public string[] RangeModes { get; } = ["Frames", "Time"];
    private RangeMode _mode = RangeMode.Time;
    public int RangeModeIndex
    {
        get => (int)_mode;
        set { if (value is 0 or 1) { _mode = (RangeMode)value; OnPropertyChanged(); UpdateTexts(); } }
    }
    private string _from = "", _to = "", _info = "", _error = "";
    public string From { get => _from; set => Set(ref _from, value); }
    public string To { get => _to; set => Set(ref _to, value); }
    public string Info { get => _info; private set => Set(ref _info, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }

    public void Load(LogRecording rec, FrameRange? range = null)
    {
        Recording = rec;
        Values.Clear();
        foreach (var key in CompareViewModel.Keys) Values[key] = new double?[rec.Frames.Count];
        for (int i = 0; i < rec.Frames.Count; i++)
        {
            var f = rec.Frames[i];
            bool off = f.B(4) is not > 0;
            foreach (var p in ParameterDecoder.Decode(f, _settings))
                if (Values.TryGetValue(p.Key, out var arr))
                    arr[i] = off && CompareViewModel.RunningOnly.Contains(p.Key) ? null : p.Value;
        }
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(HasData));
        SetRange(range ?? FrameRange.All(rec.Frames.Count));
    }

    private void Open()
    {
        var dlg = new OpenFileDialog { Filter = "CarTester log (*.ctr)|*.ctr|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Load(CtrFormat.Load(dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot open file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Apply()
    {
        if (Recording == null) return;
        if (!RangeParser.TryParse(Recording.Frames, _mode, From, To, out var r, out var e)) { Error = e; return; }
        SetRange(r);
    }

    private void SetRange(FrameRange r)
    {
        if (Recording == null) return;
        Error = "";
        Range = r;
        var trip = new TripMeter();
        for (int i = r.Start; i <= r.End; i++) trip.Add(Recording.Frames[i], _settings);
        Trip = trip.At(r.Count - 1);
        UpdateTexts();
        _changed();
    }

    private void UpdateTexts()
    {
        if (!HasData) { From = To = Info = ""; return; }
        var frames = Recording!.Frames;
        From = RangeParser.Format(frames, _mode, Range.Start);
        To = RangeParser.Format(frames, _mode, Range.End);
        var d = Range.Duration(frames);
        Info = $"{Range.Count:N0} frames  ·  {CompareViewModel.FormatSpan(d.TotalSeconds)}  ·  {frames[Range.Start].Time:dd.MM.yyyy HH:mm}";
    }

    /// <summary>MIN / AVG / MAX параметра в діапазоні.</summary>
    public (double Min, double Avg, double Max)? Stats(string key)
    {
        if (!Values.TryGetValue(key, out var arr)) return null;
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        int count = 0;
        for (int i = Range.Start; i <= Range.End; i++)
        {
            if (arr[i] is not double v) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            sum += v;
            count++;
        }
        return count == 0 ? null : (min, sum / count, max);
    }

    public string FrameLabel(int frame) =>
        frame < 0 || Recording == null ? "—" : $"frame {frame + 1} · {Recording.Frames[frame].Time.ToString("HH:mm:ss", Ci)}";
}

/// <summary>Рядок таблиці порівняння.</summary>
public sealed class CompareRow : ObservableObject
{
    private string _a = "—", _b = "—", _delta = "", _aStats = "", _bStats = "";
    private bool _charted;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }
    public required string Format { get; init; }
    public bool Chartable { get; init; } = true;
    public Color Color { get; init; }
    public Brush ColorBrush => new SolidColorBrush(Color);
    public string A { get => _a; set => Set(ref _a, value); }
    public string B { get => _b; set => Set(ref _b, value); }
    public string Delta { get => _delta; set => Set(ref _delta, value); }
    public string AStats { get => _aStats; set => Set(ref _aStats, value); }
    public string BStats { get => _bStats; set => Set(ref _bStats, value); }
    public bool Charted { get => _charted; set { if (Set(ref _charted, value)) ChartedChanged?.Invoke(); } }
    public event Action? ChartedChanged;
}

/// <summary>
/// Порівняння двох записів. Кожен має свій діапазон; на спільній осі — час від початку діапазону,
/// з кроком 1 с (значення утримується до наступного кадру). A — суцільні лінії, B — пунктир, шкала спільна.
/// </summary>
public sealed class CompareViewModel : ObservableObject
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public static readonly string[] Keys = ["rpm", "ect", "air", "inj", "ign", "iac", "tps", "spd", "af9", "lph"];
    public static readonly HashSet<string> RunningOnly = ["inj", "ign"];
    private const int MaxChartPoints = 3000;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0x00, 0x97, 0xA7), Color.FromRgb(0xF2, 0x6B, 0x1D),
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0xE5, 0x48, 0x4D), Color.FromRgb(0x18, 0xA8, 0x66),
        Color.FromRgb(0xC9, 0x8A, 0x00), Color.FromRgb(0x5C, 0x6B, 0x7A), Color.FromRgb(0xD8, 0x1B, 0x60),
        Color.FromRgb(0x3F, 0x51, 0xB5)
    ];

    private readonly DecoderSettings _settings;
    private int[] _mapA = [], _mapB = [];
    private int _seconds, _step = 1;

    public CompareSide SideA { get; }
    public CompareSide SideB { get; }
    public ObservableCollection<CompareRow> Rows { get; } = new();
    public ObservableCollection<CompareRow> TripRows { get; } = new();

    public CompareViewModel(LogRecording a, DecoderSettings settings, FrameRange aRange)
    {
        _settings = settings;
        SideA = new CompareSide("A", Freeze(Color.FromRgb(0xF2, 0x6B, 0x1D)), settings, Rebuild);
        SideB = new CompareSide("B", Freeze(Color.FromRgb(0x1E, 0x88, 0xE5)), settings, Rebuild);

        var sample = ParameterDecoder.Decode(new ObdFrame { Bytes = new byte[12] }, settings).ToDictionary(p => p.Key);
        int c = 0;
        foreach (var key in Keys)
        {
            if (!sample.TryGetValue(key, out var p)) continue;
            var row = new CompareRow
            {
                Key = key, Name = p.Name, Unit = p.Unit, Format = FormatOf(p.Text),
                Color = Palette[c++ % Palette.Length], Charted = key is "rpm" or "ect" or "iac"
            };
            row.ChartedChanged += UpdateChart;
            Rows.Add(row);
        }
        foreach (var (key, name, unit, fmt) in new[]
                 {
                     ("fuel", "Fuel used", "L", "0.00"), ("dist", "Distance", "km", "0.0"),
                     ("avg", "Average", "L/100km", "0.0"), ("eng", "Engine time", "", ""), ("dur", "Duration", "", "")
                 })
            TripRows.Add(new CompareRow { Key = key, Name = name, Unit = unit, Format = fmt, Chartable = false });

        if (a.Frames.Count > 0) SideA.Load(a, aRange);
    }

    private int _cursor;
    /// <summary>Курсор — секунди від початку діапазонів.</summary>
    public int Cursor
    {
        get => _cursor;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, _seconds - 1));
            if (!Set(ref _cursor, value)) return;
            OnPropertyChanged(nameof(ChartCursor));
            UpdateCursorValues();
        }
    }
    public int ChartCursor => _cursor / _step;
    public void SeekChart(int point) => Cursor = point * _step;
    public void Step(int seconds) => Cursor += seconds;

    private string _cursorInfo = "";
    public string CursorInfo { get => _cursorInfo; private set => Set(ref _cursorInfo, value); }

    private IReadOnlyList<ChartSeries>? _chart;
    public IReadOnlyList<ChartSeries>? Chart { get => _chart; private set => Set(ref _chart, value); }
    public Func<int, string> ChartXLabel => i => "+" + FormatSpan(i * _step);

    private void Rebuild()
    {
        double Dur(CompareSide s) => s.HasData ? s.Range.Duration(s.Recording!.Frames).TotalSeconds : 0;
        _seconds = (int)Math.Ceiling(Math.Max(Dur(SideA), Dur(SideB))) + 1;
        _mapA = SideA.HasData ? Timeline.BySecond(SideA.Recording!.Frames, SideA.Range, _seconds) : new int[_seconds].Select(_ => -1).ToArray();
        _mapB = SideB.HasData ? Timeline.BySecond(SideB.Recording!.Frames, SideB.Range, _seconds) : new int[_seconds].Select(_ => -1).ToArray();
        _step = Math.Max(1, (int)Math.Ceiling(_seconds / (double)MaxChartPoints));

        foreach (var row in Rows)
        {
            row.AStats = StatsText(SideA, row);
            row.BStats = StatsText(SideB, row);
        }
        UpdateTrip();
        UpdateChart();
        _cursor = -1;
        Cursor = 0;
    }

    private void UpdateChart()
    {
        if (_seconds == 0 || (!SideA.HasData && !SideB.HasData)) { Chart = null; return; }
        int points = (_seconds - 1) / _step + 1;
        var series = new List<ChartSeries>();
        foreach (var row in Rows.Where(r => r.Charted))
        {
            var a = Sample(SideA, _mapA, row.Key, points);
            var b = Sample(SideB, _mapB, row.Key, points);
            // Спільна шкала для A і B — інакше лінії не порівняти
            var all = a.Concat(b).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            double? min = all.Count > 0 ? all.Min() : null, max = all.Count > 0 ? all.Max() : null;
            string unit = row.Unit.Equals(row.Name, StringComparison.OrdinalIgnoreCase) ? "" : row.Unit;
            if (SideA.HasData) series.Add(new ChartSeries { Name = row.Name + " A", Unit = unit, Color = row.Color, Values = a, Min = min, Max = max });
            if (SideB.HasData) series.Add(new ChartSeries { Name = row.Name + " B", Unit = unit, Color = row.Color, Values = b, Min = min, Max = max, Dashed = true });
        }
        Chart = series;
        OnPropertyChanged(nameof(ChartXLabel));
        OnPropertyChanged(nameof(ChartCursor));
    }

    private double?[] Sample(CompareSide side, int[] map, string key, int points)
    {
        var result = new double?[points];
        if (!side.HasData || !side.Values.TryGetValue(key, out var arr)) return result;
        for (int i = 0; i < points; i++)
        {
            int sec = i * _step;
            int frame = sec < map.Length ? map[sec] : -1;
            result[i] = frame >= 0 ? arr[frame] : null;
        }
        return result;
    }

    private void UpdateCursorValues()
    {
        int fa = _cursor < _mapA.Length ? _mapA[_cursor] : -1;
        int fb = _cursor < _mapB.Length ? _mapB[_cursor] : -1;
        foreach (var row in Rows)
        {
            double? a = fa >= 0 && SideA.Values.TryGetValue(row.Key, out var va) ? va[fa] : null;
            double? b = fb >= 0 && SideB.Values.TryGetValue(row.Key, out var vb) ? vb[fb] : null;
            row.A = a?.ToString(row.Format, Ci) ?? "—";
            row.B = b?.ToString(row.Format, Ci) ?? "—";
            row.Delta = a is double x && b is double y ? (y - x > 0 ? "+" : "") + (y - x).ToString(row.Format, Ci) : "";
        }
        CursorInfo = $"+{FormatSpan(_cursor)}    A: {SideA.FrameLabel(fa)}    B: {SideB.FrameLabel(fb)}";
    }

    private void UpdateTrip()
    {
        foreach (var row in TripRows)
        {
            string Text(CompareSide s)
            {
                if (!s.HasData) return "—";
                var t = s.Trip;
                return row.Key switch
                {
                    "fuel" => t.FuelL.ToString("0.00", Ci),
                    "dist" => t.DistanceKm.ToString("0.0", Ci),
                    "avg" => t.AverageL100?.ToString("0.0", Ci) ?? "—",
                    "eng" => FormatSpan(t.EngineSeconds),
                    _ => FormatSpan(s.Range.Duration(s.Recording!.Frames).TotalSeconds)
                };
            }
            row.A = Text(SideA);
            row.B = Text(SideB);
            double? Num(CompareSide s) => !s.HasData ? null : row.Key switch
            {
                "fuel" => s.Trip.FuelL,
                "dist" => s.Trip.DistanceKm,
                "avg" => s.Trip.AverageL100,
                _ => null
            };
            row.Delta = Num(SideA) is double x && Num(SideB) is double y ? (y - x > 0 ? "+" : "") + (y - x).ToString(row.Format, Ci) : "";
        }
    }

    private static string StatsText(CompareSide side, CompareRow row) =>
        side.Stats(row.Key) is var (min, avg, max)
            ? $"{min.ToString(row.Format, Ci)} / {avg.ToString(row.Format, Ci)} / {max.ToString(row.Format, Ci)}"
            : "—";

    /// <summary>Формат з кількістю знаків після коми, як у тексті декодера («6.25» → "0.00").</summary>
    private static string FormatOf(string sampleText)
    {
        int dot = sampleText.IndexOf('.');
        return dot < 0 ? "0" : "0." + new string('0', sampleText.Length - dot - 1);
    }

    public static string FormatSpan(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
