// ============================================================
// Model.cs —— 数据模型（没有行为，只有形状）
// ============================================================
//
// 一个「行」= 一句英文原文 + 它的随文译注 + 你写的中文。
// 整章 = 标题 + 若干行 + 一份落盘状态。
//
// ★ 这里的 En 是**洗干净**的英文：所有 〔n〕 锚点和 （**…**） 行内注
//   都已经剥掉（注单独放在 Note 字段）。这样复制给 AI 时不会带噪点。
// ============================================================

using System.Collections.Generic;

namespace PairDesk.Core;

/// <summary>一行：一句原文 + 该句的注 + 对应的译文。</summary>
public sealed class Segment
{
    /// <summary>干净英文（已剥注）。复制按钮发出去的就是这个。</summary>
    public string En { get; set; } = "";

    /// <summary>该句的随文译注原文（没有就是空串）。只在界面上展开看，不参与复制。</summary>
    public string Note { get; set; } = "";

    /// <summary>你写的中文。空串 = 还没译。</summary>
    public string Zh { get; set; } = "";

    public bool IsDone => Zh.Trim().Length > 0;

    public Segment Clone() => new() { En = En, Note = Note, Zh = Zh };
}

/// <summary>解析一份带译注的原文之后得到的东西。</summary>
public sealed class ParsedSource
{
    /// <summary>章标题，例如「第 3 章 · 英文原文（带译注锚点）」。解析不出来就是空串。</summary>
    public string Title { get; set; } = "";

    /// <summary>「本章看点」那一串 chip 文案（按 ｜ 切开）。</summary>
    public List<string> Tips { get; set; } = new();

    /// <summary>剥注后的纯正文（未切句）。</summary>
    public string CleanBody { get; set; } = "";

    /// <summary>切好并修过碎片的行。</summary>
    public List<Segment> Segments { get; set; } = new();

    /// <summary>切句过程中被判为碎片、已并入上一句的数量（诊断用）。</summary>
    public int MergedFragments { get; set; }
}

/// <summary>一份文本文件被读进来时的原始面貌（回写要照原样还回去）。</summary>
public sealed class TextFileInfo
{
    /// <summary>文件是否存在。</summary>
    public bool Exists { get; set; }

    /// <summary>正文（换行已统一成 \n，方便内部处理）。</summary>
    public string Text { get; set; } = "";

    /// <summary>原文件的换行符："\r\n" 或 "\n"。首行是章名的 md 里两者混用过，必须分别记住。</summary>
    public string Eol { get; set; } = "\n";

    /// <summary>是否有 UTF-8 BOM。你现有译稿是无 BOM，回写也必须无 BOM。</summary>
    public bool HasBom { get; set; }

    /// <summary>原文件末尾是否带换行。</summary>
    public bool EndsWithNewline { get; set; }

    /// <summary>内容的 sha256（前 16 位够用），用于判断"磁盘上这份是不是我上次写的那份"。</summary>
    public string Sha { get; set; } = "";
}

/// <summary>一章的完整工作状态：这一章是什么、有哪些行、上次对应哪份 md。</summary>
public sealed class ChapterState
{
    public const int SchemaVersion = 1;

    public int Version { get; set; } = SchemaVersion;

    /// <summary>章号，1..27。</summary>
    public int Number { get; set; }

    /// <summary>译稿里的章名（第一段），如「第三章」。</summary>
    public string Title { get; set; } = "";

    /// <summary>生成这些行时，原文文件的 sha。原文换过就要重切。</summary>
    public string SourceSha { get; set; } = "";

    /// <summary>上次写盘后，译稿 md 的 sha。对不上 = 有外部改动。</summary>
    public string MdSha { get; set; } = "";

    public List<Segment> Rows { get; set; } = new();

    public int DoneCount
    {
        get
        {
            int n = 0;
            foreach (var r in Rows) if (r.IsDone) n++;
            return n;
        }
    }
}
