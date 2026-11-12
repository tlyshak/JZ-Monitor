using System.Windows;
using JzMonitor.Protocol;
using JzMonitor.Recording;
using JzMonitor.ViewModels;

namespace JzMonitor;

public partial class CompareWindow : Window
{
    public CompareWindow(LogRecording current, DecoderSettings settings, FrameRange currentRange)
    {
        InitializeComponent();
        // Майже на весь екран (94 % робочої області, по центру), але не розгорнуте — вікно можна рухати й змінювати
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, area.Width * 0.94);
        Height = Math.Max(MinHeight, area.Height * 0.94);
        var vm = new CompareViewModel(current, settings, currentRange);
        DataContext = vm;
        Chart.PointClicked += vm.SeekChart;
        Chart.Stepped += vm.Step;
    }
}
