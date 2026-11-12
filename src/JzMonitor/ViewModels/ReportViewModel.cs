using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using JzMonitor.Protocol;
using JzMonitor.Recording;
using Microsoft.Win32;

namespace JzMonitor.ViewModels;

/// <summary>Посилання на відрізок кадрів у звіті: клік — перейти до нього в головному вікні.</summary>
public sealed class SpanLink
{
    public required string Label { get; init; }
    public required RelayCommand Command { get; init; }
}

/// <summary>Рядок звіту: показник цього запису, еталону і різниця. IsHeader — заголовок групи.</summary>
public sealed class ReportRow
{
    public required string Name { get; init; }
    public bool IsHeader { get; init; }
    public string This { get; init; } = "";
    public string Reference { get; init; } = "";
    public string Delta { get; init; } = "";
    public string Unit { get; init; } = "";
    /// <summary>Різниця з еталоном більша за допустиму.</summary>
    public bool Deviates { get; init; }
    /// <summary>Перевірка спрацювала в цьому записі (лічильник проблем > 0).</summary>
    public bool Attention { get; init; }
    /// <summary>Де саме в записі (номери кадрів); порожньо — не показувати.</summary>
    public IReadOnlyList<SpanLink> Links { get; init; } = [];
    public string More { get; init; } = "";
    public bool HasLinks => Links.Count > 0;
}

/// <summary>Звіт по поточному запису (діапазону) з порівнянням з еталонним записом.</summary>
public sealed class ReportViewModel : ObservableObject
{
    private static readonly System.Globalization.CultureInfo Ci = System.Globalization.CultureInfo.InvariantCulture;
    private readonly DecoderSettings _settings;
    private readonly LogRecording _current;
    private readonly List<ReportItem> _items;
    private List<ReportItem>? _refItems;
    /// <summary>Перший кадр звіту в записі: відрізки у звіті — від нього.</summary>
    private readonly int _offset;
    private readonly Action<int>? _jump;
    /// <summary>Скільки відрізків показувати в рядку; решта — «+N more».</summary>
    private const int MaxLinks = 12;

    public ReportViewModel(LogRecording current, FrameRange range, DecoderSettings settings, Action<int>? jump = null)
    {
        _settings = settings;
        _current = current;
        _offset = range.Start;
        _jump = jump;
        var frames = current.Frames.Skip(range.Start).Take(range.Count).ToList();
        _items = RecordingReport.Build(frames, settings);
        string name = current.Source is string s ? Path.GetFileName(s) : "Current recording (not saved)";
        ThisInfo = range.Count == current.Frames.Count ? name : $"{name}  ·  frames {range.Start + 1}–{range.End + 1}";

        UseCurrentCommand = new RelayCommand(() => SetReference(_current.Source!), () => _current.Source != null);
        ChooseCommand = new RelayCommand(ChooseReference);
        ClearCommand = new RelayCommand(() => SetReference(null), () => _settings.ReferenceFile != null);
        CopyCommand = new RelayCommand(Copy);
        LoadReference();
    }

    public string ThisInfo { get; }
    private string _referenceInfo = "";
    public string ReferenceInfo { get => _referenceInfo; private set => Set(ref _referenceInfo, value); }
    public ObservableCollection<ReportRow> Rows { get; } = new();

    public RelayCommand UseCurrentCommand { get; }
    public RelayCommand ChooseCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }

    private void ChooseReference()
    {
        var dlg = new OpenFileDialog { Filter = "CarTester log (*.ctr)|*.ctr|All files|*.*", Title = "Choose the reference recording" };
        if (dlg.ShowDialog() == true) SetReference(dlg.FileName);
    }

    private void SetReference(string? path)
    {
        _settings.ReferenceFile = path;
        _settings.Save();
        LoadReference();
    }

    /// <summary>Еталон — увесь записаний файл (не діапазон).</summary>
    private void LoadReference()
    {
        _refItems = null;
        var path = _settings.ReferenceFile;
        if (path == null) ReferenceInfo = "No reference — choose a recording of the car in good condition";
        else if (!File.Exists(path)) ReferenceInfo = $"Reference not found: {path}";
        else
        {
            try
            {
                var rec = CtrFormat.Load(path);
                _refItems = RecordingReport.Build(rec.Frames, _settings);
                ReferenceInfo = Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                ReferenceInfo = $"Cannot open reference: {ex.Message}";
            }
        }
        BuildRows();
    }

    private void BuildRows()
    {
        Rows.Clear();
        string? group = null;
        foreach (var item in _items)
        {
            if (item.Group != group)
            {
                group = item.Group;
                Rows.Add(new ReportRow { Name = group.ToUpperInvariant(), IsHeader = true });
            }
            var r = _refItems?.FirstOrDefault(x => x.Key == item.Key);
            string delta = "";
            bool deviates = false;
            if (item.Value is double v && r?.Value is double rv)
            {
                double d = v - rv;
                // Знак — за округленим значенням, щоб не було «-0» і «+0.0»
                string text = Math.Abs(d).ToString(item.Format, Ci);
                bool zero = double.Parse(text, Ci) == 0;
                delta = zero ? text : (d > 0 ? "+" : "−") + text;
                // Лічильники проблем (допуск 0) — гірше лише коли їх більше; решта — відхилення в будь-який бік
                deviates = item.Tolerance is double tol && (tol == 0 ? d > 0 : Math.Abs(d) > tol);
            }
            Rows.Add(new ReportRow
            {
                Name = item.Name,
                This = item.Text,
                Reference = r?.Text ?? "",
                Delta = delta,
                Unit = item.Unit,
                Deviates = deviates,
                Attention = item.Group == RecordingReport.Checks && item.Tolerance == 0 && item.Value > 0,
                Links = (item.Spans ?? []).Take(MaxLinks).Select(Link).ToList(),
                More = item.Spans is { Count: > MaxLinks } sp ? $"+{sp.Count - MaxLinks} more" : ""
            });
        }
    }

    /// <summary>Номери кадрів — як у головному вікні (з 1, від початку запису).</summary>
    private string SpanText(ReportSpan sp)
    {
        int a = sp.Start + _offset + 1, b = sp.End + _offset + 1;
        string frames = a == b ? $"#{a}" : $"#{a}–{b}";
        return sp.Seconds is double sec ? $"{frames} ({sec:0} s)" : frames;
    }

    private SpanLink Link(ReportSpan sp) => new()
    {
        Label = SpanText(sp),
        Command = new RelayCommand(() => _jump?.Invoke(sp.Start + _offset), () => _jump != null)
    };

    /// <summary>Звіт текстом у буфер обміну (щоб переслати).</summary>
    private void Copy()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"JZ Monitor report: {ThisInfo}");
        if (_refItems != null) sb.AppendLine($"Reference: {ReferenceInfo}");
        foreach (var row in Rows)
        {
            if (row.IsHeader) { sb.AppendLine().AppendLine(row.Name); continue; }
            sb.Append($"  {row.Name,-36} {row.This,10} {row.Unit,-8}");
            if (_refItems != null) sb.Append($"  ref {row.Reference,10}  Δ {row.Delta,8}{(row.Deviates ? "  !" : "")}");
            sb.AppendLine();
            var spans = _items.FirstOrDefault(i => i.Name == row.Name)?.Spans;
            if (spans is { Count: > 0 }) sb.AppendLine("      frames: " + string.Join(", ", spans.Select(SpanText)));
        }
        try { Clipboard.SetText(sb.ToString()); } catch { }
    }
}
