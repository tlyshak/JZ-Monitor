using System.ComponentModel;
using System.IO;
using System.Windows;
using JzMonitor.ViewModels;

namespace JzMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        Chart.PointClicked += _vm.SeekChart;
        Chart.Stepped += d => _vm.CurrentIndex += d;
        Chart.RangeSelected += _vm.SelectChartRange;
        Overview.PointClicked += _vm.SeekOverview;
        Overview.Stepped += d => _vm.CurrentIndex += d;
        Overview.RangeSelected += _vm.SelectOverviewRange;
        // JzMonitor.exe file.ctr — відкрити запис одразу
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += (_, _) => _vm.OpenFile(args[1]);
    }

    // ── Кнопки вікна у верхньому рядку (стандартного заголовка немає) ──
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>
    /// Розгорнуте вікно без стандартної рамки виходить за край екрана на товщину рамки — відступ її компенсує.
    /// Значок кнопки: розгорнути / відновити.
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        double f = SystemParameters.WindowNonClientFrameThickness.Left;
        Root.Margin = max ? new Thickness(f, f, f, f) : new Thickness(0);
        MaxGlyph.Text = max ? "\uE923" : "\uE922";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscard("The recording will be lost when JZ Monitor closes.")) { e.Cancel = true; return; }
        _vm.Dispose();
        base.OnClosing(e);
    }
}
