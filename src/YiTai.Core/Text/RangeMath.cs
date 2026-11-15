using System;
using System.Collections.Generic;

namespace YiTai.Core;

/// <summary>
/// 区间运算：把一段区间"减去"或"交上"一组遮盖区间。
///
/// 只为一件事服务 —— 进度高亮的底带要给 <c>==高亮==</c> 让路：
/// 一句里可能只有几个字被 `==...==` 标过，不能让整句都退化成下划线，
/// 得把这一句切成"没标过的部分"和"标过的部分"，各画各的。
///
/// 全部是 [Start, End) 左闭右开，和 SentenceIndex、progress.json 同一套口径。
/// 纯整数运算，能单测。
/// </summary>
public static class RangeMath
{
    /// <summary>[start,end) 与 covers 的交集（按起点排序、已合并相邻与重叠）。</summary>
    public static List<(int Start, int End)> Intersect(
        int start, int end, IReadOnlyList<(int Start, int End)>? covers)
    {
        var result = new List<(int Start, int End)>();
        if (end <= start) return result;

        foreach (var (cs, ce) in Normalize(start, end, covers))
        {
            int s = Math.Max(start, cs);
            int e = Math.Min(end, ce);
            if (e > s) result.Add((s, e));
        }

        return result;
    }

    /// <summary>[start,end) 里去掉 covers 之后剩下的几段（可能为空）。</summary>
    public static List<(int Start, int End)> Subtract(
        int start, int end, IReadOnlyList<(int Start, int End)>? covers)
    {
        var result = new List<(int Start, int End)>();
        if (end <= start) return result;

        int cursor = start;

        foreach (var (cs, ce) in Normalize(start, end, covers))
        {
            if (cs > cursor)
            {
                int gapEnd = Math.Min(cs, end);
                if (gapEnd > cursor) result.Add((cursor, gapEnd));
            }

            if (ce > cursor) cursor = ce;
            if (cursor >= end) break;
        }

        if (cursor < end) result.Add((cursor, end));
        return result;
    }

    /// <summary>
    /// 把遮盖区间裁到 [start,end) 内、丢掉空的、按起点排序、合并重叠与相邻的。
    /// 调用方传进来的东西可能很脏（跨段、重叠、倒序），先在这里理顺一次。
    /// </summary>
    private static List<(int Start, int End)> Normalize(
        int start, int end, IReadOnlyList<(int Start, int End)>? covers)
    {
        var list = new List<(int Start, int End)>();
        if (covers == null) return list;

        foreach (var (s, e) in covers)
        {
            int cs = Math.Max(start, s);
            int ce = Math.Min(end, e);
            if (ce > cs) list.Add((cs, ce));
        }

        if (list.Count == 0) return list;

        list.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(int Start, int End)>(list.Count) { list[0] };

        for (int i = 1; i < list.Count; i++)
        {
            var (s, e) = list[i];
            var last = merged[^1];

            // 相邻（last.End == s）也合并：中间没有空隙，没必要画两段
            if (s <= last.End) merged[^1] = (last.Start, Math.Max(last.End, e));
            else merged.Add((s, e));
        }

        return merged;
    }
}
