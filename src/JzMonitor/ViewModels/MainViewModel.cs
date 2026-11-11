using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using JzMonitor.Controls;
using JzMonitor.Protocol;
using JzMonitor.Recording;
using Microsoft.Win32;

namespace JzMonitor.ViewModels;

public enum LinkState { Offline, Waiting, Live, Errors }

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
    public bool Chartable => !IsTrip;

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
    private static readonly string[] TileKeys = ["rpm", "ect", "air", "inj", "ign", "tps", "iac", "spd"];

    private static readonly Brush AccentBrush = Freeze(Color.FromRgb(0xF2, 0x6B, 0x1D));
    private static readonly Brush ColdBrush = Freeze(Color.FromRgb(0x1E, 0x88, 0xE5));
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x18, 0xA8, 0x66));
    private static readonly Brush HotBrush = Freeze(Color.FromRgb(0xE5, 0x48, 0x4D));

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
    /// <summary>Мінімуми й максимуми параметрів за поточний запис (лише кадри з працюючим двигуном).</summary>
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
        DeleteFrameCommand = new RelayCommand(DeleteFrame, () => !IsRunning && CurrentFrame != null);
        VehicleCommand = new RelayCommand(EditVehicle);
        PrevCommand = new RelayCommand(() => CurrentIndex--, () => _currentIndex > 0);
        NextCommand = new RelayCommand(() => CurrentIndex++, () => _currentIndex < _rec.Frames.Count - 1);
        LatestCommand = new RelayCommand(() => { Follow = true; CurrentIndex = _rec.Frames.Count - 1; }, () => _rec.Frames.Count > 0);

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
    public RelayCommand DeleteFrameCommand { get; }
    public RelayCommand VehicleCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand LatestCommand { get; }

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
            value = Math.Clamp(value, -1, _rec.Frames.Count - 1);
            if (!Set(ref _currentIndex, value)) return;
            if (IsRunning && value < _rec.Frames.Count - 1) Follow = false;
            ShowFrame();
        }
    }
    public int MaxIndex => Math.Max(0, _rec.Frames.Count - 1);
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
    public int ChartCursor => _currentIndex - ChartStart;

    public string Title => "JZ Monitor" + (_rec.Source != null ? " — " + Path.GetFileName(_rec.Source) : "") + (_dirty ? " •" : "");
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
            AccumulateStats(f);
            MarkDirty();
            OnPropertyChanged(nameof(MaxIndex));
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
        _chartSig = null;
        ChartStart = 0;
        OnPropertyChanged(nameof(MaxIndex));
        OnPropertyChanged(nameof(Title));
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
        _chartSig = null;
        ChartStart = 0;
        _min.Clear();
        _max.Clear();
        _trip.Reset();
        _dirty = false;
        _currentIndex = -1;
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(MaxIndex));
        OnPropertyChanged(nameof(Title));
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
        MarkDirty();
        ShowFrame();
    }

    private void DeleteFrame()
    {
        if (CurrentFrame is not { } f) return;
        _rec.Frames.Remove(f);
        _chartSig = null;
        RecomputeStats();
        MarkDirty();
        OnPropertyChanged(nameof(MaxIndex));
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
        foreach (var f in _rec.Frames) AccumulateStats(f);
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
            var row = new ParamRow { Key = key, Name = name, Unit = unit };
            Parameters.Add(row);
            Analog.Add(row);
        }
        foreach (var key in TileKeys)
            if (Parameters.FirstOrDefault(r => r.Key == key) is { } t) Tiles.Add(t);
        ShowFrame();
    }

    private static readonly (string Key, string Name, string Unit)[] TripRows =
    [
        ("trip_fuel", "Trip fuel used", "L"),
        ("trip_dist", "Trip distance", "km"),
        ("trip_avg", "Trip average", "L/100km"),
        ("trip_time", "Trip engine time", ""),
    ];

    private void ShowTrip()
    {
        var t = _trip.At(_currentIndex);
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
        }
    }

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
            foreach (var p in Parameters) { p.Text = "—"; p.Raw = ""; p.IsOn = false; p.Fraction = 0; p.Accent = AccentBrush; }
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
            row.Accent = TileAccent(row.Key, v.Value);
        }
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
    };

    private static double TileFraction(ObdFrame f, string key, ParamValue v)
    {
        if (TileScales.TryGetValue((key, v.Unit), out var sc) && v.Value is double x)
            return Math.Clamp((x - sc.Min) / (sc.Max - sc.Min), 0, 1);
        int n = Array.IndexOf(new[] { "inj", "ign", "iac", "rpm", "air", "ect", "tps", "spd" }, key) + 1;
        return n > 0 && f.B(n) is byte b ? b / 255.0 : 0;
    }

    private static Brush TileAccent(string key, double? value) => key switch
    {
        "ect" when value < 60 => ColdBrush,
        "ect" when value > 105 => HotBrush,
        "ect" => OkBrush,
        "rpm" when value > 6500 => HotBrush,
        _ => AccentBrush
    };

    /// <summary>Скільки кадрів показує графік одночасно (для швидкодії).</summary>
    private const int ChartWindow = 600;
    /// <summary>Підпис побудованих серій: якщо не змінився — перебудова не потрібна, рухається лише курсор.</summary>
    private string? _chartSig;

    private void UpdateChart()
    {
        var keys = Parameters.Where(p => p.Charted).ToList();
        int n = _rec.Frames.Count;
        if (keys.Count == 0 || n == 0) { Chart = null; _chartSig = null; return; }

        // Вікно стоїть на місці, доки поточний кадр у його межах, — інакше під час перетягування
        // мишею дані «їхали б» під курсором. Під час живого читання — останні кадри справа.
        int start = ChartStart;
        if (n <= ChartWindow) start = 0;
        else if (IsRunning && Follow) start = n - ChartWindow;
        else
        {
            // Мінімальний зсув: вікно «підтягується» рівно настільки, щоб поточний кадр був видимим —
            // так прокрутка плавна, без перескоку на пів вікна.
            if (_currentIndex < start) start = _currentIndex;
            else if (_currentIndex >= start + ChartWindow) start = _currentIndex - ChartWindow + 1;
            start = Math.Clamp(start, 0, n - ChartWindow);
        }
        int end = Math.Min(n, start + ChartWindow);

        string sig = $"{start}:{end}:{_settingsVersion}:{string.Join(",", keys.Select(k => k.Key))}";
        if (sig == _chartSig && Chart != null) { OnPropertyChanged(nameof(ChartCursor)); return; }
        _chartSig = sig;

        var frames = _rec.Frames.Skip(start).Take(end - start).Select(f => ParameterDecoder.Decode(f, Settings).ToDictionary(p => p.Key)).ToList();
        ChartStart = start;
        Chart = keys.Select(k => new ChartSeries
        {
            Name = k.Name,
            // одиницю, що дублює назву (RPM / rpm), не показуємо
            Unit = k.Unit.Equals(k.Name, StringComparison.OrdinalIgnoreCase) ? "" : k.Unit,
            Color = k.Color,
            // Стала шкала — MIN/MAX усього запису: під час прокрутки лінії не стрибають по вертикалі
            Min = _min.TryGetValue(k.Key, out var mn) ? mn.Value : null,
            Max = _max.TryGetValue(k.Key, out var mx) ? mx.Value : null,
            // Впорскування й кут при заглушеному двигуні не мають сенсу — розрив лінії
            Values = frames.Select(d => d.TryGetValue(k.Key, out var v)
                && !(RunningOnlyKeys.Contains(k.Key) && d.TryGetValue("rpm", out var r) && r.Value == 0) ? v.Value : null).ToArray()
        }).ToList();
        OnPropertyChanged(nameof(ChartCursor));
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
