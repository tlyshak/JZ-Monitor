using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace JzMonitor.Controls;

public enum IconKind
{
    None, Add, Cancel, Filter, Refresh, Star, StarFilled, Check, Delete, Save, Play, Stop,
    ChevronLeft, ChevronRight, ChevronDown, Latest, Car, Compare, Import, Open, Report, Table, Copy,
    Minimize, Maximize, Restore, Close, Sun, Contrast, Moon
}

/// <summary>
/// Векторний значок. Замість шрифту Segoe MDL2 Assets, якого немає в Windows 7 (там значки не показувались).
/// Фігури намальовані на сітці 16×16 тонким контуром, як у шрифті; колір — успадкований Foreground (кнопка
/// сіріє, коли вимкнена). Розмір — Size (пікселі, за замовчуванням 14).
/// </summary>
public sealed class VectorIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(VectorIcon),
        new FrameworkPropertyMetadata(IconKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(VectorIcon), new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    // Вирівнювання за замовчуванням — через метадані, а не в конструкторі: значення з конструктора сильніші
    // за задані в шаблонах (стрілка ComboBox у темі тоді ігнорувала HorizontalAlignment="Right")
    static VectorIcon()
    {
        HorizontalAlignmentProperty.OverrideMetadata(typeof(VectorIcon), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(VectorIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(VectorIcon), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Фігура (у координатах 16×16) і чи заливати її (інакше — контур).</summary>
    private static readonly Dictionary<IconKind, (Geometry Geometry, bool Filled)> Shapes = new()
    {
        [IconKind.Add] = (G("M8,2.5 V13.5 M2.5,8 H13.5"), false),
        [IconKind.Cancel] = (G("M4,4 L12,12 M12,4 L4,12"), false),
        [IconKind.Filter] = (G("M2,3 H14 L9.5,8.5 V13 L6.5,11.5 V8.5 Z"), false),
        [IconKind.Refresh] = (G("M13,8 A5,5 0 1 1 11.6,4.4 M12.2,1.6 V4.8 H9"), false),
        [IconKind.Star] = (G(StarPath), false),
        [IconKind.StarFilled] = (G(StarPath), true),
        [IconKind.Check] = (G("M3,8.5 L6.5,12 L13,4.5"), false),
        [IconKind.Delete] = (G("M2.5,4 H13.5 M6,4 V2.5 H10 V4 M4,4 L4.8,13.5 H11.2 L12,4 M6.8,6.5 V11.5 M9.2,6.5 V11.5"), false),
        [IconKind.Save] = (G("M2.5,2.5 H11 L13.5,5 V13.5 H2.5 Z M5,2.5 V6 H10.5 V2.5 M5,13.5 V9.5 H11 V13.5"), false),
        [IconKind.Play] = (G("M4.5,2.5 L13,8 L4.5,13.5 Z"), true),
        [IconKind.Stop] = (G("M4,4 H12 V12 H4 Z"), true),
        [IconKind.ChevronLeft] = (G("M10,3 L5,8 L10,13"), false),
        [IconKind.ChevronRight] = (G("M6,3 L11,8 L6,13"), false),
        [IconKind.ChevronDown] = (G("M3,6 L8,11 L13,6"), false),
        [IconKind.Latest] = (G("M4,3 L9,8 L4,13 M12,3 V13"), false),
        [IconKind.Car] = (G("M1.5,11.5 V8.5 L3.2,4.5 H12.8 L14.5,8.5 V11.5 Z M1.5,8.5 H14.5 M4,11.5 V13.5 M12,11.5 V13.5 M4,10 H5 M11,10 H12"), false),
        [IconKind.Compare] = (G("M2,5.5 H13 M10,2.5 L13,5.5 L10,8.5 M14,10.5 H3 M6,7.5 L3,10.5 L6,13.5"), false),
        [IconKind.Import] = (G("M8,2 V10 M4.5,6.5 L8,10 L11.5,6.5 M2.5,10.5 V13.5 H13.5 V10.5"), false),
        [IconKind.Open] = (G("M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z M1.5,7 H14.5"), false),
        [IconKind.Report] = (G("M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M5.5,8 H10.5 M5.5,10.5 H10.5 M5.5,12.5 H8.5"), false),
        [IconKind.Table] = (G("M2,3 H14 V13 H2 Z M2,6.5 H14 M2,9.8 H14 M6.5,3 V13"), false),
        [IconKind.Copy] = (G("M5.5,5 H13.5 V14.5 H5.5 Z M10.5,5 V1.5 H2.5 V11 H5.5"), false),
        [IconKind.Minimize] = (G("M3,8 H13"), false),
        [IconKind.Maximize] = (G("M3.5,3.5 H12.5 V12.5 H3.5 Z"), false),
        [IconKind.Restore] = (G("M3.5,5.5 H10.5 V12.5 H3.5 Z M5.5,5.5 V3.5 H12.5 V10.5 H10.5"), false),
        [IconKind.Close] = (G("M3,3 L13,13 M13,3 L3,13"), false),
        [IconKind.Sun] = (G("M8,5 A3,3 0 1 1 8,11 A3,3 0 1 1 8,5 Z M8,1.5 V3 M8,13 V14.5 M1.5,8 H3 M13,8 H14.5 M3.4,3.4 L4.4,4.4 M11.6,11.6 L12.6,12.6 M3.4,12.6 L4.4,11.6 M11.6,4.4 L12.6,3.4"), false),
        // Кільце з залитою правою половиною (контраст): EvenOdd — внутрішня ліва половина лишається порожньою
        [IconKind.Contrast] = (G("F0 M8,1.5 A6.5,6.5 0 1 1 8,14.5 A6.5,6.5 0 1 1 8,1.5 Z M8,3 A5,5 0 0 0 8,13 Z"), true),
        [IconKind.Moon] = (G("M10.5,2 A6,6 0 1 0 14,10.5 A4.8,4.8 0 0 1 10.5,2 Z"), false),
    };

    private const string StarPath =
        "M8,2 L9.59,6.42 L14.28,6.56 L10.57,9.43 L11.88,13.94 L8,11.3 L4.12,13.94 L5.43,9.43 L1.72,6.56 L6.41,6.42 Z";

    private static Geometry G(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (!Shapes.TryGetValue(Kind, out var shape)) return;
        double size = Size, scale = size / 16;
        // Товщина лінії — у пікселях екрана (≈1–1.3 px), незалежно від розміру значка
        double px = size <= 11 ? 1.0 : 1.3;
        var pen = new Pen(Foreground, px / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(shape.Filled ? Foreground : null, shape.Filled ? null : pen, shape.Geometry);
        dc.Pop();
    }
}
