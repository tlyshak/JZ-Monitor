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
        Chart.PointClicked += i => _vm.CurrentIndex = _vm.ChartStart + i;
        Chart.Stepped += d => _vm.CurrentIndex += d;
        // JzMonitor.exe file.ctr — відкрити запис одразу
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += (_, _) => _vm.OpenFile(args[1]);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscard("The recording will be lost when JZ Monitor closes.")) { e.Cancel = true; return; }
        _vm.Dispose();
        base.OnClosing(e);
    }
}
