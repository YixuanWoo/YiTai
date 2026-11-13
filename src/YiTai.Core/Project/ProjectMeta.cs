// ============================================================
// ProjectMeta.cs —— 项目的数据模型（写进 project.json 的那份）
// ============================================================
//
// 设计原则：**只描述项目是什么，不描述界面怎么显示。**
// 源文件不预设"每章一个文件"、不预设"有什么注"，只记"有一份文件、叫什么、
// 从哪来、译文在哪"。怎么切句、怎么认注、怎么显示，都是以后叠加的"阅读器"，
// 不属于项目数据。
//
// 每个字段都可缺省：旧项目文件缺字段时按默认值读，不让升级卡住人。
// ============================================================

using System;
using System.Collections.Generic;

namespace YiTai.Core;

public sealed class ProjectMeta
{
    /// <能看到什么、想显示什么，以后由阅读器决定>
    public string Name { get; set; } = "未命名项目";

    /// <summary>项目格式版本，用来兼容旧文件。</summary>
    public int Schema { get; set; } = 1;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>源文件清单，顺序即左侧下拉里的顺序。</summary>
    public List<SourceEntry> Sources { get; set; } = new();

    /// <summary>
    /// 译文清单。**和源文件清单没有对应关系**：译文是独立的一篇文档，
    /// 叫什么、写哪一页，由人决定，不由"源文件第几章"派生。
    /// 磁盘上是 translations\ 里的 .md，这里只是索引（按文件名排）。
    /// </summary>
    public List<TranslationEntry> Translations { get; set; } = new();

    /// <summary>上次打开的译文（按 Id 记，改名也不丢）。</summary>
    public string? LastTranslationId { get; set; }

    /// <summary>上次打开的是哪一个源文件（按 Id 记，改名也不丢）。</summary>
    public string? LastSourceId { get; set; }
}

/// <summary>一篇译文。位置一律在 translations\ 里（统一落盘），只记文件名。</summary>
public sealed class TranslationEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>相对 translations\ 的文件名。</summary>
    public string FileName { get; set; } = "";

    /// <summary>给人看的备注，程序不解释。</summary>
    public string Note { get; set; } = "";
}

public sealed class SourceEntry
{
    /// <summary>稳定标识（新建时生成，删了再加不会串位）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>在项目里的文件名（相对 sources\）。</summary>
    public string FileName { get; set; } = "";

    /// <summary>原始位置，仅作记录 / 以后"重新导入"用；不参与打开。</summary>
    public string? OriginPath { get; set; }

    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 译文位置，相对项目根。留空 = 用默认（translations\&lt;同名&gt;.md）。
    /// 「另存为」就是改这个字段，而不是改文件约定。
    /// </summary>
    public string TranslationPath { get; set; } = "";

    /// <summary>给人看的备注（语言、版本之类），程序不解释它。</summary>
    public string Note { get; set; } = "";
}
