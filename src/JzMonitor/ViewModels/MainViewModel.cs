using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JzMonitor.Controls;
using JzMonitor.Protocol;
using JzMonitor.Recording;
using Microsoft.Win32;

namespace JzMonitor.ViewModels;

public enum LinkState { Offline, Waiting, Live, Errors }

public enum FlagWarning { None, Warn, Alarm }

/// <summary>Один параметр: рядок таблиці, плитка на панелі приладів або індикатор біта.</summary>
public sealed class ParamRow : ObservableObject
{
    private string _text = "—";
    private string _raw = "";
    private string _unit = "";
    private bool _charted;
    private bool _isOn;
    private double _fraction;
    private Brush _accent = Brushes.Transparent;
    private string _maxText = "—";
    private string _minText = "—";

    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool HiddenInOriginal { get; init; }
    public bool IsFlag => Key.StartsWith('f');
    public string BitLabel => IsFlag ? Key[1..] : "";
    /// <summary>Біт, увімкнення якого — тривожна подія (детонація).</summary>
    public bool IsAlarm => Key == "f11.4";

    public string Unit { get => _unit; set => Set(ref _unit, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Raw { get => _raw; set => Set(ref _raw, value); }
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    private FlagWarning _warning;
    private string? _warningText;
    /// <summary>Попередження для індикатора біта (застрягла лямбда, концевик ХХ при закритій заслінці).</summary>
    public FlagWarning Warning { get => _warning; set => Set(ref _warning, value); }
    public string? WarningText { get => _warningText; set => Set(ref _warningText, value); }
    /// <summary>0..1 — заповнення шкали на плитці (за сирим байтом).</summary>
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    /// <summary>Максимум за сесію (поточний запис).</summary>
    public string MaxText { get => _maxText; set => Set(ref _maxText, value); }
    /// <summary>Мінімум за сесію (поточний запис).</summary>
    public string MinText { get => _minText; set => Set(ref _minText, value); }
    /// <summary>Які статистики показувати для цього параметра.</summary>
    public bool ShowMin => !IsFlag && !IsTrip && MinKeys.Contains(Key);
    public bool ShowMax => !IsFlag && !IsTrip && !NoMaxKeys.Contains(Key);
    /// <summary>Підсумок поїздки (накопичувальне значення, не з кадру).</summary>
    public bool IsTrip => Key.StartsWith("trip_");
    /// <summary>З підсумків поїздки на графік можна вивести лише середню витрату.</summary>
    public bool Chartable => !IsTrip || Key == "trip_avg";

    private static readonly HashSet<string> MinKeys = ["inj", "ign", "iac"];
    // ISCV — лише MIN; витрата на 100 км біля нуля швидкості дає абсурдні числа — без MAX
    private static readonly HashSet<string> NoMaxKeys = ["iac", "l100km"];
    /// <summary>Колір шкали/значення на плитці.</summary>
    public Brush Accent { get => _accent; set => Set(ref _accent, value); }
    public Color Color { get; set; }
    public Brush ColorBrush => new SolidColorBrush(Color);
    public bool Charted { get => _charted; set { if (Set(ref _charted, value)) ChartedChanged?.Invoke(); } }
    public event Action? ChartedChanged;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xF2, 0x6B, 0x1D), Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x18, 0xA8, 0x66),
        Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0xE5, 0x48, 0x4D), Color.FromRgb(0x00, 0x97, 0xA7),
        Color.FromRgb(0xC9, 0x8A, 0x00), Color.FromRgb(0x5C, 0x6B, 0x7A)
    ];

    /// <summary>Параметри, що показуються великими плитками.</summary>
    private static readonly string[] TileKeys = ["rpm", "ect", "air", "inj", "ign", "tps", "iac", "spd", "trip_avg"];

    private static readonly Brush AccentBrush = Freeze(Color.FromRgb(0xF2, 0x6B, 0x1D));
    private static readonly Brush ColdBrush = Freeze(Color.FromRgb(0x1E, 0x88, 0xE5));
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x18, 0xA8, 0x66));
    private static readonly Brush HotBrush = Freeze(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(0xE8, 0xA3, 0x17));
    private static readonly Brush OrangeBrush = Freeze(Color.FromRgb(0xF0, 0x7E, 0x1F));
    private static readonly Brush DeepRedBrush = Freeze(Color.FromRgb(0xC6, 0x28, 0x28));

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly FrameAssembler _assembler = new();
    private readonly object _asmLock = new();
    private readonly DispatcherTimer _linkTimer;
    private IEdgeSource? _source;
    private EdgeLogger? _edgeLog;
    private LogRecording _rec = new();
    private bool _dirty;
    private DateTime _lastValidFrame;
    private int _okFrames, _badFrames;
    /// <summary>Мінімуми й максимуми параметрів у діапазоні перегляду.</summary>
    private readonly Dictionary<string, ParamValue> _min = new(), _max = new();
    /// <summary>Витрата палива, відстань і час за поїздку (поточний запис).</summary>
    private readonly TripMeter _trip = new();

    public DecoderSettings Settings { get; }
    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<ParamRow> Parameters { get; } = new();
    public ObservableCollection<ParamRow> Tiles { get; } = new();
    public ObservableCollection<ParamRow> Flags { get; } = new();
    /// <summary>Усе, крім бітів стану (вони у блоці ECU STATUS).</summary>
    public ObservableCollection<ParamRow> Analog { get; } = new();
    public ObservableCollection<string> RawLog { get; } = new();


    public MainViewModel()
    {
        Settings = DecoderSettings.Load();
        ApplyAssemblerSettings();
        _assembler.FrameCompleted += OnFrame;
        RefreshPorts();
        TryLoadDefaultVehicle();

        RefreshPortsCommand = new RelayCommand(RefreshPorts, () => !IsRunning);
        StartCommand = new RelayCommand(StartStop, () => IsRunning || !string.IsNullOrEmpty(Settings.Port));
        NewCommand = new RelayCommand(New, () => !IsRunning);
        OpenCommand = new RelayCommand(Open, () => !IsRunning);
        SaveCommand = new RelayCommand(() => Save(), () => _rec.Frames.Count > 0);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _rec.Frames.Count > 0);
        ImportIniCommand = new RelayCommand(ImportIni);
        ReplayEdgesCommand = new RelayCommand(ReplayEdges, () => !IsRunning);
        ToggleMarkCommand = new RelayCommand(ToggleMark, () => CurrentFrame != null);
        PrevMarkCommand = new RelayCommand(() => GoToMark(-1), () => MarkCount > 0);
        NextMarkCommand = new RelayCommand(() => GoToMark(+1), () => MarkCount > 0);
        DeleteFrameCommand = new RelayCommand(DeleteFrame, () => !IsRunning && CurrentFrame != null);
        VehicleCommand = new RelayCommand(EditVehicle);
        PrevCommand = new RelayCommand(() => CurrentIndex--, () => _currentIndex > RangeLo);
        NextCommand = new RelayCommand(() => CurrentIndex++, () => _currentIndex < RangeHi);
        LatestCommand = new RelayCommand(() =>
        {
            if (!IsRangeFull) SetRange(FrameRange.All(_rec.Frames.Count));
            Follow = true;
            CurrentIndex = _rec.Frames.Count - 1;
        }, () => _rec.Frames.Count > 0);
        FullRangeCommand = new RelayCommand(() => SetRange(FrameRange.All(_rec.Frames.Count)), () => _rec.Frames.Count > 0 && !IsRangeFull);
        CompareCommand = new RelayCommand(OpenCompare);
        ReportCommand = new RelayCommand(OpenReport, () => _rec.Frames.Count > 0);
        ToggleRangePanelCommand = new RelayCommand(() => RangePanelOpen = !RangePanelOpen);

        _linkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _linkTimer.Tick += (_, _) => UpdateLinkState();
        _linkTimer.Start();

        RebuildParameters();
    }

    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ImportIniCommand { get; }
    public RelayCommand ReplayEdgesCommand { get; }
    public RelayCommand ToggleMarkCommand { get; }
    public RelayCommand PrevMarkCommand { get; }
    public RelayCommand NextMarkCommand { get; }
    public RelayCommand DeleteFrameCommand { get; }
    public RelayCommand VehicleCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand LatestCommand { get; }
    public RelayCommand FullRangeCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand ReportCommand { get; }
    public RelayCommand ToggleRangePanelCommand { get; }

    #region Властивості для прив'язки

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(StartText));
            OnPropertyChanged(nameof(StartIcon));
            UpdateLinkState();
        }
    }
    public string StartText => IsRunning ? "Stop" : "Connect";
    public string StartIcon => IsRunning ? "" : "";

    private LinkState _link = LinkState.Offline;
    public LinkState Link { get => _link; private set { if (Set(ref _link, value)) OnPropertyChanged(nameof(LinkText)); } }
    public string LinkText => Link switch
    {
        LinkState.Live => "LIVE",
        LinkState.Waiting => "NO SIGNAL",
        LinkState.Errors => "ERRORS",
        _ => ""
    };

    public string? SelectedPort
    {
        get => Settings.Port;
        set { Settings.Port = value; OnPropertyChanged(); Settings.Save(); }
    }

    public int InjectorFlowCcMin { get => Settings.InjectorFlowCcMin; set { Settings.InjectorFlowCcMin = value; SettingsChanged(); } }
    public int InjectorCount { get => Settings.InjectorCount; set { Settings.InjectorCount = value; SettingsChanged(); } }
    public int InjectionRate { get => Settings.InjectionRate; set { Settings.InjectionRate = value; SettingsChanged(); } }
    public double SpeedDivisor { get => Settings.SpeedDivisor; set { if (value > 0) { Settings.SpeedDivisor = value; SettingsChanged(); } } }
    public bool ShowShortO2Stuck { get => Settings.ShowShortO2Stuck; set { Settings.ShowShortO2Stuck = value; SettingsChanged(); } }
    public bool ShowShortIdlMismatch { get => Settings.ShowShortIdlMismatch; set { Settings.ShowShortIdlMismatch = value; SettingsChanged(); } }
    public double MapKpaPerCount { get => Settings.MapKpaPerCount; set { if (value > 0) { Settings.MapKpaPerCount = value; SettingsChanged(); } } }

    public double BitTimeMs { get => Settings.BitTimeMs; set { Settings.BitTimeMs = value; SettingsChanged(); } }
    public int GapBits { get => Settings.GapBits; set { Settings.GapBits = value; SettingsChanged(); } }
    public int MinDataBytes { get => Settings.MinDataBytes; set { Settings.MinDataBytes = value; SettingsChanged(); } }

    private bool _showHidden = true;
    /// <summary>Показувати біти/байти, які оригінальна програма не відображала.</summary>
    public bool ShowHidden { get => _showHidden; set { if (Set(ref _showHidden, value)) RebuildParameters(); } }

    private bool _recordEdges;
    public bool RecordEdges { get => _recordEdges; set => Set(ref _recordEdges, value); }

    private bool _follow = true;
    /// <summary>Автоматично показувати останній кадр під час читання.</summary>
    public bool Follow { get => _follow; set => Set(ref _follow, value); }

    private int _currentIndex = -1;
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            value = _rec.Frames.Count == 0 ? -1 : Math.Clamp(value, RangeLo, RangeHi);
            if (!Set(ref _currentIndex, value)) return;
            if (IsRunning && value < _rec.Frames.Count - 1) Follow = false;
            ShowFrame();
        }
    }
    public int MaxIndex => Math.Max(0, _rec.Frames.Count - 1);

    // ── Діапазон перегляду ──
    // Початок і кінець (індекси, включно). _rangeEnd = null — «до кінця запису»: під час живого читання
    // діапазон росте разом із записом.
    private int _rangeStart;
    private int? _rangeEnd;
    public int RangeLo => _rec.Frames.Count == 0 ? 0 : Math.Clamp(_rangeStart, 0, _rec.Frames.Count - 1);
    public int RangeHi => _rec.Frames.Count == 0 ? 0 : Math.Clamp(_rangeEnd ?? _rec.Frames.Count - 1, RangeLo, _rec.Frames.Count - 1);
    public bool IsRangeFull => _rangeStart == 0 && _rangeEnd == null;
    private FrameRange Range => new(RangeLo, RangeHi);

    private string _rangeInfo = "";
    public string RangeInfo { get => _rangeInfo; private set => Set(ref _rangeInfo, value); }
    public ObdFrame? CurrentFrame => _currentIndex >= 0 && _currentIndex < _rec.Frames.Count ? _rec.Frames[_currentIndex] : null;

    private string _frameNumber = "—";
    public string FrameNumber { get => _frameNumber; set => Set(ref _frameNumber, value); }
    private string _frameTime = "";
    public string FrameTime { get => _frameTime; set => Set(ref _frameTime, value); }
    private bool _engineOff;
    /// <summary>У поточному кадрі двигун не працює (RPM = 0): впорскування й кут не показуються.</summary>
    public bool EngineOff { get => _engineOff; set => Set(ref _engineOff, value); }
    private bool _frameMarked;
    public bool FrameMarked { get => _frameMarked; set => Set(ref _frameMarked, value); }
    private string _idInfo = "—";
    public string IdInfo { get => _idInfo; set => Set(ref _idInfo, value); }
    private string _hexInfo = "—";
    public string HexInfo { get => _hexInfo; set => Set(ref _hexInfo, value); }
    private int _byteCount;
    public int ByteCount { get => _byteCount; set => Set(ref _byteCount, value); }

    private string _status = "Ready";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _counters = "";
    public string Counters { get => _counters; set => Set(ref _counters, value); }
    private string _lineStats = "";
    public string LineStats { get => _lineStats; set => Set(ref _lineStats, value); }

    private IReadOnlyList<ChartSeries>? _chart;
    public IReadOnlyList<ChartSeries>? Chart { get => _chart; set => Set(ref _chart, value); }
    public int ChartStart { get; private set; }
    /// <summary>Крок проріджування: точка графіка i = кадр ChartStart + i × ChartStep.</summary>
    public int ChartStep { get; private set; } = 1;
    public int ChartCursor => ChartStep > 0 ? (_currentIndex - ChartStart) / ChartStep : 0;
    /// <summary>Підпис осі часу графіка: час кадру під точкою.</summary>
    public Func<int, string> ChartXLabel => i =>
    {
        int f = ChartStart + i * ChartStep;
        return f >= 0 && f < _rec.Frames.Count ? _rec.Frames[f].Time.ToString("HH:mm:ss") : "";
    };

    /// <summary>Клік або перетягування мишею по графіку.</summary>
    public void SeekChart(int point) => CurrentIndex = ChartStart + point * ChartStep;

    // ── Огляд усього запису (вкладка Range): ті самі параметри, вибраний діапазон підсвічено ──
    private IReadOnlyList<ChartSeries>? _overview;
    public IReadOnlyList<ChartSeries>? Overview { get => _overview; private set => Set(ref _overview, value); }
    /// <summary>Крок проріджування огляду: точка i = кадр i × _overviewStep.</summary>
    private int _overviewStep = 1;
    public int OverviewCursor => _currentIndex < 0 ? -1 : _currentIndex / _overviewStep;
    public int OverviewSelStart => IsRangeFull ? -1 : RangeLo / _overviewStep;
    public int OverviewSelEnd => IsRangeFull ? -1 : RangeHi / _overviewStep;
    public Func<int, string> OverviewXLabel => i =>
    {
        int f = Math.Min(i * _overviewStep, _rec.Frames.Count - 1);
        return f >= 0 ? _rec.Frames[f].Time.ToString("HH:mm:ss") : "";
    };
    public bool IsRangeSet => !IsRangeFull;

    // ── Позначені кадри (зірочка): мітки на графіках і перехід між ними ──
    private IReadOnlyList<int>? _chartMarks, _overviewMarks;
    public IReadOnlyList<int>? ChartMarks { get => _chartMarks; private set => Set(ref _chartMarks, value); }
    public IReadOnlyList<int>? OverviewMarks { get => _overviewMarks; private set => Set(ref _overviewMarks, value); }
    private int _markCount;
    public int MarkCount { get => _markCount; private set { if (Set(ref _markCount, value)) OnPropertyChanged(nameof(HasMarks)); } }
    public bool HasMarks => _markCount > 0;
    /// <summary>Лічильник змін позначок — щоб не перераховувати мітки на кожному кадрі.</summary>
    private int _marksVersion;
    private string? _marksSig;

    private void UpdateMarks()
    {
        int n = _rec.Frames.Count;
        // Пороги перевірок із Setup теж у підписі — інакше знаки «!» не оновлюються, доки файл не відкрити знову
        string sig = $"{_marksVersion}:{n}:{ChartStart}:{ChartStep}:{RangeHi}:{_overviewStep}:{Settings.ShowShortO2Stuck}:{Settings.ShowShortIdlMismatch}";
        if (sig == _marksSig) return;
        _marksSig = sig;
        var marked = new List<int>();
        for (int i = 0; i < n; i++) if (_rec.Frames[i].Marked) marked.Add(i);
        MarkCount = marked.Count;
        int hi = RangeHi;
        ChartMarks = marked.Where(i => i >= ChartStart && i <= hi).Select(i => (i - ChartStart) / Math.Max(1, ChartStep)).ToList();
        OverviewMarks = marked.Select(i => i / _overviewStep).ToList();

        // Знаки «!» — спрацювання перевірок (перераховуються лише коли змінилися кадри)
        string alertSig = $"{_marksVersion}:{n}:{Settings.ShowShortO2Stuck}:{Settings.ShowShortIdlMismatch}";
        if (alertSig != _alertsSig)
        {
            _alertsSig = alertSig;
            _alerts = Diagnostics.Alerts(_rec.Frames, Settings);
        }
        int step = Math.Max(1, ChartStep);
        ChartAlerts = _alerts.Where(a => a.Span.End >= ChartStart && a.Span.Start <= hi)
            .Select(a => new ChartAlert((Math.Max(a.Span.Start, ChartStart) - ChartStart) / step, (Math.Min(a.Span.End, hi) - ChartStart) / step,
                a.Level == AlertLevel.Alarm, AlertTip(a))).ToList();
        OverviewAlerts = _alerts.Select(a => new ChartAlert(a.Span.Start / _overviewStep, a.Span.End / _overviewStep,
            a.Level == AlertLevel.Alarm, AlertTip(a))).ToList();
    }

    /// <summary>Текст підказки над знаком «!»: що спрацювало, кадри й час.</summary>
    private string AlertTip(Alert a)
    {
        var f = _rec.Frames;
        string frames = a.Span.Start == a.Span.End ? $"frame {a.Span.Start + 1}" : $"frames {a.Span.Start + 1}–{a.Span.End + 1}";
        return $"{a.Text}\n{frames}  ·  {f[a.Span.Start].Time:HH:mm:ss}–{f[a.Span.End].Time:HH:mm:ss}";
    }

    private List<Alert> _alerts = [];
    private string? _alertsSig;
    private IReadOnlyList<ChartAlert>? _chartAlerts, _overviewAlerts;
    /// <summary>Знаки «!» на графіку тренду й огляді: де спрацювали перевірки (лямбда, концевик ХХ, детонація).</summary>
    public IReadOnlyList<ChartAlert>? ChartAlerts { get => _chartAlerts; private set => Set(ref _chartAlerts, value); }
    public IReadOnlyList<ChartAlert>? OverviewAlerts { get => _overviewAlerts; private set => Set(ref _overviewAlerts, value); }

    /// <summary>Попередня / наступна позначка (по колу); поза діапазоном — діапазон скидається.</summary>
    private void GoToMark(int dir)
    {
        int n = _rec.Frames.Count;
        if (n == 0) return;
        int cur = Math.Max(_currentIndex, 0);
        for (int k = 1; k <= n; k++)
        {
            int i = ((cur + dir * k) % n + n) % n;
            if (!_rec.Frames[i].Marked) continue;
            if (!Range.Contains(i)) SetRange(FrameRange.All(n));
            Follow = false;
            CurrentIndex = i;
            return;
        }
    }

    /// <summary>Замість графіка тренду показано огляд усього запису з вибором діапазону.</summary>
    private bool _rangePanelOpen;
    public bool RangePanelOpen
    {
        get => _rangePanelOpen;
        set { if (Set(ref _rangePanelOpen, value)) OnPropertyChanged(nameof(TrendVisible)); }
    }
    public bool TrendVisible => !_rangePanelOpen;

    /// <summary>Клік по огляду: курсор; поза діапазоном — діапазон скидається, щоб кадр було видно.</summary>
    public void SeekOverview(int point)
    {
        int f = Math.Min(point * _overviewStep, _rec.Frames.Count - 1);
        if (f < 0) return;
        if (!Range.Contains(f)) SetRange(FrameRange.All(_rec.Frames.Count));
        CurrentIndex = f;
    }

    /// <summary>Перетягування по огляду — новий діапазон.</summary>
    public void SelectOverviewRange(int a, int b)
    {
        int n = _rec.Frames.Count;
        if (n == 0) return;
        // остання точка огляду покриває хвіст запису до кінця
        int last = b >= (n - 1) / _overviewStep ? n - 1 : Math.Min(b * _overviewStep, n - 1);
        SetRange(new FrameRange(Math.Min(a * _overviewStep, n - 1), last));
        ShowRangeTotals();
    }

    /// <summary>Shift + перетягування по графіку — новий діапазон.</summary>
    public void SelectChartRange(int a, int b)
    {
        int n = _rec.Frames.Count;
        if (n == 0) return;
        SetRange(new FrameRange(Math.Clamp(ChartStart + a * ChartStep, 0, n - 1), Math.Clamp(ChartStart + b * ChartStep, 0, n - 1)));
        ShowRangeTotals();
    }

    /// <summary>
    /// Після вибору діапазону мишею курсор — на останній кадр діапазону: підсумки поїздки (паливо, відстань,
    /// середня витрата) на плитках і в таблиці одразу показують увесь вибраний відрізок.
    /// </summary>
    private void ShowRangeTotals()
    {
        Follow = false;
        CurrentIndex = RangeHi;
    }

    public string Title => "JZ Monitor" + (_rec.Source != null ? " — " + Path.GetFileName(_rec.Source) : "") + (_dirty ? " •" : "");
    /// <summary>Назва файлу у верхньому рядку (стандартного заголовка вікна немає); • — є незбережені зміни.</summary>
    public string FileTitle => (_rec.Source != null ? Path.GetFileName(_rec.Source) : _rec.Frames.Count > 0 ? "Unsaved recording" : "") + (_dirty ? " •" : "");
    public string VehicleSummary
    {
        get
        {
            var s = string.Join("  ·  ", new[] { 0, 1, 2, 3 }.Select(i => _rec.Vehicle.Values[i]).Where(v => !string.IsNullOrWhiteSpace(v)));
            return s.Length > 0 ? s : "Toyota OBD-1 live data";
        }
    }

    #endregion

    private void ApplyAssemblerSettings()
    {
        lock (_asmLock)
        {
            _assembler.BitTimeMs = Settings.BitTimeMs > 1 ? Settings.BitTimeMs : 8.2;
            _assembler.GapBits = Math.Max(12, Settings.GapBits);
            _assembler.MinDataBytes = Math.Max(1, Settings.MinDataBytes);
        }
    }

    /// <summary>Зростає при зміні налаштувань — графік перебудовується з новими одиницями.</summary>
    private int _settingsVersion;

    private void SettingsChanged()
    {
        _settingsVersion++;
        Settings.Save();
        ApplyAssemblerSettings();
        RebuildParameters();
    }

    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in NativeComm.GetPortNames()) Ports.Add(p);
        if (Settings.Port == null || !Ports.Contains(Settings.Port)) SelectedPort = Ports.FirstOrDefault();
        else OnPropertyChanged(nameof(SelectedPort));
    }

    private void TryLoadDefaultVehicle()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var ini in new[] { Path.Combine(dir, "CarTester.ini"), Path.Combine(dir, "..", "CarTester.ini") })
        {
            try
            {
                if (File.Exists(ini)) { _rec.Vehicle = VehicleInfo.FromIni(ini); return; }
            }
            catch { }
        }
    }

    private void UpdateLinkState()
    {
        if (!IsRunning) { Link = LinkState.Offline; return; }
        var age = DateTime.Now - _lastValidFrame;
        Link = age < TimeSpan.FromSeconds(4) ? LinkState.Live : _badFrames > 0 ? LinkState.Errors : LinkState.Waiting;
    }

    #region Читання

    private void StartStop()
    {
        if (IsRunning) { Stop(); return; }
        if (string.IsNullOrEmpty(Settings.Port)) return;
        Start(new ComLineReader(Settings.Port, Settings.Invert, Settings.Dtr, Settings.Rts), Settings.Port);
    }

    private void Start(IEdgeSource src, string name)
    {
        if (!ConfirmDiscard("Connecting starts a new recording — the current one will be lost if you don't save it.")) { src.Dispose(); return; }
        ResetRecording();
        RawLog.Clear();
        lock (_asmLock) _assembler.Reset();
        _okFrames = _badFrames = 0;
        _lastValidFrame = DateTime.MinValue;
        if (RecordEdges)
        {
            var dlg = new SaveFileDialog { Filter = "Raw edge log (*.edges.csv)|*.edges.csv", FileName = $"edges_{DateTime.Now:yyyyMMdd_HHmmss}.edges.csv" };
            if (dlg.ShowDialog() == true) _edgeLog = new EdgeLogger(dlg.FileName);
        }
        src.EdgeReceived += OnEdge;
        src.Error += msg => _ui.BeginInvoke(() => { Status = "Error: " + msg; Stop(); });
        try
        {
            src.Start();
        }
        catch (Exception ex)
        {
            src.Dispose();
            _edgeLog?.Dispose();
            _edgeLog = null;
            MessageBox.Show(ex.Message, "Cannot open port", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _source = src;
        IsRunning = true;
        Follow = true;
        Status = $"Reading {name} — waiting for frame sync…";
    }

    private void OnEdge(Edge e)
    {
        _edgeLog?.Write(e);
        lock (_asmLock) _assembler.Push(e);
    }

    private void Stop()
    {
        _source?.Dispose();
        _source = null;
        _edgeLog?.Dispose();
        _edgeLog = null;
        IsRunning = false;
        Status = $"Stopped. {_rec.Frames.Count} frames recorded, {_badFrames} rejected.";
    }

    private void OnFrame(ObdFrame f)
    {
        double bit = _assembler.MeasuredBitMs, avg = _assembler.AvgJitterMs, max = _assembler.MaxJitterMs;
        _ui.BeginInvoke(() =>
        {
            AddRawLog(f);
            if (f.Valid)
            {
                if (_okFrames == 0) Status = "Receiving frames";
                _okFrames++;
                _lastValidFrame = DateTime.Now;
            }
            else _badFrames++;
            UpdateLinkState();
            LineStats = $"Bit {bit:0.00} ms  ·  jitter avg {avg:0.00} / max {max:0.00} ms" + (max > 2 ? "  ⚠ unstable timing" : "");
            Counters = $"{_okFrames} ok  ·  {_badFrames} rejected";
            if (!f.Valid) return;
            _rec.Frames.Add(f);
            if (_rangeEnd == null && _rec.Frames.Count - 1 >= _rangeStart) AccumulateStats(f);
            MarkDirty();
            RaiseRange();
            if (Follow) { _currentIndex = _rec.Frames.Count - 1; OnPropertyChanged(nameof(CurrentIndex)); ShowFrame(); }
            else { UpdateStatTexts(); ShowTrip(); UpdateChart(); }
        });
    }

    private void AddRawLog(ObdFrame f)
    {
        string line = $"{f.Time:HH:mm:ss.fff}  [{f.Bytes.Length,2}]  {f.HexDump}" + (f.Valid ? "" : "   ✗ " + f.Error);
        RawLog.Insert(0, line);
        while (RawLog.Count > 300) RawLog.RemoveAt(RawLog.Count - 1);
    }

    /// <summary>Програвання раніше записаного файлу сирих фронтів через декодер (з поточними налаштуваннями).</summary>
    private void ReplayEdges()
    {
        if (!ConfirmDiscard("Replaying an edge log replaces the current recording.")) return;
        var dlg = new OpenFileDialog { Filter = "Raw edge log (*.edges.csv)|*.edges.csv|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        ResetRecording();
        RawLog.Clear();
        var asm = new FrameAssembler { BitTimeMs = _assembler.BitTimeMs, GapBits = _assembler.GapBits, MinDataBytes = _assembler.MinDataBytes };
        int bad = 0;
        asm.FrameCompleted += f =>
        {
            AddRawLog(f);
            if (f.Valid) _rec.Frames.Add(f); else bad++;
        };
        foreach (var e in EdgeLogger.Read(dlg.FileName)) asm.Push(e);
        Status = $"Replayed {Path.GetFileName(dlg.FileName)}: {_rec.Frames.Count} frames, {bad} rejected, bit {asm.MeasuredBitMs:0.00} ms";
        AfterLoad();
    }

    #endregion

    #region Файли

    private void Open()
    {
        if (!ConfirmDiscard("Opening another file closes the current recording.")) return;
        var dlg = new OpenFileDialog { Filter = "CarTester log (*.ctr)|*.ctr|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        OpenFile(dlg.FileName);
    }

    /// <summary>Відкрити запис .ctr (з діалогу або з командного рядка).</summary>
    public void OpenFile(string path)
    {
        try
        {
            _rec = CtrFormat.Load(path);
            RawLog.Clear();
            _dirty = false;
            Status = $"Loaded {_rec.Frames.Count} frames from {Path.GetFileName(path)}";
            AfterLoad();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot open file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AfterLoad()
    {
        _marksVersion++;
        _rangeStart = 0;
        _rangeEnd = null;
        _chartSig = null;
        ChartStart = 0;
        RaiseRange();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FileTitle));
        OnPropertyChanged(nameof(VehicleSummary));
        _currentIndex = -1;
        CurrentIndex = 0;
        UpdateLinkState();
        RebuildParameters();
    }

    private bool Save()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CarTester log (*.ctr)|*.ctr",
            DefaultExt = ".ctr",
            FileName = _rec.Source != null ? Path.GetFileName(_rec.Source) : $"{DateTime.Now:yyyy-MM-dd_HH-mm}.ctr"
        };
        if (dlg.ShowDialog() != true) return false;
        try
        {
            CtrFormat.Save(dlg.FileName, _rec);
            _rec.Source = dlg.FileName;
            _dirty = false;
            OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FileTitle));
            int extra = _rec.Frames.Count(f => f.Bytes.Length > 12);
            Status = "Saved " + dlg.FileName + (extra > 0 ? $" — {extra} frame(s) had bytes 13+ that .ctr cannot hold (use CSV to keep them)" : "");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot save file", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog { Filter = "CSV for Excel (*.csv)|*.csv", DefaultExt = ".csv", FileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm}.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            RecordingIO.SaveCsv(dlg.FileName, _rec.Frames, Settings);
            Status = "Exported " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void New()
    {
        if (!ConfirmDiscard("A new recording clears the current one.")) return;
        ResetRecording();
        RawLog.Clear();
        Status = "New recording";
    }

    private void ResetRecording()
    {
        var vehicle = _rec.Vehicle;
        _rec = new LogRecording { Vehicle = vehicle };
        _rangeStart = 0;
        _rangeEnd = null;
        _chartSig = null;
        ChartStart = 0;
        _min.Clear();
        _max.Clear();
        _trip.Reset();
        _dirty = false;
        _currentIndex = -1;
        OnPropertyChanged(nameof(CurrentIndex));
        RaiseRange();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FileTitle));
        UpdateLinkState();
        ShowFrame();
    }

    private void ImportIni()
    {
        var dlg = new OpenFileDialog { Filter = "CarTester.ini|*.ini" };
        if (dlg.ShowDialog() != true) return;
        Settings.ImportCarTesterIni(dlg.FileName);
        try { _rec.Vehicle = VehicleInfo.FromIni(dlg.FileName); } catch { }
        Settings.Save();
        foreach (var p in new[] { nameof(InjectorFlowCcMin), nameof(InjectorCount), nameof(InjectionRate), nameof(VehicleSummary) })
            OnPropertyChanged(p);
        RebuildParameters();
        Status = "Settings imported from " + Path.GetFileName(dlg.FileName);
    }

    private void EditVehicle()
    {
        var w = new VehicleWindow(_rec) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true) return;
        MarkDirty();
        OnPropertyChanged(nameof(VehicleSummary));
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(FileTitle));
    }

    /// <summary>true — можна продовжувати (дані збережено або їх не шкода). reason — що станеться без збереження.</summary>
    public bool ConfirmDiscard(string reason)
    {
        if (!_dirty || _rec.Frames.Count == 0) return true;
        return SaveChangesDialog.Ask(_rec, reason) switch
        {
            SaveChoice.Save => Save(),
            SaveChoice.Discard => true,
            _ => false
        };
    }

    private void ToggleMark()
    {
        if (CurrentFrame is not { } f) return;
        f.Marked = !f.Marked;
        _marksVersion++;
        MarkDirty();
        ShowFrame();
    }

    private void DeleteFrame()
    {
        if (CurrentFrame is not { } f) return;
        int removed = _currentIndex;
        _rec.Frames.Remove(f);
        _marksVersion++;
        if (removed < _rangeStart) _rangeStart--;
        if (_rangeEnd is int e && removed <= e) _rangeEnd = e - 1 < _rangeStart ? null : e - 1;
        _chartSig = null;
        RecomputeStats();
        MarkDirty();
        RaiseRange();
        _currentIndex = Math.Min(_currentIndex, _rec.Frames.Count - 1);
        OnPropertyChanged(nameof(CurrentIndex));
        ShowFrame();
    }

    #endregion

    #region Відображення

    /// <summary>
    /// Параметри, які при заглушеному двигуні (RPM = 0) не мають сенсу — ECU видає службові значення
    /// (впорскування ~30 мс, кут −30°). Їх не показуємо й не рахуємо в MIN/MAX; решта параметрів реальні.
    /// </summary>
    private static readonly HashSet<string> RunningOnlyKeys = ["inj", "ign"];

    private static bool IsEngineOff(ObdFrame f) => f.B(4) is not > 0;

    private void AccumulateStats(ObdFrame f)
    {
        _trip.Add(f, Settings);
        bool off = IsEngineOff(f);
        foreach (var p in ParameterDecoder.Decode(f, Settings))
        {
            if (p.Key.StartsWith('f') || p.Value is not double v) continue;
            if (off && RunningOnlyKeys.Contains(p.Key)) continue;
            if (!_max.TryGetValue(p.Key, out var mx) || v > mx.Value) _max[p.Key] = p;
            if (!_min.TryGetValue(p.Key, out var mn) || v < mn.Value) _min[p.Key] = p;
        }
    }

    private void RecomputeStats()
    {
        _min.Clear();
        _max.Clear();
        _trip.Reset();
        if (_rec.Frames.Count == 0) return;
        var range = Range;
        for (int i = range.Start; i <= range.End; i++) AccumulateStats(_rec.Frames[i]);
    }

    private void UpdateStatTexts()
    {
        foreach (var row in Parameters)
        {
            row.MaxText = row.ShowMax && _max.TryGetValue(row.Key, out var mx) ? mx.Text : "";
            row.MinText = row.ShowMin && _min.TryGetValue(row.Key, out var mn) ? mn.Text : "";
        }
    }

    private void RebuildParameters()
    {
        RecomputeStats(); // одиниці/формули могли змінитися разом з налаштуваннями
        var charted = Parameters.Where(p => p.Charted).Select(p => p.Key).ToHashSet();
        if (Parameters.Count == 0) charted = ["rpm", "iac", "ect"];
        Parameters.Clear();
        Tiles.Clear();
        Flags.Clear();
        Analog.Clear();
        var sample = CurrentFrame ?? _rec.Frames.LastOrDefault() ?? new ObdFrame { Bytes = new byte[12] };
        int c = 0;
        foreach (var p in ParameterDecoder.Decode(sample, Settings))
        {
            if (p.HiddenInOriginal && !ShowHidden) continue;
            var row = new ParamRow
            {
                Key = p.Key, Name = p.Name, Unit = p.Unit, HiddenInOriginal = p.HiddenInOriginal,
                Color = Palette[c++ % Palette.Length]
            };
            row.Charted = charted.Contains(p.Key);
            row.ChartedChanged += UpdateChart;
            Parameters.Add(row);
            if (row.IsFlag) Flags.Add(row); else Analog.Add(row);
        }
        foreach (var (key, name, unit) in TripRows)
        {
            // Окремий колір (коричневий), щоб лінія середньої витрати не збігалася з параметрами з палітри
            var row = new ParamRow { Key = key, Name = name, Unit = unit, Color = Color.FromRgb(0x8D, 0x5A, 0x3C) };
            if (row.Chartable)
            {
                row.Charted = charted.Contains(key);
                row.ChartedChanged += UpdateChart;
            }
            Parameters.Add(row);
            Analog.Add(row);
        }
        foreach (var key in TileKeys)
            if (Parameters.FirstOrDefault(r => r.Key == key) is { } t) Tiles.Add(t);
        ShowFrame();
    }

    private static readonly (string Key, string Name, string Unit)[] TripRows =
    [
        ("trip_avg", "Trip average", "L/100km"),
        ("trip_fuel", "Trip fuel used", "L"),
        ("trip_dist", "Trip distance", "km"),
        ("trip_time", "Trip engine time", ""),
    ];

    /// <summary>Попередження на індикаторах бітів для поточного кадру.</summary>
    private void ShowFlagWarnings(ObdFrame f)
    {
        foreach (var row in Flags)
        {
            switch (row.Key)
            {
                case "f12.4":
                    // Лямбда в зворотному зв'язку не перемикається: від 20 с (з налаштуванням — від 10 с) — помаранчевий
                    double stuck = Diagnostics.O2StuckFor(_rec.Frames, _currentIndex);
                    row.Warning = stuck > 0 && stuck >= Diagnostics.O2Threshold(Settings) ? FlagWarning.Warn : FlagWarning.None;
                    row.WarningText = row.Warning == FlagWarning.None ? null
                        : $"O2 has been {(Diagnostics.O2Rich(f) ? "RICH" : "LEAN")} for {stuck:0} s in closed loop — it should switch several times a second";
                    break;
                case "f12.1":
                    // Концевик розімкнений при дроселі 0 довше за 5 с (з налаштуванням — 2 с)
                    double? idl = Diagnostics.IdlMismatchFor(_rec.Frames, _currentIndex);
                    row.Warning = idl > Diagnostics.IdlThreshold(Settings) ? FlagWarning.Warn : FlagWarning.None;
                    row.WarningText = row.Warning == FlagWarning.None ? null
                        : $"Idle switch is open while the throttle reads exactly 0 % for {idl:0} s — check the idle switch / throttle sensor adjustment";
                    break;
            }
        }
    }

    private void ShowTrip()
    {
        var t = _trip.At(_currentIndex - RangeLo);
        bool any = _currentIndex >= 0;
        foreach (var row in Parameters.Where(r => r.IsTrip))
        {
            row.Text = !any ? "—" : row.Key switch
            {
                "trip_fuel" => t.FuelL.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                "trip_dist" => t.DistanceKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                "trip_avg" => t.AverageL100 is double a ? a.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "—",
                _ => TimeSpan.FromSeconds(t.EngineSeconds).ToString(t.EngineSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
            };
            row.Raw = "";
            if (row.Key == "trip_avg")
            {
                // Плитка: шкала 0–40 л/100км; колір — до 10 зелений, далі плавно: 15 світло-зелений,
                // 20 жовтий, 25 помаранчевий, від 30 червоний
                double? avg = any ? t.AverageL100 : null;
                row.Fraction = avg is double a ? Math.Clamp(a / TripAvgTileMax, 0, 1) : 0;
                row.Accent = avg is double b ? HeatBrush(b, TripAvgOk, TripAvgLimit) : OkBrush;
            }
        }
    }

    private const double TripAvgTileMax = 40, TripAvgOk = 10, TripAvgLimit = 30;

    private void ShowFrame()
    {
        var f = CurrentFrame;
        if (f == null)
        {
            FrameNumber = "—";
            FrameTime = "";
            FrameMarked = false;
            EngineOff = false;
            IdInfo = HexInfo = "—";
            ByteCount = 0;
            foreach (var p in Parameters) { p.Text = "—"; p.Raw = ""; p.IsOn = false; p.Fraction = 0; p.Accent = AccentBrush; p.Warning = FlagWarning.None; }
            UpdateStatTexts();
            UpdateChart();
            return;
        }
        var values = ParameterDecoder.Decode(f, Settings).ToDictionary(p => p.Key);
        int extraShown = Parameters.Count(p => p.Key.StartsWith('b'));
        int extraNow = values.Keys.Count(k => k.StartsWith('b'));
        if (ShowHidden && extraShown != extraNow) { RebuildParameters(); return; }

        EngineOff = IsEngineOff(f);
        foreach (var row in Parameters)
        {
            if (row.IsTrip) continue;
            if (!values.TryGetValue(row.Key, out var v)) { row.Text = "—"; row.Raw = ""; row.IsOn = false; row.Fraction = 0; continue; }
            if (EngineOff && RunningOnlyKeys.Contains(row.Key))
            {
                row.Text = "—";
                row.Raw = v.Raw;
                row.Unit = v.Unit;
                row.Fraction = 0;
                continue;
            }
            row.Text = v.Text;
            row.Raw = v.Raw;
            row.Unit = v.Unit;
            row.IsOn = row.IsFlag && v.Value == 1;
            row.Fraction = TileFraction(f, row.Key, v);
            row.Accent = TileAccent(row.Key, v, values);
        }
        ShowFlagWarnings(f);
        ShowTrip();
        FrameNumber = $"{_currentIndex + 1} / {_rec.Frames.Count}";
        FrameTime = f.Time.ToString("dd.MM.yyyy  HH:mm:ss.fff");
        FrameMarked = f.Marked;
        IdInfo = $"{f.IdBitString}  (0x{f.IdValue:X3})";
        HexInfo = f.HexDump;
        ByteCount = f.Bytes.Length;
        UpdateStatTexts();
        UpdateChart();
    }

    /// <summary>
    /// Межі шкал на плитках (фізичні одиниці), підібрані під 2JZ-GTE. Ключ — параметр і одиниця,
    /// бо шкала залежить від режиму датчика. Що тут немає — шкала від сирого байта (0..255).
    /// </summary>
    private static readonly Dictionary<(string Key, string Unit), (double Min, double Max)> TileScales = new()
    {
        [("rpm", "rpm")] = (0, 8000),
        [("air", "bar")] = (-1, 1.5),
        [("ign", "°")] = (-10, 50),
        [("inj", "ms")] = (0, 20),
        [("iac", "steps")] = (0, 125), // повний хід ISCV — 125 кроків
        [("spd", "km/h")] = (0, 220),
    };

    private static double TileFraction(ObdFrame f, string key, ParamValue v)
    {
        if (TileScales.TryGetValue((key, v.Unit), out var sc) && v.Value is double x)
            return Math.Clamp((x - sc.Min) / (sc.Max - sc.Min), 0, 1);
        int n = Array.IndexOf(new[] { "inj", "ign", "iac", "rpm", "air", "ect", "tps", "spd" }, key) + 1;
        return n > 0 && f.B(n) is byte b ? b / 255.0 : 0;
    }

    /// <summary>
    /// Зони кольору смужок на плитках: до Warn — зелена (норма), від Warn — жовта (близько до межі),
    /// від Danger — червона. Пороги — орієнтовні для стокового 2JZ-GTE non-VVTi (не з протоколу).
    /// Ключ — параметр і одиниця, як у TileScales.
    /// </summary>
    private static readonly Dictionary<(string Key, string Unit), (double Warn, double Danger)> TileZones = new()
    {
        [("rpm", "rpm")] = (5500, 6500),  // відсічка ~7000
    };

    /// <summary>Плавна шкала кольору: зелений → світло-зелений → жовтий → помаранчевий → червоний.</summary>
    private static readonly Color[] HeatColors =
    [
        Color.FromRgb(0x18, 0xA8, 0x66), Color.FromRgb(0x8B, 0xC3, 0x4A), Color.FromRgb(0xE8, 0xA3, 0x17),
        Color.FromRgb(0xF0, 0x7E, 0x1F), Color.FromRgb(0xE5, 0x48, 0x4D),
    ];

    /// <summary>
    /// Колір між «норма» (ok) і «межа» (limit): до ok — зелений, від limit — червоний, між ними — плавно.
    /// limit може бути меншим за ok (для розрідження: що слабше, то гірше).
    /// </summary>
    private static Brush HeatBrush(double x, double ok, double limit)
    {
        double k = (x - ok) / (limit - ok);
        if (k <= 0) return OkBrush;
        if (k >= 1) return HotBrush;
        double pos = k * (HeatColors.Length - 1);
        int i = (int)pos;
        double t = pos - i;
        var (c0, c1) = (HeatColors[i], HeatColors[i + 1]);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Freeze(Color.FromRgb(Mix(c0.R, c1.R), Mix(c0.G, c1.G), Mix(c0.B, c1.B)));
    }

    // Пороги плавних шкал (орієнтовні для стокового 2JZ-GTE non-VVTi, не з протоколу)
    /// <summary>ОР, °C: норма прогрітого ~85–95, від 105 — перегрів.</summary>
    private const double EctOk = 95, EctLimit = 105;
    /// <summary>Наддув, bar: штатний ~0.6–0.7, близько 1.0 — відсічка палива штатним ECU.</summary>
    private const double BoostOk = 0.7, BoostLimit = 1.0;
    /// <summary>
    /// Розрідження на прогрітому холостому ходу, bar: у записах еталонного авто −0.63…−0.80, медіана ≈ −0.69;
    /// слабше за −0.5 — підсос повітря, клапани, запалювання.
    /// </summary>
    private const double IdleVacOk = -0.6, IdleVacLimit = -0.5;

    /// <summary>Скважність форсунок, %: від Warn — жовта, від Danger — червона (запас продуктивності).</summary>
    private const double InjDutyWarn = 80, InjDutyDanger = 90;

    /// <summary>
    /// Прогріті холості (двигун прогрітий, авто стоїть, дросель закритий) — у записах еталонного авто:
    /// впорскування 1.9–2.9 мс, кут 10–27°. Більше впорскування — ECU бачить більше повітря або додає паливо
    /// (підсос повітря, пропуски); кут поза межами — ECU сильно «ловить» оберти кутом або зрізає його.
    /// </summary>
    private const double IdleInjOk = 3.0, IdleInjLimit = 3.6;
    private const double IdleIgnLowOk = 10, IdleIgnLowLimit = 5, IdleIgnHighOk = 28, IdleIgnHighLimit = 34;

    /// <summary>Прогріті холості: оберти 400–999 (нижче — прокрутка стартером), ОР ≥ 80 °C, дросель закритий (IDL), авто стоїть.</summary>
    private static bool IsWarmIdle(IReadOnlyDictionary<string, ParamValue> values) =>
        values.TryGetValue("rpm", out var rpm) && rpm.Value is >= 400 and < 1000
        && values.TryGetValue("ect", out var ect) && ect.Value >= 80
        && values.TryGetValue("f12.1", out var idl) && idl.Value == 1
        && (!values.TryGetValue("spd", out var spd) || spd.Value is not > 0);

    /// <summary>Швидкість, км/год: від порогу — колір (зелений до 100).</summary>
    private static readonly (double From, Brush Brush)[] SpeedZones =
    [
        (200, HotBrush),
        (175, Freeze(Color.FromRgb(0xEA, 0x5B, 0x3A))), // червонуватий
        (160, OrangeBrush),
        (140, WarnBrush),
        (120, Freeze(Color.FromRgb(0xC0, 0xCA, 0x33))), // зелено-жовтий
        (100, Freeze(Color.FromRgb(0x8B, 0xC3, 0x4A))), // світло-зелений
    ];

    private static Brush TileAccent(string key, ParamValue v, IReadOnlyDictionary<string, ParamValue> values)
    {
        if (v.Value is not double x) return OkBrush;
        if (key == "spd")
        {
            foreach (var (from, brush) in SpeedZones)
                if (x > from) return brush;
            return OkBrush;
        }
        if (key == "ect" && v.Unit == "°C")
        {
            if (x < 60) return ColdBrush; // двигун ще не прогрітий
            return HeatBrush(x, EctOk, EctLimit);
        }
        // Холостий хід провалюється: нижче 600 — жовта, 575 — помаранчева, 550 — червона, 525 — темно-червона
        // (0 — двигун заглушено, це не провал)
        if (key == "rpm" && x > 0 && x < 600)
            return x < 525 ? DeepRedBrush : x < 550 ? HotBrush : x < 575 ? OrangeBrush : WarnBrush;
        if (key == "inj")
        {
            if (values.TryGetValue("rpm", out var rpm) && rpm.Value is > 0)
            {
                // Під навантаженням важливо, яку частку циклу (2 оберти) займає форсунка
                double duty = x / (120_000 / rpm.Value.Value) * 100;
                if (duty > InjDutyWarn) return HeatBrush(duty, InjDutyWarn, InjDutyDanger);
                if (IsWarmIdle(values)) return HeatBrush(x, IdleInjOk, IdleInjLimit);
            }
            return OkBrush;
        }
        if (key == "ign")
        {
            // Детонація: ECU зрізає кут — одразу червоний
            if (values.TryGetValue("f11.4", out var knock) && knock.Value == 1) return HotBrush;
            if (!IsWarmIdle(values)) return OkBrush;
            return x < IdleIgnLowOk ? HeatBrush(x, IdleIgnLowOk, IdleIgnLowLimit) : HeatBrush(x, IdleIgnHighOk, IdleIgnHighLimit);
        }
        if (key == "air" && v.Unit == "bar")
        {
            if (x > BoostOk) return HeatBrush(x, BoostOk, BoostLimit);
            // Розрідження оцінюємо лише на прогрітому холостому ходу
            return IsWarmIdle(values) ? HeatBrush(x, IdleVacOk, IdleVacLimit) : OkBrush;
        }
        if (TileZones.TryGetValue((key, v.Unit), out var z))
            return x >= z.Danger ? HotBrush : x >= z.Warn ? WarnBrush : OkBrush;
        return OkBrush;
    }

    /// <summary>
    /// Середня витрата для графіка — як у таблиці (з 0.1 км). На початку поїздки вона стрибає на сотні л/100км,
    /// тому шкала стала, 0–50 л/100км: більші значення впираються у верх, а не стискають решту лінії.
    /// </summary>
    private const double TripAvgChartMax = 50;
    private static double? ChartTripAvg(TripPoint t) => t.AverageL100;

    /// <summary>Скільки точок малює графік щонайбільше; довші діапазони проріджуються.</summary>
    private const int MaxChartPoints = 1500;
    /// <summary>Підпис побудованих серій: якщо не змінився — перебудова не потрібна, рухається лише курсор.</summary>
    private string? _chartSig;

    private void UpdateChart()
    {
        var keys = Parameters.Where(p => p.Charted).ToList();
        int n = _rec.Frames.Count;
        UpdateOverview(keys);
        if (keys.Count == 0 || n == 0) { Chart = null; _chartSig = null; UpdateMarks(); return; }

        // Графік показує весь діапазон перегляду; задовгий — через кожен step-й кадр
        int start = RangeLo, last = RangeHi;
        int step = Math.Max(1, (int)Math.Ceiling((last - start + 1) / (double)MaxChartPoints));

        string sig = $"{start}:{last}:{step}:{_settingsVersion}:{string.Join(",", keys.Select(k => k.Key))}";
        if (sig == _chartSig && Chart != null) { OnPropertyChanged(nameof(ChartCursor)); UpdateMarks(); return; }
        _chartSig = sig;

        var frames = Enumerable.Range(0, (last - start) / step + 1)
            .Select(i => ParameterDecoder.Decode(_rec.Frames[start + i * step], Settings).ToDictionary(p => p.Key)).ToList();
        ChartStart = start;
        ChartStep = step;
        OnPropertyChanged(nameof(ChartXLabel));
        Chart = keys.Select(k => new ChartSeries
        {
            Name = k.Name,
            // одиницю, що дублює назву (RPM / rpm), не показуємо
            Unit = k.Unit.Equals(k.Name, StringComparison.OrdinalIgnoreCase) ? "" : k.Unit,
            Color = k.Color,
            // Стала шкала — MIN/MAX діапазону перегляду (середня витрата — 0–50 л/100км)
            Min = k.Key == "trip_avg" ? 0 : _min.TryGetValue(k.Key, out var mn) ? mn.Value : null,
            Max = k.Key == "trip_avg" ? TripAvgChartMax : _max.TryGetValue(k.Key, out var mx) ? mx.Value : null,
            // Середня витрата — з підсумків поїздки діапазону (точка i = кадр RangeLo + i × step)
            Values = k.Key == "trip_avg"
                ? Enumerable.Range(0, frames.Count).Select(i => ChartTripAvg(_trip.At(i * step))).ToArray()
                // Впорскування й кут при заглушеному двигуні не мають сенсу — розрив лінії
                : frames.Select(d => d.TryGetValue(k.Key, out var v)
                    && !(RunningOnlyKeys.Contains(k.Key) && d.TryGetValue("rpm", out var r) && r.Value == 0) ? v.Value : null).ToArray()
        }).ToList();
        OnPropertyChanged(nameof(ChartCursor));
        UpdateMarks();
    }

    /// <summary>Скільки точок щонайбільше має огляд усього запису.</summary>
    private const int MaxOverviewPoints = 1200;
    private string? _overviewSig;

    private void UpdateOverview(List<ParamRow> keys)
    {
        int n = _rec.Frames.Count;
        OnPropertyChanged(nameof(OverviewCursor));
        if (keys.Count == 0 || n == 0) { Overview = null; _overviewSig = null; return; }
        int step = Math.Max(1, (int)Math.Ceiling(n / (double)MaxOverviewPoints));
        string sig = $"{n}:{step}:{_settingsVersion}:{string.Join(",", keys.Select(k => k.Key))}";
        if (sig == _overviewSig && Overview != null) return;
        _overviewSig = sig;
        _overviewStep = step;

        var frames = Enumerable.Range(0, (n - 1) / step + 1)
            .Select(i => ParameterDecoder.Decode(_rec.Frames[i * step], Settings).ToDictionary(p => p.Key)).ToList();
        // Середня витрата на огляді — за весь запис від його початку (діапазон тут ще не вибрано)
        TripMeter? wholeTrip = null;
        if (keys.Any(k => k.Key == "trip_avg"))
        {
            wholeTrip = new TripMeter();
            foreach (var f in _rec.Frames) wholeTrip.Add(f, Settings);
        }
        // Шкала огляду — за всім записом (а не за діапазоном), тому Min/Max не задаються
        Overview = keys.Select(k => new ChartSeries
        {
            Name = k.Name,
            Unit = k.Unit.Equals(k.Name, StringComparison.OrdinalIgnoreCase) ? "" : k.Unit,
            Color = k.Color,
            Min = k.Key == "trip_avg" ? 0 : null,
            Max = k.Key == "trip_avg" ? TripAvgChartMax : null,
            Values = k.Key == "trip_avg" && wholeTrip != null
                ? Enumerable.Range(0, frames.Count).Select(i => ChartTripAvg(wholeTrip.At(i * step))).ToArray()
                : frames.Select(d => d.TryGetValue(k.Key, out var v)
                    && !(RunningOnlyKeys.Contains(k.Key) && d.TryGetValue("rpm", out var r) && r.Value == 0) ? v.Value : null).ToArray()
        }).ToList();
        OnPropertyChanged(nameof(OverviewXLabel));
        OnPropertyChanged(nameof(OverviewCursor));
        OnPropertyChanged(nameof(OverviewSelStart));
        OnPropertyChanged(nameof(OverviewSelEnd));
    }

    private void RaiseRange()
    {
        OnPropertyChanged(nameof(MaxIndex));
        OnPropertyChanged(nameof(RangeLo));
        OnPropertyChanged(nameof(RangeHi));
        OnPropertyChanged(nameof(IsRangeSet));
        OnPropertyChanged(nameof(OverviewSelStart));
        OnPropertyChanged(nameof(OverviewSelEnd));
        UpdateRangeTexts();
    }

    private void UpdateRangeTexts()
    {
        var frames = _rec.Frames;
        if (frames.Count == 0)
        {
            RangeInfo = "";
            return;
        }
        var range = Range;
        var d = range.Duration(frames);
        RangeInfo = $"{range.Count:N0} of {frames.Count:N0} frames  ·  {(d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))}"
                    + (IsRangeFull ? "" : "  ·  range");
        OnPropertyChanged(nameof(IsRangeFull));
    }

    /// <summary>Застосувати діапазон перегляду: графік, повзунок, MIN/MAX і підсумки поїздки — лише в його межах.</summary>
    private void SetRange(FrameRange range)
    {
        int n = _rec.Frames.Count;
        if (n == 0) return;
        _rangeStart = Math.Clamp(range.Start, 0, n - 1);
        // Діапазон до останнього кадру лишається «відкритим» — під час читання росте з записом
        _rangeEnd = range.End >= n - 1 ? null : Math.Max(range.End, _rangeStart);
        if (!IsRangeFull) Follow = false;
        _chartSig = null;
        RecomputeStats();
        RaiseRange();
        int cur = _currentIndex;
        _currentIndex = -2; // змусити оновлення навіть якщо індекс не змінився
        CurrentIndex = Range.Contains(cur) ? cur : RangeLo;
        CommandManager.InvalidateRequerySuggested(); // Full / Prev / Next — одразу, без очікування введення
        Status = IsRangeFull ? "Showing the whole recording" : $"Range: frames {RangeLo + 1}–{RangeHi + 1}";
    }

    /// <summary>Звіт по діапазону перегляду (або всьому запису) з порівнянням з еталоном.</summary>
    private void OpenReport()
    {
        var w = new ReportWindow(_rec, Range, Settings, JumpTo) { Owner = Application.Current.MainWindow };
        w.Show();
    }

    /// <summary>Перейти до кадру зі звіту: поза діапазоном — діапазон скидається, щоб кадр було видно.</summary>
    private void JumpTo(int index)
    {
        int n = _rec.Frames.Count;
        if (index < 0 || index >= n) return;
        if (!Range.Contains(index)) SetRange(FrameRange.All(n));
        Follow = false;
        CurrentIndex = index;
    }

    private void OpenCompare()
    {
        var w = new CompareWindow(_rec, Settings, Range) { Owner = Application.Current.MainWindow };
        w.Show();
    }

    #endregion

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void Dispose()
    {
        _linkTimer.Stop();
        _source?.Dispose();
        _edgeLog?.Dispose();
        Settings.Save();
    }
}
