using System.IO;
using System.Windows;
using System.Windows.Input;
using JzMonitor.Recording;

namespace JzMonitor;

public enum SaveChoice { Save, Discard, Cancel }

/// <summary>Запит «зберегти зміни?» у стилі програми замість системного MessageBox.</summary>
public partial class SaveChangesDialog : Window
{
    private SaveChoice _choice = SaveChoice.Cancel;

    private SaveChangesDialog(LogRecording rec, string reason)
    {
        InitializeComponent();
        Reason.Text = reason;
        FileText.Text = rec.Source != null ? Path.GetFileName(rec.Source) : "New recording";
        int marked = rec.Frames.Count(f => f.Marked);
        FramesText.Text = $"{rec.Frames.Count:N0}" + (marked > 0 ? $"  ·  {marked} marked" : "");
        if (rec.Frames.Count > 0)
        {
            var from = rec.Frames[0].Time;
            var to = rec.Frames[^1].Time;
            var span = to - from;
            TimeText.Text = $"{from:dd.MM.yyyy  HH:mm} – {to:HH:mm}   ({(span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss"))})";
        }
    }

    /// <summary>Показати діалог. reason — що станеться, якщо не зберегти (одне речення).</summary>
    public static SaveChoice Ask(LogRecording rec, string reason)
    {
        var owner = Application.Current.MainWindow;
        var dlg = new SaveChangesDialog(rec, reason) { Owner = owner is { IsLoaded: true } ? owner : null };
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Close(SaveChoice.Save);
    private void Discard_Click(object sender, RoutedEventArgs e) => Close(SaveChoice.Discard);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(SaveChoice.Cancel);

    private void Close(SaveChoice choice)
    {
        _choice = choice;
        Close();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
