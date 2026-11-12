using System.Windows;
using System.Windows.Controls;
using JzMonitor.Recording;

namespace JzMonitor;

public partial class VehicleWindow : Window
{
    private readonly LogRecording _rec;
    private readonly TextBox?[] _boxes = new TextBox?[9];

    public VehicleWindow(LogRecording rec)
    {
        InitializeComponent();
        _rec = rec;
        int row = 0;
        for (int i = 0; i < 9; i++)
        {
            if (VehicleInfo.UnusedSlots.Contains(i)) continue; // поля немає в програмі (див. VehicleInfo)
            Fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            var label = new TextBlock { Text = VehicleInfo.Labels[i], Style = (Style)FindResource("FieldLabel") };
            Grid.SetRow(label, row);
            var box = new TextBox { Text = rec.Vehicle.Values[i], Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(box, row++);
            Grid.SetColumn(box, 1);
            Fields.Children.Add(label);
            Fields.Children.Add(box);
            _boxes[i] = box;
        }
        CommentsBox.Text = string.Join(Environment.NewLine, rec.Comments);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < 9; i++)
            if (_boxes[i] is { } box) _rec.Vehicle.Values[i] = box.Text.Replace("\r", "").Replace("\n", " ");
        _rec.Comments.Clear();
        var text = CommentsBox.Text.Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Length > 0) _rec.Comments.AddRange(text.Split('\n'));
        DialogResult = true;
    }
}
