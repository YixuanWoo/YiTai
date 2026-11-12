// ============================================================
// ProjectLayout.cs —— 项目在磁盘上的目录约定
// ============================================================
//
// 只在这一个文件里规定"什么放在哪儿"。其他任何模块都不许拼路径，
// 一律问它要 —— 这样以后想改布局（比如译文按语言分目录），只改这里。
//
// 目录约定：
//
//   <项目根>\
//     project.json        项目元信息（唯一入口文件，双击/打开项目就是打开它）
//     sources\            导入时复制进来的源文件（项目自足，整包可搬走）
//     translations\       译文（默认与源文件同名，扩展名换成 .md）
//     backups\            每日首次保存前的备份
//     .yitai\             程序自己用的东西（以后可能放索引缓存），不给人看
//
// 为什么译文放在项目里而不是"源文件旁边"：
//   译文是项目的产物，源文件以后可能来自任何地方（另一台机器、别人的排版稿）。
//   放在项目里，一个文件夹就是一次完整的翻译工程，压缩、搬走、给朋友都不会断链。
// ============================================================

using System;
using System.IO;

namespace YiTai.Core;

public static class ProjectLayout
{
    public const string ProjectFileName = "project.json";
    public const string SourcesDirName = "sources";
    public const string TranslationsDirName = "translations";
    public const string BackupsDirName = "backups";
    public const string PrivateDirName = ".yitai";
    public const string ProgressFileName = "progress.json";

    public static string ProjectFile(string root) => Path.Combine(root, ProjectFileName);
    public static string Sources(string root) => Path.Combine(root, SourcesDirName);
    public static string Translations(string root) => Path.Combine(root, TranslationsDirName);
    public static string Backups(string root) => Path.Combine(root, BackupsDirName);
    public static string Private(string root) => Path.Combine(root, PrivateDirName);

    /// <summary>
    /// "译到哪了"的进度标记，按源文件名记着。
    /// 放 .yitai 而不进 project.json：它是干活的临时状态，不是"这本书怎么翻"的清单，
    /// 不该让清单的 schema 隔三差五地变。
    /// </summary>
    public static string ProgressFile(string root) => Path.Combine(Private(root), ProgressFileName);

    /// <summary>建好一个新项目所需的空目录（不覆盖已存在的内容）。</summary>
    public static void EnsureCreated(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Sources(root));
        Directory.CreateDirectory(Translations(root));
        Directory.CreateDirectory(Backups(root));
        Directory.CreateDirectory(Private(root));
    }

    /// <summary>默认译文路径：translations\&lt;源文件名去扩展名&gt;.md</summary>
    public static string DefaultTranslationPath(string root, string sourceFileName)
    {
        string stem = Path.GetFileNameWithoutExtension(sourceFileName);
        if (stem.Length == 0) stem = "未命名";
        return Path.Combine(Translations(root), stem + ".md");
    }

    /// <summary>把绝对路径转成相对项目根的路径（存进 project.json 用）。</summary>
    public static string ToRelative(string root, string absolute)
    {
        string rel = Path.GetRelativePath(root, absolute);
        return rel.Replace('/', '\\');
    }

    /// <summary>相对路径 → 绝对路径。若记录的是绝对路径（外部文件），原样返回。</summary>
    public static string ToAbsolute(string root, string path)
    {
        if (path.Length == 0) return path;
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(root, path);
    }
}
