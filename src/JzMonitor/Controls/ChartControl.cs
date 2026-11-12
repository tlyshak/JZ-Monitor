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

    public static readonly DependencyProperty CursorIndexProperty = DependencyProperty.Register(
        nameof(CursorIndex), typeof(int), typeof(ChartControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public static readonly DependencyProperty ShowLegendProperty = DependencyProperty.Register(
        nameof(ShowLegend), typeof(bool), typeof(ChartControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DragSelectsProperty = DependencyProperty.Register(
        nameof(DragSelects), typeof(bool), typeof(ChartControl), new PropertyMetadata(false, (d, _) => ((ChartControl)d).UpdateHint()));

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
    private bool _selecting;

    public ChartControl()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.SizeWE;
        UpdateHint();
    }

    private void UpdateHint() => ToolTip = DragSelects
        ? "Drag to select a range · click to move the cursor"
        : "Drag to move the cursor · Shift+drag to select a range · wheel / arrow keys to step";

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
    private static readonly DashStyle SeriesDash = Frozen(new DashStyle([4, 3], 0));

    private int PointCount => Series?.Count > 0 ? Series.Max(s => s.Values.Length) : 0;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var series = Series;
        int n = PointCount;

        // Легенда — окремою смугою над графіком (переноситься на кілька рядків), щоб не перекривати лінії
        var legend = new List<(FormattedText text, Brush brush, bool dashed, double min, double max)>();
        foreach (var s in series ?? [])
        {
            var vals = s.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (vals.Count == 0) continue;
            double min = s.Min ?? vals.Min(), max = s.Max ?? vals.Max();
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            string cur = CursorIndex >= 0 && CursorIndex < s.Values.Length && s.Values[CursorIndex] is double cv
                ? cv.ToString("0.#", CultureInfo.InvariantCulture) : "—";
            string label = s.Unit.Length > 0 ? $"{s.Name}  {cur} {s.Unit}" : $"{s.Name}  {cur}";
            legend.Add((Text(label, brush, 11.5, Face), brush, s.Dashed, min, max));
        }
        double legendH = 0;
        var legendPos = new List<Point>();
        if (ShowLegend)
        {
            double x = 0, y = 0;
            foreach (var l in legend)
            {
                double itemW = l.text.Width + 18 + 22;
                if (x > 0 && x + itemW > w) { x = 0; y += 20; }
                legendPos.Add(new Point(x, y));
                x += itemW;
            }
            if (legend.Count > 0) legendH = y + 20 + 6;
        }
        for (int i = 0; i < legendPos.Count; i++)
        {
            var p = legendPos[i];
            if (legend[i].dashed)
            {
                dc.DrawRoundedRectangle(legend[i].brush, null, new Rect(p.X, p.Y + 6, 5, 4), 1, 1);
                dc.DrawRoundedRectangle(legend[i].brush, null, new Rect(p.X + 7, p.Y + 6, 5, 4), 1, 1);
            }
            else dc.DrawRoundedRectangle(legend[i].brush, null, new Rect(p.X, p.Y + 6, 12, 4), 2, 2);
            dc.DrawText(legend[i].text, new Point(p.X + 18, p.Y));
        }

        var xLabel = XLabel;
        double axisH = xLabel != null && n >= 2 ? 18 : 0;
        double plotTop = legendH, plotBottom = h - axisH, plotH = plotBottom - plotTop;
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

        double X(int i) => i * (w - 1) / (n - 1);

        // Виділення діапазону (Shift + перетягування)
        if (_selecting && _selA >= 0 && _selB >= 0)
        {
            double x1 = X(Math.Min(_selA, _selB)), x2 = X(Math.Max(_selA, _selB));
            dc.DrawRectangle(SelectionFill, null, new Rect(x1, plotTop, Math.Max(1, x2 - x1), plotH));
            dc.DrawLine(SelectionEdge, new Point(x1, plotTop), new Point(x1, plotBottom));
            dc.DrawLine(SelectionEdge, new Point(x2, plotTop), new Point(x2, plotBottom));
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
        int li = 0;
        foreach (var s in series)
        {
            if (!s.Values.Any(v => v.HasValue)) continue;
            var (_, brush, _, min, max) = legend[li++];
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

        if (CursorIndex >= 0 && CursorIndex < n)
        {
            var x = Math.Round(X(CursorIndex)) + 0.5;
            dc.DrawLine(CursorPen, new Point(x, plotTop), new Point(x, plotBottom));
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
        if (!IsMouseCaptured) return;
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
