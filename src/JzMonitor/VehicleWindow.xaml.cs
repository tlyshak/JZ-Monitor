using System.Windows;
using System.Windows.Controls;
using JzMonitor.Recording;

namespace JzMonitor;

public partial class VehicleWindow : Window
{
    private readonly CtrRecording _rec;
    private readonly TextBox[] _boxes = new TextBox[9];

    public VehicleWindow(CtrRecording rec)
    {
        InitializeComponent();
        _rec = rec;
        for (int i = 0; i < 9; i++)
        {
            Fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = VehicleInfo.Labels[i] + ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2) };
            Grid.SetRow(label, i);
            var box = new TextBox { Text = rec.Vehicle.Values[i] };
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            Fields.Children.Add(label);
            Fields.Children.Add(box);
            _boxes[i] = box;
        }
        CommentsBox.Text = string.Join(Environment.NewLine, rec.Comments);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < 9; i++) _rec.Vehicle.Values[i] = _boxes[i].Text.Replace("\r", "").Replace("\n", " ");
        _rec.Comments.Clear();
        var text = CommentsBox.Text.Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Length > 0) _rec.Comments.AddRange(text.Split('\n'));
        DialogResult = true;
    }
}
