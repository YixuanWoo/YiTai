// ============================================================
// SourceParser.cs —— 把「带译注的原文」变成「一行一句 + 该句的注」
// ============================================================
//
// 这一层是全项目唯一的真难点，三个坑都是实测踩过的：
//
// 【坑 1 · 注释格式逐章迁移】
//   早期章节：表头 + --- + 正文 + 文末 〔n〕 卡片。
//   后期章节：注直接嵌在句后（**〔3〕`chose` 是「东西」…**）。
//   → 对策：不解析注的语义，只"剥出来单独存"。两种格式都当
//     "正文 + 噪声"处理，格式再变也不会崩。
//
// 【坑 2 · 注里面还套着括号】
//   注文里常出现「（我希望）」这种中文括号。用正则 [^）]* 会在第一个
//   右括号处截断，剥不干净还把残渣留进正文。
//   → 对策：逐字符扫描 + 括号深度计数。
//
// 【坑 3 · 先剥后切，不能先切后剥】
//   注文里也有句号。如果先切句再剥注，注文里的句号会被当成句界，
//   切出一堆 "." / "." 的碎片（实测多出约 18% 的假句子）。
//   → 对策：先把注换成占位标记，再切句，切完把标记里的注接回该句。
//
// 【坑 4 · 原文没有分段】
//   PDF 抽取把段落并成了一整行，文件里没有空行。
//   → 对策：以句为单位切（这本书本来就碎），切完再修碎片。
// ============================================================

using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PairDesk.Core;

public static class SourceParser
{
    /// <summary>注的占位标记。用私有区字符，正常正文里不可能出现。</summary>
    private const char Mark = '\u0001';

    /// <summary>句末标点 + 可选收尾引号/括号 + 空白。连续的 .!?… 当一个边界一起吃掉。</summary>
    private static readonly Regex SentEnd = new(@"[.!?…]+[""”’')\]]*\s+", RegexOptions.Compiled);

    /// <summary>只剩标点/引号的碎片。</summary>
    private static readonly Regex OnlyPunct = new(@"^[\s.!?…,;:\-—–""”’'()\[\]]*$", RegexOptions.Compiled);

    /// <summary>标点前面多出来的空格（`seriously .` 这种）。</summary>
    private static readonly Regex SpaceBeforePunct = new(@"\s+([.,;:!?])", RegexOptions.Compiled);

    private static readonly Regex MarkerRx = new("\u0001(\\d+)\u0001");

    // ---------------------------------------------------------------- 公开入口

    /// <summary>从整份文件文本解析出标题、看点、正文与行。</summary>
    public static ParsedSource Parse(string rawText)
    {
        var src = new ParsedSource();
        string text = rawText.Replace("\r\n", "\n").Replace('\r', '\n');

        // 旧格式：正文与文末译注卡片之间有一条 --- 分隔线。切掉后面那半。
        string head = text;
        int sep = text.IndexOf("\n---\n", System.StringComparison.Ordinal);
        if (sep >= 0) head = text.Substring(0, sep);

        var bodyLines = new List<string>();
        bool bodyStarted = false;

        foreach (string raw in head.Split('\n'))
        {
            string line = raw.Trim();

            if (!bodyStarted)
            {
                // 正文之前：标题、引述块（>）、看点行 —— 只识别，不进正文
                if (line.Length == 0) continue;

                if (line.StartsWith("# ", System.StringComparison.Ordinal))
                {
                    if (src.Title.Length == 0) src.Title = line.Substring(2).Trim();
                    continue;
                }
                if (line.StartsWith("#", System.StringComparison.Ordinal)) continue;
                if (line.StartsWith(">", System.StringComparison.Ordinal)) continue;

                if (line.Contains("本章看点", System.StringComparison.Ordinal))
                {
                    ParseTips(line, src.Tips);
                    continue;
                }

                bodyStarted = true;   // 第一行既不是标题也不是引述 —— 正文从这里开始
                bodyLines.Add(line);
                continue;
            }

            bodyLines.Add(line);      // 正文期间空行要留下（将来段落变规整时有用）
        }

        var notesBox = new List<string>();
        string body = CaptureNotes(string.Join("\n", bodyLines), notesBox);
        body = SpaceBeforePunct.Replace(body, "$1");
        body = Regex.Replace(body, @"[ \t]+", " ");
        src.CleanBody = body.Trim();

        SplitInto(src, src.CleanBody, notesBox);
        return src;
    }

    // ---------------------------------------------------------------- 剥注（带捕获）

    /// <summary>
    /// 剥掉 （**〔n〕…**） 这类行内注，把注文收进 <paramref name="notes"/>，
    /// 并在原位置留一个 \u0001i\u0001 占位标记，等切完句再把注接回对应句。
    /// 正文里真正的括号（如 "(I shall not draw my airplane…)"）不含 **，原样保留。
    /// </summary>
    private static string CaptureNotes(string s, List<string> notes)
    {
        var sb = new StringBuilder(s.Length + 16);
        int i = 0, n = s.Length;

        while (i < n)
        {
            char c = s[i];

            if ((c == '（' || c == '(') && HasBoldMarkerAhead(s, i))
            {
                int depth = 0, j = i;
                bool closed = false;

                while (j < n)
                {
                    char cj = s[j];
                    if (cj == '（' || cj == '(') depth++;
                    else if (cj == '）' || cj == ')')
                    {
                        depth--;
                        if (depth == 0) { j++; closed = true; break; }
                    }
                    j++;
                }

                if (closed)
                {
                    // 注文 = 括号内的内容，去掉两侧的 ** 和空白
                    string inner = s.Substring(i + 1, j - i - 2).Trim().Trim('*').Trim();
                    notes.Add(inner);
                    sb.Append(' ').Append(Mark).Append(notes.Count - 1).Append(Mark).Append(' ');
                    i = j;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        string t = sb.ToString();
        t = Regex.Replace(t, @"〔\d+〕", "");   // 裸露的锚点，不是注
        t = Regex.Replace(t, @"\[\d+\]", "");
        return t;
    }

    /// <summary>从 i 开始最多看 6 个字符，判断这是不是一个 （**…**） 注。</summary>
    private static bool HasBoldMarkerAhead(string s, int i)
    {
        int limit = System.Math.Min(s.Length - 1, i + 6);
        for (int k = i + 1; k <= limit; k++)
        {
            if (s[k] == '*' && k + 1 <= limit && s[k + 1] == '*') return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 切句

    private static void SplitInto(ParsedSource src, string body, List<string> notes)
    {
        if (body.Length == 0) return;

        var parts = new List<string>();
        int start = 0;

        foreach (Match m in SentEnd.Matches(body))
        {
            string seg = body.Substring(start, m.Index + m.Length - start).Trim();
            if (seg.Length > 0) parts.Add(seg);
            start = m.Index + m.Length;
        }

        if (start < body.Length)
        {
            string tail = body.Substring(start).Trim();
            if (tail.Length > 0) parts.Add(tail);
        }

        // 修碎片：只剩标点、或极短且不含字母汉字 → 并回上一句。
        // `It flies.` / `"What!"` 这种短但完整的对白要保留，它们是正文。
        var segs = new List<Segment>();
        int fragCount = 0;

        foreach (string rawPart in parts)
        {
            var noteIdx = new List<int>();
            string text = TakeMarkers(rawPart, noteIdx);

            if (segs.Count > 0 && IsFragment(text))
            {
                var last = segs[segs.Count - 1];
                last.En = (last.En + " " + text).Trim();
                AppendNote(last, notes, noteIdx);
                fragCount++;
                continue;
            }

            // 注通常写在**它所属那句**的句尾，切句后标记会落到下一句的开头。
            // 所以生成新行时：标记在行首就接给上一行。
            var seg = new Segment { En = text };
            if (segs.Count > 0 && rawPart.TrimStart().StartsWith(Mark))
                AppendNote(segs[segs.Count - 1], notes, noteIdx);
            else
                AppendNote(seg, notes, noteIdx);

            segs.Add(seg);
        }

        src.MergedFragments = fragCount;

        foreach (var seg in segs)
        {
            if (seg.En.Length == 0 && seg.Note.Length == 0) continue;
            src.Segments.Add(seg);
        }
    }

    /// <summary>从一个切片里摘掉占位标记，返回干净文本和它带走的注序号。</summary>
    private static string TakeMarkers(string s, List<int> idx)
    {
        string clean = MarkerRx.Replace(s, m =>
        {
            idx.Add(int.Parse(m.Groups[1].Value));
            return "";
        });

        // 标记被摘走后会留下"标点前一个空格"（`from .`）。复制给 AI 时会带着，
        // 所以这里统一收一次：空格紧贴标点的，把空格去掉。
        clean = Regex.Replace(clean, @"[ \t]+", " ");
        clean = SpaceBeforePunct.Replace(clean, "$1");
        clean = Regex.Replace(clean, @"([""”’])\s+([.,;:!?])", "$1$2");
        return clean.Trim();
    }

    private static void AppendNote(Segment seg, List<string> notes, List<int> idx)
    {
        foreach (int i in idx)
        {
            if (i < 0 || i >= notes.Count) continue;
            string n = notes[i].Trim();
            if (n.Length == 0) continue;
            seg.Note = seg.Note.Length == 0 ? n : seg.Note + "\n\n" + n;
        }
    }

    private static bool IsFragment(string s)
    {
        if (OnlyPunct.IsMatch(s)) return true;
        if (s.Length >= 3) return false;

        foreach (char c in s)
        {
            if (char.IsLetter(c)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 看点

    private static void ParseTips(string line, List<string> tips)
    {
        string q = line.TrimStart('>', ' ').Trim();
        q = q.Replace("**本章看点**：", "").Replace("**本章看点**", "").Replace("本章看点：", "").Trim();

        foreach (string piece in Regex.Split(q, "｜"))
        {
            string t = piece.Trim();
            if (t.Length > 0) tips.Add(t);
        }
    }
}
