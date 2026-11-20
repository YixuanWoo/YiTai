// ============================================================
// AnnotationRender.cs —— 把批注画进渲染出来的正文
// ============================================================
//
// 只做一件事：在 MdRender 画完之后，把批注盖住的那几截文字包进 Span：
//   · 橙色虚线（下划线）+ 极浅橙底 —— 正文里一眼能看出"这几个字有批注"；
//   · Span.ToolTip = 批注正文 —— 鼠标悬停直接显示，不用点开、不用开面板。
//
// 为什么是"包 Span"而不是画浮层：
//   浮层要跟着滚动、缩放、重排重新算坐标，而且选不中、悬停不到。
//   包进文字里，选中/复制/重排全归 WPF 管，字号一改它自己跟着走。
//
// 与既有 ==高亮== 的关系（红线：不许覆盖或破坏既有高亮）：
//   ==高亮== 的 Span 一个字都不改，只切它里面的 Run。
//   重叠处只画批注的下划线、不铺底（两片半透明色叠一起会发脏）；
//   高亮外的那一小段才铺浅底。分段由 Core 的 AnnotationMath.Slice 算。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using YiTai.Core;

namespace YiTai.App;

/// <summary>一条已经贴好位置的批注（Start/End 是文档偏移）。</summary>
public sealed class AnnotationView
{
    public AnnotationItem Item = null!;
    public int Start = -1;
    public int End = -1;

    public bool Anchored => Start >= 0 && End > Start;
}

public static class AnnotationRender
{
    /// <summary>批注色：偏红的橘（#C2410C），和进度高亮的琥珀 #E8A33D、正文的灰都分得开。</summary>
    public static readonly Color AnnColor = Color.FromRgb(0xC2, 0x41, 0x0C);

    /// <summary>浅底（半透明，深色主题上也压得住，不抢正文）。</summary>
    public static readonly Brush AnnBand = MakeBand();

    private static readonly Brush AnnLine = MakeLine();
    private static readonly TextDecoration AnnUnderline = MakeUnderline();

    private static Brush MakeLine()
    {
        var b = new SolidColorBrush(AnnColor);
        b.Freeze();
        return b;
    }

    private static Brush MakeBand()
    {
        var b = new SolidColorBrush(Color.FromArgb(0x30, AnnColor.R, AnnColor.G, AnnColor.B));
        b.Freeze();
        return b;
    }

    private static TextDecoration MakeUnderline()
    {
        var pen = new Pen(AnnLine, 1.5) { DashStyle = DashStyles.Dash };
        pen.Freeze();

        var d = new TextDecoration(TextDecorationLocation.Underline, pen, 1.5,
                                   TextDecorationUnit.Pixel, TextDecorationUnit.Pixel);
        d.Freeze();
        return d;
    }

    /// <summary>
    /// 把批注画进文档。没有可贴的批注就什么都不做（文档保持原样）。
    /// 返回真正画上去的批注条数。
    /// </summary>
    public static int Apply(FlowDocument doc, IReadOnlyList<AnnotationView>? views)
    {
        if (doc == null || views == null || views.Count == 0) return 0;

        var anns = new List<(string Id, int Start, int End)>();
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var v in views)
        {
            if (v == null || !v.Anchored || v.Item == null) continue;
            if (v.Item.Id.Length == 0) continue;

            anns.Add((v.Item.Id, v.Start, v.End));
            bodies[v.Item.Id] = v.Item.Body ?? "";
        }

        if (anns.Count == 0) return 0;

        var highlights = MdRender.HighlightRanges(doc);

        // 角标去重：一条批注只补一个小记号（同一段文字被高亮/正文切过好几截也不会补两遍）
        var badged = new HashSet<string>(StringComparer.Ordinal);

        // 先快照块集合再改：WPF 的 TextElementEnumerator 只要内容一变就会失效，
        // 边枚举边往段落里插 Span 会直接抛"集合被修改"。
        var blocks = new List<Block>(doc.Blocks.Count);
        foreach (Block b in doc.Blocks) blocks.Add(b);

        foreach (Block b in blocks)
            RewriteBlock(b, anns, highlights, bodies, badged);

        return anns.Count;
    }

    private static void RewriteBlock(Block block, IReadOnlyList<(string Id, int Start, int End)> anns,
                                     IReadOnlyList<(int Start, int End)> highlights,
                                     Dictionary<string, string> bodies,
                                     HashSet<string> badged)
    {
        if (block is Paragraph p)
        {
            RewriteInlines(p.Inlines, anns, highlights, bodies, badged);
        }
        else if (block is System.Windows.Documents.List list)
        {
            var items = new List<ListItem>(list.ListItems.Count);
            foreach (ListItem li in list.ListItems) items.Add(li);

            foreach (ListItem li in items)
            {
                var inner = new List<Block>(li.Blocks.Count);
                foreach (Block b in li.Blocks) inner.Add(b);
                foreach (Block b in inner) RewriteBlock(b, anns, highlights, bodies, badged);
            }
        }
    }

    /// <summary>
    /// 重写一层行内集合：Run 按批注/高亮边界切开，Span（粗体/斜体/高亮/链接…）只递归进去，
    /// 其余元素原样保留。Span 自己的样式一个字都不改。
    /// </summary>
    private static void RewriteInlines(InlineCollection target,
                                       IReadOnlyList<(string Id, int Start, int End)> anns,
                                       IReadOnlyList<(int Start, int End)> highlights,
                                       Dictionary<string, string> bodies,
                                       HashSet<string> badged)
    {
        if (target.Count == 0) return;

        var children = new List<Inline>(target.Count);
        foreach (Inline inl in target) children.Add(inl);

        bool changed = false;
        var rebuilt = new List<Inline>(children.Count);

        foreach (Inline child in children)
        {
            if (child is Run run)
            {
                var nodes = BuildRun(run, anns, highlights, bodies, badged);
                if (nodes != null)
                {
                    changed = true;
                    rebuilt.AddRange(nodes);
                }
                else
                {
                    rebuilt.Add(run);
                }
            }
            else if (child is Span span)
            {
                RewriteInlines(span.Inlines, anns, highlights, bodies, badged);
                rebuilt.Add(span);
            }
            else
            {
                rebuilt.Add(child);
            }
        }

        if (!changed) return;   // 这一层没有批注：一个字都不动

        target.Clear();
        foreach (Inline inl in rebuilt) target.Add(inl);
    }

    /// <summary>切一个 Run。没被批注盖住返回 null（调用方保留原 Run，不重建）。</summary>
    private static List<Inline>? BuildRun(Run run,
                                          IReadOnlyList<(string Id, int Start, int End)> anns,
                                          IReadOnlyList<(int Start, int End)> highlights,
                                          Dictionary<string, string> bodies,
                                          HashSet<string> badged)
    {
        if (run.Tag is not int start) return null;

        int end = start + run.Text.Length;
        if (end <= start) return null;

        bool touched = false;
        foreach (var a in anns)
            if (a.Start < end && a.End > start) { touched = true; break; }

        if (!touched) return null;

        var slices = AnnotationMath.Slice(start, end, anns, highlights);
        var nodes = new List<Inline>(slices.Count);

        foreach (var slice in slices)
        {
            string text = run.Text.Substring(slice.Start - start, slice.End - slice.Start);
            var seg = new Run(text) { Tag = slice.Start };

            Inline node = seg;

            if (slice.HasAnnotation)
            {
                // 多条批注叠在同一截：从里到外一层层包；最里面那层负责样式与悬停提示
                Span? inner = null;
                for (int k = slice.ItemIds.Count - 1; k >= 0; k--)
                {
                    var span = new Span { Tag = "ann:" + slice.ItemIds[k] };
                    span.Inlines.Add(node);
                    node = span;
                    if (k == slice.ItemIds.Count - 1) inner = span;
                }

                if (inner != null)
                {
                    inner.TextDecorations = new TextDecorationCollection { AnnUnderline };

                    // 撞上 ==高亮== 只画下划线，不铺底（两片半透明色叠一起会发脏）
                    if (!slice.Highlighted) inner.Background = AnnBand;

                    string id = slice.ItemIds[slice.ItemIds.Count - 1];
                    if (bodies.TryGetValue(id, out string? body) && body.Length > 0)
                        inner.ToolTip = body;   // 悬停即读，不用点开
                }
            }

            nodes.Add(node);

            // 角标：这条批注在正文里到这一截就结束了 → 紧跟一个橙色小记号，
            // 光有虚线+极浅底在密密麻麻的中文里还是不够显眼（董事会要"角标"）。
            // 记号自己的 Tag 故意用字符串：偏移反查、切句、高亮都只认 int Tag，
            // 多出来的这个字形绝不会被算进任何锚点。
            if (slice.HasAnnotation)
            {
                foreach (string id in slice.ItemIds)
                {
                    if (EndOfAnnotation(anns, id) != slice.End) continue;
                    if (!badged.Add(id)) continue;
                    nodes.Add(MakeBadge(bodies, id));
                }
            }
        }

        return nodes;
    }

    private static int EndOfAnnotation(IReadOnlyList<(string Id, int Start, int End)> anns, string id)
    {
        foreach (var a in anns)
            if (a.Id == id) return a.End;
        return -1;
    }

    /// <summary>橙色小菱形角标：悬停同样弹出批注正文，不用去点「批注」按钮。</summary>
    private static Run MakeBadge(Dictionary<string, string> bodies, string id)
    {
        var badge = new Run("◆")
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            BaselineAlignment = BaselineAlignment.Superscript,
            Foreground = AnnLine,
            Tag = "annbadge",
        };

        if (bodies.TryGetValue(id, out string? body) && body.Length > 0)
            badge.ToolTip = body;

        return badge;
    }

    /// <summary>从文本位置往上找"这是哪条批注"（命中测试用）。没有就返回空串。</summary>
    public static string AnnotationIdAt(TextPointer? pos)
    {
        if (pos == null) return "";

        DependencyObject? cur = pos.Parent;
        while (cur != null)
        {
            if (cur is Span span && span.Tag is string tag
                && tag.StartsWith("ann:", StringComparison.Ordinal))
                return tag.Substring(4);

            cur = (cur as FrameworkContentElement)?.Parent;
        }

        return "";
    }
}
