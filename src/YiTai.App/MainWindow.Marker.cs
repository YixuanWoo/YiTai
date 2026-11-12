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
    /// 排队等着落的标记。单击不立刻落，先等过一个系统双击间隔：
    /// 双击的第一下也是"没选区的一次抬起"，若当场标，双击选词就变成"顺手标了个词"。
    /// 第二下（ClickCount=2）一到，就把排队这条撤掉。
    /// </summary>
    private int _pendingClick = -1;

    private System.Windows.Threading.DispatcherTimer? _clickTimer;

    /// <summary>
    /// 上一次左键抬起的时刻。第二道"这是不是双击"的判据：
    /// ClickCount 由系统给，但如果它没递进来（不同输入路径、触控板、远程桌面），
    /// 光看它就会漏判 —— 那时双击仍然会插线。用时间间隔自己再判一次。
    /// </summary>
    private DateTime _lastUpAt = DateTime.MinValue;

    /// <summary>系统的双击间隔（毫秒）。比它短就等不住第二下。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();

    /// <summary>
    /// 等多久才认定"这一下是真单击"。
    /// 系统默认多在 250～500ms 之间，但我们宁可等到 500ms：
    /// 用户第二下按得快，落标就会抢在第二次按下之前，双击选词仍然会插一条线。
    /// 这半秒只为"确定没有第二下"，线落下之后一切照旧。
    /// </summary>
    private static int DoubleClickMs => Math.Max(500, GetDoubleClickTime());

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
        // 双击的第二下，按下就撤掉第一下排队的标记 —— 不等抬起，抢在它落标之前
        body.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1) CancelPendingMark();
        };

        body.PreviewMouseLeftButtonUp += (_, e) => OnPaneMouseUp(pane, e);
        body.PreviewMouseRightButtonUp += (_, e) => OnPaneMouseRightUp(pane, e);

        // 栏宽变了，文字重新折行，线的位置也得重算
        body.SizeChanged += (_, _) => UpdateMarkerVisual(pane);

        // 文档刚赋值时还没排版，那时算不出字符位置 —— 等布局完成后再摆一次线。
        // 滚动、缩放、切章最终都会走到这里，所以这一条就把"线跟着文字走"兜住了。
        body.LayoutUpdated += (_, _) => UpdateMarkerVisual(pane);

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
        if (pane.Spec.Kind != RefKind.FollowSource) return;   // 只有"跟随当前章"那栏有进度可言
        if (_store == null || _current == null) return;

        // 第二下（双击）来了：撤销第一下排队的那条，这一下也不标。
        // 双击选词从此不会顺手在词上插一条竖线。
        if (e.ClickCount > 1)
        {
            CancelPendingMark();
            _lastUpAt = DateTime.Now;
            return;
        }

        // 松手时选着字 = 人家在拖选复制，别插一脚（拖选也不该动双击计时）
        if (!pane.Body.Selection.IsEmpty) return;

        // ClickCount 没递进来的时候（触控板 / 远程桌面 / 别的输入路径），按时间自己判：
        // 上一次抬起到现在还不到一个双击间隔，那这一下就是第二下。
        bool tooFast = (DateTime.Now - _lastUpAt).TotalMilliseconds < DoubleClickMs;
        _lastUpAt = DateTime.Now;

        if (tooFast)
        {
            CancelPendingMark();
            return;
        }

        int offset = OffsetAtPoint(pane, e.GetPosition(pane.Body));
        if (offset < 0) return;                                // 点在栏的空白处
        if (offset == CurrentOffset()) return;                 // 点在原处，什么都不用做

        ScheduleMark(offset);
    }

    /// <summary>排一个待落的标记，过完双击间隔再落。</summary>
    private void ScheduleMark(int offset)
    {
        _pendingClick = offset;

        if (_clickTimer == null)
        {
            _clickTimer = new System.Windows.Threading.DispatcherTimer();
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer!.Stop();

                int at = _pendingClick;
                _pendingClick = -1;
                if (at >= 0) SetMarker(at);
            };
        }

        _clickTimer.Stop();
        _clickTimer.Interval = TimeSpan.FromMilliseconds(DoubleClickMs + 30);
        _clickTimer.Start();
    }

    private void CancelPendingMark()
    {
        _pendingClick = -1;
        _clickTimer?.Stop();
    }

    private void OnPaneMouseRightUp(PaneView pane, MouseButtonEventArgs e)
    {
        if (pane.Spec.Kind != RefKind.FollowSource) return;
        if (_store == null || _current == null) return;

        CancelPendingMark();   // 右键要弹菜单了，别让刚排队的那一下稍后又落一条线

        int offset = OffsetAtPoint(pane, e.GetPosition(pane.Body));
        if (offset < 0) return;

        _pendingOffset = offset;

        var menu = new ContextMenu { PlacementTarget = pane.Body };

        var put = new MenuItem { Header = "把进度标记放在这里" };
        put.Click += (_, _) => SetMarker(_pendingOffset);
        menu.Items.Add(put);

        var clear = new MenuItem { Header = "清除进度标记" };
        clear.Click += (_, _) => ClearMarker();
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

    private int CurrentOffset()
    {
        if (_store == null || _current == null) return -1;
        return ProgressStore.Get(_progress, _current.FileName);
    }

    private void SetMarker(int offset, bool scroll = false)
    {
        if (_store == null || _current == null) return;

        // 写不进盘（只读目录之类）也让线先立起来：界面不能因为存不了就卡住
        try { ProgressStore.Set(_store.Root, _progress, _current.FileName, offset); }
        catch (Exception) { /* 记不住就算了，这一处线照样立 */ }

        RefreshMarkerVisuals();

        if (scroll) ScrollToMarker();
    }

    private void ClearMarker()
    {
        if (_store == null || _current == null) return;

        try { ProgressStore.Set(_store.Root, _progress, _current.FileName, -1); }
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
            UpdateMarkerVisual(pane);
    }

    /// <summary>把某一栏的竖线摆到当前标记旁边。没有标记 / 不是原文栏 → 藏起来。</summary>
    private void UpdateMarkerVisual(PaneView pane)
    {
        var line = pane.Line;
        if (line == null) return;

        line.Visibility = Visibility.Collapsed;

        if (pane.Spec.Kind != RefKind.FollowSource) return;
        if (_store == null || _current == null) return;

        int offset = CurrentOffset();
        if (offset < 0) return;

        var doc = pane.Body?.Document;
        if (doc == null) return;

        Rect? rect = RectAtOffset(doc, offset);
        if (rect == null) return;

        Rect r = rect.Value;
        if (r.Height < 4) return;          // 那一处没排出高度（行间空隙），不立线

        // 立在那个字的左边缘，高度取整行 —— 像一根光标，而不是一根分割线
        Canvas.SetLeft(line, r.X - MarkerWidth / 2);
        Canvas.SetTop(line, r.Y + 1);
        line.Height = Math.Max(9, r.Height - 2);
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
            if (pane.Spec.Kind != RefKind.FollowSource) continue;

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
    private static string WithProgress(string note, int marker, string text)
    {
        if (marker < 0) return note;

        string t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int total = t.Length;
        if (total <= 0) return note;

        int done = Math.Clamp(marker, 0, total);
        int pct = (int)Math.Round(done * 100.0 / total);

        return note + $" · 译到 {done:N0}/{total:N0} 字（{pct}%）";
    }

    // ---------------------------------------------------------------- 键盘挪一句

    private void StepMarker(int delta)
    {
        var pane = FirstFollowPane();
        if (pane?.Body?.Document == null) return;
        if (_store == null || _current == null) return;

        // 按"句子"走，不按"段落"走：原文常常一整章就是一大段，
        // 按段落跳一下就是半章，那不叫一句。
        string text = ResolvePaneContent(pane.Spec, out _, out _, out _);
        if (text.Length == 0) return;

        int offset = CurrentOffset();

        if (offset < 0)
        {
            SetMarker(0, scroll: true);   // 还没标过：先标到最前面
            return;
        }

        int target = delta > 0
            ? NextSentence(text, offset)
            : PrevSentence(text, offset);

        if (target >= 0 && target != offset) SetMarker(target, scroll: true);
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

    private PaneView? FirstFollowPane()
    {
        foreach (var pane in _panes)
            if (pane.Spec.Kind == RefKind.FollowSource) return pane;

        return null;
    }
}
