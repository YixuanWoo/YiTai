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

        foreach (string raw in t.Split('\n'))
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;

            any = true;
            var p = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, st.FontSize * 0.5),
                LineHeight = st.FontSize * st.LineSpacing,
            };
            p.Inlines.Add(new Run(s));
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
        switch (block.Kind)
        {
            case MdBlockKind.Heading:
                doc.Blocks.Add(Heading(block, st));
                break;

            case MdBlockKind.CodeBlock:
                doc.Blocks.Add(CodeBlock(block, st));
                break;

            case MdBlockKind.Quote:
                doc.Blocks.Add(Quote(block, st));
                break;

            case MdBlockKind.List:
                doc.Blocks.Add(ListBlock(block, st));
                break;

            case MdBlockKind.Divider:
                doc.Blocks.Add(Divider(st));
                break;

            default:
                doc.Blocks.Add(ParagraphBlock(block, st));
                break;
        }
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

    private static void AppendInlines(Paragraph p, List<MdInline> inlines, MdStyle st)
    {
        foreach (var inl in inlines)
        {
            switch (inl.Kind)
            {
                case MdInlineKind.Bold:
                {
                    var b = new Bold();
                    b.Inlines.Add(new Run(inl.Text));
                    p.Inlines.Add(b);
                    break;
                }

                case MdInlineKind.Italic:
                {
                    var it = new Italic();
                    it.Inlines.Add(new Run(inl.Text));
                    p.Inlines.Add(it);
                    break;
                }

                case MdInlineKind.BoldItalic:
                {
                    var b = new Bold();
                    var it = new Italic();
                    it.Inlines.Add(new Run(inl.Text));
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
                    span.Inlines.Add(new Run(inl.Text));
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
                    span.Inlines.Add(new Run(inl.Text));
                    p.Inlines.Add(span);
                    break;
                }

                case MdInlineKind.Link:
                {
                    var link = new Hyperlink { Foreground = st.Accent };
                    link.Inlines.Add(new Run(inl.Text));
                    p.Inlines.Add(link);
                    break;
                }

                default:
                    p.Inlines.Add(new Run(inl.Text));
                    break;
            }
        }
    }
}
