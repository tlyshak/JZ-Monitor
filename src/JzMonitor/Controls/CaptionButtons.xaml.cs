using System.Windows;
using System.Windows.Controls;

namespace JzMonitor.Controls;

/// <summary>
/// Кнопки вікна для вікон із власним заголовком (WindowChrome без стандартного заголовка).
/// Стежить за станом вікна: значок «розгорнути / відновити» і відступ розгорнутого вікна — без стандартної
/// рамки воно виходить за край екрана на її товщину.
/// </summary>
public partial class CaptionButtons : UserControl
{
    private Window? _window;

    public CaptionButtons()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_window != null || Window.GetWindow(this) is not { } w) return;
            _window = w;
            w.StateChanged += (_, _) => UpdateState();
            UpdateState();
        };
    }

    private void UpdateState()
    {
        if (_window == null) return;
        bool max = _window.WindowState == WindowState.Maximized;
        double f = SystemParameters.WindowNonClientFrameThickness.Left;
        if (_window.Content is FrameworkElement root) root.Margin = max ? new Thickness(f) : new Thickness(0);
        MaxGlyph.Kind = max ? IconKind.Restore : IconKind.Maximize;
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (_window != null) SystemCommands.MinimizeWindow(_window);
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }

    private void OnClose(object sender, RoutedEventArgs e) => _window?.Close();
}
