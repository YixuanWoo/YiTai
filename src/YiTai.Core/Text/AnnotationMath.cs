// ============================================================
// AnnotationMath.cs —— 批注区间与既有 ==高亮== 的合成（纯逻辑，零界面依赖）
// ============================================================
//
// 一段渲染出来的文字（一个 Run 的 [start,end)）可能同时被：
//   · 一条或多条批注盖住；
//   · 既有 ==高亮== 盖住（那层粉色底是用户原来标的，不能被批注覆盖）。
//
// 这里只算"该切成哪几小段、每段属于哪几条批注、这段在不在高亮里"，
// 界面层照着这个计划去包 Span。纯函数，能直接进冒烟测试。
//
// 规则：重叠处两者都要可见 —— 高亮里的那一小段只画批注的下划线（不铺底），
// 高亮外的那一小段才有浅底。高亮那层本身一个字都不改。
// ============================================================

using System;
using System.Collections.Generic;

namespace YiTai.Core;

/// <summary>一个 Run 被切出来的一小段，以及它身上挂着的批注与高亮状态。</summary>
public readonly record struct AnnotationSlice(
    int Start,
    int End,
    IReadOnlyList<string> ItemIds,
    bool Highlighted)
{
    public bool HasAnnotation => ItemIds.Count > 0;
}

public static class AnnotationMath
{
    /// <summary>
    /// 把 [start,end) 切成本次渲染的若干小段。
    ///
    /// <paramref name="annotations"/> 是"这条批注落在文档的哪一段"（已经回贴/校验过的区间），
    /// <paramref name="highlights"/> 是既有 ==高亮== 的区间（MdRender.HighlightRanges 给的）。
    /// 没有批注盖住时原样返回一整段（HasAnnotation=false），调用方什么都不用改。
    /// </summary>
    public static List<AnnotationSlice> Slice(
        int start, int end,
        IReadOnlyList<(string Id, int Start, int End)>? annotations,
        IReadOnlyList<(int Start, int End)>? highlights)
    {
        var slices = new List<AnnotationSlice>();
        if (end <= start) return slices;

        var cuts = new List<int> { start, end };

        if (annotations != null)
        {
            foreach (var ann in annotations)
            {
                if (ann.End <= ann.Start) continue;
                int a = Math.Max(start, ann.Start);
                int b = Math.Min(end, ann.End);
                if (b <= a) continue;
                cuts.Add(a);
                cuts.Add(b);
            }
        }

        if (highlights != null)
        {
            foreach (var hl in highlights)
            {
                if (hl.End <= hl.Start) continue;
                int a = Math.Max(start, hl.Start);
                int b = Math.Min(end, hl.End);
                if (b <= a) continue;
                cuts.Add(a);
                cuts.Add(b);
            }
        }

        cuts.Sort();

        // 去重后的切点 → 相邻两点的每一段都问一次"谁盖着"
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            int a = cuts[i];
            int b = cuts[i + 1];
            if (b <= a) continue;

            var ids = new List<string>();
            if (annotations != null)
            {
                foreach (var ann in annotations)
                {
                    if (ann.End <= ann.Start) continue;
                    if (ann.Start <= a && ann.End >= b && ann.Id.Length > 0 && !ids.Contains(ann.Id))
                        ids.Add(ann.Id);
                }
            }

            bool hl = false;
            if (highlights != null)
            {
                foreach (var h in highlights)
                {
                    if (h.End <= h.Start) continue;
                    if (h.Start <= a && h.End >= b) { hl = true; break; }
                }
            }

            slices.Add(new AnnotationSlice(a, b, ids, hl));
        }

        return slices;
    }

    /// <summary>这条批注落在 [start,end) 里还剩哪几段（用来判断"这一截要不要包 Span"）。</summary>
    public static List<(int Start, int End)> Clip(int start, int end, int annStart, int annEnd)
    {
        var list = new List<(int, int)>();
        int a = Math.Max(start, annStart);
        int b = Math.Min(end, annEnd);
        if (b > a) list.Add((a, b));
        return list;
    }
}
