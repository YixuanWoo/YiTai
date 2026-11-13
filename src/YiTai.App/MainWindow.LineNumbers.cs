// ============================================================
// MainWindow.LineNumbers.cs —— 译文栏的行号
// ============================================================
//
// 像代码编辑器一样，在译文编辑区左侧画一栏行号。
//
// 关键约束：编辑区是 TextWrapping=Wrap，一段长文会折成好几行视觉行。
// 所以"行号"必须跟着**折行**走，否则滚几段就对不上。
// 做法：用一个和 DocBox 同字体、同字号、同行高（29px）的隐藏 TextBlock
// 去量每一逻辑行的折行数，再据此生成"数字 + 空行"的行号文本，
// 把行号栏的滚动器和编辑区的滚动器绑到同一个 VerticalOffset。
//
// 折行后，只有逻辑行的第一视觉行标数字，续行留空 —— 和 VS Code 的
// 软换行一致，不是每一视觉行都编个号。
// ============================================================

using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YiTai.App;

public partial class MainWindow
{
    private const double LineHeightPx = 29;   // 与 DocBox / LineNumBox 的 TextBlock.LineHeight 一致

    private ScrollViewer? _editorScroller;    // TbxTranslation 内层滚动器（PART_ContentHost）
    private ScrollViewer? _gutterScroller;    // LineNumGutter 内层滚动器
    private bool _lineNumbersReady;

    // 量折行用的隐藏控件：只在内存里 Measure，从不放进可视树
    private readonly TextBlock _measure = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Microsoft YaHei"),
        FontSize = 15,
        LineHeight = LineHeightPx,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    /// <summary>构造函数里调一次：挂上文本变化 / 尺寸变化 / 首次布局。</summary>
    private void InitLineNumbers()
    {
        TbxTranslation.TextChanged += (_, _) => RefreshLineNumbers();
        TbxTranslation.SizeChanged += (_, _) => RefreshLineNumbers();
        TbxTranslation.Loaded += (_, _) => AttachLineNumberScrollers();
        LineNumGutter.Loaded += (_, _) => AttachLineNumberScrollers();
    }

    /// <summary>模板应用之后才能找到内层 ScrollViewer；找到后绑滚动、并刷一次行号。</summary>
    private void AttachLineNumberScrollers()
    {
        if (_editorScroller == null) _editorScroller = FindDescendantScrollViewer(TbxTranslation);
        if (_gutterScroller == null) _gutterScroller = FindDescendantScrollViewer(LineNumGutter);
        if (_editorScroller == null || _gutterScroller == null) return;

        _editorScroller.ScrollChanged -= OnEditorScrolled;
        _editorScroller.ScrollChanged += OnEditorScrolled;
        _editorScroller.SizeChanged -= OnEditorSizeChanged;
        _editorScroller.SizeChanged += OnEditorSizeChanged;

        _lineNumbersReady = true;
        RefreshLineNumbers();
    }

    private void OnEditorScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_gutterScroller == null) return;
        if (Math.Abs(_gutterScroller.VerticalOffset - e.VerticalOffset) > 0.5)
            _gutterScroller.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e) => RefreshLineNumbers();

    /// <summary>按当前文本和折行宽度重建行号文本。</summary>
    private void RefreshLineNumbers()
    {
        if (!_lineNumbersReady || _editorScroller == null) return;

        // 折行宽度 = 编辑区可视宽度；留 1px 余地，避免刚好卡在边界多折一行
        double wrapWidth = Math.Max(20, _editorScroller.ViewportWidth - 1);

        string text = TbxTranslation.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');

        var sb = new StringBuilder(lines.Length * 4);
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(i + 1);
            int rows = RowsFor(lines[i], wrapWidth);
            // 续行：补空行，让下一个逻辑行的数字落到它该在的视觉行
            for (int r = 1; r < rows; r++) sb.Append('\n');
            sb.Append('\n');
        }

        LineNumGutter.Text = sb.ToString();
    }

    /// <summary>量一行文本在给定宽度下会折成几行（空行恒算 1 行）。</summary>
    private int RowsFor(string line, double wrapWidth)
    {
        if (line.Length == 0) return 1;

        _measure.Text = line;
        _measure.Measure(new Size(wrapWidth, double.PositiveInfinity));
        int rows = (int)Math.Ceiling(_measure.DesiredSize.Height / LineHeightPx);
        return Math.Max(1, rows);
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
