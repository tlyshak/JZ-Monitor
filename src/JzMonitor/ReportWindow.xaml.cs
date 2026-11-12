using System.Windows;
using JzMonitor.Protocol;
using JzMonitor.Recording;
using JzMonitor.ViewModels;

namespace JzMonitor;

public partial class ReportWindow : Window
{
    /// <param name="jump">Перейти до кадру (індекс у записі) в головному вікні.</param>
    public ReportWindow(LogRecording current, FrameRange range, DecoderSettings settings, Action<int>? jump = null)
    {
        InitializeComponent();
        DataContext = new ReportViewModel(current, range, settings, jump);
    }
}
