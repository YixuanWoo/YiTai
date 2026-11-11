// ============================================================
// MainWindow.Panes.cs —— 对照栏：多文档同屏
// ============================================================
//
// 摆在窗口里的是这样一排：
//
//   [资料] [原文] [另一种语言的译文] …… [译文]
//     ↑ 可增减、可左右调、可换成任意文件      ↑ 钉死在最右，只有它能写
//
// 三条规矩（写死在代码里，不做成开关）：
//   1. 译文永远最右。对照栏只能在自己这一侧挪，挪不过译文。
//   2. 对照栏只读，但会"画"出来：Markdown 的加粗、标题、列表、代码块都渲染，
//      不是一堆星号。多开几栏是为了"看得见"，不是为了"都能改"。
//   3. 每栏各记各的。关掉软件再打开，还是上次那几栏、上次的文件、上次的显示比例。
//
// 缩放（70%~250%）只改这一栏的字号基准，然后用 FlowDocument 按当前栏宽重画：
// 放大是"字变大、行重新折"，不是把内容拉宽挤出栏外。
//
// 为什么布局不进 project.json：那是"这本书怎么翻"；栏怎么摆是
// "这个软件上次什么样"，跟 AppSettings 一起放在 %APPDATA%\YiTai。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow
{
    /// <summary>对照栏上限。再多每栏只剩一条缝，不如开两个窗口。</summary>
    private const int MaxRefPanes = 4;

    /// <summary>对照栏正文的基准字号（100% 时）。</summary>
    private const double BaseFontSize = 13.5;

    private const double MinZoom = 0.7;
    private const double MaxZoom = 2.5;
    private const double ZoomStep = 0.1;

    private readonly List<PaneView> _panes = new();

    /// <summary>各栏宽度比例（依次 对照栏…、译文栏）；空 = 用默认。</summary>
    private List<double> _weights = new();

    /// <summary>下拉里还有一项「打开别的文件…」，选中它只是去弹对话框，不是真换了文档。</summary>
    private bool _choiceGuard;

    private sealed class PaneView
    {
        public RefDocSpec Spec = new();
        public Border Root = null!;

        /// <summary>只读的渲染区。Markdown 在这里画成带格式的文档，选中复制归框架管。</summary>
        public FlowDocumentScrollViewer Body = null!;

        public TextBlock Caption = null!;

        /// <summary>底部那枚显示百分比的按钮，点一下回 100%。</summary>
        public Button ZoomLabel = null!;

        public ComboBox Choice = null!;
    }

    /// <summary>下拉里的一项：显示名 + 它对应的选择。</summary>
    private sealed class PaneChoice
    {
        public string Display = "";
        public RefDocSpec Spec = new();
        public bool Browse;

        // ComboBox 没设 DisplayMemberPath 时按 ToString 显示
        public override string ToString() => Display;
    }

    // ================================================================ 生命周期

    /// <summary>窗口刚建好：先摆一栏"跟随当前章"，等项目打开再按项目记忆替换。</summary>
    private void InitPanes()
    {
        _panes.Clear();
        _panes.Add(NewPane(new RefDocSpec { Kind = RefKind.FollowSource }));
        RebuildPanes();
    }

    /// <summary>换项目时调用：恢复这个项目上次的栏位，没有就只给一栏原文。</summary>
    private void LoadPanesForProject()
    {
        _panes.Clear();
        _weights.Clear();

        string key = PaneSettingsKey();
        List<RefDocSpec>? saved = null;
        if (key.Length > 0 && _settings.PanesByProject.TryGetValue(key, out var list) && list != null)
            saved = list;

        if (saved != null && saved.Count > 0)
        {
            foreach (var s in saved)
            {
                if (_panes.Count >= MaxRefPanes) break;
                if (s == null) continue;
                _panes.Add(NewPane(s));
            }
        }

        if (_panes.Count == 0)
            _panes.Add(NewPane(new RefDocSpec { Kind = RefKind.FollowSource }));

        if (key.Length > 0 && _settings.PaneWeightsByProject.TryGetValue(key, out var w) && w != null)
        {
            _weights.Clear();
            foreach (double d in w) _weights.Add(d);
        }

        RebuildPanes();
    }

    /// <summary>把当前栏位写进设置（只进内存，落盘由窗口关闭时统一做）。</summary>
    private void SavePaneLayout()
    {
        string key = PaneSettingsKey();
        if (key.Length == 0) return;

        var list = new List<RefDocSpec>();
        foreach (var p in _panes) list.Add(CloneSpec(p.Spec));
        _settings.PanesByProject[key] = list;

        CaptureWidths();
        _settings.PaneWeightsByProject[key] = new List<double>(_weights);
    }

    private string PaneSettingsKey()
        => _store == null ? "" : _store.Root.ToLowerInvariant();

    // ================================================================ 排版

    /// <summary>
    /// 重建整排：清掉所有列和子元素，按 _panes 顺序重新摆一遍，
    /// 最后把译文栏钉到最右。
    ///
    /// 之所以敢整排重建：一栏就是"一个只读 TextBox + 一个下拉"，
    /// 重建代价极小，而增量挪动反而容易让列号错位。
    /// </summary>
    private void RebuildPanes()
    {
        CaptureWidths();

        PanesGrid.ColumnDefinitions.Clear();
        PanesGrid.Children.Clear();

        if (_panes.Count == 0)
        {
            PanesGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 320,
            });
            Grid.SetColumn(TransPane, 0);
            PanesGrid.Children.Add(TransPane);
            RefreshPanes();
            return;
        }

        int need = _panes.Count + 1;
        int col = 0;

        for (int i = 0; i < _panes.Count; i++)
        {
            PanesGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = StarFor(i, need),
                MinWidth = 200,
            });

            var root = BuildPane(_panes[i], i);
            _panes[i].Root = root;
            Grid.SetColumn(root, col);
            PanesGrid.Children.Add(root);
            col++;

            PanesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var splitter = new GridSplitter
            {
                Style = (Style)FindResource("SplitterThin"),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Columns,
            };
            Grid.SetColumn(splitter, col);
            PanesGrid.Children.Add(splitter);
            col++;
        }

        PanesGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = StarFor(_panes.Count, need),
            MinWidth = 320,
        });

        Grid.SetColumn(TransPane, col);
        PanesGrid.Children.Add(TransPane);

        RefreshPanes();
    }

    private GridLength StarFor(int index, int need)
    {
        if (_weights.Count == need && _weights[index] > 0)
            return new GridLength(_weights[index], GridUnitType.Star);

        // 默认：对照栏等分，译文栏略宽（要写字，也要看得见段）
        return index == need - 1
            ? new GridLength(1.25, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
    }

    /// <summary>把眼下各列的实际宽度换成比例记住，重建时照它还回去。</summary>
    private void CaptureWidths()
    {
        var defs = PanesGrid.ColumnDefinitions;
        if (_panes.Count == 0) return;
        if (defs.Count != _panes.Count * 2 + 1) return;   // 栏数刚变过，旧宽度不作数

        var vals = new List<double>();
        double sum = 0;

        for (int i = 0; i < defs.Count; i += 2)
        {
            double v = defs[i].ActualWidth;
            if (v <= 0) v = 1;
            vals.Add(v);
            sum += v;
        }

        if (sum <= 0) return;

        _weights.Clear();
        foreach (double v in vals) _weights.Add(v / sum * vals.Count);
    }

    // ================================================================ 单栏外观

    private Border BuildPane(PaneView pane, int index)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("BgCard"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        var dock = new DockPanel();

        // ---------------------------------------------------------- 顶部条
        var head = new Border
        {
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7, 6, 6),
        };

        var headDock = new DockPanel();

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var moveLeft = MiniButton("◀", "往前挪一栏", () => MovePane(index, -1));
        moveLeft.IsEnabled = index > 0;

        var moveRight = MiniButton("▶", "往后挪一栏", () => MovePane(index, +1));
        moveRight.IsEnabled = index < _panes.Count - 1;

        tools.Children.Add(moveLeft);
        tools.Children.Add(moveRight);
        tools.Children.Add(MiniButton("✕", "关掉这一栏", () => RemovePane(index)));

        DockPanel.SetDock(tools, Dock.Right);
        headDock.Children.Add(tools);

        var cb = new ComboBox
        {
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var choices = BuildChoices();
        cb.ItemsSource = choices;
        cb.SelectedItem = MatchChoice(choices, pane.Spec);   // 先定值再挂事件，免得一建好就触发一次
        cb.SelectionChanged += (_, _) => OnChoiceChanged(pane, cb);
        headDock.Children.Add(cb);

        pane.Choice = cb;
        head.Child = headDock;
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        // ---------------------------------------------------------- 底部：字数 + 缩放
        var footBar = new Border { Padding = new Thickness(12, 1, 6, 3) };

        var foot = new DockPanel();

        var footTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        footTools.Children.Add(MiniButton("−", "缩小这一栏（Ctrl+滚轮也行）", () => ChangeZoom(index, -ZoomStep)));

        var zoomButton = MiniButton(Pct(Math.Clamp(pane.Spec.Zoom, MinZoom, MaxZoom)),
                                    "点一下回到 100%", () => ResetZoom(index));
        zoomButton.MinWidth = 46;
        pane.ZoomLabel = zoomButton;
        footTools.Children.Add(zoomButton);

        footTools.Children.Add(MiniButton("+", "放大这一栏（Ctrl+滚轮也行）", () => ChangeZoom(index, +ZoomStep)));

        DockPanel.SetDock(footTools, Dock.Right);
        foot.Children.Add(footTools);

        var footInfo = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("TextFaint"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pane.Caption = footInfo;
        foot.Children.Add(footInfo);

        footBar.Child = foot;
        DockPanel.SetDock(footBar, Dock.Bottom);
        dock.Children.Add(footBar);

        // ---------------------------------------------------------- 正文：渲染后的 Markdown
        // 用 FlowDocumentScrollViewer 而不是 TextBox：
        //   · 加粗/斜体/标题/列表/代码块都画得出来；
        //   · 它按"给多宽就按多宽折行"排版，所以放大字号只会变高、不会溢出栏宽。
        var body = new FlowDocumentScrollViewer
        {
            IsToolBarVisible = false,
            IsSelectionEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        // 鼠标停在这一栏上按 Ctrl+滚轮，就是缩放这一栏
        body.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            ChangeZoom(index, e.Delta > 0 ? ZoomStep : -ZoomStep);
            e.Handled = true;
        };

        pane.Body = body;
        dock.Children.Add(body);

        border.Child = dock;
        return border;
    }

    private Button MiniButton(string text, string tip, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("BtnMini"),
            ToolTip = tip,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(5, 2, 5, 2),
            MinWidth = 22,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ================================================================ 栏的几个动作

    private void OnAddPane(object sender, RoutedEventArgs e) => AddPane();

    private void AddPane()
    {
        if (_panes.Count >= MaxRefPanes)
        {
            Warn("最多 4 栏", $"对照栏最多 {MaxRefPanes} 个。再多每栏只剩一条缝，不如关掉一个、或者开第二个窗口。");
            return;
        }

        _panes.Add(NewPane(new RefDocSpec { Kind = RefKind.FollowSource }));
        _weights.Clear();   // 栏数变了，旧比例不适用
        RebuildPanes();
        SavePaneLayout();
    }

    private void RemovePane(int index)
    {
        if (index < 0 || index >= _panes.Count) return;

        _panes.RemoveAt(index);
        _weights.Clear();
        RebuildPanes();
        SavePaneLayout();
    }

    /// <summary>和相邻的一栏换位。译文栏不参与换位，所以到边界就不动。</summary>
    private void MovePane(int index, int delta)
    {
        if (index < 0 || index >= _panes.Count) return;

        int target = index + delta;
        if (target < 0 || target >= _panes.Count) return;

        var p = _panes[index];
        _panes.RemoveAt(index);
        _panes.Insert(target, p);

        RebuildPanes();
        SavePaneLayout();
    }

    // ================================================================ 选文件

    /// <summary>
    /// 这一栏能选什么：跟随当前章 / 项目里每一份原文 / 项目里每一份译文 /
    /// 已经打开过的外部文件 / 最后一个"打开别的文件…"。
    /// </summary>
    private List<PaneChoice> BuildChoices()
    {
        var list = new List<PaneChoice>();

        list.Add(new PaneChoice
        {
            Display = "原文 · 跟随当前章",
            Spec = new RefDocSpec { Kind = RefKind.FollowSource },
        });

        if (_store != null)
        {
            foreach (var s in _store.Meta.Sources)
            {
                list.Add(new PaneChoice
                {
                    Display = "原文 · " + s.FileName,
                    Spec = new RefDocSpec { Kind = RefKind.Source, Path = s.FileName, Label = s.FileName },
                });

                string abs = _store.TranslationPath(s);
                string rel = abs.StartsWith(_store.Root, StringComparison.OrdinalIgnoreCase)
                    ? ProjectLayout.ToRelative(_store.Root, abs)
                    : abs;

                list.Add(new PaneChoice
                {
                    Display = "译文 · " + Path.GetFileName(abs),
                    Spec = new RefDocSpec { Kind = RefKind.Translation, Path = rel, Label = Path.GetFileName(abs) },
                });
            }
        }

        // 已经作为外部文件开着的，补回下拉，别让它一重建就从列表里消失
        foreach (var p in _panes)
        {
            if (p.Spec.Kind != RefKind.External) continue;
            if (list.Exists(c => !c.Browse && SameSpec(c.Spec, p.Spec))) continue;

            list.Add(new PaneChoice
            {
                Display = "文件 · " + Path.GetFileName(p.Spec.Path),
                Spec = CloneSpec(p.Spec),
            });
        }

        list.Add(new PaneChoice { Display = "打开别的文件…", Browse = true });

        return list;
    }

    private void OnChoiceChanged(PaneView pane, ComboBox cb)
    {
        if (_choiceGuard) return;
        if (cb.SelectedItem is not PaneChoice choice) return;

        // 选了最后那一项：去弹文件对话框。取消了就把下拉拨回原样
        if (choice.Browse)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "打开一份对照文档（只读，不会进项目）",
                Filter = "文本 (*.md;*.txt;*.markdown)|*.md;*.txt;*.markdown|所有文件 (*.*)|*.*",
            };

            if (dlg.ShowDialog(this) != true)
            {
                _choiceGuard = true;
                cb.SelectedItem = MatchChoice(BuildChoices(), pane.Spec);
                _choiceGuard = false;
                return;
            }

            pane.Spec = new RefDocSpec
            {
                Kind = RefKind.External,
                Path = dlg.FileName,
                Label = Path.GetFileName(dlg.FileName),
                Zoom = pane.Spec.Zoom,
            };

            RebuildPanes();     // 下拉要把这份文件补进去
            SavePaneLayout();
            return;
        }

        double keepZoom = pane.Spec.Zoom;   // 换文档不重置这一栏的显示比例
        pane.Spec = CloneSpec(choice.Spec);
        pane.Spec.Zoom = keepZoom;
        RefreshOne(pane);
        SavePaneLayout();
    }

    private PaneChoice? MatchChoice(List<PaneChoice> list, RefDocSpec spec)
    {
        foreach (var c in list)
            if (!c.Browse && SameSpec(c.Spec, spec)) return c;

        return list.Count > 0 ? list[0] : null;
    }

    // ================================================================ 往栏里填字

    private void RefreshPanes()
    {
        foreach (var p in _panes) RefreshOne(p);
        BtnAddPane.IsEnabled = _panes.Count < MaxRefPanes;
    }

    private void RefreshOne(PaneView pane)
    {
        if (pane.Body == null) return;   // 刚加进来还没画

        double zoom = Math.Clamp(pane.Spec.Zoom, MinZoom, MaxZoom);
        string text = ResolvePaneContent(pane.Spec, out string note, out bool exists, out bool isMarkdown);
        var style = MakeStyle(zoom);

        try
        {
            if (!exists)
                pane.Body.Document = MdRender.Message(text, style);
            else if (isMarkdown)
                pane.Body.Document = MdRender.Build(MdParser.Parse(text), style);
            else
                pane.Body.Document = MdRender.Plain(text, style);   // .txt 只排版，不解释格式
        }
        catch (Exception)
        {
            // 渲染出岔子也不能让这一栏空白：退回"整段当纯文字"
            pane.Body.Document = MdRender.Message(text, style);
        }

        if (pane.Caption != null) pane.Caption.Text = note;
    }

    /// <summary>一栏该显示什么。exists=false 表示这段字是提示语，不是文件内容。</summary>
    private string ResolvePaneContent(RefDocSpec spec, out string note, out bool exists, out bool isMarkdown)
    {
        if (spec.Kind == RefKind.FollowSource)
        {
            if (_store == null)
            {
                note = "还没打开项目";
                exists = false;
                isMarkdown = false;
                return "先从「文件 → 新建项目 / 打开项目」开始：\n\n" +
                       "选一个文件夹当项目根，里面会建 sources（原文）、translations（译文）、backups（备份）。\n" +
                       "这一栏会一直跟着当前章的原文走，切章自动换。";
            }

            if (_current == null)
            {
                note = "项目里还没有源文件";
                exists = false;
                isMarkdown = false;
                return "这个项目还没有源文件。文件 → 导入源文件，选几个 md/txt 进来。";
            }

            return ReadForDisplay(_store.SourcePath(_current), "原文 · " + _current.FileName, out note, out exists, out isMarkdown);
        }

        if (spec.Kind == RefKind.Source)
        {
            if (_store == null)
            {
                note = "还没打开项目";
                exists = false;
                isMarkdown = false;
                return "";
            }

            return ReadForDisplay(
                Path.Combine(ProjectLayout.Sources(_store.Root), spec.Path),
                "原文 · " + spec.Path, out note, out exists, out isMarkdown);
        }

        if (spec.Kind == RefKind.Translation)
        {
            string abs = _store == null
                ? spec.Path
                : ProjectLayout.ToAbsolute(_store.Root, spec.Path);

            return ReadForDisplay(abs, "译文 · " + Path.GetFileName(abs), out note, out exists, out isMarkdown);
        }

        return ReadForDisplay(spec.Path, "文件 · " + Path.GetFileName(spec.Path), out note, out exists, out isMarkdown);
    }

    private string ReadForDisplay(string path, string caption, out string note, out bool exists, out bool isMarkdown)
    {
        isMarkdown = IsMarkdownPath(path);
        var info = TextFile.Read(path);

        if (!info.Exists)
        {
            note = caption + " · 读不到";
            exists = false;
            return "读不到这个文件（可能被移走、改名或删了）：\n\n" + path;
        }

        note = caption + " · " + TextStats.CountChars(info.Text) + " 字 · " +
               TextStats.CountParagraphs(info.Text) + " 段";
        exists = true;
        return info.Text;
    }

    /// <summary>.md / .markdown 才按 Markdown 画；其它（.txt 等）当纯文本，免得误吃格式。</summary>
    private static bool IsMarkdownPath(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ 缩放

    /// <summary>改这一栏的显示比例。只重画这一栏，不动别的栏，也不动栏宽。</summary>
    private void ChangeZoom(int index, double delta)
    {
        if (index < 0 || index >= _panes.Count) return;

        var pane = _panes[index];
        double now = Math.Clamp(pane.Spec.Zoom, MinZoom, MaxZoom);
        double next = Math.Round(now + delta, 2);

        if (next < MinZoom) next = MinZoom;
        if (next > MaxZoom) next = MaxZoom;
        if (Math.Abs(next - now) < 0.001) return;

        pane.Spec.Zoom = next;
        if (pane.ZoomLabel != null) pane.ZoomLabel.Content = Pct(next);

        RefreshOne(pane);
        SavePaneLayout();
    }

    private void ResetZoom(int index)
    {
        if (index < 0 || index >= _panes.Count) return;

        var pane = _panes[index];
        if (Math.Abs(pane.Spec.Zoom - 1.0) < 0.001)
        {
            RefreshOne(pane);   // 本来就在 100%：重新读一遍，等于"刷新"
            return;
        }

        pane.Spec.Zoom = 1.0;
        if (pane.ZoomLabel != null) pane.ZoomLabel.Content = "100%";
        RefreshOne(pane);
        SavePaneLayout();
    }

    private static string Pct(double zoom) => (zoom * 100).ToString("0") + "%";

    /// <summary>主题里的颜色 + 当前比例，凑成渲染器要的一套样式。</summary>
    private MdStyle MakeStyle(double zoom) => MakeStyleFor(BaseFontSize * zoom, 1.7);

    /// <summary>按给定字号取一套样式（译文预览也用它，免得两处字号各写一套）。</summary>
    private MdStyle MakeStyleFor(double fontSize, double lineSpacing) => new MdStyle
    {
        Body = new FontFamily("Microsoft YaHei"),
        Mono = new FontFamily("Consolas"),
        Text = (Brush)FindResource("TextMain"),
        Dim = (Brush)FindResource("TextFaint"),
        Accent = (Brush)FindResource("Accent"),
        CodeBg = (Brush)FindResource("BgWrite"),
        CodeFg = (Brush)FindResource("TextDim"),
        QuoteLine = (Brush)FindResource("Line"),
        FontSize = fontSize,
        LineSpacing = lineSpacing,
    };

    // ================================================================ 小工具

    private PaneView NewPane(RefDocSpec spec) => new PaneView { Spec = CloneSpec(spec) };

    private static RefDocSpec CloneSpec(RefDocSpec s) => new RefDocSpec
    {
        Kind = s.Kind,
        Path = s.Path,
        Label = s.Label,
        Zoom = s.Zoom,
    };

    private static bool SameSpec(RefDocSpec a, RefDocSpec b)
        => a.Kind == b.Kind && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
}
