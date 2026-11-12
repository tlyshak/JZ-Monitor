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
        var vm = new CompareViewModel(current, settings, currentRange);
        DataContext = vm;
        Chart.PointClicked += vm.SeekChart;
        Chart.Stepped += vm.Step;
    }
}
