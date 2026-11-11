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

    private static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null, new Rect(0, 0, w, h));
        var series = Series;
        if (series == null || series.Count == 0 || w < 20 || h < 20)
        {
            Text(dc, "Позначте параметри в колонці «Графік»", 8, 8, Brushes.Gray);
            return;
        }
        int n = series.Max(s => s.Values.Length);
        if (n < 2) return;

        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));

        double top = 4, bottom = h - 4;
        double X(int i) => i * (w - 1) / (n - 1);
        int legendY = 4;
        foreach (var s in series)
        {
            var vals = s.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (vals.Count == 0) continue;
            double min = vals.Min(), max = vals.Max();
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
            var brush = new SolidColorBrush(s.Color);
            dc.DrawGeometry(null, new Pen(brush, 1.5), geo);

            string cur = CursorIndex >= 0 && CursorIndex < s.Values.Length && s.Values[CursorIndex] is double cv
                ? cv.ToString("0.##", CultureInfo.InvariantCulture) : "";
            Text(dc, $"{s.Name}: {cur}   [{min:0.##} … {max:0.##}]", 6, legendY, brush);
            legendY += 15;
        }

        if (CursorIndex >= 0 && CursorIndex < n)
        {
            var x = X(CursorIndex);
            dc.DrawLine(new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash }, new Point(x, 0), new Point(x, h));
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

    private static void Text(DrawingContext dc, string s, double x, double y, Brush b) =>
        dc.DrawText(new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, b, 1.0), new Point(x, y));
}
