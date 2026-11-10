// ============================================================
// DeskConfig.cs —— 配置（第一次运行自动生成，不做向导）
// ============================================================
//
// 这是"能不能开源"的分水岭。
//
// 自用版会忍不住把「D:\Benny\20_项目\小王子翻译」写死在代码里、
// 把「第NN章.md」当成天经地义 —— 这样的东西放上 GitHub，别人
// 打开就是一个找不到文件的空窗口。
//
// 所以从第一天起，凡是"某个具体项目"的东西全部进配置文件：
//     项目根目录 / 原文文件模式 / 译稿文件模式 / 备份目录 / 状态目录 / 章数
// 代码里一个都不许出现。
//
// 配置默认落在 %APPDATA%\PairDesk\config.json，不写进仓库目录
// （免得用户 clone 完一运行就多出个 config.json 污染 git status）。
// ============================================================

using System;
using System.IO;
using System.Text.Json;

namespace PairDesk.Core;

public sealed class DeskConfig
{
    /// <summary>项目根目录（含原文与译稿两个子目录）。</summary>
    public string ProjectRoot { get; set; } = "";

    /// <summary>原文文件模式。{nn} = 两位补零章号，{n} = 不补零。</summary>
    public string SourcePattern { get; set; } = @"01-原文\按章\英文-带标注\ch{nn}.md";

    /// <summary>译稿文件模式（本程序唯一的写目标）。</summary>
    public string TranslationPattern { get; set; } = @"04-译稿\第{nn}章.md";

    /// <summary>备份目录（每章每天首次落盘前放一份上一版）。</summary>
    public string BackupPattern { get; set; } = @"03-工作文件\备份";

    /// <summary>状态目录（存"行 ↔ 段"的映射，绝不混进正文）。</summary>
    public string StatePattern { get; set; } = @"03-工作文件\工作台状态";

    /// <summary>这本书总共多少章（只影响左侧目录列到第几章）。</summary>
    public int ChapterCount { get; set; } = 27;

    /// <summary>上次打开的章，重开时接着干。</summary>
    public int LastChapter { get; set; } = 1;

    // ---------------------------------------------------------------- 源格式档案
    //
    // 这几项决定"原文怎么读"。默认值是「拉丁切句 + 句后内嵌括号注」，
    // 也就是最保守、最不可能认错的那一档；导入时由探测结果改写。

    /// <summary>注的写法：InlineBoldParen / TrailingCards / Footnotes / None。</summary>
    public NoteStyle Notes { get; set; } = NoteStyle.InlineBoldParen;

    /// <summary>切句口径：LatinSentence / CjkSentence / Paragraph / Line。</summary>
    public SplitMode Split { get; set; } = SplitMode.LatinSentence;

    /// <summary>额外自定义正则（第一个捕获组 = 注文；空 = 不用）。</summary>
    public string CustomNotePattern { get; set; } = "";

    /// <summary>是否启用"本章看点"这类中文本地化识别。</summary>
    public bool ChineseHeuristics { get; set; } = true;

    /// <summary>标题行前缀。</summary>
    public string TitlePrefix { get; set; } = "# ";

    /// <summary>窗口尺寸。记事本级的小体贴。</summary>
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 830;

    // ---------------------------------------------------------------- 位置

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PairDesk");

    public static string DefaultPath => Path.Combine(DefaultDir, "config.json");

    public DeskPaths ToPaths() => new()
    {
        ProjectRoot = ProjectRoot,
        SourcePattern = SourcePattern,
        TranslationPattern = TranslationPattern,
        BackupPattern = BackupPattern,
        StatePattern = StatePattern,
    };

    /// <summary>把配置里的解析口径装成一份档案，交给 ChapterStore。</summary>
    public SourceProfile ToProfile() => new()
    {
        Notes = Notes,
        Split = Split,
        CustomNotePattern = CustomNotePattern,
        ChineseHeuristics = ChineseHeuristics,
        TitlePrefix = TitlePrefix,
    };

    // ---------------------------------------------------------------- 读写

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // 枚举写成字符串（Notes: "TrailingCards"），配置才看得懂、也才改得动
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// 读配置；不存在就生成一份默认的。
    /// <paramref name="seedRoot"/> 是"如果没有配置，先试试这个目录"（自用时的便利）。
    /// </summary>
    public static DeskConfig LoadOrCreate(string path, string? seedRoot)
    {
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<DeskConfig>(json, Json);
                if (cfg != null) return cfg;
            }
            catch (Exception)
            {
                // 配置坏了不该打不开程序：退回默认值，让用户重新选目录
            }
        }

        var fresh = new DeskConfig();
        if (seedRoot != null && Directory.Exists(seedRoot)) fresh.ProjectRoot = seedRoot;
        fresh.Save(path);
        return fresh;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TextFile.WriteAtomic(path, JsonSerializer.Serialize(this, Json), "\n", withBom: false);
    }

    /// <summary>这个配置能不能用（项目根目录存在，且至少有一章原文能找到）。</summary>
    public bool IsUsable()
    {
        if (ProjectRoot.Length == 0 || !Directory.Exists(ProjectRoot)) return false;

        var paths = ToPaths();
        for (int n = 1; n <= Math.Max(1, ChapterCount); n++)
        {
            if (File.Exists(paths.SourcePath(n))) return true;
        }
        return false;
    }
}

/// <summary>
/// 从"随便一个文件"反推项目根目录 —— 开源版第一次运行靠它活命：
/// 用户只需要选一份自己的原文 md，程序自己去找结构。
/// </summary>
public static class ProjectLocator
{
    /// <summary>
    /// 给定一份原文文件，向上找含「01-原文」的那一级作为项目根；
    /// 找不到就退化为"文件所在的目录"，并把原文模式改成该文件自己。
    /// </summary>
    public static DeskConfig DeriveFromSourceFile(string sourceFilePath)
    {
        string full = Path.GetFullPath(sourceFilePath);
        string dir = Path.GetDirectoryName(full)!;

        var cfg = new DeskConfig();

        // 往上走，直到看见 01-原文
        string? probe = dir;
        for (int i = 0; i < 6 && probe != null; i++)
        {
            if (Directory.Exists(Path.Combine(probe, "01-原文")))
            {
                cfg.ProjectRoot = probe;
                cfg.SourcePattern = Relative(full, probe);
                return cfg;
            }
            probe = Path.GetDirectoryName(probe);
        }

        // 认不出结构：就用"这份文件 + 同目录的译稿"，单文件模式
        string stem = Path.GetFileNameWithoutExtension(full);
        cfg.ProjectRoot = dir;
        cfg.SourcePattern = Path.GetFileName(full);
        cfg.TranslationPattern = stem + "-译文.md";
        cfg.BackupPattern = "备份";
        cfg.StatePattern = "工作台状态";
        cfg.ChapterCount = 1;
        return cfg;
    }

    private static string Relative(string file, string root)
    {
        string rel = Path.GetRelativePath(root, file);
        return rel.Replace('/', '\\');
    }
}
