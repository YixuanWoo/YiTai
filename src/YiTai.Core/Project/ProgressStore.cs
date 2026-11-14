// ============================================================
// ProgressStore.cs —— "译到哪了"的进度标记
// ============================================================
//
// 存的是 <项目根>\.yitai\progress.json：
//
//   { "Version": 1, "Files": { "ch03.md": 1287 } }
//
// 值是"文档里的字符偏移"（换行统一成 \n 之后的位置），不是"第几段"。
// 记段序号在文档增删一段之后会整体错位，而这里错位只影响那一段附近。
//
// 读不懂就当没有：这个文件坏掉，绝不能连累项目打不开。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiTai.Core;

public sealed class ProgressState
{
    public int Version { get; set; } = 1;

    /// <summary>源文件名 → 字符偏移（该处即"从这里开始还没译"）。</summary>
    public Dictionary<string, int> Files { get; set; } = new();
}

public static class ProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读进度。文件不在、读不动、内容坏了，都返回空表，不抛异常。</summary>
    public static ProgressState Load(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return new ProgressState();

        string file = ProjectLayout.ProgressFile(projectRoot);
        if (!File.Exists(file)) return new ProgressState();

        try
        {
            var state = JsonSerializer.Deserialize<ProgressState>(File.ReadAllText(file), JsonOptions);
            if (state == null) return new ProgressState();
            state.Files ??= new Dictionary<string, int>();
            return state;
        }
        catch (Exception)
        {
            return new ProgressState();
        }
    }

    /// <summary>原子写：先写 .tmp 再 move，写一半断电不会留半个坏文件。</summary>
    public static void Save(string projectRoot, ProgressState state)
    {
        if (string.IsNullOrEmpty(projectRoot)) return;

        Directory.CreateDirectory(ProjectLayout.Private(projectRoot));   // 只保证 .yitai 在

        string file = ProjectLayout.ProgressFile(projectRoot);
        string tmp = file + ".tmp";

        string json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>按文件名取标记；没有就是 -1。</summary>
    public static int Get(ProgressState state, string fileName)
    {
        if (state == null || string.IsNullOrEmpty(fileName)) return -1;
        return state.Files.TryGetValue(fileName, out int v) ? v : -1;
    }

    /// <summary>设标记并立刻落盘；offset &lt; 0 表示清掉这个文件的标记。</summary>
    public static void Set(string projectRoot, ProgressState state, string fileName, int offset)
    {
        if (state == null || string.IsNullOrEmpty(fileName)) return;

        if (offset < 0) state.Files.Remove(fileName);
        else state.Files[fileName] = offset;

        Save(projectRoot, state);
    }
}
