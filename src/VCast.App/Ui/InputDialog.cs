using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VCast.Ui;

public sealed class InputDialog : Window
{
    readonly TextBox _box;
    public string Value => _box.Text.Trim();

    InputDialog(string title, string label, string initial)
    {
        Title = title; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;
        _box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12) };
        Theme.StyleInput(_box);
        var ok = Theme.Button("OK", primary: true); ok.IsDefault = true;
        ok.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; };
        var cancel = Theme.Button("キャンセル"); cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Theme.Label(label)); sp.Children.Add(_box); sp.Children.Add(buttons);
        Content = sp;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var d = new InputDialog(title, label, initial) { Owner = owner };
        return d.ShowDialog() == true ? d.Value : null;
    }
}
