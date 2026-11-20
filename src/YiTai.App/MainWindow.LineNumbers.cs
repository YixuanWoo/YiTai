// ============================================================
// MainWindow.LineNumbers.cs —— 译文栏 / 预览页的行号
// ============================================================
//
// 两处行号，一个口径（Core/Text/LineMap.cs）：归一化原文的逻辑行号，从 1 起，
// 软换行折出来的视觉行不另编号。
//
//   · 编辑页：TextBox 里每个 \n 是一个逻辑行 → 行号 = 行序；
//   · 预览页：每个逻辑行成一段，段的 SourceOffset 反查行号 → 与编辑页同号。
//
// 位置怎么来（这是本单的根因修复）：不问"这一行会折成几行"，直接问正文
// "这一行的第一个字实际落在哪个 Y"，把号码摆在那儿（LineNumRail.cs）。
// 折行、换字号、拉窄栏位全由 WPF 排版给答案，行号不再自己估一遍。
// 旧实现（估算折行宽度 + 抄 VerticalOffset）在栏一拉窄就逐行往上漂，
// 根因是估算宽度没扣 DocBox 的左右 Padding（18+14=32px）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

public partial class MainWindow
{
    private ScrollViewer? _editorScroller;    // TbxTranslation 内层滚动器（PART_ContentHost）
    private ScrollViewer? _previewScroller;   // PreviewDoc 内层滚动器
    private LineNumRail? _editorRail;
    private LineNumRail? _previewRail;

    private bool _lineNumbersReady;
    private bool _editorNumPending;
    private bool _previewNumPending;
    private int _editorBuildAttempt;        // 同一次文本量不满时的重试次数
    private int _editorBuildTextLength = -1;

    /// <summary>构造函数里调一次：挂上文本变化 / 尺寸变化 / 首次布局。</summary>
    private void InitLineNumbers()
    {
        _editorRail = new LineNumRail(LineNumCanvas);
        _previewRail = new LineNumRail(PreviewLineNumCanvas);

        TbxTranslation.TextChanged += (_, _) =>
        {
            _editorBuildAttempt = 0;        // 换了稿子，重试计数从头来
            QueueEditorLineNumbers();
        };
        TbxTranslation.SizeChanged += (_, _) => QueueEditorLineNumbers();
        TbxTranslation.Loaded += (_, _) => AttachEditorScroller();
        PreviewDoc.Loaded += (_, _) => AttachPreviewScroller();
    }

    // ---------------------------------------------------------------- 编辑页

    /// <summary>模板应用之后才能找到内层 ScrollViewer；找到后绑滚动、并刷一次行号。</summary>
    private void AttachEditorScroller()
    {
        _editorScroller ??= FindDescendantScrollViewer(TbxTranslation);
        if (_editorScroller == null) return;

        _editorScroller.ScrollChanged -= OnEditorScrolled;
        _editorScroller.ScrollChanged += OnEditorScrolled;
        _editorScroller.SizeChanged -= OnEditorSizeChanged;
        _editorScroller.SizeChanged += OnEditorSizeChanged;

        _lineNumbersReady = true;
        RebuildEditorLineNumbers();
    }

    private void OnEditorScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!_lineNumbersReady || _editorRail == null || _editorScroller == null) return;

        // 文本/尺寸变了以后位置是旧的，先让重建排上队，别拿旧位置平移
        if (e.ExtentHeightChange != 0) { QueueEditorLineNumbers(); return; }

        // 已摆好的号码盖不住当前视口（稿子变长 / 栏位变窄 / 号码是上一版排版量的）→ 重建
        if (!_editorRail.Covers(e.VerticalOffset, _editorScroller.ViewportHeight))
        {
            QueueEditorLineNumbers();
            return;
        }

        _editorRail.ScrollTo(e.VerticalOffset, _editorScroller.ViewportHeight);
    }

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e) => QueueEditorLineNumbers();

    private void QueueEditorLineNumbers()
    {
        if (!_lineNumbersReady || _editorNumPending) return;

        _editorNumPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _editorNumPending = false;
            RebuildEditorLineNumbers();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>按当前文本与排版，重建编辑页的行号位置。</summary>
    private void RebuildEditorLineNumbers()
    {
        if (!_lineNumbersReady || _editorScroller == null || _editorRail == null) return;

        // 先把排版跑到最新：窗口刚起来 / 栏位刚变宽变窄时，正文还没重排完就量坐标，
        // 量到的会是上一版布局的 Y（或干脆量不到屏幕外的行），行号就会整体错位、后面干脆没号。
        TbxTranslation.UpdateLayout();
        _editorScroller.UpdateLayout();

        double viewport = _editorScroller.ViewportHeight;
        if (viewport <= 0) viewport = TbxTranslation.ActualHeight;
        if (viewport <= 0) return;   // 还没排出来，等下一次 SizeChanged / Loaded 再算

        double offset = _editorScroller.VerticalOffset;
        string raw = TbxTranslation.Text;

        if (raw.Length != _editorBuildTextLength)
        {
            _editorBuildTextLength = raw.Length;
            _editorBuildAttempt = 0;
        }

        var rows = new List<(int Line, double Y, double Height)>();

        int line = 1;
        int at = 0;
        double lastY = double.NaN;
        double lastH = 0;
        int missed = 0;

        while (true)
        {
            double y = double.NaN, h = 0;

            if (at < raw.Length)
            {
                try
                {
                    Rect r = TbxTranslation.GetRectFromCharacterIndex(at, false);
                    if (r.Height > 2 && !double.IsNaN(r.Y)) { y = r.Y; h = r.Height; }
                }
                catch (ArgumentOutOfRangeException) { /* 还没排到这个字，跳过 */ }
            }
            else if (!double.IsNaN(lastY))
            {
                // 末尾那个空行（稿子以 \n 收尾时）：正文里它是一整行高的空位
                y = lastY + lastH;
                h = lastH;
            }

            if (!double.IsNaN(y))
            {
                rows.Add((line, y + offset, h));   // 换成"内容坐标"，滚动时整层平移即可
                lastY = y;
                lastH = h;
            }
            else if (at < raw.Length)
            {
                missed++;
            }

            if (at >= raw.Length) break;

            int nl = raw.IndexOf('\n', at);
            if (nl < 0) break;

            at = nl + 1;
            line++;
        }

        // 量不全就别发布半套号码（缺号比没有号更难看出问题）：等一次重排再量。
        // 只重试有限次，避免极端情况下一直排队。
        if (missed > 0 && _editorBuildAttempt < 3)
        {
            _editorBuildAttempt++;
            QueueEditorLineNumbers();
            return;
        }

        _editorBuildAttempt = 0;
        _editorRail.Build(rows, offset, viewport);
    }

    // ---------------------------------------------------------------- 预览页

    /// <summary>
    /// 预览页也有自己的滚动器；行号跟着它一起走。
    /// 注意：FlowDocumentScrollViewer 的内层 ScrollViewer 是**模板里**才有的，
    /// 窗口刚起来时预览是收着的、模板还没建，这时候找不着 —— 所以第一次切到预览时要再找一次。
    /// </summary>
    private void AttachPreviewScroller()
    {
        if (_previewScroller != null) return;

        var scroller = FindDescendantScrollViewer(PreviewDoc);
        if (scroller == null) return;

        _previewScroller = scroller;
        scroller.ScrollChanged += OnPreviewScrolled;
        scroller.SizeChanged += OnPreviewSizeChanged;
    }

    private void OnPreviewScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_previewRail == null || _previewScroller == null) return;
        if (e.ExtentHeightChange != 0) { QueuePreviewLineNumbers(); return; }

        if (!_previewRail.Covers(e.VerticalOffset, _previewScroller.ViewportHeight))
        {
            QueuePreviewLineNumbers();
            return;
        }

        _previewRail.ScrollTo(e.VerticalOffset, _previewScroller.ViewportHeight);
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => QueuePreviewLineNumbers();

    /// <summary>预览文档换了 / 刚切到预览：排一次队，等布局完成再摆号码。</summary>
    private void QueuePreviewLineNumbers()
    {
        if (_previewNumPending) return;

        _previewNumPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _previewNumPending = false;
            RebuildPreviewLineNumbers();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>按预览文档的块位置，重建预览页的行号。</summary>
    private void RebuildPreviewLineNumbers()
    {
        if (_previewRail == null) return;
        if (PreviewDoc.Visibility != Visibility.Visible) return;

        AttachPreviewScroller();          // 第一次可见时才建出模板，这时才找得到滚动器
        if (_previewScroller == null) return;

        PreviewDoc.UpdateLayout();
        _previewScroller.UpdateLayout();

        double viewport = _previewScroller.ViewportHeight;
        if (viewport <= 0) viewport = PreviewDoc.ActualHeight;
        if (viewport <= 0) return;

        var doc = PreviewDoc.Document;
        if (doc == null) return;

        double offset = _previewScroller.VerticalOffset;
        string text = LineMap.Normalize(TbxTranslation.Text);

        var rows = new List<(int Line, double Y, double Height)>();

        foreach (Block block in doc.Blocks)
            CollectPreviewRows(block, text, offset, rows);

        if (rows.Count == 0 && doc.Blocks.Count > 0)
        {
            QueuePreviewLineNumbers();    // 文档刚换上来、还没排完：下一轮再量
            return;
        }

        _previewRail.Build(rows, offset, viewport);
    }

    /// <summary>把一块（含列表里的每一项）翻成"第几行 + 它排在哪"。</summary>
    private void CollectPreviewRows(Block block, string text, double offset,
                                    List<(int Line, double Y, double Height)> rows)
    {
        if (block is List list)
        {
            // 列表的每一项在原文里各占一行：逐项编号，不给整个列表只编一个号
            foreach (ListItem li in list.ListItems)
                foreach (Block inner in li.Blocks)
                    CollectPreviewRows(inner, text, offset, rows);

            return;
        }

        int anchor = AnchorOffsetOf(block);
        if (anchor < 0) return;

        var rect = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty || rect.Height < 4) return;

        rows.Add((LineMap.LineOfOffset(text, anchor), rect.Y + offset, rect.Height));
    }

    /// <summary>这一块在归一化原文里的起点：优先块的 Tag，没有就取第一截文字的 Tag。</summary>
    private static int AnchorOffsetOf(Block block)
    {
        if (block.Tag is int tagged) return tagged;

        if (block is Paragraph p)
            return FirstRunTag(p.Inlines);

        return -1;
    }

    private static int FirstRunTag(InlineCollection inlines)
    {
        foreach (Inline inl in inlines)
        {
            if (inl is Run run && run.Tag is int at) return at;

            if (inl is Span span)
            {
                int inner = FirstRunTag(span.Inlines);
                if (inner >= 0) return inner;
            }
        }

        return -1;
    }

    /// <summary>在可视树里找第一个 ScrollViewer 后代（TextBox 模板里就是 PART_ContentHost）。</summary>
    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var found = FindDescendantScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }
}
