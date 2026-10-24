using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using VCast.Obs;

namespace VCast.Ui;

// obs_properties から設定画面を自動生成する。変更は即時反映（OBS と同じ）、キャンセルで元に戻す。
public sealed unsafe class PropertiesWindow : Window
{
    readonly IntPtr _source, _settings;
    IntPtr _props;
    readonly string _original;
    readonly bool _deferred; // OBS_PROPERTIES_DEFER_UPDATE: OK を押すまでソースへ反映しない
    readonly StackPanel _panel = new() { Margin = new Thickness(16, 12, 16, 12) };
    bool _building, _rebuildQueued, _accepted, _closed;
    public bool Accepted => _accepted;
    GCHandle _self;

    public PropertiesWindow(IntPtr source)
    {
        _source = Native.obs_source_get_ref(source); // 画面を開いている間ソースを生かしておく
        var live = Native.obs_source_get_settings(_source);
        _original = Native.Str(Native.obs_data_get_json(live)) ?? "{}";
        Native.obs_data_release(live);
        _settings = Native.obs_data_create_from_json(_original); // 編集用の複製
        _props = Native.obs_source_properties(_source);
        _deferred = _props != IntPtr.Zero && (Native.obs_properties_get_flags(_props) & Native.OBS_PROPERTIES_DEFER_UPDATE) != 0;

        // ソース側から項目の作り直しを求められたら再取得（デバイス一覧の更新など）
        _self = GCHandle.Alloc(this);
        Native.signal_handler_connect(Native.obs_source_get_signal_handler(_source), "update_properties", &OnUpdateProperties, GCHandle.ToIntPtr(_self));

        Title = $"プロパティ — {Native.Str(Native.obs_source_get_name(source))}";
        Width = 560; Height = 640; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Bg; Foreground = Theme.Fg;
        FontFamily = new FontFamily("Yu Gothic UI"); FontSize = 13;

        var ok = Theme.Button("OK", primary: true);
        ok.Click += (_, _) =>
        {
            if (_deferred) Native.obs_source_update(_source, _settings);
            _accepted = true;
            Close();
        };
        var cancel = Theme.Button("キャンセル");
        cancel.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        footer.Children.Add(cancel); footer.Children.Add(ok);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Build();
        Closed += OnClosed;
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _closed = true; // 以降に届く遅延処理・シグナルは何もしない
        Native.signal_handler_disconnect(Native.obs_source_get_signal_handler(_source), "update_properties", &OnUpdateProperties, GCHandle.ToIntPtr(_self));
        _self.Free();
        if (!_accepted && !_deferred)
        {
            var orig = Native.obs_data_create_from_json(_original);
            Native.obs_source_reset_settings(_source, orig);
            Native.obs_data_release(orig);
        }
        if (_props != IntPtr.Zero) Native.obs_properties_destroy(_props);
        _props = IntPtr.Zero;
        Native.obs_data_release(_settings);
        Native.obs_source_release(_source);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnUpdateProperties(IntPtr data, IntPtr calldata)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not PropertiesWindow w) return;
            w.Dispatcher.BeginInvoke(() =>
            {
                if (w._closed) return;
                if (w._props != IntPtr.Zero) Native.obs_properties_destroy(w._props);
                w._props = Native.obs_source_properties(w._source);
                w.Build();
            });
        }
        catch { }
    }

    void Build()
    {
        if (_closed) return;
        _building = true;
        _panel.Children.Clear();
        if (_props == IntPtr.Zero)
            _panel.Children.Add(Theme.Label("このソースには設定項目がありません。"));
        else
            BuildGroup(_props, _panel);
        _building = false;
    }

    void BuildGroup(IntPtr props, Panel target)
    {
        var p = Native.obs_properties_first(props);
        while (p != IntPtr.Zero)
        {
            if (Native.obs_property_visible(p))
            {
                var row = BuildProperty(p);
                if (row != null)
                {
                    row.IsEnabled = Native.obs_property_enabled(p);
                    var tip = Native.Str(Native.obs_property_long_description(p));
                    if (!string.IsNullOrEmpty(tip)) row.ToolTip = tip;
                    target.Children.Add(row);
                }
            }
            if (!Native.obs_property_next(ref p)) break;
        }
    }

    FrameworkElement? BuildProperty(IntPtr p)
    {
        var name = Native.Str(Native.obs_property_name(p)) ?? "";
        var desc = Native.Str(Native.obs_property_description(p)) ?? name;

        switch (Native.obs_property_get_type(p))
        {
            case Native.OBS_PROPERTY_BOOL:
            {
                var cb = new CheckBox { Content = desc, IsChecked = Native.obs_data_get_bool(_settings, name), Foreground = Theme.Fg, Margin = new Thickness(0, 6, 0, 6) };
                cb.Click += (_, _) => Changed(p, () => Native.obs_data_set_bool(_settings, name, cb.IsChecked == true));
                return cb;
            }
            case Native.OBS_PROPERTY_INT:
                return NumberRow(p, desc, Native.obs_data_get_int(_settings, name),
                    Native.obs_property_int_min(p), Native.obs_property_int_max(p), Math.Max(1, Native.obs_property_int_step(p)),
                    Native.obs_property_int_type(p) == Native.OBS_NUMBER_SLIDER, Native.Str(Native.obs_property_int_suffix(p)), isInt: true,
                    v => Native.obs_data_set_int(_settings, name, (long)Math.Round(v)));
            case Native.OBS_PROPERTY_FLOAT:
                return NumberRow(p, desc, Native.obs_data_get_double(_settings, name),
                    Native.obs_property_float_min(p), Native.obs_property_float_max(p), Native.obs_property_float_step(p),
                    Native.obs_property_float_type(p) == Native.OBS_NUMBER_SLIDER, Native.Str(Native.obs_property_float_suffix(p)), isInt: false,
                    v => Native.obs_data_set_double(_settings, name, v));
            case Native.OBS_PROPERTY_TEXT:
            {
                var type = Native.obs_property_text_type(p);
                if (type == Native.OBS_TEXT_INFO)
                    return Theme.Label(desc + (Native.Str(Native.obs_data_get_string(_settings, name)) is { Length: > 0 } s ? "\n" + s : ""), sub: true);
                var val = Native.Str(Native.obs_data_get_string(_settings, name)) ?? "";
                if (type == Native.OBS_TEXT_PASSWORD)
                {
                    var pb = new PasswordBox { Password = val };
                    Theme.StyleInput(pb);
                    pb.PasswordChanged += (_, _) => Changed(p, () => Native.obs_data_set_string(_settings, name, pb.Password));
                    return Row(desc, pb);
                }
                var tb = new TextBox { Text = val };
                Theme.StyleInput(tb);
                if (type == Native.OBS_TEXT_MULTILINE) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.MinHeight = 90; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
                tb.TextChanged += (_, _) => Changed(p, () => Native.obs_data_set_string(_settings, name, tb.Text));
                return Row(desc, tb);
            }
            case Native.OBS_PROPERTY_PATH:
                return PathRow(p, name, desc);
            case Native.OBS_PROPERTY_LIST:
                return ListRow(p, name, desc);
            case Native.OBS_PROPERTY_COLOR:
            case Native.OBS_PROPERTY_COLOR_ALPHA:
                return ColorRow(p, name, desc);
            case Native.OBS_PROPERTY_BUTTON:
            {
                var b = Theme.Button(desc);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 6, 0, 6);
                b.Click += (_, _) => { if (Native.obs_property_button_clicked(p, _source)) QueueRebuild(); };
                return b;
            }
            case Native.OBS_PROPERTY_FONT:
                return FontRow(p, name, desc);
            case Native.OBS_PROPERTY_EDITABLE_LIST:
                return EditableListRow(p, name, desc);
            case Native.OBS_PROPERTY_GROUP:
            {
                var box = new StackPanel();
                var content = Native.obs_property_group_content(p);
                var border = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 8), Child = box };
                if (Native.obs_property_group_type(p) == Native.OBS_GROUP_CHECKABLE)
                {
                    var cb = new CheckBox { Content = desc, IsChecked = Native.obs_data_get_bool(_settings, name), Foreground = Theme.Fg, FontWeight = FontWeights.SemiBold };
                    cb.Click += (_, _) => Changed(p, () => Native.obs_data_set_bool(_settings, name, cb.IsChecked == true));
                    box.Children.Add(cb);
                }
                else box.Children.Add(new TextBlock { Text = desc, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
                if (content != IntPtr.Zero) BuildGroup(content, box);
                return border;
            }
            default:
                return Row(desc, Theme.Label("（この種類の設定はまだ未対応です）", sub: true));
        }
    }

    // 値を settings に書く → modified コールバック → ソースへ即時反映
    void Changed(IntPtr p, Action write)
    {
        if (_building || _closed) return;
        write();
        var refresh = Native.obs_property_modified(p, _settings);
        if (!_deferred) Native.obs_source_update(_source, _settings);
        if (refresh) QueueRebuild();
    }

    // 入力中のコントロールを壊さないよう、再構築は次のディスパッチで
    void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() => { _rebuildQueued = false; Build(); });
    }

    FrameworkElement NumberRow(IntPtr p, string desc, double value, double min, double max, double step, bool slider, string? suffix, bool isInt, Action<double> set)
    {
        var tb = new TextBox { Text = Fmt(value, isInt), Width = 90 };
        Theme.StyleInput(tb);
        var line = new DockPanel();
        if (!string.IsNullOrEmpty(suffix)) { var s = Theme.Label(suffix, sub: true); s.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(s, Dock.Right); line.Children.Add(s); }
        DockPanel.SetDock(tb, Dock.Right);
        line.Children.Add(tb);
        Slider? sl = null;
        if (slider && max > min)
        {
            sl = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            if (step > 0) { sl.TickFrequency = step; sl.IsSnapToTickEnabled = true; }
            sl.ValueChanged += (_, e) => { tb.Text = Fmt(e.NewValue, isInt); };
            line.Children.Add(sl);
        }
        tb.TextChanged += (_, _) =>
        {
            if (!double.TryParse(tb.Text, out var v)) return;
            v = Math.Clamp(v, min, max);
            if (sl != null && Math.Abs(sl.Value - v) > 1e-9) sl.Value = v;
            Changed(p, () => set(v));
        };
        return Row(desc, line);
    }

    static string Fmt(double v, bool isInt) => isInt ? ((long)Math.Round(v)).ToString() : v.ToString("0.###");

    FrameworkElement PathRow(IntPtr p, string name, string desc)
    {
        var tb = new TextBox { Text = Native.Str(Native.obs_data_get_string(_settings, name)) ?? "" };
        Theme.StyleInput(tb);
        tb.TextChanged += (_, _) => Changed(p, () => Native.obs_data_set_string(_settings, name, tb.Text));
        var browse = Theme.Button("参照…");
        browse.Margin = new Thickness(6, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            var type = Native.obs_property_path_type(p);
            var start = Native.Str(Native.obs_property_path_default_path(p));
            string? picked = null;
            if (type == Native.OBS_PATH_DIRECTORY)
            {
                var d = new OpenFolderDialog();
                if (d.ShowDialog(this) == true) picked = d.FolderName;
            }
            else
            {
                FileDialog d = type == Native.OBS_PATH_FILE_SAVE ? new SaveFileDialog() : new OpenFileDialog();
                d.Filter = ConvertFilter(Native.Str(Native.obs_property_path_filter(p)));
                d.RestoreDirectory = true;
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) d.InitialDirectory = start;
                if (d.ShowDialog(this) == true) picked = d.FileName;
            }
            if (picked != null) tb.Text = picked;
        };
        var line = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        line.Children.Add(browse); line.Children.Add(tb);
        return Row(desc, line);
    }

    // OBS のフィルタ書式 "説明 (*.png *.jpg);;すべて (*.*)" → WPF 書式
    static string ConvertFilter(string? f)
    {
        if (string.IsNullOrWhiteSpace(f)) return "すべてのファイル|*.*";
        var parts = new List<string>();
        foreach (var seg in f.Split(";;", StringSplitOptions.RemoveEmptyEntries))
        {
            var open = seg.LastIndexOf('('); var close = seg.LastIndexOf(')');
            if (open < 0 || close < open) continue;
            var pattern = string.Join(";", seg[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            parts.Add($"{seg[..open].Trim()}|{pattern}");
        }
        return parts.Count > 0 ? string.Join("|", parts) : "すべてのファイル|*.*";
    }

    FrameworkElement ListRow(IntPtr p, string name, string desc)
    {
        var format = Native.obs_property_list_format(p);
        var editable = Native.obs_property_list_type(p) == Native.OBS_COMBO_TYPE_EDITABLE;
        var combo = new ComboBox { IsEditable = editable && format == Native.OBS_COMBO_FORMAT_STRING };
        Theme.StyleInput(combo);
        var count = (int)Native.obs_property_list_item_count(p);
        var selected = -1;
        for (var i = 0; i < count; i++)
        {
            var label = Native.Str(Native.obs_property_list_item_name(p, (nuint)i)) ?? "";
            combo.Items.Add(new ComboBoxItem { Content = label, IsEnabled = !Native.obs_property_list_item_disabled(p, (nuint)i) });
            if (selected < 0 && ItemMatches(p, i, name, format)) selected = i;
        }
        combo.SelectedIndex = selected;
        if (combo.IsEditable && selected < 0) combo.Text = Native.Str(Native.obs_data_get_string(_settings, name)) ?? "";
        combo.SelectionChanged += (_, _) =>
        {
            var i = combo.SelectedIndex;
            if (i < 0) return;
            Changed(p, () =>
            {
                switch (format)
                {
                    case Native.OBS_COMBO_FORMAT_INT: Native.obs_data_set_int(_settings, name, Native.obs_property_list_item_int(p, (nuint)i)); break;
                    case Native.OBS_COMBO_FORMAT_FLOAT: Native.obs_data_set_double(_settings, name, Native.obs_property_list_item_float(p, (nuint)i)); break;
                    case Native.OBS_COMBO_FORMAT_BOOL: Native.obs_data_set_bool(_settings, name, Native.obs_property_list_item_bool(p, (nuint)i)); break;
                    default: Native.obs_data_set_string(_settings, name, Native.Str(Native.obs_property_list_item_string(p, (nuint)i)) ?? ""); break;
                }
            });
        };
        if (combo.IsEditable)
            combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
            {
                if (combo.SelectedIndex < 0) Changed(p, () => Native.obs_data_set_string(_settings, name, combo.Text));
            }));
        return Row(desc, combo);
    }

    bool ItemMatches(IntPtr p, int i, string name, int format) => format switch
    {
        Native.OBS_COMBO_FORMAT_INT => Native.obs_property_list_item_int(p, (nuint)i) == Native.obs_data_get_int(_settings, name),
        Native.OBS_COMBO_FORMAT_FLOAT => Math.Abs(Native.obs_property_list_item_float(p, (nuint)i) - Native.obs_data_get_double(_settings, name)) < 1e-6,
        Native.OBS_COMBO_FORMAT_BOOL => Native.obs_property_list_item_bool(p, (nuint)i) == Native.obs_data_get_bool(_settings, name),
        _ => Native.Str(Native.obs_property_list_item_string(p, (nuint)i)) == (Native.Str(Native.obs_data_get_string(_settings, name)) ?? ""),
    };

    // OBS の色は 0xAABBGGRR
    FrameworkElement ColorRow(IntPtr p, string name, string desc)
    {
        var alpha = Native.obs_property_get_type(p) == Native.OBS_PROPERTY_COLOR_ALPHA;
        var swatch = new Border { Width = 36, Height = 22, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        var label = Theme.Label("", sub: true);
        label.Margin = new Thickness(8, 0, 8, 0);
        void Show()
        {
            var v = (uint)Native.obs_data_get_int(_settings, name);
            var c = Color.FromArgb(alpha ? (byte)(v >> 24) : (byte)255, (byte)v, (byte)(v >> 8), (byte)(v >> 16));
            swatch.Background = new SolidColorBrush(c);
            label.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}" + (alpha ? $"  不透明度 {c.A * 100 / 255}%" : "");
        }
        Show();
        var pick = Theme.Button("色を選択…");
        pick.Click += (_, _) =>
        {
            var v = (uint)Native.obs_data_get_int(_settings, name);
            if (!ColorPicker.Pick(this, v & 0xFFFFFF, out var rgb)) return; // COLORREF も 0x00BBGGRR
            var a = alpha ? v & 0xFF000000 : 0xFF000000;
            Changed(p, () => Native.obs_data_set_int(_settings, name, a | rgb));
            Show();
        };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(swatch); line.Children.Add(label); line.Children.Add(pick);
        if (alpha)
        {
            var sl = new Slider { Minimum = 0, Maximum = 255, Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Value = (uint)Native.obs_data_get_int(_settings, name) >> 24 };
            sl.ValueChanged += (_, e) =>
            {
                var v = (uint)Native.obs_data_get_int(_settings, name);
                Changed(p, () => Native.obs_data_set_int(_settings, name, ((uint)e.NewValue << 24) | (v & 0xFFFFFF)));
                Show();
            };
            line.Children.Add(sl);
        }
        return Row(desc, line);
    }

    FrameworkElement FontRow(IntPtr p, string name, string desc)
    {
        var font = Native.obs_data_get_obj(_settings, name);
        var face = font != IntPtr.Zero ? Native.Str(Native.obs_data_get_string(font, "face")) ?? "" : "";
        var size = font != IntPtr.Zero ? Native.obs_data_get_int(font, "size") : 72;
        var flags = font != IntPtr.Zero ? Native.obs_data_get_int(font, "flags") : 0;
        if (font != IntPtr.Zero) Native.obs_data_release(font);

        var faces = new ComboBox { IsEditable = true, Text = face, MinWidth = 200 };
        Theme.StyleInput(faces);
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s)) faces.Items.Add(f);
        var sizeBox = new TextBox { Text = size.ToString(), Width = 60, Margin = new Thickness(6, 0, 0, 0) };
        Theme.StyleInput(sizeBox);
        var bold = new CheckBox { Content = "太字", IsChecked = (flags & 1) != 0, Foreground = Theme.Fg, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var italic = new CheckBox { Content = "斜体", IsChecked = (flags & 2) != 0, Foreground = Theme.Fg, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        void Apply()
        {
            if (_closed) return;
            if (!long.TryParse(sizeBox.Text, out var sz) || sz <= 0) return;
            Changed(p, () =>
            {
                var obj = Native.obs_data_create();
                Native.obs_data_set_string(obj, "face", faces.Text);
                Native.obs_data_set_string(obj, "style", "");
                Native.obs_data_set_int(obj, "size", sz);
                Native.obs_data_set_int(obj, "flags", (bold.IsChecked == true ? 1 : 0) | (italic.IsChecked == true ? 2 : 0));
                Native.obs_data_set_obj(_settings, name, obj);
                Native.obs_data_release(obj);
            });
        }
        faces.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(Apply);
        faces.LostFocus += (_, _) => Apply();
        sizeBox.TextChanged += (_, _) => Apply();
        bold.Click += (_, _) => Apply();
        italic.Click += (_, _) => Apply();

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(faces); line.Children.Add(sizeBox); line.Children.Add(bold); line.Children.Add(italic);
        return Row(desc, line);
    }

    // 文字列/ファイル/URLのリスト（スライドショー、VLCプレイリストなど）。要素は {value, selected, hidden}
    sealed record ListEntry(string Value, bool Selected, bool Hidden) { public override string ToString() => Value; }

    FrameworkElement EditableListRow(IntPtr p, string name, string desc)
    {
        var list = new ListBox { MinHeight = 80, MaxHeight = 180, Background = Theme.Panel, Foreground = Theme.Fg, BorderBrush = Theme.Line };
        var arr = Native.obs_data_get_array(_settings, name);
        if (arr != IntPtr.Zero)
        {
            for (nuint i = 0; i < Native.obs_data_array_count(arr); i++)
            {
                var item = Native.obs_data_array_item(arr, i);
                list.Items.Add(new ListEntry(Native.Str(Native.obs_data_get_string(item, "value")) ?? "",
                                             Native.obs_data_get_bool(item, "selected"), Native.obs_data_get_bool(item, "hidden")));
                Native.obs_data_release(item);
            }
            Native.obs_data_array_release(arr);
        }
        void Save() => Changed(p, () =>
        {
            var a = Native.obs_data_array_create();
            foreach (ListEntry e in list.Items)
            {
                var o = Native.obs_data_create();
                Native.obs_data_set_string(o, "value", e.Value);
                Native.obs_data_set_bool(o, "selected", e.Selected);
                Native.obs_data_set_bool(o, "hidden", e.Hidden);
                Native.obs_data_array_push_back(a, o);
                Native.obs_data_release(o);
            }
            Native.obs_data_set_array(_settings, name, a);
            Native.obs_data_array_release(a);
        });

        var type = Native.obs_property_editable_list_type(p);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (type is Native.OBS_EDITABLE_LIST_TYPE_FILES or Native.OBS_EDITABLE_LIST_TYPE_FILES_AND_URLS)
        {
            var addFile = Theme.Button("ファイルを追加…");
            addFile.Margin = new Thickness(0, 0, 6, 0);
            addFile.Click += (_, _) =>
            {
                var d = new OpenFileDialog { Multiselect = true, RestoreDirectory = true, Filter = ConvertFilter(Native.Str(Native.obs_property_editable_list_filter(p))) };
                if (d.ShowDialog(this) != true) return;
                foreach (var f in d.FileNames) list.Items.Add(new ListEntry(f, false, false));
                Save();
            };
            buttons.Children.Add(addFile);
        }
        if (type is Native.OBS_EDITABLE_LIST_TYPE_STRINGS or Native.OBS_EDITABLE_LIST_TYPE_FILES_AND_URLS)
        {
            var addText = Theme.Button(type == Native.OBS_EDITABLE_LIST_TYPE_STRINGS ? "追加…" : "URLを追加…");
            addText.Margin = new Thickness(0, 0, 6, 0);
            addText.Click += (_, _) =>
            {
                var v = InputDialog.Ask(this, desc, type == Native.OBS_EDITABLE_LIST_TYPE_STRINGS ? "追加する文字列" : "URL");
                if (v == null) return;
                list.Items.Add(new ListEntry(v, false, false));
                Save();
            };
            buttons.Children.Add(addText);
        }
        void MoveBy(int d)
        {
            var i = list.SelectedIndex;
            if (i < 0 || i + d < 0 || i + d >= list.Items.Count) return;
            var e = list.Items[i];
            list.Items.RemoveAt(i);
            list.Items.Insert(i + d, e);
            list.SelectedIndex = i + d;
            Save();
        }
        var up = Theme.Button("↑"); up.MinWidth = 32; up.Margin = new Thickness(0, 0, 6, 0); up.Click += (_, _) => MoveBy(-1);
        var down = Theme.Button("↓"); down.MinWidth = 32; down.Margin = new Thickness(0, 0, 6, 0); down.Click += (_, _) => MoveBy(1);
        var remove = Theme.Button("削除"); remove.Margin = new Thickness(0);
        remove.Click += (_, _) => { if (list.SelectedItem != null) { list.Items.Remove(list.SelectedItem); Save(); } };
        buttons.Children.Add(up); buttons.Children.Add(down); buttons.Children.Add(remove);

        var box = new StackPanel();
        box.Children.Add(list); box.Children.Add(buttons);
        return Row(desc, box);
    }

    static FrameworkElement Row(string label, UIElement input)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = Theme.Sub, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(input);
        return sp;
    }
}

// Win32 の色選択ダイアログ（WinForms を持ち込まないため）
static class ColorPicker
{
    [StructLayout(LayoutKind.Sequential)]
    struct CHOOSECOLOR
    {
        public int lStructSize; public IntPtr hwndOwner, hInstance; public uint rgbResult; public IntPtr lpCustColors;
        public int Flags; public IntPtr lCustData, lpfnHook, lpTemplateName;
    }
    [DllImport("comdlg32", EntryPoint = "ChooseColorW")] static extern bool ChooseColor(ref CHOOSECOLOR cc);
    static readonly IntPtr Custom = AllocCustom();
    static IntPtr AllocCustom()
    {
        var p = Marshal.AllocHGlobal(16 * 4);
        for (var i = 0; i < 16; i++) Marshal.WriteInt32(p, i * 4, 0xFFFFFF);
        return p;
    }

    public static bool Pick(Window owner, uint initial, out uint rgb)
    {
        var cc = new CHOOSECOLOR
        {
            lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
            hwndOwner = new System.Windows.Interop.WindowInteropHelper(owner).Handle,
            rgbResult = initial, lpCustColors = Custom, Flags = 0x1 | 0x2, // CC_RGBINIT | CC_FULLOPEN
        };
        var ok = ChooseColor(ref cc);
        rgb = cc.rgbResult & 0xFFFFFF;
        return ok;
    }
}
