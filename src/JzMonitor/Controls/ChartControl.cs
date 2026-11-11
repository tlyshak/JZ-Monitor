using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace JzMonitor.Controls;

public sealed class ChartSeries
{
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public required double?[] Values { get; init; }
}

/// <summary>Простий багатосерійний графік: кожна серія масштабується у власному діапазоні.</summary>
public sealed class ChartControl : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(ChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CursorIndexProperty = DependencyProperty.Register(
        nameof(CursorIndex), typeof(int), typeof(ChartControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public event Action<int>? PointClicked;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Back = Frozen(new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFD)));
    private static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE9, 0xED, 0xF2)), 1));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x86)));
    private static readonly Pen CursorPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x1D, 0x24, 0x30)), 1) { DashStyle = DashStyles.Dash });

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var series = Series;
        int n = series?.Count > 0 ? series.Max(s => s.Values.Length) : 0;

        // Легенда — окремою смугою над графіком (переноситься на кілька рядків), щоб не перекривати лінії
        var legend = new List<(FormattedText text, Brush brush, double min, double max)>();
        foreach (var s in series ?? [])
        {
            var vals = s.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (vals.Count == 0) continue;
            double min = vals.Min(), max = vals.Max();
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            string cur = CursorIndex >= 0 && CursorIndex < s.Values.Length && s.Values[CursorIndex] is double cv
                ? cv.ToString("0.##", CultureInfo.InvariantCulture) : "—";
            legend.Add((Text($"{s.Name}  {cur}   ({min:0.#} … {max:0.#})", brush, 11.5), brush, min, max));
        }
        double legendH = 0;
        var legendPos = new List<Point>();
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
        for (int i = 0; i < legend.Count; i++)
        {
            var p = legendPos[i];
            dc.DrawRoundedRectangle(legend[i].brush, null, new Rect(p.X, p.Y + 6, 12, 4), 2, 2);
            dc.DrawText(legend[i].text, new Point(p.X + 18, p.Y));
        }

        double plotTop = legendH, plotH = h - legendH;
        dc.DrawRoundedRectangle(Back, null, new Rect(0, plotTop, w, Math.Max(0, plotH)), 6, 6);
        for (int i = 1; i < 4; i++)
        {
            double gy = Math.Round(plotTop + plotH * i / 4) + 0.5;
            dc.DrawLine(Grid, new Point(0, gy), new Point(w, gy));
        }

        if (series == null || series.Count == 0 || w < 20 || plotH < 20)
        {
            var hint = Text("Tick parameters (tiles or table) to plot them here", Dim, 12);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, plotTop + (plotH - hint.Height) / 2));
            return;
        }
        if (n < 2) return;

        double top = plotTop + 10, bottom = h - 10;
        double X(int i) => i * (w - 1) / (n - 1);
        int li = 0;
        foreach (var s in series)
        {
            if (!s.Values.Any(v => v.HasValue)) continue;
            var (_, brush, min, max) = legend[li++];
            if (max - min < 1e-9) { min -= 1; max += 1; }
            double Y(double v) => bottom - (v - min) / (max - min) * (bottom - top);

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
            dc.DrawGeometry(null, new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round }, geo);
        }

        if (CursorIndex >= 0 && CursorIndex < n)
        {
            var x = Math.Round(X(CursorIndex)) + 0.5;
            dc.DrawLine(CursorPen, new Point(x, plotTop), new Point(x, h));
        }
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int n = Series?.Count > 0 ? Series.Max(s => s.Values.Length) : 0;
        if (n < 2) return;
        int idx = (int)Math.Round(e.GetPosition(this).X / (ActualWidth - 1) * (n - 1));
        PointClicked?.Invoke(Math.Clamp(idx, 0, n - 1));
    }

    private FormattedText Text(string s, Brush b, double size) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
