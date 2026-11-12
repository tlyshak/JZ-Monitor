using System.Windows;

namespace JzMonitor;

public enum AppTheme { Light, Medium, Dark }

/// <summary>
/// Перемикання теми на ходу: у ресурсах програми перша об'єднана палітра (Themes/Palette.*.xaml) замінюється
/// іншою, а розмітка бере кольори через DynamicResource. Те, що малюється в коді (графік), слухає <see cref="Changed"/>.
/// </summary>
public static class ThemeManager
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;
    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        var dicts = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/JzMonitor;component/Themes/Palette.{theme}.xaml") };
        if (dicts.Count > 0 && dicts[0].Source?.OriginalString.Contains("/Palette.") == true) dicts[0] = palette;
        else dicts.Insert(0, palette);
        Current = theme;
        Changed?.Invoke();
    }
}
