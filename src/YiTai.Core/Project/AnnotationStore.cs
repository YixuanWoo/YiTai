// ============================================================
// AnnotationStore.cs —— 译文批注（审阅时写在稿子上的意见）
// ============================================================
//
// 存的是 <项目根>\.yitai\annotations.json：
//
//   { "Version": 1,
//     "Items": [
//       { "Id": "…", "FileKey": "rel:translations\\ch01.md",
//         "Start": 12, "End": 20, "Quote": "他站在门口",
//         "Body": "这句语气太硬", "Created": "…", "Modified": "…" } ] }
//
// ★ 红线：**只新增这一个文件**，既有的 project.json / progress.json /
//   panes.json / worktime.json 结构一个字段都不改，译文正文一个字都不碰。
//   旧项目没有 annotations.json 就当作"没有批注"，天然兼容。
//
// 锚点为什么是"归一化字符偏移"：
//   字号、切栏、滚动只改渲染，不改文档字符序列；进度线早就用同一套坐标。
//   行号/段号/像素坐标一改字号或一换行就会整体错位，所以这里不用。
//
// 锚点为什么还要带 quote：
//   译文正文被改过之后，同一个偏移可能已经不是原来那几个字了。
//   偏移处的文字与 quote 对不上，就在附近一个小窗口里按 quote 找一次；
//   找到就回贴，找不到就**保留批注、标记为未定位**，绝不硬贴到别的文字上。
//
// 读不懂就当空表：这个文件坏掉，绝不能连累项目打不开（跟 progress/worktime 一个脾气）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiTai.Core;

/// <summary>一条批注。字段名直接进 JSON，改名字等于改 schema，别乱动。</summary>
public sealed class AnnotationItem
{
    /// <summary>稳定标识（新建时生成 GUID）。改正文、删批注都靠它认人。</summary>
    public string Id { get; set; } = "";

    /// <summary>锚在哪份文件上：ProgressStore.KeyFor 生成的 rel:/abs: 键。</summary>
    public string FileKey { get; set; } = "";

    /// <summary>起始字符偏移（换行统一成 \n 之后的位置，0 起，[Start,End) 半开区间）。</summary>
    public int Start { get; set; }

    /// <summary>结束字符偏移（不含）。</summary>
    public int End { get; set; }

    /// <summary>被批注的原文片段（校验/轻微漂移回贴用）。</summary>
    public string Quote { get; set; } = "";

    /// <summary>批注正文。</summary>
    public string Body { get; set; } = "";

    /// <summary>创建时间（ISO 8601，本地时间带偏移）。</summary>
    public string Created { get; set; } = "";

    /// <summary>最后修改时间。</summary>
    public string Modified { get; set; } = "";
}

public sealed class AnnotationState
{
    public int Version { get; set; } = 1;

    public List<AnnotationItem> Items { get; set; } = new();
}

public static class AnnotationStore
{
    /// <summary>回贴时在偏移附近找 quote 的窗口（字符）。小一点更不容易贴错地方。</summary>
    public const int DriftWindow = 600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>换行统一成 \n —— 批注偏移与 progress.json 是同一套坐标系。</summary>
    public static string Normalize(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>读批注。文件不在、读不动、内容坏了，都返回空表，不抛异常。</summary>
    public static AnnotationState Load(string projectRoot)
    {
        var empty = new AnnotationState();
        if (string.IsNullOrEmpty(projectRoot)) return empty;

        string file = ProjectLayout.AnnotationsFile(projectRoot);
        if (!File.Exists(file)) return empty;

        try
        {
            var state = JsonSerializer.Deserialize<AnnotationState>(File.ReadAllText(file), JsonOptions);
            if (state == null) return empty;

            state.Items ??= new List<AnnotationItem>();
            return state;
        }
        catch (Exception)
        {
            return empty;   // 读不懂就当没有批注，不连累打开项目
        }
    }

    /// <summary>原子写：先写 .tmp 再 move，写一半断电不会留半个坏文件。只碰 annotations.json。</summary>
    public static void Save(string projectRoot, AnnotationState state)
    {
        if (string.IsNullOrEmpty(projectRoot) || state == null) return;

        Directory.CreateDirectory(ProjectLayout.Private(projectRoot));

        string file = ProjectLayout.AnnotationsFile(projectRoot);
        string tmp = file + ".tmp";

        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>这一份文件上的全部批注（按锚点先后排，面板里读起来才顺）。</summary>
    public static List<AnnotationItem> ItemsFor(AnnotationState state, string fileKey)
    {
        var list = new List<AnnotationItem>();
        if (state == null || string.IsNullOrEmpty(fileKey)) return list;

        foreach (var item in state.Items)
            if (item != null && string.Equals(item.FileKey, fileKey, StringComparison.OrdinalIgnoreCase))
                list.Add(item);

        list.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <summary>按 Id 找一条（找不到给 null）。</summary>
    public static AnnotationItem? Find(AnnotationState state, string id)
    {
        if (state == null || string.IsNullOrEmpty(id)) return null;
        foreach (var item in state.Items)
            if (item != null && item.Id == id) return item;
        return null;
    }

    /// <summary>加一条。Id 为空就现生成一个；返回这条本身。</summary>
    public static AnnotationItem Add(AnnotationState state, AnnotationItem item)
    {
        if (state == null || item == null) return item!;

        if (item.Id.Length == 0) item.Id = NewId();
        state.Items.Add(item);
        return item;
    }

    /// <summary>删一条（只动 annotations.json，不碰译文正文）。返回是否真的删掉了。</summary>
    public static bool Remove(AnnotationState state, string id)
    {
        if (state == null) return false;

        for (int i = 0; i < state.Items.Count; i++)
        {
            if (state.Items[i] != null && state.Items[i].Id == id)
            {
                state.Items.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>新建一条批注（时间戳由这里统一写，界面不用自己拼）。</summary>
    public static AnnotationItem Create(string fileKey, int start, int end, string quote, string body,
                                        DateTime now)
    {
        string stamp = now.ToString("o");
        return new AnnotationItem
        {
            Id = NewId(),
            FileKey = fileKey ?? "",
            Start = start,
            End = end,
            Quote = quote ?? "",
            Body = body ?? "",
            Created = stamp,
            Modified = stamp,
        };
    }

    /// <summary>
    /// 把一条批注贴回 <paramref name="text"/>（必须是 Normalize 过的文本）。
    ///
    /// 返回 (start, end)：
    ///   · 偏移处就是 quote            → 原样返回；
    ///   · 偏移处对不上、附近窗口里有 quote → 返回新位置（回贴）；
    ///   · 找不到                        → (-1, -1)，调用方把它当"未定位"，绝不硬贴。
    /// </summary>
    public static (int Start, int End) Anchor(AnnotationItem item, string text, int window = DriftWindow)
    {
        if (item == null) return (-1, -1);

        string t = text ?? "";
        int len = item.Quote.Length;

        if (item.Start >= 0 && item.End > item.Start && item.End <= t.Length
            && string.Equals(t.Substring(item.Start, item.End - item.Start), item.Quote, StringComparison.Ordinal))
        {
            return (item.Start, item.End);
        }

        if (len == 0 || t.Length < len) return (-1, -1);

        // 附近小窗口里按 quote 找一次；有好几处就取离原偏移最近的那一处。
        int from = Math.Max(0, item.Start - Math.Max(0, window));
        int to = Math.Min(t.Length, Math.Max(0, item.Start) + Math.Max(0, window) + len);
        if (to <= from) return (-1, -1);

        int best = -1;
        int at = t.IndexOf(item.Quote, from, StringComparison.Ordinal);
        while (at >= 0 && at <= to - len)
        {
            if (best < 0 || Math.Abs(at - item.Start) < Math.Abs(best - item.Start)) best = at;
            int next = t.IndexOf(item.Quote, at + 1, StringComparison.Ordinal);
            if (next < 0 || next > to - len) break;
            at = next;
        }

        return best < 0 ? (-1, -1) : (best, best + len);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
