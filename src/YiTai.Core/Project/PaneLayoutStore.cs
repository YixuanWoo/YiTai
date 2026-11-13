// ============================================================
// PaneLayoutStore.cs —— 每个项目自己的"上次栏怎么摆"
// ============================================================
//
// 存在 <项目根>\.yitai\panes.json：
//
//   { "Version": 1,
//     "Panes":   [ { "Kind":3, "Path":"sources\\ch03.md", "Label":"ch03.md", "Zoom":1.0 } ],
//     "Weights": [ 1.0, 1.0, 1.2 ] }
//
// 为什么不放全局 settings.json：
//   1. 栏位是"这本书我上次怎么对照着看"，跟着项目走才对；
//   2. 项目整个文件夹拷给别人、换台机器打开，也得还是那几栏；
//   3. settings.json 是软件全局的，任何一次误写都会把所有项目的记忆一起连坐
//      （2026-11-13 出过这事：一次冒烟测试就把用户的栏位记忆整份覆盖了）。
//
// 路径尽量存相对项目根的写法；读的时候由 NormalizeSpec 折回绝对路径。
// 文件坏了就当没有，绝不连累项目打不开。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiTai.Core;

public sealed class PaneLayout
{
    public int Version { get; set; } = 1;

    /// <summary>从左到右的对照栏。译文栏不在里面：它永远在最右，且只有一栏。</summary>
    public List<RefDocSpec> Panes { get; set; } = new();

    /// <summary>各栏宽度比例（依次是 各对照栏…、译文栏）。空 = 用默认比例。</summary>
    public List<double> Weights { get; set; } = new();
}

public static class PaneLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PaneLayout Load(string projectRoot)
    {
        var empty = new PaneLayout();

        if (string.IsNullOrEmpty(projectRoot)) return empty;

        string file = ProjectLayout.PaneLayoutFile(projectRoot);
        if (!File.Exists(file)) return empty;

        try
        {
            var state = JsonSerializer.Deserialize<PaneLayout>(File.ReadAllText(file), Options);
            if (state == null) return empty;

            state.Panes ??= new List<RefDocSpec>();
            state.Weights ??= new List<double>();
            return state;
        }
        catch (Exception)
        {
            return empty;   // 读不懂就当没记过
        }
    }

    /// <summary>原子写：先写 .tmp 再 move，写一半断电不会留半个坏文件。</summary>
    public static void Save(string projectRoot, PaneLayout state)
    {
        if (string.IsNullOrEmpty(projectRoot) || state == null) return;

        Directory.CreateDirectory(ProjectLayout.Private(projectRoot));

        string file = ProjectLayout.PaneLayoutFile(projectRoot);
        string tmp = file + ".tmp";

        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        File.Move(tmp, file, overwrite: true);
    }
}
