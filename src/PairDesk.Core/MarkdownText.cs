// ============================================================
// MarkdownText.cs —— 译稿 md 的读法 / 写法（段落与章名约定）
// ============================================================
//
// 译稿的约定（跟你现有 27 份译稿完全一致，不能改）：
//
//    第三章                      ← 第一段是章名
//    （空行）
//    正文第一段…
//    （空行）
//    正文第二段…
//
// 回写规则只有一条，但它解决了一个大问题：
//     **只写"有内容"的行，行与行之间空一行。**
//   → 你把两句并成一段，根本不用点任何按钮：第二句留空，
//     它自然就不会出现在 md 里，两段自动并成一段。
//   → 你想在段内再分段，就在框里敲一个空行，它会被原样保留。
//
// 这条规则也让"回读"变简单：md 的每一段 = 一个有内容的行。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace PairDesk.Core;

public static class MarkdownText
{
    /// <summary>
    /// 拆成 [章名, 正文段落…]。第一段视为章名（译稿约定）。
    /// 空文件返回空列表。
    /// </summary>
    public static List<string> SplitParagraphs(string textLf)
    {
        var list = new List<string>();
        string t = textLf.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (t.Length == 0) return list;

        foreach (string block in System.Text.RegularExpressions.Regex.Split(t, @"\n\s*\n"))
        {
            string p = block.Trim();
            if (p.Length > 0) list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// 把「章名 + 各行中文」拼成一份 md（内部换行一律 \n，由 TextFile 负责换成目标换行）。
    /// 只输出有内容的行。
    /// </summary>
    public static string Render(string title, IEnumerable<Segment> rows)
    {
        var sb = new StringBuilder();
        sb.Append(title.Trim()).Append('\n').Append('\n');

        bool first = true;
        foreach (var row in rows)
        {
            string zh = row.Zh.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (zh.Length == 0) continue;

            if (!first) sb.Append('\n');   // 段与段之间空一行
            sb.Append(zh).Append('\n');
            first = false;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把章名规范化：允许 "第三章"、"第3章"、"3"，统一成 "第N章"。
    /// 没有原稿时用它给新章起名。
    /// </summary>
    public static string NormalizeTitle(string? raw, int number)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string t = raw.Trim();
            if (t.Length > 0 && t.Length < 20) return t;
        }
        return "第" + ChineseNumber(number) + "章";
    }

    /// <summary>1..99 → 中文数字（1→一，10→十，21→二十一）。</summary>
    public static string ChineseNumber(int n)
    {
        string[] digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

        if (n < 0) return n.ToString();
        if (n < 10) return digits[n];
        if (n < 20) return "十" + (n % 10 == 0 ? "" : digits[n % 10]);
        if (n < 100)
        {
            string tens = digits[n / 10] + "十";
            return n % 10 == 0 ? tens : tens + digits[n % 10];
        }
        return n.ToString();
    }
}
