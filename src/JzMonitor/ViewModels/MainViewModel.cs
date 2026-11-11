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

public sealed class ParamRow : ObservableObject
{
    private string _text = "";
    private string _raw = "";
    private bool _charted;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Unit { get; set; } = "";
    public bool HiddenInOriginal { get; init; }
    public bool IsFlag => Key.StartsWith('f');
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Raw { get => _raw; set => Set(ref _raw, value); }
    public bool Charted { get => _charted; set { if (Set(ref _charted, value)) ChartedChanged?.Invoke(); } }
    public Color Color { get; set; }
    public Brush ColorBrush => new SolidColorBrush(Color);
    public event Action? ChartedChanged;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0x81, 0xC7, 0x84),
        Color.FromRgb(0xE5, 0x73, 0x73), Color.FromRgb(0xBA, 0x68, 0xC8), Color.FromRgb(0xFF, 0xF1, 0x76),
        Color.FromRgb(0x4D, 0xD0, 0xE1), Color.FromRgb(0xF0, 0x62, 0x92)
    ];

    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly FrameAssembler _assembler = new();
    private IEdgeSource? _source;
    private EdgeLogger? _edgeLog;
    private CtrRecording _rec = new();
    private bool _dirty;

    public DecoderSettings Settings { get; }
    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<ParamRow> Parameters { get; } = new();
    public ObservableCollection<string> RawLog { get; } = new();
    public string[] AirModes { get; } = ["Сирий байт", "MAP, кПа", "MAP, мм рт.ст.", "MAP турбо, кПа", "MAP турбо, мм рт.ст.", "MAF, г/с", "VAF, В"];
    public string[] IacModes { get; } = ["Сирий байт", "% від 255", "Кроки"];
    public string[] ThrottleModes { get; } = ["Сирий байт", "Градуси", "%"];
    public string[] AfModes { get; } = ["Сирий байт", "Вольти", "%"];
    public string[] LineModes { get; } = ["DSR (як оригінал)", "CTS"];

    public MainViewModel()
    {
        Settings = DecoderSettings.Load();
        ApplyAssemblerSettings();
        _assembler.FrameCompleted += OnFrame;
        RefreshPorts();
        TryLoadDefaultVehicle();

        RefreshPortsCommand = new RelayCommand(RefreshPorts, () => !IsRunning);
        StartCommand = new RelayCommand(StartStop, () => IsRunning || !string.IsNullOrEmpty(Settings.Port));
        DemoCommand = new RelayCommand(StartDemo, () => !IsRunning);
        OpenCommand = new RelayCommand(Open, () => !IsRunning);
        SaveCommand = new RelayCommand(() => SaveCtr(), () => _rec.Frames.Count > 0);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _rec.Frames.Count > 0);
        NewCommand = new RelayCommand(New, () => !IsRunning);
        ImportIniCommand = new RelayCommand(ImportIni);
        ReplayEdgesCommand = new RelayCommand(ReplayEdges, () => !IsRunning);
        ToggleMarkCommand = new RelayCommand(ToggleMark, () => CurrentFrame != null);
        DeleteFrameCommand = new RelayCommand(DeleteFrame, () => !IsRunning && CurrentFrame != null);
        VehicleCommand = new RelayCommand(EditVehicle);
    }

    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand DemoCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand ImportIniCommand { get; }
    public RelayCommand ReplayEdgesCommand { get; }
    public RelayCommand ToggleMarkCommand { get; }
    public RelayCommand DeleteFrameCommand { get; }
    public RelayCommand VehicleCommand { get; }

    #region Властивості для прив'язки

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(StartText)); }
    }
    public string StartText => IsRunning ? "■ Стоп" : "▶ Читати";

    public string? SelectedPort
    {
        get => Settings.Port;
        set { Settings.Port = value; OnPropertyChanged(); Settings.Save(); }
    }

    public int AirIndex { get => (int)Settings.Air; set { Settings.Air = (AirSensor)value; SettingsChanged(); } }
    public int IacIndex { get => (int)Settings.Iac; set { Settings.Iac = (IacMode)value; SettingsChanged(); } }
    public int ThrottleIndex { get => (int)Settings.Throttle; set { Settings.Throttle = (ThrottleMode)value; SettingsChanged(); } }
    public int AfIndex { get => (int)Settings.Af; set { Settings.Af = (AfMode)value; SettingsChanged(); } }
    public int LineIndex { get => (int)Settings.Line; set { Settings.Line = (InputLine)value; SettingsChanged(); } }
    public bool CoolantDirect { get => Settings.CoolantDirect; set { Settings.CoolantDirect = value; SettingsChanged(); } }
    public bool Invert { get => Settings.Invert; set { Settings.Invert = value; SettingsChanged(); } }
    public bool Dtr { get => Settings.Dtr; set { Settings.Dtr = value; SettingsChanged(); } }
    public bool Rts { get => Settings.Rts; set { Settings.Rts = value; SettingsChanged(); } }
    public int InjectorFlow { get => Settings.InjectorFlow; set { Settings.InjectorFlow = value; SettingsChanged(); } }
    public int InjectorCount { get => Settings.InjectorCount; set { Settings.InjectorCount = value; SettingsChanged(); } }
    public int InjectionRate { get => Settings.InjectionRate; set { Settings.InjectionRate = value; SettingsChanged(); } }
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

    private string _frameInfo = "Немає даних";
    public string FrameInfo { get => _frameInfo; set => Set(ref _frameInfo, value); }
    private string _idInfo = "";
    public string IdInfo { get => _idInfo; set => Set(ref _idInfo, value); }
    private string _hexInfo = "";
    public string HexInfo { get => _hexInfo; set => Set(ref _hexInfo, value); }
    private string _status = "Готово";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _lineStats = "";
    public string LineStats { get => _lineStats; set => Set(ref _lineStats, value); }
    private bool _linkOk;
    public bool LinkOk { get => _linkOk; set => Set(ref _linkOk, value); }

    private IReadOnlyList<ChartSeries>? _chart;
    public IReadOnlyList<ChartSeries>? Chart { get => _chart; set => Set(ref _chart, value); }

    public string Title => "JZ Monitor — Toyota OBD-1" + (_rec.Source != null ? " — " + Path.GetFileName(_rec.Source) : "") + (_dirty ? " *" : "");

    #endregion

    private int _okFrames, _badFrames;

    private void ApplyAssemblerSettings()
    {
        _assembler.BitTimeMs = Settings.BitTimeMs > 1 ? Settings.BitTimeMs : 8.2;
        _assembler.GapBits = Math.Max(12, Settings.GapBits);
        _assembler.MinDataBytes = Math.Max(1, Settings.MinDataBytes);
    }

    private void SettingsChanged()
    {
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
        var ini = Path.Combine(AppContext.BaseDirectory, "CarTester.ini");
        if (!File.Exists(ini)) ini = Path.Combine(Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? "", "CarTester.ini");
        try { if (File.Exists(ini)) _rec.Vehicle = VehicleInfo.FromIni(ini); } catch { }
    }

    #region Читання

    private void StartStop()
    {
        if (IsRunning) { Stop(); return; }
        if (string.IsNullOrEmpty(Settings.Port)) return;
        Start(new ComLineReader(Settings.Port, Settings.Line, Settings.Invert, Settings.Dtr, Settings.Rts), Settings.Port);
    }

    private void StartDemo() => Start(new SimulatedSource(Settings.BitTimeMs), "Демо");

    private void Start(IEdgeSource src, string name)
    {
        if (!ConfirmDiscard()) return;
        ResetRecording();
        _assembler.Reset();
        _okFrames = _badFrames = 0;
        if (RecordEdges)
        {
            var dlg = new SaveFileDialog { Filter = "Сирі фронти (*.edges.csv)|*.edges.csv", FileName = $"edges_{DateTime.Now:yyyyMMdd_HHmmss}.edges.csv" };
            if (dlg.ShowDialog() == true) _edgeLog = new EdgeLogger(dlg.FileName);
        }
        src.EdgeReceived += OnEdge;
        src.Error += msg => _ui.BeginInvoke(() => { Status = "Помилка: " + msg; Stop(); });
        try
        {
            src.Start();
        }
        catch (Exception ex)
        {
            src.Dispose();
            _edgeLog?.Dispose();
            _edgeLog = null;
            MessageBox.Show(ex.Message, "Помилка відкриття порту", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _source = src;
        IsRunning = true;
        Follow = true;
        Status = $"Читання: {name}. Очікування синхронізації…";
    }

    private readonly object _asmLock = new();

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
        LinkOk = false;
        Status = $"Зупинено. Кадрів: {_rec.Frames.Count(f => f.Valid)}, помилкових: {_badFrames}";
    }

    private void OnFrame(ObdFrame f)
    {
        double bit = _assembler.MeasuredBitMs, avg = _assembler.AvgJitterMs, max = _assembler.MaxJitterMs;
        _ui.BeginInvoke(() =>
        {
            AddRawLog(f);
            if (f.Valid) _okFrames++; else _badFrames++;
            LinkOk = f.Valid;
            LineStats = $"Біт: {bit:0.00} мс | відхилення фронтів: сер. {avg:0.00}, макс. {max:0.00} мс" +
                        (max > 2 ? " — можливі збої (оригінал: >2 мс)" : "");
            Status = $"OK: {_okFrames}  помилок: {_badFrames}  байтів у кадрі: {f.Bytes.Length}";
            if (!f.Valid) return;
            _rec.Frames.Add(f);
            MarkDirty();
            OnPropertyChanged(nameof(MaxIndex));
            if (Follow) { _currentIndex = _rec.Frames.Count - 1; OnPropertyChanged(nameof(CurrentIndex)); ShowFrame(); }
            else UpdateChart();
        });
    }

    private void AddRawLog(ObdFrame f)
    {
        string line = $"{f.Time:HH:mm:ss}  ID {f.IdBitString}  [{f.Bytes.Length,2}]  {f.HexDump}" + (f.Valid ? "" : "   ✗ " + f.Error);
        RawLog.Insert(0, line);
        while (RawLog.Count > 300) RawLog.RemoveAt(RawLog.Count - 1);
    }

    /// <summary>Програвання раніше записаного файлу сирих фронтів через декодер (з поточними налаштуваннями).</summary>
    private void ReplayEdges()
    {
        var dlg = new OpenFileDialog { Filter = "Сирі фронти (*.edges.csv)|*.edges.csv|Усі файли|*.*" };
        if (dlg.ShowDialog() != true || !ConfirmDiscard()) return;
        ResetRecording();
        var asm = new FrameAssembler { BitTimeMs = _assembler.BitTimeMs, GapBits = _assembler.GapBits, MinDataBytes = _assembler.MinDataBytes };
        int bad = 0;
        var t = File.GetLastWriteTime(dlg.FileName);
        asm.FrameCompleted += f =>
        {
            AddRawLog(f);
            if (f.Valid) _rec.Frames.Add(f); else bad++;
        };
        foreach (var e in EdgeLogger.Read(dlg.FileName)) asm.Push(e);
        Status = $"Фронти з {Path.GetFileName(dlg.FileName)}: кадрів {_rec.Frames.Count}, помилкових {bad}; біт {asm.MeasuredBitMs:0.00} мс";
        AfterLoad();
    }

    #endregion

    #region Файли

    private void Open()
    {
        var dlg = new OpenFileDialog { Filter = "Car Tester (*.ctr)|*.ctr|Усі файли|*.*" };
        if (dlg.ShowDialog() != true || !ConfirmDiscard()) return;
        try
        {
            _rec = RecordingIO.LoadCtr(dlg.FileName);
            RawLog.Clear();
            Status = $"Завантажено {_rec.Frames.Count} кадрів";
            _dirty = false;
            AfterLoad();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Помилка завантаження", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AfterLoad()
    {
        OnPropertyChanged(nameof(MaxIndex));
        OnPropertyChanged(nameof(Title));
        _currentIndex = -1;
        CurrentIndex = 0;
        RebuildParameters();
    }

    private bool SaveCtr()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Car Tester (*.ctr)|*.ctr",
            DefaultExt = ".ctr",
            FileName = _rec.Source != null && _rec.Source.EndsWith(".ctr", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(_rec.Source) : $"{DateTime.Now:yyyy-MM-dd_HH-mm}.ctr"
        };
        if (dlg.ShowDialog() != true) return false;
        try
        {
            RecordingIO.SaveCtr(dlg.FileName, _rec);
            _rec.Source = dlg.FileName;
            _dirty = false;
            OnPropertyChanged(nameof(Title));
            int extra = _rec.Frames.Count(f => f.Bytes.Length > 12);
            Status = $"Збережено {dlg.FileName}" + (extra > 0 ? $". Увага: {extra} кадр(ів) мали байти 13+, у .ctr вони не вміщуються — збережіть ще й CSV." : "");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Помилка запису", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ExportCsv()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (Excel)|*.csv", DefaultExt = ".csv", FileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm}.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            RecordingIO.SaveCsv(dlg.FileName, _rec.Frames, Settings);
            Status = "Експортовано " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Помилка запису", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void New()
    {
        if (!ConfirmDiscard()) return;
        ResetRecording();
        RawLog.Clear();
        Status = "Новий запис";
    }

    private void ResetRecording()
    {
        var vehicle = _rec.Vehicle;
        _rec = new CtrRecording { Vehicle = vehicle };
        _dirty = false;
        _currentIndex = -1;
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(MaxIndex));
        OnPropertyChanged(nameof(Title));
        ShowFrame();
    }

    private void ImportIni()
    {
        var dlg = new OpenFileDialog { Filter = "CarTester.ini|*.ini" };
        if (dlg.ShowDialog() != true) return;
        Settings.ImportCarTesterIni(dlg.FileName);
        try { _rec.Vehicle = VehicleInfo.FromIni(dlg.FileName); } catch { }
        Settings.Save();
        foreach (var p in new[] { nameof(AirIndex), nameof(IacIndex), nameof(ThrottleIndex), nameof(AfIndex), nameof(CoolantDirect),
                     nameof(InjectorFlow), nameof(InjectorCount), nameof(InjectionRate), nameof(Dtr) })
            OnPropertyChanged(p);
        RebuildParameters();
        Status = "Налаштування імпортовано з " + Path.GetFileName(dlg.FileName);
    }

    private void EditVehicle()
    {
        var w = new VehicleWindow(_rec) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() == true) MarkDirty();
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>true — можна продовжувати (дані збережено або їх не шкода).</summary>
    public bool ConfirmDiscard()
    {
        if (!_dirty || _rec.Frames.Count == 0) return true;
        var r = MessageBox.Show("Дані не збережено. Зберегти у файл .ctr?", "Увага", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return r switch
        {
            MessageBoxResult.Yes => SaveCtr(),
            MessageBoxResult.No => true,
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
        MarkDirty();
        OnPropertyChanged(nameof(MaxIndex));
        _currentIndex = Math.Min(_currentIndex, _rec.Frames.Count - 1);
        OnPropertyChanged(nameof(CurrentIndex));
        ShowFrame();
    }

    #endregion

    #region Відображення

    private void RebuildParameters()
    {
        var charted = Parameters.Where(p => p.Charted).Select(p => p.Key).ToHashSet();
        if (charted.Count == 0 && Parameters.Count == 0) charted = ["rpm", "iac", "ect"];
        Parameters.Clear();
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
        }
        ShowFrame();
    }

    private void ShowFrame()
    {
        var f = CurrentFrame;
        if (f == null)
        {
            FrameInfo = "Немає даних";
            IdInfo = HexInfo = "";
            foreach (var p in Parameters) { p.Text = ""; p.Raw = ""; }
            UpdateChart();
            return;
        }
        var values = ParameterDecoder.Decode(f, Settings).ToDictionary(p => p.Key);
        bool needRebuild = values.Keys.Count(k => k.StartsWith('b')) != Parameters.Count(p => p.Key.StartsWith('b')) && ShowHidden;
        if (needRebuild) { RebuildParameters(); return; }
        foreach (var row in Parameters)
        {
            if (values.TryGetValue(row.Key, out var v)) { row.Text = v.Text; row.Raw = v.Raw; row.Unit = v.Unit; }
            else { row.Text = "—"; row.Raw = ""; }
        }
        FrameInfo = $"Кадр {_currentIndex + 1} / {_rec.Frames.Count}   {f.Time:dd.MM.yyyy HH:mm:ss}" + (f.Marked ? "   ★ мітка" : "");
        IdInfo = $"ID: {f.IdBitString}  (0x{f.IdValue:X3})";
        HexInfo = $"Байти ({f.Bytes.Length}): {f.HexDump}";
        UpdateChart();
    }

    private void UpdateChart()
    {
        var keys = Parameters.Where(p => p.Charted).ToList();
        if (keys.Count == 0 || _rec.Frames.Count == 0) { Chart = null; return; }
        // для швидкодії — не більше 600 точок навколо поточного кадру
        int end = Math.Clamp(_currentIndex + 300, Math.Min(600, _rec.Frames.Count), _rec.Frames.Count);
        int start = Math.Max(0, end - 600);
        var frames = _rec.Frames.Skip(start).Take(end - start).Select(f => ParameterDecoder.Decode(f, Settings).ToDictionary(p => p.Key)).ToList();
        ChartStart = start;
        Chart = keys.Select(k => new ChartSeries
        {
            Name = k.Name.Trim() + (k.Unit.Length > 0 ? ", " + k.Unit : ""),
            Color = k.Color,
            Values = frames.Select(d => d.TryGetValue(k.Key, out var v) ? v.Value : null).ToArray()
        }).ToList();
        OnPropertyChanged(nameof(ChartCursor));
    }

    public int ChartStart { get; private set; }
    public int ChartCursor => _currentIndex - ChartStart;

    #endregion

    public void Dispose()
    {
        _source?.Dispose();
        _edgeLog?.Dispose();
        Settings.Save();
    }
}
