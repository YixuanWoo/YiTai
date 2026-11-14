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
    /// 表格不是文档里的普通块，而是塞进一个 BlockUIContainer：
    /// 里面再放一个 FlowDocumentScrollViewer（只读、可选中），横向滚动条在表格底下。
    /// 这样宽表不会被栏宽挤成一列单字 —— 拖它自己的滚动条看全。
    /// </summary>
    private static BlockUIContainer TableBlock(MdBlock block, MdStyle st)
    {
        var table = block.Table!;
        var inner = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            ColumnGap = 0,
            FontFamily = st.Body,
            FontSize = st.FontSize,
            Foreground = st.Text,
            LineHeight = st.FontSize * 1.45,
        };

        int cols = table.Aligns.Count;
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0) };
        for (int c = 0; c < cols; c++)
            t.Columns.Add(new TableColumn { Width = GridLength.Auto });

        var group = new TableRowGroup();
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var tr = new TableRow();

            for (int c = 0; c < cols; c++)
            {
                var cell = row.Cells[c];
                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    LineHeight = st.FontSize * 1.45,
                    TextAlignment = AlignmentOf(table.Aligns[c]),
                };
                if (row.IsHeader) p.FontWeight = FontWeights.Bold;
                AppendInlines(p, cell.Inlines, st);

                var tc = new TableCell
                {
                    BorderBrush = st.TableBorder,
                    // 只画右、下两条线，网格线不叠成 2px；最右/最下一列由外层边框补上
                    BorderThickness = new Thickness(0, 0, c < cols - 1 ? 1 : 0, r < table.Rows.Count - 1 ? 1 : 0),
                    Padding = new Thickness(8, 4, 8, 4),
                };
                if (row.IsHeader) tc.Background = st.TableHeaderBg;
                tc.Blocks.Add(p);
                tr.Cells.Add(tc);
            }
            group.Rows.Add(tr);
        }
        t.RowGroups.Add(group);
        inner.Blocks.Add(t);

        var viewer = new FlowDocumentScrollViewer
        {
            Document = inner,
            IsSelectionEnabled = true,
            IsToolBarVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        // 量一次自然宽度：栏够宽就按内容自然宽度，不够宽就让 viewer 出横向滚动条。
        // 上限挡一下"一个单元格塞了上千字没断行"的极端情况，免得横向滚动条拖出天际。
        viewer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = Math.Min(viewer.DesiredSize.Width, 3000);

        var outer = new Border
        {
            BorderBrush = st.TableBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, st.FontSize * 0.4, 0, st.FontSize * 0.7),
            Child = viewer,
        };
        outer.Tag = new TableHostInfo { Doc = inner, NaturalWidth = natural };

        return new BlockUIContainer(outer);
    }

    private static TextAlignment AlignmentOf(MdColumnAlign a) => a switch
    {
        MdColumnAlign.Center => TextAlignment.Center,
        MdColumnAlign.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    /// <summary>表格宿主信息：内层文档 + 它按内容量出来的自然宽度。由界面在布局后决定最终页宽。</summary>
    public sealed class TableHostInfo
    {
        public FlowDocument Doc = null!;
        public double NaturalWidth;
    }

    /// <summary>把这篇文档里所有的表格宿主（内层文档）收集起来，供界面设置页宽用。</summary>
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
    {
        foreach (var inl in inlines)
        {
            switch (inl.Kind)
            {
                case MdInlineKind.Bold:
                {
                    var b = new Bold();
                    b.Inlines.Add(MakeRun(inl));
                    p.Inlines.Add(b);
                    break;
                }

                case MdInlineKind.Italic:
                {
                    var it = new Italic();
                    it.Inlines.Add(MakeRun(inl));
                    p.Inlines.Add(it);
                    break;
                }

                case MdInlineKind.BoldItalic:
                {
                    var b = new Bold();
                    var it = new Italic();
                    it.Inlines.Add(MakeRun(inl));
                    b.Inlines.Add(it);
                    p.Inlines.Add(b);
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
                    p.Inlines.Add(span);
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
                    p.Inlines.Add(span);
                    break;
                }

                case MdInlineKind.Highlight:
                {
                    // 只给底色，不动字重字号 —— 高亮是"标出来"，不是"喊出来"
                    var span = new Span { Background = st.Mark };
                    span.Inlines.Add(MakeRun(inl));
                    p.Inlines.Add(span);
                    break;
                }

                case MdInlineKind.Link:
                {
                    var link = new Hyperlink { Foreground = st.Accent };
                    link.Inlines.Add(MakeRun(inl));
                    p.Inlines.Add(link);
                    break;
                }

                default:
                    p.Inlines.Add(MakeRun(inl));
                    break;
            }
        }
    }
}
