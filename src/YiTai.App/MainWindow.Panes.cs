// ============================================================
// MainWindow.Panes.cs —— 对照栏：多文档同屏
// ============================================================
//
// 摆在窗口里的是这样一排：
//
//   [资料] [另一种语言的译文] [PDF 原件] …… [译文]
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
// 为什么布局不进 project.json、而放 .yitai\panes.json：
//   project.json 是"这本书怎么翻"的清单，schema 要稳；
//   栏怎么摆是"我上次怎么对照着看"，跟着项目走（整个文件夹拷走、换台机器也还是那几栏），
//   但不该混进清单。全局设置里只留一份兼容用的旧记录。
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

        /// <summary>
        /// 只读的渲染区。Markdown 在这儿画成带格式的文档，选中复制归框架管。
        /// 用只读 RichTextBox 而不是纯显示控件，是因为只有它能把"鼠标点"换成"文档里的位置"。
        /// </summary>
        public RichTextBox Body = null!;

        /// <summary>
        /// 进度竖线。浮在正文上面的一层，不属于文档：
        /// 插进文档会改排版（切开段落、行距变动），而"译到哪了"不该动正文一个字的排布。
        /// </summary>
        public System.Windows.Shapes.Rectangle? Line;

        public Canvas? LineLayer;

        /// <summary>
        /// 这一栏现在显示的是真文件（不是"还没打开项目"这类提示语），
        /// 并且能点出"译到这里"。资料、译文、导入的 md/txt/pdf 都算。
        /// </summary>
        public bool Markable;

        /// <summary>
        /// 进度记在哪个名字下。一律用栏里那份文件的文件名。
        /// 于是"在法语 PDF 上点"记的是法国 PDF 的进度，不会把别的栏的进度改掉。
        /// </summary>
        public string ProgressKey = "";

        public TextBlock Caption = null!;

        /// <summary>
        /// 这一栏如果是 PDF，正文换成 WebView2（Edge 内核直接把原件画出来：图、版式、页码都在）。
        /// 这种情况下 Body 是 null，也不再有点选进度那套（拿不到文字坐标）。
        /// </summary>
        public Microsoft.Web.WebView2.Wpf.WebView2? PdfView;

        /// <summary>这一栏当前排出来的正文。底栏"译到 X/Y 字"要用它数非空白字，改竖线时不必再读一遍文件。</summary>
        public string PaneText = "";

        /// <summary>底栏原本该写的字（字数/段数）；进度那句话是接在它后面的。</summary>
        public string BaseCaption = "";

        /// <summary>底部那枚显示百分比的按钮，点一下回 100%。</summary>
        public Button ZoomLabel = null!;

        public ComboBox Choice = null!;

        /// <summary>栏头的"文件不在了"提示条。PDF 栏与文本栏都有它（PDF 分支建栏时就插在标题条下面）。</summary>
        public Border? MissingBar;

        /// <summary>提示条里的那句话（文件路径 + 为什么读不了）。</summary>
        public TextBlock? MissingText;
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

    /// <summary>窗口刚建好：先摆一栏空的；打开项目时再按项目记忆替换。</summary>
    private void InitPanes()
    {
        _panes.Clear();
        _panes.Add(NewPane(new RefDocSpec()));
        RebuildPanes();
    }

    /// <summary>换项目时调用：恢复这个项目上次的栏位，没有就按项目现状摆出来。</summary>
    private void LoadPanesForProject()
    {
        _panes.Clear();
        _weights.Clear();

        string root = _store?.Root ?? "";

        // ① 项目自己记的（.yitai\panes.json）—— 首选，跟着项目走
        var layout = PaneLayoutStore.Load(root);

        if (layout.Panes.Count > 0)
        {
            foreach (var s in layout.Panes)
            {
                if (_panes.Count >= MaxRefPanes) break;
                if (s == null) continue;
                _panes.Add(NewPane(NormalizeSpec(s)));
            }

            foreach (double d in layout.Weights) _weights.Add(d);
        }
        else
        {
            // ② 老版本只存在全局设置里：兼容读一次，下次保存就会落到项目里
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
                    _panes.Add(NewPane(NormalizeSpec(s)));
                }
            }

            if (key.Length > 0 && _settings.PaneWeightsByProject.TryGetValue(key, out var w) && w != null)
            {
                _weights.Clear();
                foreach (double d in w) _weights.Add(d);
            }
        }

        // ③ 都没有：按"上次译到哪儿"自己摆出来，而不是甩一排空栏给人重新挑
        if (_panes.Count == 0)
            FillPanesFromProject();

        if (_panes.Count == 0)
            _panes.Add(NewPane(new RefDocSpec()));

        RebuildPanes();

        // 打开项目第一眼就该看见"上次停在哪"，不用再自己翻
        ScrollPanesToMarkerSoon();
    }

    /// <summary>
    /// 没有任何栏位记忆时，按项目现状把栏摆出来：
    /// 先在进度里记过的那几份（那正是"我正在译的"），再按章序补到上限。
    /// PDF 不自动摆：一栏 WebView2 启动慢，要用时自己加。
    /// </summary>
    private void FillPanesFromProject()
    {
        if (_store == null) return;

        var picked = new List<string>();

        // ① 进度里记过 offset 的文件（键是路径：rel:… / abs:…，旧裸文件名认不出位置就跳过）
        foreach (string key in _progress.Files.Keys)
        {
            string abs = ProgressStore.ToAbsolutePath(_store.Root, key);
            if (abs.Length == 0) continue;
            abs = Path.GetFullPath(abs);
            if (File.Exists(abs) && !picked.Contains(abs)) picked.Add(abs);
        }

        // ② sources\ 里其余的资料文件，按文件名（也就是章序）补
        //    （清单里已经没有"源文件"这回事了，归档区是用户自己往里放的，直接看目录）
        var rest = new List<string>();
        string srcDir = ProjectLayout.Sources(_store.Root);
        if (Directory.Exists(srcDir))
        {
            foreach (string f in Directory.GetFiles(srcDir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".md" or ".markdown" or ".txt")) continue;
                string abs = Path.GetFullPath(f);
                if (picked.Contains(abs)) continue;
                rest.Add(abs);
            }
        }

        rest.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string abs in rest) picked.Add(abs);

        // 只摆"正在译的"：进度里记着的那几份；不够两栏再按章序补。
        // 一上来摆满四栏反而挤，也未必是你此刻要看的东西。
        int want = Math.Min(MaxRefPanes, 2);

        foreach (string abs in picked)
        {
            if (_panes.Count >= want) break;
            _panes.Add(NewPane(new RefDocSpec
            {
                Path = Path.GetFullPath(abs),
                Label = Path.GetFileName(abs),
                Zoom = 1.0,
            }));
        }
    }

    /// <summary>
    /// 打开项目后，把各栏滚到进度线那儿。
    /// 排到 ContextIdle：等文档排完版、字有了位置，再滚才滚得准。
    /// </summary>
    private void ScrollPanesToMarkerSoon()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var p in _panes)
            {
                if (!p.Markable) continue;

                UpdateMarkerVisual(p);
                if (CurrentOffset(p) >= 0) p.Line?.BringIntoView();
            }

            // 布局已完成，这时记宽度才是真的宽度；顺手把这次摆法落到项目里
            SavePaneLayout();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// 旧版设置里栏位还分"跟随当前章 / 项目里的源 / 项目里的译文"三种。
    /// 那一套已经废了：栏里只记"一个文件在哪"。这里把旧记录折算成绝对路径，
    /// 折不出真实文件就当作空栏 —— 宁可空着等人自己挑，也不擅自替人决定看什么。
    /// </summary>
    private RefDocSpec NormalizeSpec(RefDocSpec s)
    {
        var spec = CloneSpec(s);

        if (spec.Path.Length == 0) return spec;

        // 旧记录可能是相对项目根的路径，也可能是 sources\ 里的文件名
        string abs = ProjectLayout.ToAbsolute(_store?.Root ?? "", spec.Path);
        if (File.Exists(abs)) { spec.Path = Path.GetFullPath(abs); return spec; }

        string inSources = Path.Combine(ProjectLayout.Sources(_store?.Root ?? ""), spec.Path);
        if (File.Exists(inSources)) { spec.Path = Path.GetFullPath(inSources); return spec; }

        string inTrans = Path.Combine(ProjectLayout.Translations(_store?.Root ?? ""), spec.Path);
        if (Path.IsPathRooted(spec.Path) == false && File.Exists(inTrans))
        {
            spec.Path = Path.GetFullPath(inTrans);
            return spec;
        }

        // ★ 找不到 ≠ 这一栏空着。
        //   以前这里清空 Path，结果"文件被移走/删了"在界面上退化成"这一栏还空着"，
        //   用户以为是自己没放东西。改成留路径、不清空：这一栏能说出"文件不在了，
        //   你重新指一下，或者关掉这一栏"，进度也还认得它原来的名字。
        spec.Path = Path.IsPathRooted(spec.Path)
            ? spec.Path
            : Path.GetFullPath(abs);
        return spec;
    }

    /// <summary>
    /// 把当前栏位记下来：一份进项目（.yitai\panes.json，主），一份进全局设置（兼容旧版）。
    /// 项目那份当场落盘，全局那份只进内存（窗口关闭时统一存）。
    /// </summary>
    private void SavePaneLayout()
    {
        string key = PaneSettingsKey();
        if (key.Length == 0 || _store == null) return;

        CaptureWidths();

        // ① 项目内（主）：路径尽量存相对项目根，整个文件夹搬走也还在
        var layout = new PaneLayout();
        foreach (var p in _panes)
        {
            var spec = CloneSpec(p.Spec);
            spec.Path = PathForStore(spec.Path);
            layout.Panes.Add(spec);
        }
        layout.Weights = new List<double>(_weights);

        try { PaneLayoutStore.Save(_store.Root, layout); }
        catch (Exception) { /* 记不住也不能打断干活 */ }

        // ② 全局（兼容）：项目文件夹没权限写时，至少还有一份
        var list = new List<RefDocSpec>();
        foreach (var p in _panes) list.Add(CloneSpec(p.Spec));
        _settings.PanesByProject[key] = list;
        _settings.PaneWeightsByProject[key] = new List<double>(_weights);
    }

    /// <summary>项目内的文件存相对项目根的路径；项目外的保持绝对。</summary>
    private string PathForStore(string path)
    {
        if (path.Length == 0 || _store == null) return path;

        string root = _store.Root;
        if (path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return ProjectLayout.ToRelative(root, path);

        return path;
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

        // ---------------------------------------------------------- 文件不在了：栏内提示条
        // 不弹窗、不占屏幕：就在标题条下面一行说清楚，并给出"重新指定 / 移除这一栏"两条路。
        // 为什么放在这里而不是画进正文：正文是 RichTextBox 里的 FlowDocument，
        // 提示条要是混进去，就变成"文件内容"的一部分，选中、复制、进度线都会带上它。
        var missText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Warn"),
        };

        var missTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        missTools.Children.Add(MiniButton("重新指定…", "告诉我这个文件现在在哪", () => RelocatePane(index)));
        missTools.Children.Add(MiniButton("移除这一栏", "关掉这一栏", () => RemovePane(index)));

        DockPanel.SetDock(missTools, Dock.Right);

        var missDock = new DockPanel();
        missDock.Children.Add(missTools);
        missDock.Children.Add(missText);

        var missBar = new Border
        {
            Background = (Brush)FindResource("BgCardAlt"),
            BorderBrush = (Brush)FindResource("Warn"),
            BorderThickness = new Thickness(0, 0, 0, 2),   // 一条橙色下划线，够看见又不刺眼
            Padding = new Thickness(10, 7, 8, 7),
            Visibility = Visibility.Collapsed,
            Child = missDock,
        };
        DockPanel.SetDock(missBar, Dock.Top);
        dock.Children.Add(missBar);

        pane.MissingBar = missBar;
        pane.MissingText = missText;

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

        // ---------------------------------------------------------- 正文：Markdown / 纯文本 / PDF 原件
        // PDF 这一栏不排文字，直接让 Edge 内核把原件画出来 —— 图、分栏、页码、字体全在，
        // 因为你要的是"看这本书"，不是"看这本书被我抽成的一串字"。
        if (IsPdfPath(pane.Spec.Path))
        {
            var web = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                // 别把缓存写进程序目录（单文件版会在一堆 PDF 旁边生成 EBWebView 文件夹）
                CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
                {
                    UserDataFolder = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "YiTai", "WebView2"),
                },
            };

            pane.PdfView = web;
            dock.Children.Add(web);

            border.Child = dock;
            return border;
        }

        // 用只读的 RichTextBox 而不是纯显示控件：
        //   · 加粗/斜体/标题/列表/代码块都画得出来（它装的就是一份 FlowDocument）；
        //   · 它按"给多宽就按多宽折行"排版，放大字号只会变高、不会溢出栏宽；
        //   · 只有它给得出"鼠标点在哪个字上"（GetPositionFromPoint）——
        //     "译到哪了"的竖线要立在某句话前面，靠的就是这个。
        var body = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = false,        // 别让文里的链接把点击吃掉
            IsUndoEnabled = false,
            AcceptsReturn = false,
            AcceptsTab = false,
            AllowDrop = false,
            CaretBrush = Brushes.Transparent, // 只读栏不该有闪烁的光标
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
        };
        body.ContextMenu = null;              // 右键留给进度标记用

        // ---------------------------------------------------------- 进度竖线：浮在上面的单独一层
        // 不是文档的一部分：不插字、不切段、不改行距，
        // 所以画上它和没画它，正文排版一模一样。
        var line = new System.Windows.Shapes.Rectangle
        {
            Width = MarkerWidth,
            Fill = MdRender.MarkerLine,
            RadiusX = MarkerWidth / 2,        // 两端收成圆弧，像一根短光棒
            RadiusY = MarkerWidth / 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = MarkerColor,
                BlurRadius = 7,
                ShadowDepth = 0,
                Opacity = 0.55,
            },
        };

        var overlay = new Canvas
        {
            IsHitTestVisible = false,          // 别抢鼠标：点、选、拖还得落在正文上
            ClipToBounds = true,
        };
        overlay.Children.Add(line);

        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(overlay);

        pane.Body = body;
        pane.Line = line;
        pane.LineLayer = overlay;

        // 鼠标停在这一栏上按 Ctrl+滚轮，就是缩放这一栏
        body.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            ChangeZoom(index, e.Delta > 0 ? ZoomStep : -ZoomStep);
            e.Handled = true;
        };

        AttachMarkerEvents(pane, body);   // 点一下=标进度，右键=菜单
        dock.Children.Add(host);

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

        _panes.Add(NewPane(new RefDocSpec()));
        _weights.Clear();   // 栏数变了，旧比例不适用
        RebuildPanes();
        SavePaneLayout();
    }

    /// <summary>
    /// 把若干文件各摆成一栏：只读、原件显示、不复制、不进项目。
    /// 这是"打开文件"的正路 —— 不判断格式、不判断它是资料还是译文，也不动译文栏。
    /// </summary>
    private void AddRefPanes(IEnumerable<string> paths)
    {
        bool anyAdded = false;

        foreach (string p in paths)
        {
            // 已经有空栏就先填进空栏 —— 不然点一次"打开文件"就平白多长出一栏
            PaneView? blank = null;
            foreach (var pane in _panes)
                if (pane.Spec.Path.Length == 0) { blank = pane; break; }

            if (blank != null)
            {
                blank.Spec = new RefDocSpec
                {
                    Path = Path.GetFullPath(p),
                    Label = Path.GetFileName(p),
                    Zoom = blank.Spec.Zoom,      // 空栏自己那点缩放留着
                };

                RememberRef(p);
                anyAdded = true;
                continue;
            }

            if (_panes.Count >= MaxRefPanes)
            {
                Warn("对照栏满了", $"最多 {MaxRefPanes} 栏，再多每栏只剩一条缝。先关掉一栏再打开。");
                break;
            }

            _panes.Add(NewPane(new RefDocSpec
            {
                Path = Path.GetFullPath(p),
                Label = Path.GetFileName(p),
                Zoom = 1.0,
            }));

            RememberRef(p);
            anyAdded = true;
        }

        if (!anyAdded) return;

        // 栏数没变的话，权重还留着（填进空栏不该把用户拖好的比例清掉）
        RebuildPanes();
        SavePaneLayout();
    }

    /// <summary>记住"打开过的文件"，供下拉里一键换回来（只留最近 20 个）。</summary>
    private void RememberRef(string path)
    {
        string full = Path.GetFullPath(path);
        _settings.RecentRefFiles.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
        _settings.RecentRefFiles.Insert(0, full);

        while (_settings.RecentRefFiles.Count > 20)
            _settings.RecentRefFiles.RemoveAt(_settings.RecentRefFiles.Count - 1);
    }

    private void RemovePane(int index)    {
        if (index < 0 || index >= _panes.Count) return;

        _panes.RemoveAt(index);
        _weights.Clear();
        RebuildPanes();
        SavePaneLayout();
    }

    /// <summary>
    /// 栏头的"文件不在了"提示条。只在真有路径、却读不到内容时亮 ——
    /// 空栏是正常状态（还没放东西），不该报警。
    /// </summary>
    private void UpdateMissingBar(PaneView pane, bool ok, string problem)
    {
        if (pane.MissingBar == null || pane.MissingText == null) return;

        string path = pane.Spec.Path;
        bool show = path.Length > 0 && !ok;

        pane.MissingBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        // 文件还在、只是读不了（占用/没权限）时，别骗人说"不在了"
        bool fileThere = File.Exists(path);
        string head = fileThere ? "这个文件读不了：" : "这个文件不在了：";
        string why = problem.Length > 0 ? problem : "可能被移走、改名或删了。";

        pane.MissingText.Text = head + why + "\n" + path;
    }

    /// <summary>
    /// 这一栏的文件被移走了：让用户指新位置。只改这一栏的路径，不动文件本身。
    /// 进度是按"文件位置"记的 —— 位置变了，就把旧键下那条进度迁到新键下，
    /// 不然挪个位置进度就凭空归零了。
    /// </summary>
    private void RelocatePane(int index)
    {
        if (index < 0 || index >= _panes.Count) return;
        var pane = _panes[index];

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "这个文件现在在哪？",
            Filter = "所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (_store != null) dlg.InitialDirectory = _store.Root;
        if (dlg.ShowDialog(this) != true) return;

        string oldPath = pane.Spec.Path;
        string newPath = dlg.FileName;
        string oldKey = KeyOfPath(oldPath);
        string newKey = KeyOfPath(newPath);

        if (_store != null && oldKey.Length > 0 && newKey.Length > 0
            && !string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)
            && _progress.Files.TryGetValue(oldKey, out int mark)
            && !_progress.Files.ContainsKey(newKey))
        {
            try { ProgressStore.Set(_store.Root, _progress, newKey, mark); }
            catch (Exception) { /* 进度迁不过去不影响打开文件 */ }
        }

        pane.Spec.Path = Path.GetFullPath(newPath);
        pane.Spec.Label = newKey;
        RememberRef(newPath);

        RebuildPanes();
        SavePaneLayout();
        Status("这一栏改指着 " + newKey);
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
    /// 这一栏能选什么：就是"一个文件"。
    /// 不做"源文件 / 译文"的分类 —— 软件没有资格判断一份文件在这个人手里算什么，
    /// 放进来的东西一律只读，怎么用由他自己决定。
    /// </summary>
    private List<PaneChoice> BuildChoices()
    {
        var list = new List<PaneChoice>();

        // 当前这几栏开着的东西（同一份文件只列一次）
        foreach (var p in _panes)
        {
            if (list.Exists(c => !c.Browse && SameSpec(c.Spec, p.Spec))) continue;

            string name = Path.GetFileName(p.Spec.Path);
            list.Add(new PaneChoice
            {
                Display = name.Length > 0 ? name : "（这一栏还没放东西）",
                Spec = CloneSpec(p.Spec),
            });
        }

        // 最近打开过的（去重、只留还存在的）
        foreach (string f in _settings.RecentRefFiles)
        {
            if (f.Length == 0 || !File.Exists(f)) continue;
            if (list.Exists(c => !c.Browse && string.Equals(c.Spec.Path, f, StringComparison.OrdinalIgnoreCase))) continue;

            list.Add(new PaneChoice
            {
                Display = Path.GetFileName(f),
                Spec = new RefDocSpec { Path = f, Label = Path.GetFileName(f) },
            });
        }

        list.Add(new PaneChoice { Display = "打开文件…", Browse = true });

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
                Title = "打开一份资料（只读，不复制、不进项目）",
                Filter = "所有文件 (*.*)|*.*|文本 (*.md;*.txt;*.markdown)|*.md;*.txt;*.markdown|PDF (*.pdf)|*.pdf",
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
                Path = dlg.FileName,
                Label = Path.GetFileName(dlg.FileName),
                Zoom = pane.Spec.Zoom,
            };

            RememberRef(dlg.FileName);
            RebuildPanes();     // 下拉要把这份文件补进去
            SavePaneLayout();
            return;
        }

        double keepZoom = pane.Spec.Zoom;   // 换文档不重置这一栏的显示比例
        bool wasPdf = IsPdfPath(pane.Spec.Path);

        pane.Spec = CloneSpec(choice.Spec);
        pane.Spec.Zoom = keepZoom;

        // PDF 用 WebView2、其他用 RichTextBox：换了类型就得把这一栏重摆，光刷新文档不行
        if (wasPdf != IsPdfPath(pane.Spec.Path))
        {
            _weights.Clear();
            RebuildPanes();
        }
        else
        {
            RefreshOne(pane);
        }

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
        RefreshMissingIndicator();   // 状态栏那句"N 个文件不在了"跟着一起更新
    }

    private void RefreshOne(PaneView pane)
    {
        long t0 = Diag.Tick();

        if (pane.PdfView != null) { RefreshPdfPane(pane); return; }

        if (pane.Body == null) return;   // 刚加进来还没画

        double zoom = Math.Clamp(pane.Spec.Zoom, MinZoom, MaxZoom);
        string text = ResolvePaneContent(pane.Spec, out string note, out bool exists,
                                         out bool isMarkdown, out string problem);
        var style = MakeStyle(zoom);

        // 栏头的"文件不在了"提示条：只有真指着一个路径、却读不到内容时才亮
        UpdateMissingBar(pane, exists, problem);

        // "译到哪了"的竖线：这一栏能点就画，进度记在这一栏自己那份文件的位置下
        pane.ProgressKey = exists ? ProgressKeyOf(pane.Spec) : "";
        pane.Markable = exists && text.Length > 0 && pane.ProgressKey.Length > 0;

        // T-11 的"现场认领"：旧项目里只留下裸文件名（"ch03.md"）的那条进度，
        // 而眼前这份文件的键还是空的 —— 那就是它了，搬过来，线才落得回原来的位置。
        if (pane.Markable && _store != null
            && ProgressStore.Get(_progress, pane.ProgressKey) < 0)
        {
            try
            {
                ProgressStore.AdoptLegacy(_store.Root, _progress, pane.ProgressKey,
                                          Path.GetFileName(pane.Spec.Path));
            }
            catch (Exception) { /* 认领失败不影响打开 */ }
        }

        // 换了文件，撤销账本里那条就指向旧文件了 —— 切章/换源之后不能再撤上一轮的线
        ForgetUndo();

        int marker = pane.Markable ? ProgressStore.Get(_progress, pane.ProgressKey) : -1;

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

        if (pane.Caption != null)
        {
            pane.BaseCaption = note;
            pane.PaneText = text;
            pane.Caption.Text = WithProgress(note, marker, pane.PaneText);
        }

        UpdateMarkerVisual(pane);   // 竖线单独一层，改文档不用重画

        // 文档刚换进来时字还没排出来，这一下多半算不出位置。
        // 排到布局完成之后（Loaded 优先级）再补算一次 ——
        // 原先这件事是靠每帧都跑的 LayoutUpdated 兜的，那等于让程序一直空转。
        Dispatcher.BeginInvoke(
            new Action(() => UpdateMarkerVisual(pane)),
            System.Windows.Threading.DispatcherPriority.Loaded);

        Diag.AddRefresh(t0, _panes.Count, text.Length);
    }

    /// <summary>
    /// PDF 栏：不打字、不抽文本，直接让 Edge 内核渲染原件。
    /// 代价是这栏拿不到"点在哪个字上"，所以它不出进度线 —— 要文字和进度线的那一栏，
    /// 把另一栏指着同一份文件（走文本模式）即可。
    /// </summary>
    private void RefreshPdfPane(PaneView pane)
    {
        var web = pane.PdfView;
        if (web == null) return;

        pane.Markable = false;
        pane.ProgressKey = "";

        string path = pane.Spec.Path;
        bool exists = File.Exists(path);

        UpdateMissingBar(pane, exists, exists ? "" : "可能被移走、改名或删了。");

        if (pane.Caption != null)
        {
            pane.BaseCaption = exists
                ? "PDF · " + Path.GetFileName(path) + " · 原件"
                : "PDF · " + Path.GetFileName(path) + " · 这个文件不在了";
            pane.Caption.Text = pane.BaseCaption;
        }

        // 缩放按钮 / Ctrl+滚轮在这一栏调的是 PDF 的显示比例
        double zoom = Math.Clamp(pane.Spec.Zoom, MinZoom, MaxZoom);
        web.ZoomFactor = zoom;

        if (!exists) return;

        try
        {
            var uri = new Uri(path);
            if (web.Source == null || web.Source.AbsoluteUri != uri.AbsoluteUri)
                web.Source = uri;
        }
        catch (Exception)
        {
            // 路径里有怪异字符导致 URI 建不出来：不崩，底栏已经写明文件名
        }
    }

    /// <summary>一栏该显示什么。exists=false 表示这段字是提示语，不是文件内容。</summary>
    private string ResolvePaneContent(RefDocSpec spec, out string note, out bool exists,
                                      out bool isMarkdown, out string problem)
    {
        problem = "";

        if (spec.Path.Length == 0)
        {
            note = "还没放东西";
            exists = false;
            isMarkdown = false;
            return "这一栏还空着。\n\n点栏顶的下拉选「打开文件…」，挑一份什么都行 —— 只读，不复制，也不进项目。";
        }

        if (!File.Exists(spec.Path))
        {
            note = "文件不在了";
            exists = false;
            isMarkdown = false;
            problem = "可能被移走、改名或删了。";

            // 这句只说"这里缺东西、上面能怎么办"；路径和原因已经由栏头提示条说清楚了，
            // 正文里再抄一遍只会让一栏字看起来更乱。
            return "这个文件不在了。\n\n它原来的位置还记在项目里 —— 用上面的「重新指定…」告诉我它现在在哪，或者「移除这一栏」。";
        }

        return ReadForDisplay(spec.Path, Path.GetFileName(spec.Path), out note, out exists,
                              out isMarkdown, out problem);
    }

    private string ReadForDisplay(string path, string caption, out string note, out bool exists,
                                  out bool isMarkdown, out string problem)
    {
        problem = "";

        // PDF 一律走 WebView2 原件栏（BuildPane 那一步就分流了），到不了这里。
        // 留一句兜底：万一路径被别处改了、PDF 落进文字分支，也只说"原件显示"，
        // 绝不把一串二进制当正文排出来。**不抽文字层**（T-12 选了方案 A）。
        if (IsPdfPath(path))
        {
            isMarkdown = false;
            exists = false;
            note = caption + " · PDF 原件";
            problem = "";
            return "这一栏是 PDF，按原件显示。";
        }

        isMarkdown = IsMarkdownPath(path);
        var info = TextFile.Read(path);     // 不抛异常：读不了的原因在 State / Problem 里

        if (info.State != TextReadState.Ok)
        {
            note = caption + " · 读不到";
            exists = false;
            problem = info.Problem;

            return info.State == TextReadState.Missing
                ? "找不到这个文件（可能被移走、改名或删了）：\n\n" + path
                : "这个文件读不了：\n\n" + info.Problem + "\n\n" + path;
        }

        note = caption + " · " + TextStats.CountChars(info.Text) + " 字 · " +
               TextStats.CountParagraphs(info.Text) + " 段";
        exists = true;
        return info.Text;
    }

    private static bool IsPdfPath(string path)
        => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>这一栏的进度记在哪个键下 —— 绑的是"这份文件的位置"，不是光一个文件名。</summary>
    private string ProgressKeyOf(RefDocSpec spec) => KeyOfPath(spec.Path);

    /// <summary>路径 → 进度键（项目内相对、项目外绝对；同一个路径永远同一个键）。</summary>
    private string KeyOfPath(string path)
    {
        if (path.Length == 0 || _store == null) return "";
        return ProgressStore.KeyFor(_store.Root, ProjectLayout.ToAbsolute(_store.Root, path));
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
        Path = s.Path,
        Label = s.Label,
        Zoom = s.Zoom,
    };

    private static bool SameSpec(RefDocSpec a, RefDocSpec b)
        => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
}
