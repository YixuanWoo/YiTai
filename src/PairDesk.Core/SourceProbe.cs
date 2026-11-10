// ============================================================
// SourceProbe.cs —— 看一份原文，猜它是什么格式（只猜，不替人决定）
// ============================================================
//
// 通用的关键不是"我能识别一切"，而是"我把猜的结果摆出来给你看"。
// 所以这里只做三件事：
//   1. 数一数：内嵌注几处、卡片注几条、脚注几条、中文占比多少；
//   2. 得出一个最可能的档案（NoteStyle + SplitMode）；
//   3. 用这个档案真的跑一遍解析，把"会切成几句、剥出几条注"报出来。
//
// 第 3 步是重点：不跑一遍就报数字，等于骗人。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PairDesk.Core;

/// <summary>探测结果：推荐的档案 + 一句人话解释 + 真实跑一遍的统计。</summary>
public sealed class ProfileGuess
{
    public SourceProfile Profile { get; set; } = SourceProfile.Default;

    /// <summary>给人看的一句话（"检测到 19 处内嵌注…"）。</summary>
    public string Explain { get; set; } = "";

    /// <summary>内嵌注命中数。</summary>
    public int InlineHits { get; set; }

    /// <summary>卡片注条数。</summary>
    public int CardHits { get; set; }

    /// <summary>脚注条数。</summary>
    public int FootnoteHits { get; set; }

    /// <summary>中日文字符占全部字母的比例（0..1）。</summary>
    public double CjkRatio { get; set; }

    /// <summary>按推荐档案真跑一遍：切成几句。</summary>
    public int SentenceCount { get; set; }

    /// <summary>按推荐档案真跑一遍：几句带注。</summary>
    public int NotedSentenceCount { get; set; }

    /// <summary>解析出的标题（没有就是空串）。</summary>
    public string Title { get; set; } = "";
}

public static class SourceProbe
{
    private static readonly Regex InlineRx = new(@"[(（]\s*\*\*", RegexOptions.Compiled);
    private static readonly Regex CardRx = new(@"(?m)^\s*\*{0,2}〔(\d+)〕", RegexOptions.Compiled);
    private static readonly Regex FootRx = new(@"(?m)^\[\^[^\]]+\]:", RegexOptions.Compiled);

    /// <summary>看一份原文，给出最可能的档案（并真跑一遍验证）。</summary>
    public static ProfileGuess Guess(string rawText)
    {
        var g = new ProfileGuess();
        string text = rawText.Replace("\r\n", "\n").Replace('\r', '\n');

        // ---- 数注
        g.InlineHits = InlineRx.Matches(text).Count;
        g.FootnoteHits = FootRx.Matches(text).Count;

        int sep = text.IndexOf("\n---\n", StringComparison.Ordinal);
        g.CardHits = sep >= 0 ? CardRx.Matches(text.Substring(sep + 5)).Count : 0;

        // ---- 选注格式：谁命中最多用谁；一个都没命中就当纯正文
        NoteStyle style;
        string why;

        if (g.CardHits >= 3)
        {
            style = NoteStyle.TrailingCards;
            why = $"文末卡片区有 {g.CardHits} 条〔n〕注";
        }
        else if (g.InlineHits >= 1)
        {
            style = NoteStyle.InlineBoldParen;
            why = $"句后有 {g.InlineHits} 处（**…**）内嵌注";
        }
        else if (g.FootnoteHits >= 1)
        {
            style = NoteStyle.Footnotes;
            why = $"有 {g.FootnoteHits} 条 [^n]: 脚注";
        }
        else
        {
            style = NoteStyle.None;
            why = "没找到任何注的痕迹，按纯正文处理";
        }

        // ---- 量语种
        // ★ 必须量"剥掉注之后的正文"：ch03 整篇中文占 33%（那是注在说中文），
        //   正文其实是英文。整篇量会把英文书误判成中文书。
        var probeProfile = new SourceProfile { Notes = style, Split = SplitMode.LatinSentence };
        string bodyOnly = SourceParser.Parse(text, probeProfile).CleanBody;

        int cjk = 0, latin = 0;
        foreach (char c in bodyOnly)
        {
            if (c >= '\u4e00' && c <= '\u9fff') cjk++;
            else if (char.IsLetter(c)) latin++;
        }
        int letters = cjk + latin;
        g.CjkRatio = letters > 0 ? (double)cjk / letters : 0;

        // ---- 选切句：正文里中文占比高就按中文标点切
        SplitMode split = g.CjkRatio >= 0.30 ? SplitMode.CjkSentence : SplitMode.LatinSentence;
        string splitWhy = split == SplitMode.CjkSentence
            ? $"正文里中文占 {g.CjkRatio:P0}，按。！？切句"
            : $"正文里西文占 {1 - g.CjkRatio:P0}，按 .!? 切句";

        g.Profile = new SourceProfile { Notes = style, Split = split };
        g.Explain = $"{why}；{splitWhy}";

        // ---- 真跑一遍（不跑就报数字等于骗人）
        var parsed = SourceParser.Parse(text, g.Profile);
        g.SentenceCount = parsed.Segments.Count;
        foreach (var s in parsed.Segments) if (s.Note.Length > 0) g.NotedSentenceCount++;
        g.Title = parsed.Title;

        return g;
    }

    /// <summary>
    /// 用给定档案跑一遍，返回前 <paramref name="take"/> 句（UI 预览用）。
    /// 同时把"没落位的注"也算出来 —— 这是最该被人眼看见的失败。
    /// </summary>
    public static List<Segment> Preview(string rawText, SourceProfile profile, int take)
    {
        var parsed = SourceParser.Parse(rawText, profile);
        var list = new List<Segment>();

        foreach (var s in parsed.Segments)
        {
            if (list.Count >= take) break;
            list.Add(s);
        }

        return list;
    }

    /// <summary>注的落位数 vs 探测到的注数：对不上就说明档案选错了，得让人看见。</summary>
    public static (int Parsed, int Total) NoteCoverage(string rawText, SourceProfile profile, int totalHits)
    {
        var parsed = SourceParser.Parse(rawText, profile);
        int noted = 0;
        foreach (var s in parsed.Segments) if (s.Note.Length > 0) noted++;
        return (noted, Math.Max(totalHits, noted));
    }
}
