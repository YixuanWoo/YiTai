// ============================================================
// TextStats.cs —— 字数 / 段数统计
// ============================================================
//
// 只统计"看得见的东西"：去掉所有空白后计字符。
// 中文一个字算 1，英文一个字母也算 1 —— 用它看体量，不用它做翻译计费。
// 段数按"空行分隔的块"数，跟 Markdown 的直觉一致。
// ============================================================

using System;
using System.Text.RegularExpressions;

namespace YiTai.Core;

public static class TextStats
{
    public static int CountChars(string text)
    {
        int n = 0;
        foreach (char c in text)
            if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    public static int CountParagraphs(string text)
    {
        string t = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (t.Length == 0) return 0;
        return Regex.Split(t, @"\n\s*\n").Length;
    }
}
