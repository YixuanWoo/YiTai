// ============================================================
// SourceParser.cs —— 把「带译注的原文」变成「一行一句 + 该句的注」
// ============================================================
//
// 【为什么这里要接受一个 SourceProfile】
//   第一版把"注长什么样""句子怎么切"写死在代码里，等于只认我这本书的
//   格式。别人拿到就废。现在这两件事都进 profile：

//     注格式：句后内嵌（**〔3〕…**） / 文末卡片区〔n〕 / 脚注 [^1] / 不认注 / 自定义正则
//     切句：  拉丁句末标点 / 中日文句末标点 / 按段落 / 按行
//
//   但**不承诺"任意格式全自动"**：认错的后果是注混进正文，而那正是
//   你要复制去问 AI 的文本，混进去看不出来。所以界面上导入时必须先预览，
//   让人眼看一遍再确认。
//
// 【四个实测踩过的坑（都是真数据踩出来的，别改回去）】
//   坑 1 · 注释格式逐章迁移（早期卡片注 / 后期内嵌注）→ 只剥不解析语义。
//   坑 2 · 注里面还套着括号（"（我希望）"）→ 逐字符扫描 + 括号深度计数，
//          不能用 [^）]* 这种正则会截断。
//   坑 3 · 先剥后切，不能先切后剥（注文里的句号会被当成句界，
//          实测多切出约 18% 的假句子：1855 → 1572）。
//   坑 4 · 原文没有分段（PDF 抽取并成一整行）→ 只能以句为单位切。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PairDesk.Core;

public static class SourceParser
{
    /// <summary>注的占位标记。用私有区字符，正常正文里不可能出现。</summary>
    private const char Mark = '\u0001';

    /// <summary>拉丁句末标点 + 可选收尾引号/括号 + 空白。连续的 .!?… 当一个边界一起吃掉。</summary>
    private static readonly Regex LatinEnd = new(@"[.!?…]+[""”’')\]]*\s+", RegexOptions.Compiled);

    /// <summary>中/日文句末标点（。！？…），后面可以再跟收尾引号括号，不需要空格。</summary>
    private static readonly Regex CjkEnd = new(@"[。！？…]+[」』”’）\)】]*", RegexOptions.Compiled);

    /// <summary>只剩标点/引号的碎片。</summary>
    private static readonly Regex OnlyPunct =
        new(@"^[\s.!?…,;:\-—–。、，；：""”’'()\[\]「」『』（）]*$", RegexOptions.Compiled);

    /// <summary>标点前面多出来的空格（`seriously .` 这种）。</summary>
    private static readonly Regex SpaceBeforePunct = new(@"\s+([.,;:!?])", RegexOptions.Compiled);

    private static readonly Regex MarkerRx = new("\u0001(\\d+)\u0001", RegexOptions.Compiled);

    private static readonly Regex CardHead = new(@"^\*{0,2}\s*〔(\d+)〕", RegexOptions.Compiled);

    private static readonly Regex FootDef = new(@"^\[\^([^\]]+)\]:\s*(.*)$", RegexOptions.Compiled);

    // ---------------------------------------------------------------- 公开入口

    /// <summary>用默认档案解析（拉丁切句 + 句后内嵌括号注）。</summary>
    public static ParsedSource Parse(string rawText) => Parse(rawText, SourceProfile.Default);

    /// <summary>按给定档案解析出标题、看点、正文与行。</summary>
    public static ParsedSource Parse(string rawText, SourceProfile profile)
    {
        var src = new ParsedSource();
        string text = rawText.Replace("\r\n", "\n").Replace('\r', '\n');

        var notes = new List<string>();
        string body = text;

        // ---- 0) 结构先行：`---` 之后如果是〔n〕卡片区，不管选的是哪一档，都先切掉。
        //      （实测：不切的话，ch01/ch02 的随文译注整段会被当成正文，
        //        27 章多切出 109 句假句子。这是结构问题，不是格式偏好问题。）
        int cardCount = 0;
        int sep = text.IndexOf("\n---\n", StringComparison.Ordinal);
        if (sep >= 0)
        {
            string tail = text.Substring(sep + 5);
            var cards = ParseCards(tail);
            if (cards.Count >= 2)
            {
                notes.AddRange(cards);
                cardCount = notes.Count;
                body = text.Substring(0, sep);
            }
        }

        // ---- 1) 把注从正文里摘出来，换成占位标记
        switch (profile.Notes)
        {
            case NoteStyle.TrailingCards:
                body = AnchorToMarker(body, cardCount);
                break;

            case NoteStyle.Footnotes:
                body = TakeFootnotes(body, notes);
                break;

            case NoteStyle.InlineBoldParen:
                // 先接锚点（指到卡片区），再剥内嵌注（追加到同一个 notes 列表）
                if (cardCount > 0) body = AnchorToMarker(body, cardCount);
                body = CaptureInlineNotes(body, notes);
                break;

            case NoteStyle.None:
                body = Regex.Replace(body, @"〔\d+〕", "").Replace("*", "");
                break;
        }

        // 自定义正则：在预设之外再剥一层（第一个捕获组 = 注文）
        if (profile.CustomNotePattern.Length > 0)
            body = CaptureByPattern(body, profile.CustomNotePattern, notes);

        // ---- 2) 前置块（标题 / 引述 / 看点）只识别，不进正文
        body = StripPreamble(body, src, profile);

        // ---- 3) 收拾空白
        body = SpaceBeforePunct.Replace(body, "$1");
        body = Regex.Replace(body, @"[ \t]+", " ");
        src.CleanBody = body.Trim();

        // ---- 4) 切句
        SplitInto(src, src.CleanBody, notes, profile);

        return src;
    }

    /// <summary>按空行切块，收「〔n〕起头」的卡片；编号可能跳号，按最大编号铺平。</summary>
    private static List<string> ParseCards(string tail)
    {
        var byNumber = new Dictionary<int, string>();
        var notes = new List<string>();

        foreach (string block in Regex.Split(tail, @"\n[ \t]*\n"))
        {
            string t = block.Trim();
            var m = CardHead.Match(t);
            if (!m.Success) continue;   // 「## 随文译注」这种小标题跳过

            int num = int.Parse(m.Groups[1].Value);
            string content = t.Substring(m.Length).Trim().Trim('*').Trim();
            if (content.Length == 0) continue;
            byNumber[num] = content;
        }

        int max = 0;
        foreach (var kv in byNumber) if (kv.Key > max) max = kv.Key;
        for (int i = 1; i <= max; i++)
            notes.Add(byNumber.TryGetValue(i, out string? v) ? v : "");

        return notes;
    }

    /// <summary>把正文里的 〔n〕 锚点换成占位标记（指到 notes 的下标）。</summary>
    private static string AnchorToMarker(string body, int noteCount)
    {
        return Regex.Replace(body, @"〔(\d+)〕", m =>
        {
            int n = int.Parse(m.Groups[1].Value);
            return n >= 0 && n < noteCount ? " " + Mark + n + Mark + " " : "";
        });
    }

    /// <summary>脚注：定义行 [^id]: …，正文里 [^id] 是引用。</summary>
    private static string TakeFootnotes(string text, List<string> notes)
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new StringBuilder(text.Length);

        foreach (string raw in text.Split('\n'))
        {
            var m = FootDef.Match(raw.Trim());
            if (m.Success)
            {
                byId[m.Groups[1].Value] = notes.Count;
                notes.Add(m.Groups[2].Value.Trim());
                continue;   // 定义行本身不进正文
            }
            kept.Append(raw).Append('\n');
        }

        string body = kept.ToString();
        return Regex.Replace(body, @"\[\^([^\]]+)\]", m =>
        {
            if (!byId.TryGetValue(m.Groups[1].Value, out int idx)) return "";
            return " " + Mark + idx + Mark + " ";
        });
    }

    /// <summary>
    /// 剥掉 （**〔n〕…**） 这类行内注，把注文收进 <paramref name="notes"/>，
    /// 并在原位置留一个 \u0001i\u0001 占位标记，等切完句再把注接回对应句。
    /// 正文里真正的括号（如 "(I shall not draw my airplane…)"）不含 **，原样保留。
    /// </summary>
    private static string CaptureInlineNotes(string s, List<string> notes)
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
        t = Regex.Replace(t, @"〔\d+〕", "");
        t = Regex.Replace(t, @"\[\d+\]", "");
        return t;
    }

    /// <summary>自定义正则剥注：第一个非空捕获组是注文。</summary>
    private static string CaptureByPattern(string body, string pattern, List<string> notes)
    {
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline); }
        catch (ArgumentException) { return body; }   // 正则写错了就当没填，不让程序崩

        return rx.Replace(body, m =>
        {
            string text = "";
            foreach (Group g in m.Groups)
            {
                if (g.Success && g.Index != m.Index) { text = g.Value.Trim(); break; }
                if (g.Success && m.Groups.Count == 1) { text = g.Value.Trim(); break; }
            }
            notes.Add(text);
            return " " + Mark + (notes.Count - 1) + Mark + " ";
        });
    }

    /// <summary>从 i 开始最多看 6 个字符，判断这是不是一个 （**…**） 注。</summary>
    private static bool HasBoldMarkerAhead(string s, int i)
    {
        int limit = Math.Min(s.Length - 1, i + 6);
        for (int k = i + 1; k <= limit; k++)
        {
            if (s[k] == '*' && k + 1 <= limit && s[k + 1] == '*') return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 前置块

    /// <summary>标题（#）、引述（&gt;）、本章看点：只识别，不进正文。</summary>
    private static string StripPreamble(string text, ParsedSource src, SourceProfile profile)
    {
        var kept = new List<string>();
        bool bodyStarted = false;

        foreach (string raw in text.Split('\n'))
        {
            if (bodyStarted) { kept.Add(raw); continue; }

            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (profile.TitlePrefix.Length > 0
                && line.StartsWith(profile.TitlePrefix, StringComparison.Ordinal))
            {
                if (src.Title.Length == 0)
                {
                    src.Title = line.Substring(profile.TitlePrefix.Length).Trim();
                    // 形如「# 第 3 章 · 英文原文（带译注锚点）」：括号里的说明不要
                    int cut = src.Title.IndexOfAny(new[] { '（', '(' });
                    if (cut > 0) src.Title = src.Title.Substring(0, cut).Trim();
                }
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                if (profile.ChineseHeuristics && line.Contains("本章看点", StringComparison.Ordinal))
                    ParseTips(line, src.Tips);
                continue;
            }

            if (profile.ChineseHeuristics && line.Contains("本章看点", StringComparison.Ordinal))
            {
                ParseTips(line, src.Tips);
                continue;
            }

            bodyStarted = true;
            kept.Add(line);
        }

        return string.Join("\n", kept);
    }

    // ---------------------------------------------------------------- 切句

    private static void SplitInto(ParsedSource src, string body, List<string> notes, SourceProfile profile)
    {
        if (body.Length == 0) return;

        var parts = new List<string>();

        switch (profile.Split)
        {
            case SplitMode.Paragraph:
                foreach (string block in Regex.Split(body, @"\n[ \t]*\n"))
                {
                    string t = Regex.Replace(block.Trim(), @"[ \t]*\n[ \t]*", " ");
                    if (t.Length > 0) parts.Add(t);
                }
                break;

            case SplitMode.Line:
                foreach (string line in body.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length > 0) parts.Add(t);
                }
                break;

            default:
                parts = CutBySentenceEnd(body, profile.Split == SplitMode.CjkSentence ? CjkEnd : LatinEnd);
                break;
        }

        bool mergeFragments = profile.Split == SplitMode.LatinSentence || profile.Split == SplitMode.CjkSentence;

        var segs = new List<Segment>();
        int fragCount = 0;

        foreach (string rawPart in parts)
        {
            var noteIdx = new List<int>();
            string text = TakeMarkers(rawPart, noteIdx);

            if (mergeFragments && segs.Count > 0 && IsFragment(text))
            {
                var last = segs[segs.Count - 1];
                last.En = (last.En + " " + text).Trim();
                AppendNote(last, notes, noteIdx);
                fragCount++;
                continue;
            }

            // 注通常写在这句的句尾，切句后标记会落到下一句开头 → 接给上一句
            var seg = new Segment { En = text };
            if (segs.Count > 0 && rawPart.TrimStart().StartsWith(((char)Mark).ToString()))
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

    private static List<string> CutBySentenceEnd(string body, Regex rx)
    {
        var parts = new List<string>();
        int start = 0;

        foreach (Match m in rx.Matches(body))
        {
            if (m.Index + m.Length <= start) continue;
            string seg = body.Substring(start, m.Index + m.Length - start).Trim();
            if (seg.Length > 0) parts.Add(seg);
            start = m.Index + m.Length;
        }

        if (start < body.Length)
        {
            string tail = body.Substring(start).Trim();
            if (tail.Length > 0) parts.Add(tail);
        }

        return parts;
    }

    /// <summary>从一个切片里摘掉占位标记，返回干净文本和它带走的注序号。</summary>
    private static string TakeMarkers(string s, List<int> idx)
    {
        string clean = MarkerRx.Replace(s, m =>
        {
            idx.Add(int.Parse(m.Groups[1].Value));
            return "";
        });

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
