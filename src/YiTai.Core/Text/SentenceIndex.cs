using System;
using System.Collections.Generic;

namespace YiTai.Core;

/// <summary>
/// 把一整篇源文本切成"句子"，给进度高亮用。
///
/// 只做纯字符串算法、不碰界面，所以能单测。切出来的区间是**源文件的字符偏移**
/// （[Start, End) 左闭右开），跟 progress.json 里存的是同一套坐标。
///
/// 规则（宁可少切，也不能把 Mr. Smith 切成两句）：
///  · 句末：. ! ? 。！？…（省略号停顿：./…/。 的连续 run，…≥1 或 .≥2 或 。≥2 算一个，
///    并吞掉紧随的收尾；见 T-30）
///  · 句末后的收尾引号/括号 ”’"'）)】」 归给上一句
///  · 缩写守卫：Mr. Dr. etc. e.g. U.S. 以及单个大写字母（J.）后面的点不切
///  · 小数点不切：3.5 / 10,000.00
///  · 空行是硬边界；标题/列表/引用/表格这类块标记行，行尾也是硬边界
///  · 围栏代码块整块算一句，块内的句号不切
/// </summary>
public sealed class SentenceIndex
{
    /// <summary>一句：源偏移 [Start, End)。</summary>
    public readonly struct Span
    {
        public int Start { get; }
        public int End { get; }

        public Span(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Length => End - Start;
    }

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "vs", "etc",
        "e.g", "i.e", "cf", "fig", "no", "vol", "pp", "ch", "sec", "al",
        "a.m", "p.m", "u.s", "u.k", "inc", "ltd", "co", "dept", "approx",
    };

    private readonly List<Span> _spans = new();

    /// <summary>
    /// 用一批已经切好的区间直接建索引。
    /// 界面那边是"在渲染后的文档上切句"（Markdown 的字符偏移跟原文不是一套），
    /// 切完把区间换算成文档偏移再交回来，用这个方法装。
    /// </summary>
    public static SentenceIndex FromSpans(IReadOnlyList<(int Start, int End)> spans)
    {
        var idx = new SentenceIndex();

        foreach (var s in spans)
            if (s.End > s.Start) idx._spans.Add(new Span(s.Start, s.End));

        idx._spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return idx;
    }

    public int Count => _spans.Count;
    public Span At(int i) => _spans[Math.Clamp(i, 0, _spans.Count - 1)];

    // ------------------------------------------------------------- 构造

    public static SentenceIndex FromPlain(string text)
    {
        var idx = new SentenceIndex();
        if (string.IsNullOrEmpty(text)) return idx;

        var code = CodeZones(text);
        int len = text.Length;
        int i = 0;

        while (i < len)
        {
            while (i < len && char.IsWhiteSpace(text[i])) i++;
            if (i >= len) break;

            int start = i;
            int j = i;

            bool startedInCode = InZone(code, start);
            int codeEnd = startedInCode ? ZoneEnd(code, start) : -1;
            bool markerLine = !startedInCode && IsMarkerLine(text, start);

            while (j < len)
            {
                // 代码块整块算一句：到了块尾就收，不切里面的句号
                if (startedInCode)
                {
                    if (j >= codeEnd) break;
                }
                else
                {
                    if (IsHardBoundary(text, j)) break;
                    if (markerLine && text[j] == '\n') break;

                    char c = text[j];
                    if (c is '.' or '!' or '?' or '。' or '！' or '？' or '…')
                    {
                        // 小数点：3.5、0.85
                        if (c == '.' && j > 0 && j + 1 < len
                            && char.IsDigit(text[j - 1]) && char.IsDigit(text[j + 1]))
                        {
                            j++;
                            continue;
                        }

                        // 缩写里的点：Mr. / e.g. / U.S.
                        if (c == '.' && IsAbbreviationDot(text, j)) { j++; continue; }

                        // 省略号停顿（T-30）：E = { '.' , '…' , '。' } 的极大连续 run（不跨行）。
                        // run 中 …≥1 或 .≥2 或 。≥2 → 判为省略号停顿：整段 run 只产生**一个**
                        // 句末边界，并连同紧随的 T 类收尾（T = E ∪ ？！，含收尾引号/括号）一起吞掉
                        // ——「……！」「。。。？」「他说："……"？」都只算一句。
                        // 不满足触发条件的单个 '.' / '。' 落到下面的原逻辑，行为不变。
                        if (c is '.' or '。' or '…')
                        {
                            int k = j;
                            while (k < len && text[k] != '\n' && (text[k] is '.' or '。' or '…')) k++;

                            int halfDots = 0, cnDots = 0, marks = 0;
                            for (int q = j; q < k; q++)
                            {
                                if (text[q] == '.') halfDots++;
                                else if (text[q] == '。') cnDots++;
                                else marks++;
                            }

                            if (marks >= 1 || halfDots >= 2 || cnDots >= 2)
                            {
                                j = k;
                                while (j < len && text[j] != '\n'
                                       && (text[j] is '.' or '。' or '…' or '!' or '?' or '！' or '？'
                                           || IsCloser(text[j])))
                                    j++;
                                break;
                            }
                        }

                        j++;
                        break;
                    }
                }

                j++;
            }

            // 收尾引号/括号归上一句
            while (j < len && IsCloser(text[j])) j++;
            while (j < len && (text[j] == ' ' || text[j] == '\t')) j++;

            int end = j;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;

            if (end > start) idx._spans.Add(new Span(start, end));

            i = j > i ? j : i + 1;
        }

        return idx;
    }

    // ------------------------------------------------------------- 高亮整句合并

    /// <summary>
    /// ==高亮== 整句口径（T-31）：任一句区间与任一高亮区间相交 → 并成一个区间，
    /// 可传递合并（一句里多段高亮、高亮跨原句都收敛成一句）。
    /// 高亮区本身以并集形式进入输出 —— "紧邻普通文字并入同一句"就是靠重叠自然带上
    /// （到下一句末标点为止，因为句子边界是切句器给的）。
    /// 输出按 Start 排序、去重叠（**相邻可保留**：相邻两句是两句，不并）。
    /// zone 为空或 span 为空时原样返回（保持原顺序，不改输入）——
    /// 未闭合 == 不产生高亮区，走的就是 zone 空这条路，整句口径不触发。
    /// Core 纯函数：零依赖、不碰界面，渲染层（MdRender.SentenceIndexFor）与测试共用。
    /// </summary>
    public static List<(int Start, int End)> MergeSpansOverZones(
        IReadOnlyList<(int Start, int End)> spans,
        IReadOnlyList<(int Start, int End)> zones)
    {
        var sp = new List<(int Start, int End)>();
        var zl = new List<(int Start, int End)>();

        foreach (var s in spans) if (s.End > s.Start) sp.Add(s);
        foreach (var z in zones) if (z.End > z.Start) zl.Add(z);

        if (zl.Count == 0) return sp;   // 没高亮区：原样
        if (sp.Count == 0) return zl;   // 没句子：高亮区自己就是输出

        // 迭代到不动点：zone 与 span 相交 → 并成一个新区间放回 spans。
        // 并完的区间可能够到别的 zone / span，所以归并重叠后重扫一轮，直到并不动为止。
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int zi = 0; zi < zl.Count && !changed; zi++)
                for (int si = 0; si < sp.Count && !changed; si++)
                    if (sp[si].Start < zl[zi].End && zl[zi].Start < sp[si].End)
                    {
                        var u = (Start: Math.Min(sp[si].Start, zl[zi].Start),
                                 End: Math.Max(sp[si].End, zl[zi].End));
                        sp.RemoveAt(si);
                        zl.RemoveAt(zi);
                        sp.Add(u);
                        UnionOverlaps(sp);
                        changed = true;
                    }
        }

        // 输出 = 合并后的句区间 ∪ 没碰到句子的高亮区，按 Start 排序、去重叠
        var all = new List<(int Start, int End)>(sp);
        all.AddRange(zl);
        UnionOverlaps(all);
        return all;
    }

    /// <summary>把严格重叠（相邻不算）的区间并成一块，并按 Start 排序。</summary>
    private static void UnionOverlaps(List<(int Start, int End)> list)
    {
        if (list.Count < 2) return;

        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        int w = 0;
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Start < list[w].End)   // 严格重叠才并；首尾相接（==）保留
            {
                if (list[i].End > list[w].End) list[w] = (list[w].Start, list[i].End);
            }
            else
            {
                list[++w] = list[i];
            }
        }

        list.RemoveRange(w + 1, list.Count - w - 1);
    }

    // ------------------------------------------------------------- 查询

    /// <summary>offset 落在第几句；找不到给 -1。</summary>
    public int IndexAt(int offset)
    {
        for (int i = 0; i < _spans.Count; i++)
        {
            var s = _spans[i];
            if (offset >= s.Start && offset < s.End) return i;
        }

        for (int i = 0; i < _spans.Count; i++)
            if (offset >= _spans[i].Start) return i;

        return _spans.Count > 0 ? _spans.Count - 1 : -1;
    }

    /// <summary>把任意偏移吸附到它所在句的句首。</summary>
    public int StartOf(int offset)
    {
        int i = IndexAt(offset);
        return i < 0 ? -1 : _spans[i].Start;
    }

    public int NextStart(int offset)
    {
        int i = IndexAt(offset);
        if (i < 0) return _spans.Count > 0 ? _spans[0].Start : -1;
        return i + 1 < _spans.Count ? _spans[i + 1].Start : -1;
    }

    public int PrevStart(int offset)
    {
        int i = IndexAt(offset);
        if (i <= 0) return _spans.Count > 0 ? _spans[0].Start : -1;
        return _spans[i - 1].Start;
    }

    // ------------------------------------------------------------- 细活

    private static bool IsCloser(char c)
        => c is '"' or '\'' or '”' or '’' or '）' or ')' or '】' or '」';

    /// <summary>空行，或下一行以 Markdown 块标记开头 —— 都当硬边界。</summary>
    private static bool IsHardBoundary(string text, int j)
    {
        if (text[j] != '\n') return false;

        int k = j + 1;
        while (k < text.Length && (text[k] == ' ' || text[k] == '\t')) k++;
        if (k >= text.Length) return true;
        if (text[k] == '\n') return true;   // 空行

        char c = text[k];
        return c is '#' or '>' or '|'
               || (c is '-' or '*' or '+' && k + 1 < text.Length && text[k + 1] == ' ');
    }

    /// <summary>这一行本身是不是以块标记开头（标题/列表/引用/表格）。</summary>
    private static bool IsMarkerLine(string text, int start)
    {
        int lb = start;
        while (lb > 0 && text[lb - 1] != '\n') lb--;

        int p = lb;
        int spaces = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t') && spaces < 4) { p++; spaces++; }
        if (p >= text.Length) return false;

        char c = text[p];
        return c is '#' or '>' or '|'
               || (c is '-' or '*' or '+' && p + 1 < text.Length && text[p + 1] == ' ');
    }

    /// <summary>这个位置的点是不是缩写里的点（不该切）：Mr. / e.g. / U.S. / J.</summary>
    private static bool IsAbbreviationDot(string text, int j)
    {
        int a = j;
        while (a >= 0 && (char.IsLetter(text[a]) || text[a] == '.')) a--;
        int b = j;
        while (b < text.Length && (char.IsLetter(text[b]) || text[b] == '.')) b++;

        string token = text.Substring(a + 1, b - a - 1);   // 例："Mr." / "e.g." / "U.S."
        string key = token.TrimEnd('.').ToLowerInvariant();
        if (key.Length == 0) return false;

        if (Abbreviations.Contains(key)) return true;

        // 单个大写字母：J. / R.
        return key.Length == 1 && char.IsUpper(token[0]);
    }

    /// <summary>围栏代码块的行区间（``` 或 ~~~ 围起来的整段）。</summary>
    private static List<(int Start, int End)> CodeZones(string text)
    {
        var zones = new List<(int, int)>();
        int lineStart = 0;
        int? openStart = null;

        for (int i = 0; i <= text.Length; i++)
        {
            bool endOfLine = i == text.Length || text[i] == '\n';
            if (!endOfLine) continue;

            string line = text.Substring(lineStart, i - lineStart).TrimStart();
            bool fence = line.StartsWith("```") || line.StartsWith("~~~");

            if (fence)
            {
                if (openStart == null) openStart = lineStart;
                else
                {
                    zones.Add((openStart.Value, i));
                    openStart = null;
                }
            }

            lineStart = i + 1;
        }

        if (openStart != null) zones.Add((openStart.Value, text.Length));
        return zones;
    }

    private static bool InZone(List<(int Start, int End)> zones, int offset)
        => ZoneEnd(zones, offset) >= 0;

    private static int ZoneEnd(List<(int Start, int End)> zones, int offset)
    {
        foreach (var (s, e) in zones)
            if (offset >= s && offset < e) return e;
        return -1;
    }
}
