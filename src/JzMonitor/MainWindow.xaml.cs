using System.ComponentModel;
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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscard()) { e.Cancel = true; return; }
        _vm.Dispose();
        base.OnClosing(e);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Prev_Click(object sender, RoutedEventArgs e) => _vm.CurrentIndex--;
    private void Next_Click(object sender, RoutedEventArgs e) => _vm.CurrentIndex++;
}
