// ============================================================
// AnnotationWindows.cs —— 批注的小输入框与"本页批注"列表
// ============================================================
//
// 两个窗口都刻意做得很小，不另开一套设计：
//   · AnnotationEditorWindow —— 写/改一条批注正文（多行，Ctrl+Enter 保存）；
//   · AnnotationPanelWindow  —— 列出当前这篇译文的全部批注，可定位 / 改 / 删。
//
// 颜色与按钮样式一律沿用主窗口 Theme.xaml 里的资源（DynamicResource 同款取法），
// 不在这里另发明一套主题。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

/// <summary>写一条批注的小窗。确定返回正文，取消返回 null。</summary>
public sealed class AnnotationEditorWindow : Window
{
    private readonly TextBox _box;
    private string? _result;

    private AnnotationEditorWindow(string title, string initial, string quote)
    {
        Title = title;
        Width = 520;
        Height = 340;
        MinWidth = 380;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei");

        SetResourceReference(Window.BackgroundProperty, "BgCard");
        SetResourceReference(Window.ForegroundProperty, "TextMain");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string excerpt = quote.Replace("\n", " ");
        if (excerpt.Length > 80) excerpt = excerpt.Substring(0, 80) + "…";

        var head = new TextBlock
        {
            Text = "「" + excerpt + "」",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        Grid.SetRow(head, 0);
        grid.Children.Add(head);

        _box = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12.5,
        };
        _box.SetResourceReference(Control.BackgroundProperty, "BgWrite");
        _box.SetResourceReference(Control.ForegroundProperty, "TextMain");
        _box.SetResourceReference(Control.BorderBrushProperty, "Line");
        Grid.SetRow(_box, 1);
        grid.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var cancel = MiniButton("取消");
        cancel.Click += (_, _) => { _result = null; Close(); };

        var ok = MiniButton("保存（Ctrl+Enter）");
        ok.IsDefault = false;
        ok.Click += (_, _) => Accept();

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Accept();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _result = null;
                Close();
            }
        };

        Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }

    private void Accept()
    {
        string t = _box.Text.Trim();
        if (t.Length == 0) { MessageBox.Show(this, "批注正文不能是空的。", Title); return; }
        _result = t;
        Close();
    }

    private Button MiniButton(string text)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        if (TryFindResource("BtnMini") is Style s) b.Style = s;
        return b;
    }

    /// <summary>弹窗写一条批注。返回正文；取消返回 null。</summary>
    public static string? Ask(Window owner, string title, string initial, string quote)
    {
        var w = new AnnotationEditorWindow(title, initial, quote) { Owner = owner };
        w.ShowDialog();
        return w._result;
    }
}

/// <summary>"本页批注"列表：定位 / 改 / 删。模型窗口，跟着主窗口走。</summary>
public sealed class AnnotationPanelWindow : Window
{
    private readonly StackPanel _list = new StackPanel();
    private readonly TextBlock _summary = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Func<List<AnnotationView>> _provider;
    private readonly Action<AnnotationView> _edit;
    private readonly Action<AnnotationView> _delete;
    private readonly Action<AnnotationView> _locate;

    public AnnotationPanelWindow(Window owner, Func<List<AnnotationView>> provider,
                                 Action<AnnotationView> edit, Action<AnnotationView> delete,
                                 Action<AnnotationView> locate)
    {
        _provider = provider;
        _edit = edit;
        _delete = delete;
        _locate = locate;

        Title = "本页批注";
        Width = 460;
        Height = 520;
        MinWidth = 360;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei");

        if (TryFindResource("BgCard") is Brush bg) Background = bg;
        if (TryFindResource("TextMain") is Brush fg) Foreground = fg;

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);

        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        root.Children.Add(scroll);

        Content = root;
        Reload();
    }

    /// <summary>重新读一遍批注并重画列表。批注增删改之后调它。</summary>
    public void Reload()
    {
        _list.Children.Clear();

        var views = _provider();
        int anchored = 0;
        foreach (var v in views) if (v.Anchored) anchored++;

        int loose = views.Count - anchored;
        string looseText = loose > 0 ? "，" + loose + " 条未定位" : "";

        _summary.Text = views.Count == 0
            ? "这篇译文还没有批注。在正文里选中文字 → 右键「为选中文字加批注…」。"
            : $"共 {views.Count} 条批注（{anchored} 条钉在正文上{looseText}）。";

        foreach (var v in views) _list.Children.Add(Row(v));
    }

    private Border Row(AnnotationView v)
    {
        string excerpt = (v.Item.Quote ?? "").Replace("\n", " ");
        if (excerpt.Length > 70) excerpt = excerpt.Substring(0, 70) + "…";

        var stack = new StackPanel();

        var quote = new TextBlock
        {
            Text = "「" + excerpt + "」",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        quote.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        stack.Children.Add(quote);

        var body = new TextBlock
        {
            Text = v.Item.Body ?? "",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
        stack.Children.Add(body);

        if (!v.Anchored)
        {
            var warn = new TextBlock
            {
                Text = "未定位：正文改过，找不到这几个字了（批注已保留，没有硬贴到别处）",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            stack.Children.Add(warn);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };

        if (v.Anchored)
        {
            var go = MiniButton("定位");
            go.Click += (_, _) => _locate(v);
            buttons.Children.Add(go);
        }

        var edit = MiniButton("改");
        edit.Click += (_, _) => { _edit(v); Reload(); };
        buttons.Children.Add(edit);

        var del = MiniButton("删");
        del.Click += (_, _) =>
        {
            _delete(v);
            Reload();
        };
        buttons.Children.Add(del);

        stack.Children.Add(buttons);

        var box = new Border
        {
            Child = stack,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        box.SetResourceReference(Border.BorderBrushProperty, "Line");
        box.SetResourceReference(Border.BackgroundProperty, "BgCardAlt");

        return box;
    }

    private Button MiniButton(string text)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 56 };
        if (TryFindResource("BtnMini") is Style s) b.Style = s;
        return b;
    }
}
