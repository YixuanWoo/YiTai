// ============================================================
// ProgressStore.cs —— "译到哪了"的进度标记
// ============================================================
//
// 存的是 <项目根>\.yitai\progress.json：
//
//   { "Version": 2, "Files": { "rel:01-原文\按章\ch03.md": 1287 } }
//
// 值是"文档里的字符偏移"（换行统一成 \n 之后的位置），不是"第几段"。
// 记段序号在文档增删一段之后会整体错位，而这里错位只影响那一段附近。
//
// ★ 键必须是"能唯一认出这份文件"的东西，不是光一个文件名（Version 1 的旧做法）：
//   资料可以从任何目录打开，`英文-带标注\ch03.md` 与 `英文-纯文本\ch03.md` 要是
//   共用 "ch03.md" 这个键，在一份上标的线会跑到另一份上，互相覆盖。
//   所以现在的键带前缀：
//     rel:<相对项目根的路径>   项目文件夹整个搬走，键还成立
//     abs:<绝对路径>           项目外的文件（别的盘、别人的目录），只能记绝对
//   旧文件里的裸文件名（"ch03.md"）由 Migrate 尽力搬一次：只有"唯一对上"才搬，
//   同名对上好几份就原样留着不动 —— 宁可那一栏的线要重标，也不能让它跳到别人的文件上。
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
    public int Version { get; set; } = 2;

    /// <summary>进度键（rel:… / abs:…，旧文件里可能是裸文件名）→ 字符偏移。</summary>
    public Dictionary<string, int> Files { get; set; } = new();
}

public static class ProgressStore
{
    private const string RelPrefix = "rel:";
    private const string AbsPrefix = "abs:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 一份文件的进度键：项目内的记相对路径（整个文件夹搬走也不断），项目外的记绝对路径。
    /// 同一个路径永远得到同一个键，不同路径（哪怕文件名一样）永远得到不同的键。
    /// </summary>
    public static string KeyFor(string projectRoot, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return "";

        string full = Path.GetFullPath(absolutePath);
        string root = string.IsNullOrWhiteSpace(projectRoot) ? "" : Path.GetFullPath(projectRoot);

        if (root.Length > 0)
        {
            string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return RelPrefix + ProjectLayout.ToRelative(root, full);
        }

        return AbsPrefix + full;
    }

    /// <summary>键 → 绝对路径；裸文件名的旧键认不出位置，返回空串。</summary>
    public static string ToAbsolutePath(string projectRoot, string key)
    {
        if (key.Length == 0) return "";

        if (key.StartsWith(RelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rel = key.Substring(RelPrefix.Length);
            return string.IsNullOrEmpty(projectRoot) ? "" : Path.Combine(projectRoot, rel);
        }

        if (key.StartsWith(AbsPrefix, StringComparison.OrdinalIgnoreCase))
            return key.Substring(AbsPrefix.Length);

        return "";   // 旧格式（裸文件名）：不知道是哪一份
    }

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

    /// <summary>按键取标记；没有就是 -1。</summary>
    public static int Get(ProgressState state, string key)
    {
        if (state == null || string.IsNullOrEmpty(key)) return -1;
        return state.Files.TryGetValue(key, out int v) ? v : -1;
    }

    /// <summary>设标记并立刻落盘；offset &lt; 0 表示清掉这份文件的标记。</summary>
    public static void Set(string projectRoot, ProgressState state, string key, int offset)
    {
        if (state == null || string.IsNullOrEmpty(key)) return;

        if (offset < 0) state.Files.Remove(key);
        else state.Files[key] = offset;

        Save(projectRoot, state);
    }

    /// <summary>
    /// 把旧格式（裸文件名）的键搬到新键上，尽力而为、绝不猜。
    ///
    /// 只在 <paramref name="openPaths"/>（当前各栏记着的那几份 —— 也就是"我上次真正在看的那份"）
    /// 里找同名文件：
    ///   · 正好一份 → 搬到它的新键，删掉旧键；
    ///   · 一份都没有，或同名有好几份 → **原样留着**（之后打开那份文件时由 AdoptLegacy 现场认领）。
    ///
    /// 为什么不拿 sources\ 里的归档文件当候选：归档区常常和原文目录各存一份同名的，
    /// 猜错了就是把线焊到一份你不会打开的文件上；宁可留着等它被真正打开。
    ///
    /// 真要搬家时先留一份 progress.json.bak，再写新文件。
    /// 返回 (搬了几条, 拿不准留下的几条)。
    /// </summary>
    public static (int Migrated, int Held) Migrate(string projectRoot, ProgressState state,
                                                   IEnumerable<string> openPaths)
    {
        if (state == null || string.IsNullOrEmpty(projectRoot)) return (0, 0);

        var open = GroupByName(openPaths);

        var legacy = new List<string>();
        foreach (string key in state.Files.Keys)
            if (!key.StartsWith(RelPrefix, StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith(AbsPrefix, StringComparison.OrdinalIgnoreCase))
                legacy.Add(key);

        if (legacy.Count == 0) return (0, 0);

        int migrated = 0, held = 0;
        foreach (string key in legacy)
        {
            var candidates = open.TryGetValue(key, out var a) ? a : null;

            if (candidates == null || candidates.Count != 1) { held++; continue; }

            string newKey = KeyFor(projectRoot, candidates[0]);
            if (newKey.Length == 0 || state.Files.ContainsKey(newKey)) { held++; continue; }

            state.Files[newKey] = state.Files[key];
            state.Files.Remove(key);
            migrated++;
        }

        if (migrated > 0)
        {
            state.Version = 2;

            // 搬之前留个底：旧文件不覆盖，另存 .bak（同一份只留第一次的）
            string file = ProjectLayout.ProgressFile(projectRoot);
            string bak = file + ".bak";
            try
            {
                if (File.Exists(file) && !File.Exists(bak))
                    File.Copy(file, bak, overwrite: false);
            }
            catch (Exception) { /* 留底失败也不能拦着搬家 */ }

            Save(projectRoot, state);
        }

        return (migrated, held);
    }

    /// <summary>
    /// 打开一份文件、发现进度表里只有它旧名字（裸文件名）下的那条，就现场认领过来。
    ///
    /// 为什么允许"现场认领"：那一刻我们**确切知道**这份文件指的是哪一份 ——
    /// 就是眼前打开的这份。比起在打开项目时凭目录猜，这更不容易猜错。
    /// （同名文件若有好几份，先被打开的那份先认领；此后旧键就没了，另一份只能重标。）
    /// </summary>
    public static bool AdoptLegacy(string projectRoot, ProgressState state, string key, string fileName)
    {
        if (state == null || key.Length == 0 || fileName.Length == 0) return false;
        if (state.Files.ContainsKey(key)) return false;
        if (!state.Files.TryGetValue(fileName, out int mark)) return false;

        state.Files[key] = mark;
        state.Files.Remove(fileName);
        state.Version = 2;

        try { Save(projectRoot, state); }
        catch (Exception) { /* 存不下去也先在内存里认领，这次运行照样有线 */ }

        return true;
    }

    /// <summary>绝对路径按文件名归堆（去重；同一个文件出现两次不算两份）。</summary>
    private static Dictionary<string, List<string>> GroupByName(IEnumerable<string> paths)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string full;
            try { full = Path.GetFullPath(raw); } catch (Exception) { continue; }

            string name = Path.GetFileName(full);
            if (name.Length == 0) continue;

            if (!byName.TryGetValue(name, out var list)) { list = new List<string>(); byName[name] = list; }
            if (!list.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase))) list.Add(full);
        }
        return byName;
    }
}
