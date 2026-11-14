// ============================================================
// TranslationStore.cs —— 译文的读与安全写
// ============================================================
//
// 它只干一件事：把一份 md 的东西读出来、写回去，且**不悄悄覆盖**。
//
// 三条硬规矩（都是踩过坑换来的）：
//   1. 换行与 BOM 照源文件：CRLF 的稿子写回去还是 CRLF，否则 diff 全乱。
//   2. 原子替换：先写 .tmp 再 move，写一半断电不会留半个稿子。
//   3. 冲突检测：落盘前比对磁盘上的 sha，跟我上次读到的不一致就抛冲突，
//      由上层问用户"用我的 / 用磁盘的"，绝不自己选一个。
//
// 备份策略：每天首次保存前，把上一版复制到 backups\。
// ============================================================

using System;
using System.IO;

namespace YiTai.Core;

/// <summary>读进来的一份译文，带着它的书写习惯。</summary>
public sealed class TranslationDoc
{
    public string Path { get; init; } = "";
    public bool Exists { get; init; }
    public string TextLf { get; init; } = "";
    public string Sha { get; init; } = "";
    public string Eol { get; init; } = "\n";
    public bool HasBom { get; init; }
    public bool EndsWithNewline { get; init; } = true;

    public int Chars => TextStats.CountChars(TextLf);
    public int Paragraphs => TextStats.CountParagraphs(TextLf);
}

/// <summary>磁盘上的文件与"我以为的"不一致。上层必须让用户选，不能自己决定。</summary>
public sealed class TranslationConflictException : Exception
{
    public TranslationConflictException(string path, string diskSha)
        : base("磁盘上的文件已被其他程序改动：" + path)
    {
        DiskSha = diskSha;
    }

    public string DiskSha { get; }
}

public static class TranslationStore
{
    public static TranslationDoc Load(string path)
    {
        var info = TextFile.Read(path);
        return new TranslationDoc
        {
            Path = path,
            Exists = info.Exists,
            TextLf = info.Text,
            Sha = info.Sha,
            Eol = info.Eol,
            HasBom = info.HasBom,
            EndsWithNewline = info.EndsWithNewline,
        };
    }

    /// <summary>
    /// 写回。empty 保护：正文全空时拒绝写（避免一次误删清空整章）。
    /// </param>
    public static TranslationDoc Save(string path, string textLf, string? expectedSha, string backupDir, DateTime now)
    {
        if (textLf.Trim().Length == 0)
            throw new InvalidOperationException("译文是空的，不落盘（防止误清空）。");

        var current = TextFile.Read(path);

        // 冲突：磁盘上跟我上次读到的不一样了（别的编辑器改过 / 同步盘拉过）
        if (current.Exists && expectedSha != null && current.Sha != expectedSha)
            throw new TranslationConflictException(path, current.Sha);

        string eol = current.Exists ? current.Eol : "\n";
        bool bom = current.Exists && current.HasBom;
        bool endsNl = !current.Exists || current.EndsWithNewline;

        if (current.Exists && backupDir.Length > 0)
            TextFile.BackupOncePerDay(path, backupDir, now);

        TextFile.WriteAtomic(path, textLf, eol, bom, endsNl);

        return Load(path);
    }
}
