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
//   · 在原文栏里点一下（不拖选、不是双击的第二下）→ 竖线落到那句话前面
//   · Ctrl+Shift+↑ / ↓  → 上一句 / 下一句（按标点切句，不按段落跳）
//   · 右键 → 「把进度标记放在这里」/「清除进度标记」（兜底）
//
// 为什么不自动跟着译文光标走：译文和原文之间没有对齐数据，按段落序号硬推，
// 一段对不上就整条线静默错位 —— 比没有标记更坏。宁可让人点一下。
//
// 位置怎么记：记"原文里的字符偏移"，不记"第几段"。
// 原文增删一段之后，段序号全乱，字符偏移只是附近偏一点。
//
// 落盘在 .yitai\progress.json（见 ProjectLayout.ProgressFile）。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow
{
    /// <summary>竖线宽度。细了看不见，粗了像块膏药。</summary>
    private const double MarkerWidth = 3;

    private static readonly Color MarkerColor = Color.FromRgb(0x3F, 0xB5, 0x66);

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
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;

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

    /// <summary>每栏的正文挂鼠标事件与位置刷新。BuildPane 里调。</summary>
    private void AttachMarkerEvents(PaneView pane, RichTextBox body)
    {
        // 双击的第二下，一按下就把第一下落下的线收回去：
        // 若等抬起才撤，双击选词的整个过程里都会多一根线在那儿闪。
        body.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1) UndoLastMark();
        };

        body.PreviewMouseLeftButtonUp += (_, e) => OnPaneMouseUp(pane, e);
        body.PreviewMouseRightButtonUp += (_, e) => OnPaneMouseRightUp(pane, e);

        // 栏宽变了，文字重新折行，线的位置也得重算
        body.SizeChanged += (_, _) => UpdateMarkerVisual(pane);

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
        if (offset == CurrentOffset(pane)) return;             // 点在原处，什么都不用做

        PlaceMarkNow(pane, offset);
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

        ForgetUndo();   // 右键要弹菜单了：账本清掉，免得下一记双击把右键之前那条线撤走

        int offset = OffsetAtPoint(pane, e.GetPosition(pane.Body));
        if (offset < 0) return;

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
    /// 鼠标位置对应原文的哪个字符。
    /// 渲染时每一截文字都记了自己在原文的起点，所以这里能精确到"第几个字"，
    /// 而不是只到"第几段"——原文常常一整章就是一大段。
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
            int delta = run.ContentStart.GetOffsetToPosition(pos);   // 这一截是 1:1 对应原文的
            return runStart + Math.Max(0, delta);
        }

        // 落在段落空白、行首行尾：退回这一块（=这一行）的起点
        var block = TaggedBlockAt(pos);
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

        if (scroll) ScrollToMarker();
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

    /// <summary>只重写底栏那行字（字数、百分比），不动文档。</summary>
    private void UpdateCaption(PaneView pane)
    {
        if (pane.Caption == null || pane.BaseCaption.Length == 0) return;
        if (pane.ProgressKey.Length == 0) return;

        int marker = ProgressStore.Get(_progress, pane.ProgressKey);
        pane.Caption.Text = WithProgress(pane.BaseCaption, marker, pane.PaneText);
    }

    /// <summary>把某一栏的竖线摆到它自己的标记旁边。没标过 / 这一栏不能标 → 藏起来。</summary>
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

        // 先算出"应该在哪"，再一次性决定显示与位置 —— 不先藏再显。
        // 先藏再显每轮都让布局失效两次，而绝大多数刷新里位置根本没变，
        // 那两个来回就是白干的（也是以前空转的一部分）。
        Rect? rect = null;

        var doc = pane.Body?.Document;
        if (pane.Markable && doc != null)
        {
            int offset = CurrentOffset(pane);
            if (offset >= 0) rect = RectAtOffset(doc, offset);
        }

        if (rect == null || rect.Value.Height < 4)   // 没标过 / 那一处没排出高度（行间空隙）
        {
            if (line.Visibility != Visibility.Collapsed)
                line.Visibility = Visibility.Collapsed;
            return;
        }

        Rect r = rect.Value;

        // 立在那个字的左边缘，高度取整行 —— 像一根光标，而不是一根分割线
        double left = r.X - MarkerWidth / 2;
        double top = r.Y + 1;
        double height = Math.Max(9, r.Height - 2);

        // 位置、高度都没变，本来就显示着：一个属性都别写
        if (line.Visibility == Visibility.Visible
            && Math.Abs(Canvas.GetLeft(line) - left) < 0.5
            && Math.Abs(Canvas.GetTop(line) - top) < 0.5
            && Math.Abs(line.Height - height) < 0.5)
            return;

        Canvas.SetLeft(line, left);
        Canvas.SetTop(line, top);
        line.Height = height;
        line.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 原文偏移 → 那个字在栏里的矩形。
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

    /// <summary>键盘跳句之后，把视线拉到线那儿（鼠标点击不用滚，人就在那儿）。</summary>
    private void ScrollToMarker()
    {
        foreach (var pane in _panes)
        {
            if (!pane.Markable) continue;

            UpdateMarkerVisual(pane);
            pane.Line?.BringIntoView();
        }
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

    /// <summary>栏底那句：字数/段数之后接"译到多少字"。没标过就原样返回。</summary>
    /// <summary>
    /// 底栏那句"译到 X/Y 字（Z%）"。
    /// X、Y 都按"非空白字"数，跟栏底"字数"是同一个口径，免得一个 21 万一个 26 万。
    /// </summary>
    private static string WithProgress(string note, int marker, string text)
    {
        if (marker < 0) return note;

        int total = TextStats.CountChars(text);
        if (total <= 0) return note;

        int done = marker <= 0 ? 0 : TextStats.CountChars(text.Substring(0, Math.Min(marker, text.Length)));
        done = Math.Clamp(done, 0, total);
        int pct = (int)Math.Round(done * 100.0 / total);

        return note + $" · 译到 {done:N0}/{total:N0} 字（{pct}%）";
    }

    // ---------------------------------------------------------------- 键盘挪一句

    private void StepMarker(int delta)
    {
        var pane = FirstMarkablePane();
        if (pane?.Body?.Document == null) return;

        // 按"句子"走，不按"段落"走：原文常常一整章就是一大段，
        // 按段落跳一下就是半章，那不叫一句。
        string text = ResolvePaneContent(pane.Spec, out _, out _, out _);
        if (text.Length == 0) return;

        int offset = CurrentOffset(pane);

        if (offset < 0)
        {
            SetMarker(pane, 0, scroll: true);   // 还没标过：先标到最前面
            return;
        }

        int target = delta > 0
            ? NextSentence(text, offset)
            : PrevSentence(text, offset);

        if (target >= 0 && target != offset) SetMarker(pane, target, scroll: true);
    }

    /// <summary>句末标点。英文原文用 . ! ?，中文引文里可能出现全角。</summary>
    private static bool IsStop(char c) => c is '.' or '!' or '?' or '。' or '！' or '？' or '…';

    /// <summary>跟在句末标点后面的收尾引号/括号，算作上一句的一部分。</summary>
    private static bool IsCloser(char c) => c is '"' or '\'' or '”' or '’' or '）' or ')' or '】' or '」';

    /// <summary>下一个句子的开头；到末尾则返回 -1。</summary>
    private static int NextSentence(string text, int from)
    {
        int i = Math.Clamp(from, 0, text.Length);

        while (i < text.Length && !IsStop(text[i]) && !IsCloser(text[i])) i++;
        while (i < text.Length && (IsStop(text[i]) || IsCloser(text[i]))) i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

        return i < text.Length ? i : -1;
    }

    /// <summary>上一个句子的开头；已经在最前面则返回 0。</summary>
    private static int PrevSentence(string text, int from)
    {
        int pos = Math.Clamp(from, 0, text.Length);
        int guard = 0;

        while (pos > 0 && guard++ < 80)
        {
            int i = pos - 1;
            while (i >= 0 && (char.IsWhiteSpace(text[i]) || IsCloser(text[i]))) i--;

            int j = i;
            while (j >= 0 && !IsStop(text[j])) j--;
            if (j < 0) return 0;

            int k = j + 1;
            while (k < text.Length && (char.IsWhiteSpace(text[k]) || IsCloser(text[k]))) k++;

            if (k < pos) return k;     // 真往前了，就停这儿
            pos = j;                   // 还在原地/往后，再退一句
        }

        return 0;
    }

    /// <summary>键盘跳句作用在第一栏能标的（"跟随当前章"那套已经废了，栏与栏之间没有主次）。</summary>
    private PaneView? FirstMarkablePane()
    {
        foreach (var pane in _panes)
            if (pane.Markable) return pane;

        return null;
    }
}
