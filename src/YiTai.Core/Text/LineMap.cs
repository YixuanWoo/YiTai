// ============================================================
// LineMap.cs —— 译文行号的口径（编辑页与预览页共用一套）
// ============================================================
//
// 行号只有一件事要说清楚：**按归一化原文（\r\n → \n）的逻辑行编号，从 1 起**。
//   · 编辑页是 TextBox，一个 \n 就是一个逻辑行 → 第 i 个行首的号码就是 i+1；
//   · 预览页是 FlowDocument，每个逻辑行成一段，段上记着它在原文里的偏移
//     （MdBlock.SourceOffset），反查"这个偏移前面有几个 \n"就是同一个号码。
//
// 软换行（栏拉窄后一个字被折到下一视觉行）不另编号 —— 那是同一行的延续，
// 和 VS Code 的行号是一个道理。所以这里只认 \n，不认像素。
//
// 为什么放在 Core：两套实现（编辑页的 TextBox 与预览页的 FlowDocument）
// 必须用同一个算法，否则"预览页第 12 行"和"编辑页第 12 行"迟早会分家；
// 放进 Core 还能被冒烟测试直接钉住，不用起界面。
// ============================================================

using System;
using System.Collections.Generic;

namespace YiTai.Core;

public static class LineMap
{
    /// <summary>口径的唯一入口：\r\n 和裸 \r 都当 \n。批注、进度也走这一套。</summary>
    public static string Normalize(string text)
    {
        if (text.Length == 0) return text;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>逻辑行数（末尾换行不算多出一行以外的空行语义，按 split 口径：空串也算 1 行）。</summary>
    public static int CountLines(string normalized)
    {
        if (normalized.Length == 0) return 1;

        int n = 1;
        for (int i = 0; i < normalized.Length; i++)
            if (normalized[i] == '\n') n++;
        return n;
    }

    /// <summary>
    /// 每个逻辑行的行首偏移（第 0 项恒为 0）。
    /// 稿子以 \n 收尾时，后面那个空行也算一行（TextBox 里它就是看得见的一行空位），
    /// 它的行首偏移就是文本长度 —— 与 <see cref="CountLines"/> 口径一致。
    /// </summary>
    public static List<int> LineStarts(string normalized)
    {
        var starts = new List<int>(CountLines(normalized)) { 0 };

        for (int i = 0; i < normalized.Length; i++)
            if (normalized[i] == '\n')
                starts.Add(i + 1);

        return starts;
    }

    /// <summary>
    /// 归一化原文里的字符偏移落在第几行（1 起）。
    /// 段首偏移正好在行首时给该行；落在行中间也给该行。
    /// </summary>
    public static int LineOfOffset(string normalized, int offset)
    {
        if (offset <= 0) return 1;
        if (offset > normalized.Length) offset = normalized.Length;

        int n = 1;
        for (int i = 0; i < offset; i++)
            if (normalized[i] == '\n') n++;
        return n;
    }
}
