// ============================================================
// MdDoc.cs —— Markdown 的中间结构（纯数据，不含任何界面）
// ============================================================
//
// 为什么分两层：
//   Core 只认"这是一段话 / 这是个标题 / 这段里哪几个字是粗体"，
//   至于粗体该用什么颜色、标题多大号，全归 App。这样 Core 依旧是
//   net10.0 纯逻辑，能被单元测试直接跑，也保住了"Core 不知道界面"这条红线。
//
// 只覆盖翻译稿里真用得上的语法：标题、段落、粗体、斜体、粗斜、行内代码、
// 删除线、链接、引用、有序/无序列表、代码块、分隔线。
// 表格、脚注、HTML 内嵌故意不做 —— 半吊子支持比不支持更让人困惑。
// ============================================================

using System.Collections.Generic;

namespace YiTai.Core;

public enum MdBlockKind
{
    Paragraph = 0,
    Heading = 1,
    CodeBlock = 2,
    Quote = 3,
    List = 4,
    Divider = 5,
}

public enum MdInlineKind
{
    Text = 0,
    Bold = 1,
    Italic = 2,
    BoldItalic = 3,
    Code = 4,
    Strike = 5,
    Link = 6,
}

/// <summary>一段里的一小截：一段文字，或者一段带格式的文字。</summary>
public sealed class MdInline
{
    public MdInlineKind Kind { get; set; } = MdInlineKind.Text;

    public string Text { get; set; } = "";

    /// <summary>只有 Link 用得上。</summary>
    public string Url { get; set; } = "";

    public static MdInline Plain(string text) => new MdInline { Kind = MdInlineKind.Text, Text = text };
}

/// <summary>列表里的一项。</summary>
public sealed class MdItem
{
    /// <summary>有序列表里的序号（无序列表不解释）。</summary>
    public int Number { get; set; }

    /// <summary>缩进级别，0 起；用来给嵌套列表做左缩进，不真的做嵌套。</summary>
    public int Level { get; set; }

    public List<MdInline> Inlines { get; } = new();
}

/// <summary>一个块：段落 / 标题 / 代码块 / 引用 / 列表 / 分隔线。</summary>
public sealed class MdBlock
{
    public MdBlockKind Kind { get; set; } = MdBlockKind.Paragraph;

    /// <summary>标题级别 1..6；别的块不用。</summary>
    public int Level { get; set; }

    /// <summary>列表是否有序。</summary>
    public bool Ordered { get; set; }

    /// <summary>代码块原文（保留换行与缩进）。</summary>
    public string Text { get; set; } = "";

    /// <summary>段落 / 标题 / 引用 / 列表项里的行内内容。</summary>
    public List<MdInline> Inlines { get; } = new();

    /// <summary>列表项（只有 List 块用）。</summary>
    public List<MdItem> Items { get; } = new();
}

/// <summary>一整篇解析结果。顺序就是原文顺序。</summary>
public sealed class MdDoc
{
    public List<MdBlock> Blocks { get; } = new();
}
