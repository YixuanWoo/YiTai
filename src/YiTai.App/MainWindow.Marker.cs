// ============================================================
// MainWindow.Marker.cs —— "译到哪了"的绿色竖线
// ============================================================
//
// 一根竖着的光标：3px 宽、两端带圆弧、微微发光，立在"已译 / 未译"交界的那句话前面。
//
// 它**不属于文档**。早期版本把线画进文档里（切开那一行、在切口上加边框），
// 结果是正文排版被改动、段间距与折行都变了 —— 那不能叫"标记"，那叫"改稿"。
// 现在它浮在正文上面的一层 Canvas 上，文档一个字都不动：
// 排版、折行、行距、滚动条长度，与没有标记时一模一样。
//
// 交互（一条比一条省事）：
//   · 在资料栏里点一下（不拖选、不是双击的第二下）→ 竖线落到那句话前面
//   · Ctrl+Shift+↑ / ↓  → 上一句 / 下一句（按标点切句，不按段落跳）
//   · 右键 → 「把进度标记放在这里」/「清除进度标记」（兜底）
//
// 为什么不自动跟着译文光标走：译文和资料之间没有对齐数据，按段落序号硬推，
// 一段对不上就整条线静默错位 —— 比没有标记更坏。宁可让人点一下。
//
// 位置怎么记：记"文档里的字符偏移"，不记"第几段"。
// 资料增删一段之后，段序号全乱，字符偏移只是附近偏一点。
//
// 落盘在 .yitai\progress.json（见 ProjectLayout.ProgressFile）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow
{
    /// <summary>起笔标宽度。细了看不见，粗了像块膏药。</summary>
    private const double MarkerWidth = 3;

    /// <summary>进度高亮的主色：琥珀。底带用它 14%（非活动栏 8%），下划线/起笔标用实色。</summary>
    private static Color ProgressColor => ProgressHighlightLayers.Main;

    private static Brush ProgressBrush => ProgressHighlightLayers.MainBrush;

    /// <summary>活动栏（"我现在在翻哪一份"）。鼠标压着哪栏就是哪栏；钉住的优先。</summary>
    private PaneView? _activePane;
    private PaneView? _hoverPane;
    private PaneView? _pinnedPane;

    /// <summary>各源文件的进度（文件名 → 字符偏移）。跟随项目打开/关闭。</summary>
    private ProgressState _progress = new();

    /// <summary>右键要用的位置，等菜单被点的时候才知道落在哪儿。</summary>
    private int _pendingOffset = -1;

    /// <summary>
    /// 撤销账本：上一次单击刚落下的那条线，落之前原本在哪个偏移。
    /// 单击现在是"立刻落线"，所以双击时不是"取消排队"，而是"把刚落的那条收回去"。
    /// </summary>
    private PaneView? _undoPane;
    private int _undoBefore = -1;
    private DateTime _undoAt = DateTime.MinValue;

    /// <summary>
    /// 上一次左键抬起的栏、位置与时刻。
    /// ClickCount 由系统给，但如果它没递进来（不同输入路径、触控板、远程桌面），
    /// 光看它就漏判 —— 那时双击仍然会插线。所以再用"时间近 + 位置没动"补判一次。
    /// </summary>
    private PaneView? _lastUpPane;
    private Point _lastUpPoint;
    private DateTime _lastUpAt = DateTime.MinValue;

    /// <summary>系统的双击间隔（毫秒）。它决定"第二下多久之内来才算双击"，不是"落线要等多久"。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    /// <summary>
    /// 撤销窗口：第二下在这个时间内落到同一处，才认作双击、才收回第一下落下的线。
    /// 取系统值即可（本机 200ms，Win 默认 500ms），不再人为放大 ——
    /// 落线已经不等它了，窗口放大只会让"快速点了两处"被误当成双击。
    /// </summary>
    private static int DoubleClickMs => Math.Max(200, GetDoubleClickTime());

    /// <summary>判"同一处"的容差（像素）。系统双击矩形的尺寸通常就是这个量级。</summary>
    private const double SameSpotTolerance = 6;

    // ---------------------------------------------------------------- 接线

    /// <summary>挂上窗口级的快捷键。构造函数里调用一次。</summary>
    private void InitMarker()
    {
        PreviewKeyDown += OnMarkerKeyDown;
    }

    private void OnMarkerKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // 进度高亮的两个单手键：左手拇指按住 Ctrl+Alt，食指按 X 推一句 / 按 C 找回。
        if (mods == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (e.Key == Key.X) { StepMarker(+1); e.Handled = true; return; }
            if (e.Key == Key.C) { CenterOnActivePane(); e.Handled = true; return; }

            if (e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
            {
                SwitchToPane((int)e.Key - (int)Key.D1);
                e.Handled = true;
            }
            return;
        }

        // 保留：Ctrl+Shift+↑/↓ 手动跳句（找补、回头看）
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.Up)
            {
                StepMarker(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                StepMarker(+1);
                e.Handled = true;
            }
        }
    }

    /// <summary>每栏的正文挂鼠标事件与位置刷新。BuildPane 里调。</summary>
    private void AttachMarkerEvents(PaneView pane, RichTextBox body)
    {
        // 双击的第二下，一按下就把第一下落下的线收回去：
        // 若等抬起才撤，双击选词的整个过程里都会多一根线在那儿闪。
        body.PreviewMouseLeftButtonDown += (_, e) =>
        {
            SetActivePane(pane);
            if (e.ClickCount > 1) UndoLastMark();
        };

        body.PreviewMouseLeftButtonUp += (_, e) => OnPaneMouseUp(pane, e);
        body.PreviewMouseRightButtonUp += (_, e) => OnPaneMouseRightUp(pane, e);

        // 鼠标压在哪一栏，哪一栏就是"活动栏" —— 这是多参考文档下最省事的识别方式：
        // 不用记、不用先点、不会认错，眼睛看到哪就操作哪。
        body.MouseEnter += (_, _) =>
        {
            _hoverPane = pane;
            ApplyActivePane();
        };
        body.MouseLeave += (_, _) =>
        {
            if (_hoverPane == pane) _hoverPane = null;
            ApplyActivePane();
        };

        // 栏宽变了，文字重新折行，线的位置也得重算
        body.SizeChanged += (_, _) =>
        {
            UpdateMarkerVisual(pane);
            UpdateTablePageWidths(pane);
        };

        // 这里原本挂着 LayoutUpdated，每完成一轮布局就重算一次线的位置。
        // 那是个死循环：UpdateMarkerVisual 会改线的可见性和高度，
        // 改完又标脏布局 → 下一轮布局 → 又进这里，一秒钟六十轮，停不下来。
        // 实测代价是程序空转着占满一个 CPU 核心，用一会儿就越来越迟钝。
        //
        // 现在改由"文档真的换了"这一处主动刷新（见 RefreshOne 里的延迟调用）：
        // 换文档、改栏宽、滚动、缩放、点击落线，各有各的触发点，一个不少。

        // 滚动条只有等模板套上才存在，所以等 Loaded
        body.Loaded += (_, _) =>
        {
            if (FindScrollViewer(body) is ScrollViewer sv)
                sv.ScrollChanged += (_, _) => UpdateMarkerVisual(pane);

            UpdateMarkerVisual(pane);
            UpdateTablePageWidths(pane);
        };
    }

    // ---------------------------------------------------------------- 鼠标

    private void OnPaneMouseUp(PaneView pane, MouseButtonEventArgs e)
    {
        long t0 = Diag.Tick();

        try
        {
            OnPaneMouseUpCore(pane, e);
        }
        finally
        {
            Diag.AddClick(t0);   // "点一下多久才动"直接看这个
        }
    }

    private void OnPaneMouseUpCore(PaneView pane, MouseButtonEventArgs e)
    {
        if (!pane.Markable) return;   // 提示语、读不出的文件、空文件：都没有进度可言

        // 拖滚动条、点滚动条的上下箭头，本质是"翻页"，不是"点正文"。
        // 滚动条是正文控件模板里的子孙，抬起事件照样会以隧道方式经过这里，
        // 以前就被当成一次落线 —— 拖一下滚动条，线就跳到别处。
        if (IsFromScrollBar(e.OriginalSource)) return;

        Point at = e.GetPosition(pane.Body);

        // 这一下是不是双击的第二下？两条判据：
        //   ① 系统给的 ClickCount（权威）；
        //   ② ClickCount 丢了的时候（触控板 / 远程桌面 / 别的输入路径），
        //      时间够近 且 位置几乎没动，也算第二下。
        //      两个条件必须同时成立，否则"快速点了两处"会被误判成双击，两处都丢掉。
        bool second = e.ClickCount > 1
            || (pane == _lastUpPane
                && Math.Abs(at.X - _lastUpPoint.X) <= SameSpotTolerance
                && Math.Abs(at.Y - _lastUpPoint.Y) <= SameSpotTolerance
                && (DateTime.Now - _lastUpAt).TotalMilliseconds < DoubleClickMs);

        _lastUpAt = DateTime.Now;
        _lastUpPane = pane;
        _lastUpPoint = at;

        if (second)
        {
            UndoLastMark();   // 双击选词：把第一下抢先落下的线收回去
            return;
        }

        // 松手时选着字 = 人家在拖选复制，别插一脚
        if (!pane.Body.Selection.IsEmpty) return;

        int offset = OffsetAtPoint(pane, at);
        if (offset < 0) return;                                // 点在栏的空白处

        // 点句子里任何一个字，线都落到这句的句首 ——
        // 进度高亮标的是"一整句"，线立在句中间会跟底带对不上。
        if (pane.Sentences != null && pane.Sentences.Count > 0)
            offset = pane.Sentences.StartOf(offset);

        if (offset == CurrentOffset(pane)) return;             // 点在原处，什么都不用做

        PlaceMarkNow(pane, offset);
    }

    /// <summary>
    /// 这一下是不是落在滚动条上（拖滑块、点箭头、按轨道）。
    /// RichTextBox 的滚动条在它的模板里，属于正文的子孙；
    /// 在滚动条上抬起鼠标，隧道事件仍会先经过正文控件。
    /// 从命中的元素往上找，碰到 ScrollBar 就算滚动条，碰到 ScrollViewer 还没碰到就算正文。
    /// 这是"黑名单"判据：往后正文里若添了别的可点附件（侧栏、行号、悬浮按钮），
    /// 这里每加一个都要补一条；要更精确的控制就改"正文安全区"方案，取舍见 README「已知边界」。
    /// </summary>
    private static bool IsFromScrollBar(object? source)
    {
        DependencyObject? d = source as DependencyObject;

        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ScrollBar) return true;
            if (d is ScrollViewer) return false;

            if (d is FrameworkContentElement content)
                d = content.Parent;
            else if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                d = VisualTreeHelper.GetParent(d);
            else
                d = LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    /// <summary>
    /// 单击 → 立刻落线，并把"落之前的位置"记进撤销账本。
    /// 原来这里是等一个双击间隔再落，看起来"点了半天才动"；
    /// 现在落线不等待，双击的风险改由第二下按下时的 UndoLastMark 收回去。
    /// </summary>
    private void PlaceMarkNow(PaneView pane, int offset)
    {
        long t0 = Diag.Tick();

        _undoPane = pane;
        _undoBefore = CurrentOffset(pane);
        _undoAt = DateTime.Now;

        SetMarker(pane, offset);

        Diag.AddPlace(t0);
    }

    /// <summary>
    /// 把第一下刚落下的线收回到落之前的位置。
    /// 账过期（超出了双击窗口）就不撤 —— 那是上一轮正经点过的位置，
    /// 不能因为后来一次双击就抹掉。
    /// </summary>
    private void UndoLastMark()
    {
        Diag.AddUndo();

        var pane = _undoPane;
        int before = _undoBefore;
        DateTime at = _undoAt;

        ForgetUndo();

        if (pane == null) return;
        if ((DateTime.Now - at).TotalMilliseconds > DoubleClickMs) return;

        if (before < 0) ClearMarker(pane);
        else SetMarker(pane, before);
    }

    /// <summary>把撤销账本清掉（右键、切章之后不该还能撤上一轮的线）。</summary>
    private void ForgetUndo()
    {
        _undoPane = null;
        _undoBefore = -1;
        _undoAt = DateTime.MinValue;
    }

    private void OnPaneMouseRightUp(PaneView pane, MouseButtonEventArgs e)
    {
        if (!pane.Markable) return;
        if (IsFromScrollBar(e.OriginalSource)) return;   // 在滚动条上右键，不弹进度菜单

        ForgetUndo();   // 右键要弹菜单了：账本清掉，免得下一记双击把右键之前那条线撤走

        int offset = OffsetAtPoint(pane, e.GetPosition(pane.Body));
        if (offset < 0) return;

        if (pane.Sentences != null && pane.Sentences.Count > 0)
            offset = pane.Sentences.StartOf(offset);

        _pendingOffset = offset;

        var menu = new ContextMenu { PlacementTarget = pane.Body };

        var put = new MenuItem { Header = "把进度标记放在这里" };
        put.Click += (_, _) => SetMarker(pane, _pendingOffset);
        menu.Items.Add(put);

        var clear = new MenuItem { Header = "清除进度标记" };
        clear.Click += (_, _) => ClearMarker(pane);
        menu.Items.Add(clear);

        // 必须等这一下右键彻底走完再弹：就在抬起事件里弹，菜单会立刻吃到同一次抬起，
        // 第一个菜单项等于被"自己点掉"——右键就变成了直接标进度。
        Dispatcher.BeginInvoke(
            new Action(() => menu.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Background);

        e.Handled = true;
    }

    /// <summary>
    /// 鼠标位置对应文档里的哪个字符。
    /// 渲染时每一截文字都记了自己在文档里的起点，所以这里能精确到"第几个字"，
    /// 而不是只到"第几段"——一份资料常常一整章就是一大段。
    /// </summary>
    private static int OffsetAtPoint(PaneView pane, Point point)
    {
        if (pane.Body?.Document == null) return -1;

        var pos = pane.Body.GetPositionFromPoint(point, snapToText: true);
        if (pos == null) return -1;

        // 落在文字里：那截文字的起点 + 在这一截里的偏移
        var cur = pos.Parent;
        while (cur != null && cur is not Run)
        {
            var fce = cur as FrameworkContentElement;
            cur = fce?.Parent;
        }

        if (cur is Run run && run.Tag is int runStart)
        {
            int delta = run.ContentStart.GetOffsetToPosition(pos);   // 这一截是 1:1 对应文档的
            return runStart + Math.Max(0, delta);
        }

        // 落在段落空白、行首行尾：退回这一块（=这一行）的起点。
        // 但表格是 BlockUIContainer（拿不到字坐标，跟 PDF 一样），点它不落线。
        var block = TaggedBlockAt(pos);
        if (block is BlockUIContainer) return -1;
        return block?.Tag is int offset ? offset : -1;
    }

    /// <summary>从文本位置往上找"带 Tag 的块"（列表项会一路找到外层的 List）。</summary>
    private static Block? TaggedBlockAt(TextPointer pos)
    {
        DependencyObject? cur = pos.Paragraph;
        if (cur == null) cur = pos.Parent;

        while (cur != null)
        {
            if (cur is Block b && b.Tag is int) return b;

            var fce = cur as FrameworkContentElement;
            cur = fce?.Parent;
        }

        return null;
    }

    // ---------------------------------------------------------------- 落盘

    /// <summary>这一栏的进度：它自己那份文件记到哪儿了。没有就是 -1。</summary>
    private int CurrentOffset(PaneView pane)
    {
        if (_store == null || pane.ProgressKey.Length == 0) return -1;
        return ProgressStore.Get(_progress, pane.ProgressKey);
    }

    private void SetMarker(PaneView pane, int offset, bool scroll = false)
    {
        if (_store == null || pane.ProgressKey.Length == 0) return;

        // 写不进盘（只读目录之类）也让线先立起来：界面不能因为存不了就卡住
        long saveAt = Diag.Tick();
        try { ProgressStore.Set(_store.Root, _progress, pane.ProgressKey, offset); }
        catch (Exception) { /* 记不住就算了，这一处线照样立 */ }
        Diag.AddSave(saveAt);

        RefreshMarkerVisuals();

        if (scroll) ScrollToMarker(pane);
    }

    private void ClearMarker(PaneView pane)
    {
        if (_store == null || pane.ProgressKey.Length == 0) return;

        try { ProgressStore.Set(_store.Root, _progress, pane.ProgressKey, -1); }
        catch (Exception) { /* 同上 */ }

        RefreshMarkerVisuals();
    }

    /// <summary>打开项目后调一次；切章不用重读，_progress 一直在内存里。</summary>
    private void LoadProgress()
    {
        _progress = _store == null ? new ProgressState() : ProgressStore.Load(_store.Root);
        if (_store == null) return;

        var open = new List<string>();
        try
        {
            var layout = PaneLayoutStore.Load(_store.Root);
            foreach (var spec in layout.Panes)
                if (spec.Path.Length > 0) open.Add(ProjectLayout.ToAbsolute(_store.Root, spec.Path));
        }
        catch (Exception) { /* 栏位记忆读不出来不影响搬进度 */ }

        try
        {
            var (migrated, held) = ProgressStore.Migrate(_store.Root, _progress, open);
            if (migrated > 0)
                Status($"进度线已按文件路径归位（搬了 {migrated} 条"
                       + (held > 0 ? $"，另有 {held} 条同名对不上、先留着" : "") + "）");
        }
        catch (Exception) { /* 搬家失败就当没这回事，旧键还在，线照样能标 */ }
    }

    // ---------------------------------------------------------------- 画线

    /// <summary>
    /// 标记变了就重摆竖线。
    /// 只挪那一根线，**不重画文档** —— 所以点击不会让正文重排、也不会跳动。
    /// </summary>
    private void RefreshMarkerVisuals()
    {
        foreach (var pane in _panes)
        {
            UpdateMarkerVisual(pane);
            UpdateCaption(pane);     // 竖线不重排文档，底栏那句"译到"就得单独刷
        }
    }

    /// <summary>只重写底栏那行字（句号、字数、百分比），不动文档。</summary>
    private void UpdateCaption(PaneView pane)
    {
        if (pane.Caption == null || pane.BaseCaption.Length == 0) return;
        if (pane.ProgressKey.Length == 0) return;

        int marker = ProgressStore.Get(_progress, pane.ProgressKey);
        pane.Caption.Text = WithProgress(pane.BaseCaption, marker, pane.Body?.Document, pane.Sentences);
    }

    /// <summary>把某一栏的三层高亮摆到当前句上。没标过 / 这一栏不能标 → 全藏起来。</summary>
    private void UpdateMarkerVisual(PaneView pane)
    {
        if (!Diag.On) { UpdateMarkerVisualCore(pane); return; }

        long t0 = Diag.Tick();
        try { UpdateMarkerVisualCore(pane); }
        finally { Diag.AddLayout(t0); }
    }

    private void UpdateMarkerVisualCore(PaneView pane)
    {
        var line = pane.Line;
        if (line == null) return;

        var doc = pane.Body?.Document;
        int offset = pane.Markable && doc != null ? CurrentOffset(pane) : -1;

        if (offset < 0 || doc == null)
        {
            HideHighlight(pane);
            return;
        }

        // 当前句的源偏移区间。没有切句器（老资料/空文件）就退回"一个字的宽度"，
        // 至少起笔标还在，不会因为切不出句子就整条不显示。
        int spanStart = offset;
        int spanEnd = offset + 1;

        if (pane.Sentences is { Count: > 0 })
        {
            int si = pane.Sentences.IndexAt(offset);
            if (si >= 0)
            {
                spanStart = pane.Sentences.At(si).Start;
                spanEnd = pane.Sentences.At(si).End;
            }
        }

        List<Rect> rects;
        try { rects = SpanRects(doc, spanStart, spanEnd); }
        catch (Exception) { HideHighlight(pane); return; }   // 文档结构怪异时不崩，先藏起来

        if (rects.Count == 0) { HideHighlight(pane); return; }
        // 和 ==高亮== 撞上：不画底带，只画加深下划线（两片半透明色叠一起会发脏）
        bool clash = OverlapsHighlight(pane, spanStart, spanEnd);

        // 活动栏亮一点，别的栏退到 8%，多栏并排时眼睛知道该看哪个
        double alpha = clash ? 0 : (pane.Active ? 0.14 : 0.08);

        // 关键：图层必须撑到正文那么大，否则嵌套 Canvas 是 0×0，配上 ClipToBounds 会被整片裁掉
        double vw = pane.Body?.ActualWidth ?? 0;
        double vh = pane.Body?.ActualHeight ?? 0;

        if (vw <= 0 || vh <= 0)
        {
            // 正文还没量出尺寸时退一步，用浮层自己的尺寸（两者在同一格子里）
            if (pane.BandLayer?.Parent is FrameworkElement ov)
            {
                vw = ov.ActualWidth;
                vh = ov.ActualHeight;
            }
        }

        ProgressHighlightLayers.EnsureSize(pane.BandLayer, vw, vh);
        ProgressHighlightLayers.EnsureSize(pane.UnderlineLayer, vw, vh);

        ProgressHighlightLayers.SyncBand(pane.BandLayer, rects, alpha);
        ProgressHighlightLayers.SyncUnderline(pane.UnderlineLayer, rects, clash);

        // 起笔标：立在句首那一行的左边缘，高度取整行
        Rect r0 = rects[0];
        double left = r0.X - MarkerWidth / 2;
        double top = r0.Y + 1;
        double height = Math.Max(9, r0.Height - 2);

        bool changed = line.Visibility != Visibility.Visible
                       || Math.Abs(Canvas.GetLeft(line) - left) >= 0.5
                       || Math.Abs(Canvas.GetTop(line) - top) >= 0.5
                       || Math.Abs(line.Height - height) >= 0.5
                       || Math.Abs(line.Width - (pane.Active ? MarkerWidth : 2)) >= 0.1;

        if (changed)
        {
            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, top);
            line.Height = height;
            line.Width = pane.Active ? MarkerWidth : 2;
            line.Opacity = pane.Active ? 1 : 0.6;
            line.Visibility = Visibility.Visible;
        }
    }

    private void HideHighlight(PaneView pane)
    {
        if (pane.Line != null && pane.Line.Visibility != Visibility.Collapsed)
            pane.Line.Visibility = Visibility.Collapsed;

        ProgressHighlightLayers.Clear(pane.BandLayer, pane.UnderlineLayer);
    }

    /// <summary>这一句是不是压在 ==高亮== 上（碰一点就算，粗判够用）。</summary>
    private static bool OverlapsHighlight(PaneView pane, int start, int end)
    {
        if (pane.HighlightRanges == null || pane.HighlightRanges.Count == 0) return false;

        foreach (var (s, e) in pane.HighlightRanges)
            if (start < e && s < end) return true;

        return false;
    }

    /// <summary>一句在栏里的矩形：一个视觉行一段（算法在 MdRender 里，便于单独验证）。</summary>
    private static List<Rect> SpanRects(FlowDocument doc, int start, int end)
        => MdRender.RectSegments(doc, start, end);

    /// <summary>
    /// 文档偏移 → 那个字在栏里的矩形。
    /// 坐标是正文视图自己的坐标（RichTextBox 的 TextPointer 给的），与叠在上面的 Canvas 同一套。
    /// </summary>
    private static Rect? RectAtOffset(FlowDocument doc, int offset)
    {
        var ptr = MdRender.PointerAtOffset(doc, offset);
        if (ptr == null) return null;

        Rect r = ptr.GetCharacterRect(LogicalDirection.Forward);
        if (r.IsEmpty || r.Height < 4) r = ptr.GetCharacterRect(LogicalDirection.Backward);
        if (r.IsEmpty || r.Height < 4) return null;

        return r;
    }

    /// <summary>
    /// 推进一句之后，把视线拉过去。
    /// 注意：起笔标画在正文**外面**的浮层上，对它 BringIntoView 是滚不动正文的，
    /// 所以要自己算"这句现在在视口的什么位置、要不要滚、滚多少"。
    /// 已经看得见就不动 —— 短句子连按 X 时页面别一直往上窜。
    /// </summary>
    private void ScrollToMarker(PaneView pane)
    {
        if (pane?.Body?.Document == null) return;

        UpdateMarkerVisual(pane);

        int offset = CurrentOffset(pane);
        if (offset < 0) return;

        int start = pane.Sentences is { Count: > 0 } ? pane.Sentences.StartOf(offset) : offset;
        Rect? rect = RectAtOffset(pane.Body.Document, start);
        if (rect == null) return;

        if (FindScrollViewer(pane.Body) is not ScrollViewer sv) return;

        double y = rect.Value.Y;                       // 视口坐标
        double h = rect.Value.Height;

        // 上沿留 8px、下沿留 8px 余量，都算"看得见"
        if (y >= 8 && y + h <= sv.ViewportHeight - 8) return;

        double target = sv.VerticalOffset + y - sv.ViewportHeight * 0.33;
        target = Math.Clamp(target, 0, Math.Max(0, sv.ScrollableHeight));

        AnimateScroll(sv, sv.VerticalOffset, target);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ScrollViewer sv) return sv;

            var deeper = FindScrollViewer(child);
            if (deeper != null) return deeper;
        }

        return null;
    }

    /// <summary>栏底那句："第 X/Y 句 · 译到多少字（Z%）"。没标过就原样返回。</summary>
    /// <summary>
    /// 底栏那句"第 X/Y 句 · 译到 X/Y 字（Z%）"。
    /// 字按"非空白字"数，跟栏底"字数"是同一个口径，免得一个 21 万一个 26 万。
    /// 数的是**文档里**的字（不是原文件）：Markdown 的偏移跟原文字符位置不是一套。
    /// </summary>
    private static string WithProgress(string note, int marker, FlowDocument? doc, SentenceIndex? sentences)
    {
        if (marker < 0) return note;

        string head = note;

        if (sentences is { Count: > 0 })
        {
            int si = sentences.IndexAt(marker);
            if (si >= 0) head = note + $" · 第 {si + 1}/{sentences.Count} 句";
        }

        if (doc == null) return head;

        int total = MdRender.CountCharsBefore(doc, int.MaxValue);
        if (total <= 0) return head;

        int done = Math.Clamp(MdRender.CountCharsBefore(doc, marker), 0, total);
        int pct = (int)Math.Round(done * 100.0 / total);

        return head + $" · 译到 {done:N0}/{total:N0} 字（{pct}%）";
    }

    // ---------------------------------------------------------------- 键盘挪一句

    private void StepMarker(int delta)
    {
        var pane = ResolveActivePane();
        if (pane?.Body?.Document == null) return;

        // 一律走已经切好的句子表：跟画底带用的是同一份，不会"线跳到 A、底带留在 B"
        if (pane.Sentences is not { Count: > 0 })
        {
            Status("这一栏还没切出句子（可能是空文件或表格）");
            return;
        }

        int offset = CurrentOffset(pane);

        if (offset < 0)
        {
            SetMarker(pane, pane.Sentences.At(0).Start, scroll: true);   // 还没标过：先标到第一句
            return;
        }

        int target = delta > 0
            ? pane.Sentences.NextStart(offset)
            : pane.Sentences.PrevStart(offset);

        if (target < 0)
        {
            Status("已经是最后一句了");
            return;
        }

        if (target != offset) SetMarker(pane, target, scroll: true);
    }

    // ---------------------------------------------------------------- 活动栏

    /// <summary>
    /// 现在该操作哪一栏（P2 的四层判定）：
    ///   ① 钉住的栏 —— 人明确说了"就这一栏"，压过一切
    ///   ② 鼠标压着的栏 —— 眼睛看到哪就操作哪，最省事，也不用先点一下
    ///   ③ 最近点过的栏 —— 鼠标移开之后还记得上一步在哪
    ///   ④ 第一栏能标的 —— 兜底，什么都没有时至少有个去处
    /// </summary>
    private PaneView? ResolveActivePane()
    {
        if (_pinnedPane != null && _panes.Contains(_pinnedPane)) return _pinnedPane;
        if (_hoverPane != null && _panes.Contains(_hoverPane)) return _hoverPane;
        if (_activePane != null && _panes.Contains(_activePane)) return _activePane;
        return FirstMarkablePane();
    }

    private PaneView? FirstMarkablePane()
    {
        foreach (var pane in _panes)
            if (pane.Markable) return pane;

        return null;
    }

    private void SetActivePane(PaneView pane)
    {
        _activePane = pane;
        ApplyActivePane();
    }

    /// <summary>活动栏的视觉：卡片边框换成进度色、栏头亮一个实心圆点；其余栏退回灰边框。</summary>
    private void ApplyActivePane()
    {
        var active = ResolveActivePane();
        Brush normal = (Brush)FindResource("Line");

        foreach (var pane in _panes)
        {
            bool on = pane == active;
            pane.Active = on;

            if (pane.Root != null)
                pane.Root.BorderBrush = on ? ProgressBrush : normal;

            if (pane.Dot != null)
                pane.Dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (pane.PinButton != null)
                pane.PinButton.Content = pane == _pinnedPane ? "●" : "◎";
        }

        RefreshMarkerVisuals();
    }

    /// <summary>钉住 / 取消钉住这一栏。钉住之后活动栏不再跟着鼠标跑。</summary>
    private void TogglePin(PaneView pane)
    {
        _pinnedPane = _pinnedPane == pane ? null : pane;

        if (_pinnedPane != null)
        {
            _activePane = pane;
            Status($"已钉住「{System.IO.Path.GetFileName(pane.Spec.Path)}」这一栏");
        }
        else
        {
            Status("已取消钉住，活动栏重新跟着鼠标走");
        }

        ApplyActivePane();
    }

    private void SwitchToPane(int index)
    {
        if (index < 0 || index >= _panes.Count)
        {
            Status($"没有第 {index + 1} 栏");
            return;
        }

        _pinnedPane = null;     // 手动切栏 = 我现在就要操作它，钉住先让开
        _hoverPane = null;
        SetActivePane(_panes[index]);

        Status(_panes[index].Spec.Path.Length == 0
            ? $"活动栏：第 {index + 1} 栏"
            : $"活动栏：{System.IO.Path.GetFileName(_panes[index].Spec.Path)}");
    }

    // ---------------------------------------------------------------- 居中找回

    /// <summary>
    /// Ctrl+Alt+C：把当前句滚到栏的垂直中间，再让底带闪两下。
    /// 只动视口、不改进度 —— 这是一次"我看看自己译到哪了"，不是"我译完了"。
    /// </summary>
    private void CenterOnActivePane()
    {
        var pane = ResolveActivePane();
        if (pane?.Body?.Document == null) return;

        int offset = CurrentOffset(pane);
        if (offset < 0)
        {
            Status("这一栏还没标进度，先点一下正文");
            return;
        }

        int start = pane.Sentences is { Count: > 0 } ? pane.Sentences.StartOf(offset) : offset;
        Rect? rect = RectAtOffset(pane.Body.Document, start);
        if (rect == null) return;

        if (FindScrollViewer(pane.Body) is not ScrollViewer sv)
        {
            pane.Line?.BringIntoView();
            return;
        }

        double docY = sv.VerticalOffset + rect.Value.Y;
        double target = docY - (sv.ViewportHeight - rect.Value.Height) / 2;
        target = Math.Clamp(target, 0, Math.Max(0, sv.ScrollableHeight));

        AnimateScroll(sv, sv.VerticalOffset, target);
        PulseBand(pane);
    }

    /// <summary>约 180ms 的缓动滚动（12 帧左右）。指数缓出，起步快、收尾稳。</summary>
    private static void AnimateScroll(ScrollViewer sv, double from, double to)
    {
        if (Math.Abs(to - from) < 2)
        {
            sv.ScrollToVerticalOffset(to);
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, watch.Elapsed.TotalMilliseconds / 180.0);
            double eased = 1 - Math.Pow(1 - t, 3);
            sv.ScrollToVerticalOffset(from + (to - from) * eased);

            if (t >= 1) timer.Stop();
        };

        timer.Start();
    }

    /// <summary>底带闪两下：暗下去再回来，用来"一眼找回进度"。</summary>
    private static void PulseBand(PaneView pane)
    {
        if (pane.BandLayer == null) return;

        // 注意：Opacity 的上限是 1，原来写 1.0→1.85 会被裁成 1→1，等于根本没动。
        // 真正的脉冲得"先暗下去再回来"。
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.30,
            Duration = TimeSpan.FromMilliseconds(160),
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2),
        };

        pane.BandLayer.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
    }
}
