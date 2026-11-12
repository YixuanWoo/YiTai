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

            default:
                b = ParagraphBlock(block, st);
                break;
        }

        // 每个块都记住"我在原文的哪个字符位置"——进度竖线靠它落地
        b.Tag = block.SourceOffset;

        doc.Blocks.Add(b);
    }

    // ---------------------------------------------------------------- 位置反查
    //
    // 进度竖线要立在某句话前面，先得知道那句话在文档里的哪里。
    // 渲染时每一截文字都把"我在原文第几个字"记在 Tag 上，这里顺着它找回去。

    /// <summary>
    /// 原文偏移 → 文档里的位置（落点给前一截的末尾，越界给 null）。
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
    /// 按原文偏移找该划进度线的块：取"起始偏移不大于 offset 的最后一个块"。
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

    // ==================================================================== 行内

    /// <summary>
    /// 造一节文字，并把它的原文起点记在 Tag 上。
    /// 点到哪个字，就能顺着这个 Tag 反推出"原文第几个字符"——进度线要这么定位。
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
