using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VCast.Core;
using VCast.Obs;

namespace VCast.Ui;

// ソースのフィルタ一覧: 追加・削除・並べ替え・オンオフ・設定
public sealed class FiltersWindow : Window
{
    readonly IntPtr _source;
    readonly ListBox _list = new() { Background = Theme.Panel, Foreground = Theme.Fg, BorderBrush = Theme.Line, MinHeight = 200 };
    record Row(IntPtr Filter, string Name, string Type, bool Enabled);

    public FiltersWindow(IntPtr source)
    {
        _source = Native.obs_source_get_ref(source);
        Title = $"フィルタ — {Native.Str(Native.obs_source_get_name(source))}";
        Width = 520; Height = 520; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg; FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(14) };
        var hint = Theme.Label("上から順にかかります。チェックを外すと一時的にオフ。ダブルクリックで設定。", sub: true);
        hint.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);

        var flags = Native.obs_source_get_output_flags(_source);
        if ((flags & Native.OBS_SOURCE_AUDIO) != 0)
        {
            var preset = Theme.Button("🎙 声をきれいにする（おすすめ4点セット）", primary: true);
            preset.Margin = new Thickness(0, 0, 0, 10);
            preset.HorizontalAlignment = HorizontalAlignment.Left;
            preset.ToolTip = "ノイズ除去 → ノイズゲート → コンプレッサー → リミッター をまとめて追加します";
            preset.Click += (_, _) => { var n = Filters.ApplyCleanMicPreset(_source); Refresh(); if (n == 0) MessageBox.Show(this, "すでに全部入っています。", "VCast Studio"); };
            DockPanel.SetDock(preset, Dock.Top);
            root.Children.Add(preset);
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var add = Theme.Button("＋ 追加"); add.Margin = new Thickness(0, 0, 6, 0);
        add.Click += (_, _) => ShowAddMenu(add);
        var remove = Theme.Button("削除"); remove.Margin = new Thickness(0, 0, 6, 0);
        remove.Click += (_, _) => { if (Selected is { } r) { Filters.Remove(_source, r.Filter); Refresh(); } };
        var up = Theme.Button("↑"); up.MinWidth = 32; up.Margin = new Thickness(0, 0, 6, 0); up.Click += (_, _) => Move(Native.OBS_ORDER_MOVE_UP);
        var down = Theme.Button("↓"); down.MinWidth = 32; down.Margin = new Thickness(0, 0, 6, 0); down.Click += (_, _) => Move(Native.OBS_ORDER_MOVE_DOWN);
        var props = Theme.Button("⚙ 設定"); props.Margin = new Thickness(0, 0, 6, 0); props.Click += (_, _) => OpenSelected();
        var close = Theme.Button("閉じる"); close.Click += (_, _) => Close();
        buttons.Children.Add(add); buttons.Children.Add(remove); buttons.Children.Add(up); buttons.Children.Add(down); buttons.Children.Add(props); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list.ItemTemplate = BuildTemplate();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        root.Children.Add(_list);
        Content = root;
        Refresh();
        Closed += (_, _) => Native.obs_source_release(_source);
    }

    Row? Selected => _list.SelectedItem as Row;

    DataTemplate BuildTemplate()
    {
        var t = new DataTemplate(typeof(Row));
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var cb = new FrameworkElementFactory(typeof(CheckBox));
        cb.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(Row.Enabled)) { Mode = System.Windows.Data.BindingMode.OneWay });
        cb.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        cb.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        cb.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is CheckBox c && c.DataContext is Row r) Native.obs_source_set_enabled(r.Filter, c.IsChecked == true);
        }));
        dock.AppendChild(cb);
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Name)));
        var type = new FrameworkElementFactory(typeof(TextBlock));
        type.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Type)));
        type.SetValue(TextBlock.ForegroundProperty, Theme.Sub);
        type.SetValue(TextBlock.FontSizeProperty, 11.0);
        sp.AppendChild(name); sp.AppendChild(type);
        dock.AppendChild(sp);
        t.VisualTree = dock;
        return t;
    }

    void Refresh()
    {
        var sel = Selected?.Filter;
        var rows = Filters.List(_source).Select(f =>
        {
            var id = Native.Str(Native.obs_source_get_id(f)) ?? "";
            return new Row(f, Native.Str(Native.obs_source_get_name(f)) ?? "", SceneManager.DisplayName(id), Native.obs_source_enabled(f));
        }).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Filter == sel);
    }

    void Move(int movement)
    {
        if (Selected is not { } r) return;
        Native.obs_source_filter_set_order(_source, r.Filter, movement);
        Refresh();
        _list.SelectedItem = (_list.ItemsSource as List<Row>)?.FirstOrDefault(x => x.Filter == r.Filter);
    }

    void OpenSelected()
    {
        if (Selected is not { } r) return;
        new PropertiesWindow(r.Filter) { Owner = this }.ShowDialog();
        Refresh();
    }

    void ShowAddMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var t in Filters.AvailableFor(_source))
        {
            var header = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            header.Children.Add(new TextBlock { Text = t.DisplayName, FontWeight = FontWeights.SemiBold });
            var d = Filters.Describe(t.Id);
            if (d.Length > 0) header.Children.Add(new TextBlock { Text = d, FontSize = 11, Foreground = Brushes.DimGray });
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                var f = Filters.Add(_source, t.Id);
                Refresh();
                if (f != IntPtr.Zero)
                {
                    _list.SelectedItem = (_list.ItemsSource as List<Row>)?.FirstOrDefault(x => x.Filter == f);
                    OpenSelected();
                }
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }
}
