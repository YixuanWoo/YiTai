// ============================================================
// ProjectMeta.cs —— 项目的数据模型（写进 project.json 的那份）
// ============================================================
//
// 设计原则：**只描述"这个项目有哪些译文"，不描述界面怎么显示。**
//
// 这里曾经有一份"源文件清单"（每份源文件一条，带着"它的译文在哪"），
// 那是"一份原文配一份译文"的旧模型。界面早已改成「译文 + 若干只读资料栏」，
// 资料不登记、不复制、也不预设和译文的对应关系，所以清单整份删掉了。
//
// 现在 project.json 只有：项目名、格式版本、译文索引、上次打开的译文。
// 栏位摆法在 .yitai\panes.json（见 ProjectLayout.PaneLayoutFile）。
//
// 旧项目里的 "Sources" / "LastSourceId" 读的时候会被 System.Text.Json 直接忽略，
// 下次保存就不再写出来 —— 旧的照样能打开，新的不再带这些字段。
//
// 每个字段都可缺省：旧项目文件缺字段时按默认值读，不让升级卡住人。
// ============================================================

using System;
using System.Collections.Generic;

namespace YiTai.Core;

public sealed class ProjectMeta
{
    public string Name { get; set; } = "未命名项目";

    /// <summary>项目格式版本，用来兼容旧文件。2 = 只剩译文清单。</summary>
    public int Schema { get; set; } = 2;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 译文清单。**和别的文件没有对应关系**：译文是独立的一篇文档，
    /// 叫什么、写哪一页，由人决定。
    /// 磁盘上是 translations\ 里的 .md，这里只是索引（按文件名排）。
    /// </summary>
    public List<TranslationEntry> Translations { get; set; } = new();

    /// <summary>上次打开的译文（按 Id 记，改名也不丢）。</summary>
    public string? LastTranslationId { get; set; }
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
