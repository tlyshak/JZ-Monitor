using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace JzMonitor.Controls;

public sealed class ChartSeries
{
    public required string Name { get; init; }
    /// <summary>Одиниця для легенди; порожня — не показувати.</summary>
    public string Unit { get; init; } = "";
    public required Color Color { get; init; }
    public required double?[] Values { get; init; }
    /// <summary>Стала вертикальна шкала (наприклад, MIN/MAX діапазону). Якщо не задана — за точками серії.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Пунктирна лінія (другий файл у порівнянні).</summary>
    public bool Dashed { get; init; }
}

/// <summary>Знак «!» над графіком: відрізок точок (включно), де спрацювала перевірка.</summary>
public sealed record ChartAlert(int Start, int End, bool Alarm, string Text);

/// <summary>
/// Простий багатосерійний графік: кожна серія масштабується у власному діапазоні.
/// Миша: клік/перетягування — курсор; Shift + перетягування — виділення діапазону; коліщатко й клавіші — крок.
/// З <see cref="DragSelects"/> перетягування без Shift теж виділяє діапазон (огляд усього запису).
/// </summary>
public sealed class ChartControl : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(ChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Курсор не перемальовує лінії — лише верхній шар (курсор і легенда), див. RenderOverlay
    public static readonly DependencyProperty CursorIndexProperty = DependencyProperty.Register(
        nameof(CursorIndex), typeof(int), typeof(ChartControl),
        new FrameworkPropertyMetadata(-1, (d, _) => ((ChartControl)d).RenderOverlay()));

    public static readonly DependencyProperty XLabelProperty = DependencyProperty.Register(
        nameof(XLabel), typeof(Func<int, string>), typeof(ChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(int), typeof(ChartControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(int), typeof(ChartControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IReadOnlyList<int>), typeof(ChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AlertsProperty = DependencyProperty.Register(
        nameof(Alerts), typeof(IReadOnlyList<ChartAlert>), typeof(ChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLegendProperty = DependencyProperty.Register(
        nameof(ShowLegend), typeof(bool), typeof(ChartControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DragSelectsProperty = DependencyProperty.Register(
        nameof(DragSelects), typeof(bool), typeof(ChartControl), new PropertyMetadata(false));

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int CursorIndex
    {
        get => (int)GetValue(CursorIndexProperty);
        set => SetValue(CursorIndexProperty, value);
    }

    /// <summary>Підпис осі X для точки з індексом (час кадру, час від початку тощо). null — без осі.</summary>
    public Func<int, string>? XLabel
    {
        get => (Func<int, string>?)GetValue(XLabelProperty);
        set => SetValue(XLabelProperty, value);
    }

    /// <summary>Постійно підсвічений діапазон точок (включно); −1 — немає. Поза ним графік притемнений.</summary>
    public int SelectionStart
    {
        get => (int)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionEnd
    {
        get => (int)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    /// <summary>Позначені кадри (зірочка) — індекси точок; малюються вертикальними мітками.</summary>
    public IReadOnlyList<int>? Marks
    {
        get => (IReadOnlyList<int>?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>Спрацювання перевірок: знак «!» і лінія на першому кадрі відрізка, сам відрізок — ледь помітним тлом.</summary>
    public IReadOnlyList<ChartAlert>? Alerts
    {
        get => (IReadOnlyList<ChartAlert>?)GetValue(AlertsProperty);
        set => SetValue(AlertsProperty, value);
    }

    /// <summary>Легенда над графіком (назви й значення під курсором).</summary>
    public bool ShowLegend
    {
        get => (bool)GetValue(ShowLegendProperty);
        set => SetValue(ShowLegendProperty, value);
    }

    /// <summary>Перетягування без Shift виділяє діапазон; простий клік — як і раніше, курсор.</summary>
    public bool DragSelects
    {
        get => (bool)GetValue(DragSelectsProperty);
        set => SetValue(DragSelectsProperty, value);
    }

    /// <summary>Мишею вибрано точку (індекс у серіях).</summary>
    public event Action<int>? PointClicked;
    /// <summary>Крок навігації з коліщатка/клавіатури, у точках.</summary>
    public event Action<int>? Stepped;
    /// <summary>Shift + перетягування: виділено діапазон точок (від, до — включно).</summary>
    public event Action<int, int>? RangeSelected;

    private int _selA = -1, _selB = -1;
    /// <summary>Верх області графіка з останнього малювання — для підказки над знаками «!».</summary>
    private double _plotTop;
    private bool _selecting;

    public ChartControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
        _alertTip.PlacementTarget = this;
        AddVisualChild(_overlay);
    }

    /// <summary>
    /// Власна підказка для спрацювань перевірок: відкривається одразу й іде за мишею. Стандартна підказка WPF
    /// показується раз на вхід миші в елемент і після закриття не з'являється, доки миша не вийде — над знаками
    /// «!» вона часто не відкривалася.
    /// </summary>
    private readonly System.Windows.Controls.ToolTip _alertTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative };

    /// <summary>Над знаком «!», його лінією або тлом відрізка — підказка, що спрацювало; деінде — закрита.</summary>
    private void UpdateAlertTip(Point p)
    {
        var hit = AlertsAt(p);
        if (hit.Count == 0) { CloseAlertTip(); return; }
        _alertTip.Content = string.Join(Environment.NewLine, hit);
        _alertTip.HorizontalOffset = p.X + 14;
        _alertTip.VerticalOffset = p.Y + 18;
        _alertTip.IsOpen = true;
    }

    private void CloseAlertTip()
    {
        _alertTip.IsOpen = false;
    }

    /// <summary>
    /// Спрацювання під мишею: знак «!» (±8 px від першого кадру, на будь-якій висоті — там і лінія) або тло
    /// відрізка. Спершу знаки, бо вони точніші.
    /// </summary>
    private List<string> AlertsAt(Point p)
    {
        int n = PointCount;
        double w = ActualWidth;
        if (Alerts is not { Count: > 0 } alerts || n < 2 || w < 2 || p.Y < _plotTop) return [];
        double X(int i) => i * (w - 1) / (n - 1);
        var hits = alerts.Where(a => Math.Abs(X(a.Start) - p.X) <= 8).ToList();
        if (hits.Count == 0)
        {
            int idx = IndexAt(p.X);
            hits = alerts.Where(a => a.End > a.Start && idx >= a.Start && idx <= a.End).ToList();
        }
        return hits.Select(a => a.Text).Distinct().ToList();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        CloseAlertTip();
    }


    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface AxisFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush Back = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFD)));
    private static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE9, 0xED, 0xF2)), 1));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)));
    private static readonly Pen CursorPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x1D, 0x24, 0x30)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xF2, 0x6B, 0x1D)));
    private static readonly Pen SelectionEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0xF2, 0x6B, 0x1D)), 1));
    private static readonly Brush OutsideFill = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen RangeEdge = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0x6B, 0x1D)), 1.5));
    private static readonly Brush MarkFill = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0x8A, 0x00)));
    private static readonly Pen MarkPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xC9, 0x8A, 0x00)), 1));
    private static readonly Brush AlertWarn = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0x7E, 0x1F)));
    private static readonly Brush AlertAlarm = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static readonly Pen AlertWarnLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xF0, 0x7E, 0x1F)), 1));
    private static readonly Pen AlertAlarmLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xE5, 0x48, 0x4D)), 1));
    private static readonly Brush AlertWarnFill = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xF0, 0x7E, 0x1F)));
    private static readonly Brush AlertAlarmFill = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xE5, 0x48, 0x4D)));
    private static readonly Typeface AlertFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly DashStyle SeriesDash = Frozen(new DashStyle([4, 3], 0));

    private int PointCount => Series?.Count > 0 ? Series.Max(s => s.Values.Length) : 0;

    // ── Два шари: лінії (OnRender — лише коли змінились дані чи розмір) і верхній шар з курсором і легендою
    // (RenderOverlay — на кожен крок курсора). Раніше кожен крок курсора заново будував усі лінії. ──
    private readonly DrawingVisual _overlay = new();
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _overlay;
    /// <summary>Параметри останнього малювання ліній — для верхнього шару.</summary>
    private double _legendH = -1, _plotBottom, _drawnW;
    private int _drawnN;

    private static readonly Dictionary<Color, Brush> BrushCache = new();
    private static Brush BrushOf(Color c)
    {
        if (!BrushCache.TryGetValue(c, out var b)) BrushCache[c] = b = Frozen(new SolidColorBrush(c));
        return b;
    }

    /// <summary>Легенда: підписи зі значеннями під курсором і їх розміщення (переноситься на кілька рядків).</summary>
    private (List<(FormattedText text, Brush brush, bool dashed, Point pos)> items, double height) LegendLayout(double w)
    {
        var items = new List<(FormattedText, Brush, bool, Point)>();
        if (!ShowLegend || Series is not { } series) return (items, 0);
        double x = 0, y = 0;
        int cursor = CursorIndex;
        foreach (var s in series)
        {
            if (!s.Values.Any(v => v.HasValue)) continue;
            var brush = BrushOf(s.Color);
            string cur = cursor >= 0 && cursor < s.Values.Length && s.Values[cursor] is double cv
                ? cv.ToString("0.#", CultureInfo.InvariantCulture) : "—";
            var text = Text(s.Unit.Length > 0 ? $"{s.Name}  {cur} {s.Unit}" : $"{s.Name}  {cur}", brush, 11.5, Face);
            double itemW = text.Width + 18 + 22;
            if (x > 0 && x + itemW > w) { x = 0; y += 20; }
            items.Add((text, brush, s.Dashed, new Point(x, y)));
            x += itemW;
        }
        return (items, items.Count > 0 ? y + 20 + 6 : 0);
    }

    /// <summary>Верхній шар: легенда зі значеннями під курсором і лінія курсора.</summary>
    private void RenderOverlay()
    {
        double w = ActualWidth;
        var (items, legendH) = LegendLayout(w);
        // Легенда переклалась на інше число рядків — змінилась висота області графіка: перемалювати все
        if (Math.Abs(legendH - _legendH) > 0.1 || Math.Abs(w - _drawnW) > 0.1) { InvalidateVisual(); return; }
        using var dc = _overlay.RenderOpen();
        foreach (var (text, brush, dashed, p) in items)
        {
            if (dashed)
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(p.X, p.Y + 6, 5, 4), 1, 1);
                dc.DrawRoundedRectangle(brush, null, new Rect(p.X + 7, p.Y + 6, 5, 4), 1, 1);
            }
            else dc.DrawRoundedRectangle(brush, null, new Rect(p.X, p.Y + 6, 12, 4), 2, 2);
            dc.DrawText(text, new Point(p.X + 18, p.Y));
        }
        int n = _drawnN;
        if (n >= 2 && CursorIndex >= 0 && CursorIndex < n)
        {
            var x = Math.Round(CursorIndex * (w - 1) / (n - 1)) + 0.5;
            dc.DrawLine(CursorPen, new Point(x, _legendH), new Point(x, _plotBottom));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var series = Series;
        int n = PointCount;

        double legendH = LegendLayout(w).height;
        _legendH = legendH;
        _drawnW = w;
        _drawnN = 0;
        try { DrawPlot(dc, w, h, series, n, legendH); }
        finally { RenderOverlay(); }
    }

    private void DrawPlot(DrawingContext dc, double w, double h, IReadOnlyList<ChartSeries>? series, int n, double legendH)
    {
        var xLabel = XLabel;
        double axisH = xLabel != null && n >= 2 ? 18 : 0;
        double plotTop = legendH, plotBottom = h - axisH, plotH = plotBottom - plotTop;
        _plotTop = plotTop;
        _plotBottom = plotBottom;
        dc.DrawRoundedRectangle(Back, null, new Rect(0, plotTop, w, Math.Max(0, plotH)), 6, 6);
        for (int i = 1; i < 4; i++)
        {
            double gy = Math.Round(plotTop + plotH * i / 4) + 0.5;
            dc.DrawLine(Grid, new Point(0, gy), new Point(w, gy));
        }

        if (series == null || series.Count == 0 || w < 20 || plotH < 20)
        {
            var hint = Text("Tick parameters (tiles or table) to plot them here", Dim, 12, Face);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, plotTop + (plotH - hint.Height) / 2));
            return;
        }
        if (n < 2) return;
        _drawnN = n;

        double X(int i) => i * (w - 1) / (n - 1);

        // Виділення діапазону (Shift + перетягування)
        if (_selecting && _selA >= 0 && _selB >= 0)
        {
            double x1 = X(Math.Min(_selA, _selB)), x2 = X(Math.Max(_selA, _selB));
            dc.DrawRectangle(SelectionFill, null, new Rect(x1, plotTop, Math.Max(1, x2 - x1), plotH));
            dc.DrawLine(SelectionEdge, new Point(x1, plotTop), new Point(x1, plotBottom));
            dc.DrawLine(SelectionEdge, new Point(x2, plotTop), new Point(x2, plotBottom));
        }

        // Відрізки спрацювань перевірок — ледь помітне тло під лініями (знак «!» малюється пізніше, зверху)
        if (Alerts is { Count: > 0 } shaded)
        {
            foreach (var a in shaded)
            {
                if (a.End < 0 || a.Start >= n || a.End <= a.Start) continue;
                double x1 = X(Math.Max(a.Start, 0)), x2 = X(Math.Min(a.End, n - 1));
                dc.DrawRectangle(a.Alarm ? AlertAlarmFill : AlertWarnFill, null, new Rect(x1, plotTop, x2 - x1, plotH));
            }
        }

        // Підписи осі X: ~6 рівномірних позначок
        if (xLabel != null)
        {
            int ticks = Math.Clamp((int)(w / 160), 2, 8);
            for (int t = 0; t <= ticks; t++)
            {
                int idx = (int)Math.Round((double)t * (n - 1) / ticks);
                var ft = Text(xLabel(idx), Dim, 10.5, AxisFace);
                double tx = Math.Clamp(X(idx) - ft.Width / 2, 0, w - ft.Width);
                dc.DrawText(ft, new Point(tx, plotBottom + 3));
            }
        }

        double top = plotTop + 10, bottom = plotBottom - 10;
        foreach (var s in series)
        {
            // Шкала серії: задана (MIN/MAX діапазону) або за її точками
            double min = double.MaxValue, max = double.MinValue;
            foreach (var v in s.Values) if (v is double d) { if (d < min) min = d; if (d > max) max = d; }
            if (min > max) continue;
            if (s.Min is double smin) min = smin;
            if (s.Max is double smax) max = smax;
            var brush = BrushOf(s.Color);
            if (max - min < 1e-9) { min -= 1; max += 1; }
            double Y(double v) => bottom - (Math.Clamp(v, min, max) - min) / (max - min) * (bottom - top);

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < s.Values.Length; i++)
                {
                    if (s.Values[i] is not double v) { started = false; continue; }
                    var p = new Point(X(i), Y(v));
                    if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                    else ctx.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            var pen = new Pen(brush, s.Dashed ? 1.4 : 1.6) { LineJoin = PenLineJoin.Round };
            if (s.Dashed) pen.DashStyle = SeriesDash;
            dc.DrawGeometry(null, pen, geo);
        }

        // Вибраний діапазон: поза ним — притемнення, межі — лініями
        if (!_selecting && SelectionStart >= 0 && SelectionEnd >= SelectionStart && (SelectionStart > 0 || SelectionEnd < n - 1))
        {
            double x1 = X(Math.Min(SelectionStart, n - 1)), x2 = X(Math.Min(SelectionEnd, n - 1));
            if (x1 > 0) dc.DrawRectangle(OutsideFill, null, new Rect(0, plotTop, x1, plotH));
            if (x2 < w) dc.DrawRectangle(OutsideFill, null, new Rect(x2, plotTop, w - x2, plotH));
            dc.DrawLine(RangeEdge, new Point(x1, plotTop), new Point(x1, plotBottom));
            dc.DrawLine(RangeEdge, new Point(x2, plotTop), new Point(x2, plotBottom));
        }

        // Позначені кадри: тонка лінія й трикутник угорі
        if (Marks is { Count: > 0 } marks)
        {
            foreach (int m in marks)
            {
                if (m < 0 || m >= n) continue;
                double mx = Math.Round(X(m)) + 0.5;
                dc.DrawLine(MarkPen, new Point(mx, plotTop + 7), new Point(mx, plotBottom));
                var tri = new StreamGeometry();
                using (var ctx = tri.Open())
                {
                    ctx.BeginFigure(new Point(mx - 5, plotTop), true, true);
                    ctx.LineTo(new Point(mx + 5, plotTop), false, false);
                    ctx.LineTo(new Point(mx, plotTop + 8), false, false);
                }
                tri.Freeze();
                dc.DrawGeometry(MarkFill, null, tri);
            }
        }

        // Спрацювання перевірок — як мітки зірочки: на першому кадрі відрізка тонка лінія і «!» у кружечку вгорі
        if (Alerts is { Count: > 0 } alerts)
        {
            double lastBadge = double.NegativeInfinity;
            foreach (var a in alerts.OrderBy(a => a.Start).ThenByDescending(a => a.Alarm))
            {
                if (a.Start < 0 || a.Start >= n) continue;
                var brush = a.Alarm ? AlertAlarm : AlertWarn;
                double x = Math.Round(X(a.Start)) + 0.5;
                dc.DrawLine(a.Alarm ? AlertAlarmLine : AlertWarnLine, new Point(x, plotTop + 13), new Point(x, plotBottom));
                // Знаки, що злипаються, не малюємо один на одному — досить лінії
                if (x - lastBadge < 12) continue;
                lastBadge = x;
                var center = new Point(Math.Max(x, 6), plotTop + 7);
                dc.DrawEllipse(brush, null, center, 6, 6);
                var mark = Text("!", Brushes.White, 10, AlertFace);
                dc.DrawText(mark, new Point(center.X - mark.Width / 2, center.Y - mark.Height / 2));
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        int idx = IndexAt(e.GetPosition(this).X);
        if (DragSelects || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selecting = true;
            _selA = _selB = idx;
            InvalidateVisual();
        }
        else if (idx >= 0) PointClicked?.Invoke(idx);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) { UpdateAlertTip(e.GetPosition(this)); return; }
        CloseAlertTip(); // під час перетягування підказка лише заважає
        int idx = IndexAt(e.GetPosition(this).X);
        if (idx < 0) return;
        if (_selecting) { _selB = idx; InvalidateVisual(); }
        else PointClicked?.Invoke(idx);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_selecting && _selA >= 0 && _selB >= 0 && Math.Abs(_selB - _selA) >= 1)
            RangeSelected?.Invoke(Math.Min(_selA, _selB), Math.Max(_selA, _selB));
        else if (_selecting && DragSelects && _selA >= 0)
            PointClicked?.Invoke(_selA); // клік без перетягування — курсор
        EndSelection();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndSelection();
    }

    private void EndSelection()
    {
        if (!_selecting) return;
        _selecting = false;
        _selA = _selB = -1;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        Stepped?.Invoke(e.Delta < 0 ? step : -step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int? step = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.PageUp => -50,
            Key.PageDown => 50,
            Key.Home => -1_000_000,
            Key.End => 1_000_000,
            _ => null
        };
        if (step is int s) { Stepped?.Invoke(s); e.Handled = true; }
    }

    /// <summary>Індекс точки під координатою X (обмежений межами графіка); −1, якщо даних немає.</summary>
    private int IndexAt(double x)
    {
        int n = PointCount;
        double w = ActualWidth;
        if (n < 2 || w < 2) return -1;
        return Math.Clamp((int)Math.Round(x / (w - 1) * (n - 1)), 0, n - 1);
    }

    private FormattedText Text(string s, Brush b, double size, Typeface face) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
