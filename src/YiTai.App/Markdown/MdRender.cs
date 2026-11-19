// ============================================================
// MdRender.cs —— 把 MdDoc 画成 WPF 的 FlowDocument
// ============================================================
//
// 为什么用 FlowDocument 而不是把 Markdown 转成 HTML 再塞进 WebView：
//   1. FlowDocument 是原生控件，选中/复制/中文字体/HiDPI 都归框架管；
//   2. 它的排版是"给多宽、就按多宽折行"，宽度变了自动重排 ——
//      正好满足"放大到 200% 也不许文字溢出栏宽"这个要求；
//   3. 不用多带一个浏览器内核进单文件发布。
//
// 关键一条：ColumnWidth 设成无穷大 = 单列铺满视口宽度。
// 这一条不设，FlowDocument 会按默认列宽把自己切成好几列，
// 看起来像报纸，也会莫名其妙出现横向滚动。
//
// 所有字号都从 st.FontSize 推出来（标题按倍数），所以"缩放"只要
// 换一个 FontSize 重画一遍，整篇就跟着变大、自动重新折行。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

/// <summary>画一份 Markdown 要用的笔画与尺寸。由界面从主题里取，渲染器本身不认识主题名。</summary>
public sealed class MdStyle
{
    public FontFamily Body = new FontFamily("Microsoft YaHei");
    public FontFamily Mono = new FontFamily("Consolas");

    public Brush Text = Brushes.Black;
    public Brush Dim = Brushes.Gray;
    public Brush Accent = Brushes.SteelBlue;
    public Brush CodeBg = Brushes.WhiteSmoke;
    public Brush CodeFg = Brushes.DimGray;
    public Brush QuoteLine = Brushes.LightGray;

    /// <summary>
    /// ==高亮== 的荧光底色：红笔那一支（不是荧光黄）。
    /// 压到淡红（#FFD6D6），深色正文压在上面才看得清；再深就像一条报错横幅了。
    /// </summary>
    public Brush Mark = MarkBrush;

    /// <summary>表格网格线 / 表头底色。由界面从主题里取。</summary>
    public Brush TableBorder = Brushes.LightGray;
    public Brush TableHeaderBg = Brushes.WhiteSmoke;

    private static readonly Brush MarkBrush = CreateMarkBrush();

    private static Brush CreateMarkBrush()
    {
        var b = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0xD6));
        b.Freeze();   // 冻结：跨线程/跨栏共用同一支笔，不参与动画
        return b;
    }

    /// <summary>正文基准字号（已经乘过缩放）。</summary>
    public double FontSize = 14;

    /// <summary>行距倍数。</summary>
    public double LineSpacing = 1.7;

    /// <summary>中文长段两端对齐更好看，但英文资料会拉出大空隙，默认不齐。</summary>
    public bool Justify;
}

public static class MdRender
{
    /// <summary>进度线的颜色与粗细：一条 3px 的绿线，压在被切断的那一行上沿。</summary>
    public static readonly Brush MarkerLine = CreateMarkerLine();

    private const double MarkerThickness = 3;

    private static Brush CreateMarkerLine()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB5, 0x66));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 渲染一整篇。
    /// 进度标记不在这里画：它是一根浮在文字上的竖线（见 MainWindow.Marker.cs），
    /// 不插进文档、不切段、不改行距 —— 所以屏幕上的排版永远等于"没有标记"时的排版。
    /// </summary>
    public static FlowDocument Build(MdDoc md, MdStyle st)
    {
        var doc = NewDoc(st);

        foreach (var block in md.Blocks)
            AppendBlock(doc, block, st);

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(Note("（这份文件是空的）", st));

        return doc;
    }

    /// <summary>不用解析、直接显示一段提示（读不到文件、还没打开项目之类）。</summary>
    public static FlowDocument Message(string text, MdStyle st)
    {
        var doc = NewDoc(st);
        doc.Blocks.Add(Note(text, st));
        return doc;
    }

    /// <summary>
    /// 纯文本视图：不认任何格式，一行一段。
    /// .txt 资料用它 —— 免得正文里一个 `*` 或开头一个 `#` 被当成 Markdown 吃掉。
    /// </summary>
    public static FlowDocument Plain(string text, MdStyle st)
    {
        var doc = NewDoc(st);

        string t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        bool any = false;
        int offset = 0;

        foreach (string raw in t.Split('\n'))
        {
            int lineStart = offset;
            offset += raw.Length + 1;

            string s = raw.Trim();
            if (s.Length == 0) continue;

            any = true;
            var p = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, st.FontSize * 0.5),
                LineHeight = st.FontSize * st.LineSpacing,
            };
            p.Tag = lineStart;      // 给进度标记留个锚

            p.Inlines.Add(new Run(s) { Tag = lineStart });
            doc.Blocks.Add(p);
        }

        if (!any) doc.Blocks.Add(Note("（这份文件是空的）", st));
        return doc;
    }

    private static FlowDocument NewDoc(MdStyle st) => new FlowDocument
    {
        PagePadding = new Thickness(14, 10, 16, 18),
        ColumnWidth = double.PositiveInfinity,   // 单列铺满，按视口宽度折行
        ColumnGap = 0,
        FontFamily = st.Body,
        FontSize = st.FontSize,
        Foreground = st.Text,
        LineHeight = st.FontSize * st.LineSpacing,
        TextAlignment = st.Justify ? TextAlignment.Justify : TextAlignment.Left,
    };

    private static Paragraph Note(string text, MdStyle st)
    {
        var p = new Paragraph
        {
            Foreground = st.Dim,
            Margin = new Thickness(0, 0, 0, 8),
            LineHeight = st.FontSize * 1.6,
        };
        p.Inlines.Add(new Run(text));
        return p;
    }

    // ==================================================================== 块

    private static void AppendBlock(FlowDocument doc, MdBlock block, MdStyle st)
    {
        Block b;

        switch (block.Kind)
        {
            case MdBlockKind.Heading:
                b = Heading(block, st);
                break;

            case MdBlockKind.CodeBlock:
                b = CodeBlock(block, st);
                break;

            case MdBlockKind.Quote:
                b = Quote(block, st);
                break;

            case MdBlockKind.List:
                b = ListBlock(block, st);
                break;

            case MdBlockKind.Divider:
                b = Divider(st);
                break;

            case MdBlockKind.Table:
                b = TableBlock(block, st);
                break;

            default:
                b = ParagraphBlock(block, st);
                break;
        }

        // 每个块都记住"我在文档里的哪个字符位置"——进度竖线靠它落地
        b.Tag = block.SourceOffset;

        doc.Blocks.Add(b);
    }

    // ---------------------------------------------------------------- 位置反查
    //
    // 进度竖线要立在某句话前面，先得知道那句话在文档里的哪里。
    // 渲染时每一截文字都把"我在文档里第几个字"记在 Tag 上，这里顺着它找回去。

    /// <summary>
    /// 文档偏移 → 文档里的位置（落点给前一截的末尾，越界给 null）。
    /// </summary>
    public static TextPointer? PointerAtOffset(FlowDocument doc, int offset)
    {
        TextPointer? tail = null;

        foreach (Run run in AllRuns(doc))
        {
            if (run.Tag is not int start) continue;

            int len = run.Text.Length;
            int end = start + len;

            if (offset >= start && offset < end)
                return run.ContentStart.GetPositionAtOffset(offset - start, LogicalDirection.Forward);

            if (offset == end)
                tail = run.ContentStart.GetPositionAtOffset(len, LogicalDirection.Forward);
        }

        return tail;
    }

    /// <summary>文档里所有文字截（含粗体/斜体/链接里那些）。</summary>
    private static IEnumerable<Run> AllRuns(FlowDocument doc)
    {
        foreach (Block b in doc.Blocks)
        {
            if (b is Paragraph p)
            {
                foreach (Run r in RunsIn(p.Inlines)) yield return r;
            }
            else if (b is List l)
            {
                foreach (ListItem li in l.ListItems)
                {
                    foreach (Block ib in li.Blocks)
                    {
                        if (ib is Paragraph lp)
                            foreach (Run r in RunsIn(lp.Inlines)) yield return r;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 文档里所有 ==高亮== 区间的源偏移（[Start, End)）。
    /// 进度高亮的底带碰到它们要让路：两片半透明色叠在一起会脏，改成只画下划线。
    /// </summary>
    public static List<(int Start, int End)> HighlightRanges(FlowDocument doc)
    {
        var list = new List<(int, int)>();

        foreach (Run run in AllRuns(doc))
        {
            if (run.Tag is not int start) continue;

            DependencyObject? cur = run.Parent;
            while (cur is Span span)
            {
                if ((span.Tag as string) == "hl")
                {
                    list.Add((start, start + run.Text.Length));
                    break;
                }

                cur = span.Parent;
            }
        }

        return list;
    }

    /// <summary>
    /// 一段源文字（[start, end)）在文档里排成了哪几段矩形：**一个视觉行一段**。
    ///
    /// 为什么必须按视觉行切：一句常常折成两三行。只在句首画一根线、或者拿一个整块矩形一罩，
    /// 都会横穿行尾的空白、盖到旁边的段落上去。
    ///
    /// 坐标是 RichTextBox 视口那套（与叠在上面的浮层 Canvas 同一套），
    /// 所以滚动之后要重算 —— 调用方在 ScrollChanged 里刷。
    /// </summary>
    public static List<Rect> RectSegments(FlowDocument doc, int start, int end)
    {
        var list = new List<Rect>();

        var p0 = PointerAtOffset(doc, start);
        if (p0 == null) return list;

        var p1 = end > start ? PointerAtOffset(doc, end) : null;

        // 结尾对不上（源文末尾的语法符号没排出来、或这一句跨到了表格里）：
        // 只画句首一个字，不硬撑到文末。
        if (p1 == null)
        {
            Rect one = p0.GetCharacterRect(LogicalDirection.Forward);
            if (!one.IsEmpty && one.Height >= 4) list.Add(one);
            return list;
        }

        TextPointer? p = p0;

        for (int guard = 0; guard < 60 && p != null; guard++)
        {
            Rect r0 = p.GetCharacterRect(LogicalDirection.Forward);
            if (r0.IsEmpty || r0.Height < 4)
            {
                p = p.GetLineStartPosition(1);
                continue;
            }

            TextPointer? next = p.GetLineStartPosition(1);
            TextPointer segEnd = (next != null && next.CompareTo(p1) < 0) ? next : p1;

            Rect re = segEnd.GetCharacterRect(LogicalDirection.Backward);
            double right = (re.IsEmpty || re.Height < 4) ? r0.X + Math.Max(4, r0.Width) : re.X + re.Width;

            double left = r0.X;
            if (right <= left) right = left + 6;

            list.Add(new Rect(left, r0.Y, right - left, r0.Height));

            if (segEnd.CompareTo(p1) >= 0) break;
            p = next;
        }

        return list;
    }

    /// <summary>
    /// 在**渲染结果**上切句，返回的区间是文档偏移（Run.Tag 那套坐标）。
    ///
    /// 为什么不在原文件上切：Markdown 会吃掉 `#`、`**`、`|` 这些符号，
    /// 同一个字在原文里和在文档里的偏移不是同一个数 —— 按原文切出来的句子
    /// 落到文档里就会一行比一行偏。所以把排出来的文字重新拼一遍再切，
    /// 切完按 Run 的 Tag 换算回文档偏移。
    /// </summary>
    public static SentenceIndex SentenceIndexFor(FlowDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        var map = new List<(int ConStart, int TagStart, int Len)>();
        var hlZones = new List<(int Start, int End)>();   // ==高亮== 内容的拼接坐标（T-31）

        void AppendParagraph(Paragraph p)
        {
            // RunsIn 不带祖先信息，所以自己走一遍：遇到 Tag 为 "hl" 的 Span 时，
            // 把内部 Run 的拼接坐标记进高亮区，切句之后按它整句合并（T-31）。
            void Walk(InlineCollection inlines, bool inHl)
            {
                foreach (Inline inl in inlines)
                {
                    if (inl is Run run)
                    {
                        if (run.Tag is not int tag) continue;
                        if (run.Text.Length == 0) continue;

                        int conStart = sb.Length;
                        map.Add((conStart, tag, run.Text.Length));
                        sb.Append(run.Text);

                        if (inHl) hlZones.Add((conStart, sb.Length));
                    }
                    else if (inl is Span span)
                    {
                        Walk(span.Inlines, inHl || (span.Tag as string) == "hl");
                    }
                }
            }

            Walk(p.Inlines, inHl: false);

            sb.Append("\n\n");   // 段与段之间是硬边界
        }

        foreach (Block b in doc.Blocks)
        {
            if (b is Paragraph p)
            {
                AppendParagraph(p);
            }
            else if (b is List l)
            {
                foreach (ListItem li in l.ListItems)
                    foreach (Block ib in li.Blocks)
                        if (ib is Paragraph lp) AppendParagraph(lp);
            }
            else if (b is BlockUIContainer)
            {
                sb.Append("\n\n");   // 表格：整块当边界，不参与切句
            }
        }

        var raw = SentenceIndex.FromPlain(sb.ToString());

        var plain = new List<(int Start, int End)>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
            plain.Add((raw.At(i).Start, raw.At(i).End));

        // ==高亮== 整句口径（T-31）：与高亮区相交的句子合并成一句，再换算回文档偏移
        var merged = SentenceIndex.MergeSpansOverZones(plain, hlZones);

        var spans = new List<(int Start, int End)>();

        foreach (var (a0, b0) in merged)
        {
            int a = ConToTag(map, a0, isEnd: false);
            int b = ConToTag(map, b0, isEnd: true);
            if (b > a) spans.Add((a, b));
        }

        return SentenceIndex.FromSpans(spans);
    }

    /// <summary>拼接文本里的位置 → 文档偏移（Run.Tag）。落在段间分隔符上就贴到最近的 Run。</summary>
    private static int ConToTag(List<(int ConStart, int TagStart, int Len)> map, int pos, bool isEnd)
    {
        int p = isEnd && pos > 0 ? pos - 1 : pos;

        foreach (var e in map)
            if (p >= e.ConStart && p < e.ConStart + e.Len)
                return e.TagStart + (p - e.ConStart) + (isEnd ? 1 : 0);

        if (isEnd)
        {
            int last = -1;
            for (int i = 0; i < map.Count; i++)
                if (map[i].ConStart <= p) last = i;

            return last < 0 ? 0 : map[last].TagStart + map[last].Len;
        }

        foreach (var e in map)
            if (e.ConStart > p) return e.TagStart;

        return map.Count > 0 ? map[map.Count - 1].TagStart + map[map.Count - 1].Len : 0;
    }

    /// <summary>文档里所有文字截（含粗体/斜体/链接里那些）。批注层用同一份遍历口径。</summary>
    public static IEnumerable<Run> TextRuns(FlowDocument doc) => AllRuns(doc);

    /// <summary>
    /// 文本位置 → 文档偏移（Run.Tag 那套坐标，也就是 progress.json 的坐标系）。
    ///
    /// 落在某一截文字里就是"这一截的起点 + 在这一截里的第几个字"；
    /// 落在段落空白、表格（BlockUIContainer）上返回 null —— 那里没有字可批注。
    /// 偏移与 <see cref="PointerAtOffset"/> 互逆：PointerAtOffset(OffsetAtPointer(p)) 落回同一截里。
    /// </summary>
    public static int? OffsetAtPointer(FlowDocument doc, TextPointer? pos)
    {
        if (doc == null || pos == null) return null;

        foreach (Run run in AllRuns(doc))
        {
            if (run.Tag is not int start) continue;

            var cs = run.ContentStart;
            var ce = run.ContentEnd;

            if (pos.CompareTo(cs) < 0) continue;
            if (pos.CompareTo(ce) > 0) continue;

            int delta = cs.GetOffsetToPosition(pos);   // 这一截是 1:1 对应文档的
            if (delta < 0) continue;
            if (delta > run.Text.Length) delta = run.Text.Length;

            return start + delta;
        }

        return null;
    }

    /// <summary>文本位置所在的最近一截文字（找不到给 null）。批注命中测试用。</summary>
    public static Run? RunAtPointer(FlowDocument doc, TextPointer? pos)
    {
        if (doc == null || pos == null) return null;

        foreach (Run run in AllRuns(doc))
        {
            if (run.Tag is not int) continue;
            if (pos.CompareTo(run.ContentStart) < 0) continue;
            if (pos.CompareTo(run.ContentEnd) > 0) continue;
            return run;
        }

        return null;
    }

    /// <summary>
    /// 文档偏移之前有多少个非空白字。
    /// 底栏的"译到 X/Y 字（Z%）"要用它 ——
    /// 不能拿原文件去 Substring：Markdown 的偏移跟原文字符位置不是一套。
    /// </summary>
    public static int CountCharsBefore(FlowDocument doc, int tagOffset)
    {
        int n = 0;

        foreach (Run run in AllRuns(doc))
        {
            if (run.Tag is not int start) continue;
            if (tagOffset <= start) break;

            string t = run.Text;
            int take = Math.Min(t.Length, tagOffset - start);

            for (int i = 0; i < take; i++)
                if (!char.IsWhiteSpace(t[i])) n++;
        }

        return n;
    }

    private static IEnumerable<Run> RunsIn(InlineCollection inlines)
    {
        foreach (Inline inl in inlines)
        {
            if (inl is Run run) yield return run;
            else if (inl is Span span)
            {
                foreach (Run r in RunsIn(span.Inlines)) yield return r;
            }
        }
    }


    /// <summary>
    /// 按文档偏移找该划进度线的块：取"起始偏移不大于 offset 的最后一个块"。
    /// offset 落在第一块之前时给第一块；空文档给 null。
    /// </summary>
    public static Block? FindBlockByOffset(FlowDocument doc, int offset, out int index)
    {
        index = -1;
        Block? hit = null;
        int i = 0;

        foreach (Block b in doc.Blocks)
        {
            if (b.Tag is not int at) { i++; continue; }
            if (at <= offset)
            {
                hit = b;
                index = i;
            }
            else
            {
                if (hit == null) { hit = b; index = i; }   // 标记在第一块之前
                break;
            }
            i++;
        }

        return hit;
    }

    private static Paragraph ParagraphBlock(MdBlock block, MdStyle st)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, st.FontSize * 0.82),
            LineHeight = st.FontSize * st.LineSpacing,
        };
        AppendInlines(p, block.Inlines, st);
        return p;
    }

    private static Paragraph Heading(MdBlock block, MdStyle st)
    {
        double scale = block.Level switch
        {
            1 => 1.7,
            2 => 1.42,
            3 => 1.22,
            4 => 1.1,
            5 => 1.02,
            _ => 0.96,
        };

        var p = new Paragraph
        {
            FontSize = st.FontSize * scale,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, st.FontSize * (block.Level <= 2 ? 0.9 : 0.6), 0, st.FontSize * 0.42),
            LineHeight = st.FontSize * scale * 1.45,
        };
        AppendInlines(p, block.Inlines, st);
        return p;
    }

    private static Paragraph CodeBlock(MdBlock block, MdStyle st)
    {
        var p = new Paragraph
        {
            FontFamily = st.Mono,
            FontSize = st.FontSize * 0.94,
            Background = st.CodeBg,
            Foreground = st.CodeFg,
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, st.FontSize * 0.35, 0, st.FontSize * 0.85),
            LineHeight = st.FontSize * 1.55,
        };
        p.Inlines.Add(new Run(block.Text));
        return p;
    }

    private static Paragraph Quote(MdBlock block, MdStyle st)
    {
        var p = new Paragraph
        {
            Foreground = st.Dim,
            BorderBrush = st.QuoteLine,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 4),
            Margin = new Thickness(0, st.FontSize * 0.3, 0, st.FontSize * 0.8),
            LineHeight = st.FontSize * st.LineSpacing,
        };
        AppendInlines(p, block.Inlines, st);
        return p;
    }

    private static System.Windows.Documents.List ListBlock(MdBlock block, MdStyle st)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = block.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(st.FontSize * 1.1, st.FontSize * 0.25, 0, st.FontSize * 0.85),
            Padding = new Thickness(0),
            FontSize = st.FontSize,
        };

        foreach (var item in block.Items)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(item.Level * st.FontSize * 1.2, 0, 0, 4),
                LineHeight = st.FontSize * st.LineSpacing,
            };
            AppendInlines(p, item.Inlines, st);
            list.ListItems.Add(new ListItem(p));
        }

        return list;
    }

    private static BlockUIContainer Divider(MdStyle st)
    {
        var rule = new System.Windows.Controls.Border
        {
            Height = 1,
            Background = st.QuoteLine,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, st.FontSize * 0.6, 0, st.FontSize * 0.7),
        };
        return new BlockUIContainer(rule);
    }

    // ---------------------------------------------------------------- 表格（GFM 管道表）

    /// <summary>
    /// 表格用 Grid 画，外面套一个 ScrollViewer（横向滚动条在表格正下方）。
    /// 不用 FlowDocument 里的 Table：那个 Table 的列宽天生会"缩到页宽"，
    /// 宽表只会被挤成一列单字，怎么给页宽都不出横向滚动条。
    /// Grid 的列宽是固定像素，宽了 ScrollViewer 自己就滚；窄表由界面把列放大到撑满
    /// （见 MainWindow.UpdateTablePageWidths）。
    /// </summary>
    private static BlockUIContainer TableBlock(MdBlock block, MdStyle st)
    {
        var table = block.Table!;
        int cols = table.Aligns.Count;

        // 量每一列的自然宽度：取该列最宽那格的文字宽度，写成固定像素列宽。
        var colW = new List<double>(cols);
        for (int c = 0; c < cols; c++)
        {
            double widest = 0;
            foreach (var row in table.Rows)
            {
                double w = MeasureWidth(CellText(row.Cells[c]), st);
                if (w > widest) widest = w;
            }

            // 左右内边距 16 + 右边线 1 + 一点余量，免得差一像素就提前折行
            double width = Math.Clamp(widest + 22, 44, st.FontSize * 40);
            colW.Add(width);
        }

        var grid = new Grid();
        foreach (double w in colW)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        for (int r = 0; r < table.Rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];

            for (int c = 0; c < cols; c++)
            {
                var cell = row.Cells[c];

                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = AlignmentOf(table.Aligns[c]),
                    FontFamily = st.Body,
                    FontSize = st.FontSize,
                    Foreground = st.Text,
                    LineHeight = st.FontSize * 1.45,
                };
                if (row.IsHeader) tb.FontWeight = FontWeights.Bold;
                AppendInlinesTo(tb.Inlines, cell.Inlines, st);

                var box = new Border
                {
                    BorderBrush = st.TableBorder,
                    // 只画右、下两条线，网格线不叠成 2px；最右/最下一列由外层边框补上
                    BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < table.Rows.Count - 1 ? 1 : 0),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = tb,
                };
                if (row.IsHeader) box.Background = st.TableHeaderBg;

                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                grid.Children.Add(box);
            }
        }

        double natural = 0;
        foreach (double w in colW) natural += w;

        var sv = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        var outer = new Border
        {
            BorderBrush = st.TableBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, st.FontSize * 0.4, 0, st.FontSize * 0.7),
            Child = sv,
        };
        outer.Tag = new TableHostInfo { Grid = grid, Viewer = sv, Host = outer, ColWidths = colW, NaturalWidth = natural };

        return new BlockUIContainer(outer);
    }

    /// <summary>单元格里的纯文字（几截行内拼起来），只用来量列宽。</summary>
    private static string CellText(MdTableCell cell)
    {
        if (cell.Inlines.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var inl in cell.Inlines) sb.Append(inl.Text);
        return sb.ToString();
    }

    /// <summary>这段文字在这套字体字号下有多宽。量列宽用；粗体比这里宽一点点，已留余量。</summary>
    private static double MeasureWidth(string text, MdStyle st)
    {
        if (text.Length == 0) return 0;

        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(st.Body, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            st.FontSize,
            Brushes.Black,
            1.0);

        return ft.WidthIncludingTrailingWhitespace;
    }

    private static TextAlignment AlignmentOf(MdColumnAlign a) => a switch
    {
        MdColumnAlign.Center => TextAlignment.Center,
        MdColumnAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    /// <summary>
    /// 表格宿主信息：表格本体（Grid，列宽是固定像素）/ 外层边框（拿它的实际宽度当"栏够不够宽"）/
    /// 每列的自然宽度 / 所有列加起来多宽。界面布局后决定"撑满"还是"让它滚"。
    /// </summary>
    public sealed class TableHostInfo
    {
        public Grid Grid = null!;
        public ScrollViewer Viewer = null!;
        public FrameworkElement Host = null!;
        public List<double> ColWidths = new();
        public double NaturalWidth;
    }

    /// <summary>把这篇文档里所有的表格宿主收集起来，供界面决定"撑满"还是"出滚动条"。</summary>
    public static List<TableHostInfo> ExtractTableHosts(FlowDocument doc)
    {
        var list = new List<TableHostInfo>();
        foreach (Block b in doc.Blocks)
            if (b is BlockUIContainer c && c.Child is Border bd && bd.Tag is TableHostInfo info)
                list.Add(info);
        return list;
    }

    // ==================================================================== 行内

    /// <summary>
    /// 造一节文字，并把它的文档起点记在 Tag 上。
    /// 点到哪个字，就能顺着这个 Tag 反推出"文档第几个字符"——进度线要这么定位。
    /// </summary>
    private static Run MakeRun(MdInline inl)
    {
        var run = new Run(inl.Text);
        run.Tag = inl.SourceOffset;
        return run;
    }

    private static void AppendInlines(Paragraph p, List<MdInline> inlines, MdStyle st)
        => AppendInlinesTo(p.Inlines, inlines, st);

    /// <summary>
    /// 把行内段落塞进任意 InlineCollection：段落（FlowDocument）和表格单元格（TextBlock）
    /// 用的都是同一套行内样式，只是装的地方不同，所以共用这一份。
    /// </summary>
    private static void AppendInlinesTo(InlineCollection target, List<MdInline> inlines, MdStyle st)
    {
        foreach (var inl in inlines)
        {
            switch (inl.Kind)
            {
                case MdInlineKind.Bold:
                {
                    var b = new Bold();
                    b.Inlines.Add(MakeRun(inl));
                    target.Add(b);
                    break;
                }

                case MdInlineKind.Italic:
                {
                    var it = new Italic();
                    it.Inlines.Add(MakeRun(inl));
                    target.Add(it);
                    break;
                }

                case MdInlineKind.BoldItalic:
                {
                    var b = new Bold();
                    var it = new Italic();
                    it.Inlines.Add(MakeRun(inl));
                    b.Inlines.Add(it);
                    target.Add(b);
                    break;
                }

                case MdInlineKind.Code:
                {
                    var span = new Span
                    {
                        FontFamily = st.Mono,
                        Background = st.CodeBg,
                        Foreground = st.CodeFg,
                    };
                    span.Inlines.Add(MakeRun(inl));
                    target.Add(span);
                    break;
                }

                case MdInlineKind.Strike:
                {
                    var span = new Span
                    {
                        Foreground = st.Dim,
                        TextDecorations = TextDecorations.Strikethrough,
                    };
                    span.Inlines.Add(MakeRun(inl));
                    target.Add(span);
                    break;
                }

                case MdInlineKind.Highlight:
                {
                    // 只给底色，不动字重字号 —— 高亮是"标出来"，不是"喊出来"
                    var span = new Span { Background = st.Mark, Tag = "hl" };
                    span.Inlines.Add(MakeRun(inl));
                    target.Add(span);
                    break;
                }

                case MdInlineKind.Link:
                {
                    var link = new Hyperlink { Foreground = st.Accent };
                    link.Inlines.Add(MakeRun(inl));
                    target.Add(link);
                    break;
                }

                default:
                    target.Add(MakeRun(inl));
                    break;
            }
        }
    }
}
