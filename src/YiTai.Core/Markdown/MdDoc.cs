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
// 删除线、链接、引用、有序/无序列表、代码块、分隔线、表格（GFM 管道表）。
// 表格只认 GFM 管道表；脚注、HTML 内嵌、跨行/跨列合并仍不做 —— 半吊子支持比不支持更让人困惑。
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
    Table = 6,
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

    /// <summary>==高亮==（GFM / Obsidian / Typora / Pandoc 的事实标准，不是 CommonMark 正规语法）。</summary>
    Highlight = 7,
}

/// <summary>一段里的一小截：一段文字，或者一段带格式的文字。</summary>
public sealed class MdInline
{
    public MdInlineKind Kind { get; set; } = MdInlineKind.Text;

    public string Text { get; set; } = "";

    /// <summary>只有 Link 用得上。</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 这一截文字在文档里的起始偏移（绝对，不是段内相对）。
    /// 有了它，"译到哪了"的绿线才能落在某一段话的中间 —— 记号（** ` &gt;）被吃掉后，
    /// 渲染出来的字与文档的位置对不上，只能靠解析时就记下来。
    /// </summary>
    public int SourceOffset { get; set; }

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

/// <summary>表格某一列的对齐方式。GFM 管道表：分隔行里 `:---` 左、`:---:` 中、`---:` 右。</summary>
public enum MdColumnAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>表格里的一个单元格：行内内容照常解析（粗体 / 高亮 / 行内代码都行）。</summary>
public sealed class MdTableCell
{
    public List<MdInline> Inlines { get; } = new();
}

/// <summary>表格里的一行。首行 IsHeader = true。</summary>
public sealed class MdTableRow
{
    public bool IsHeader { get; set; }
    public List<MdTableCell> Cells { get; } = new();
}

/// <summary>一张 GFM 管道表。列数由 Aligns 决定，行数与每行单元格数已对齐成一样多。</summary>
public sealed class MdTable
{
    public List<MdColumnAlign> Aligns { get; } = new();
    public List<MdTableRow> Rows { get; } = new();
}

/// <summary>
/// 译文预览口径下这一段的角色（默认口径一律是 <see cref="None"/>）。
/// 为什么要有它：编辑页是 TextBox，一个 <c>\n</c> 就是一个看得见的换行；
/// 译文常见排版是"每句一行、句间只有单换行"，预览若照默认把段内软换行拼回一段，
/// 整页就挤成一整块（T-fdbe0163 的反馈）。所以译文那两条渲染链路会按行成段，
/// 但"空行分开的两段"仍要有段间距 —— 于是要把"段首"与"续行"分开记，
/// 渲染时才知道间距该加在哪一行（Core 只标角色，间距多少归 App）。
/// </summary>
public enum MdLineBreakRole
{
    /// <summary>默认 Markdown：一段就是一个块，段内软换行已经拼回一行。</summary>
    None = 0,

    /// <summary>按行分段时，这一块是这一"段"的第一行（段间距照常）。</summary>
    GroupStart = 1,

    /// <summary>按行分段时，这是软换行拆出来的续行：紧贴上一行，不另外加段间距。</summary>
    Continuation = 2,
}

/// <summary>
/// 段落里的一行原始文本。
/// 只给解析器内部用：把一段的多行拼回去（Markdown 的软换行语义），
/// 拼的时候每一截文字都带着自己的绝对偏移，"译到哪了"的竖线才落得准。
/// </summary>
public sealed class MdLine
{
    /// <summary>这一行在文档里的字符偏移。</summary>
    public int SourceOffset { get; set; }

    /// <summary>这一行结束的位置（不含换行符）。</summary>
    public int EndOffset { get; set; }

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

    /// <summary>代码块原始文本（保留换行与缩进）。</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// 这一块在文档里的字符偏移（换行统一成 \n 之后的位置，0 起）。
    /// 只有一个用途：把"译到哪了"的绿色标记钉在文档上 ——
    /// 记"第几段"会在文档增删一段之后整体错位，记字符偏移则不会。
    /// </summary>
    public int SourceOffset { get; set; }

    /// <summary>段落 / 标题 / 引用 / 列表项里的行内内容。</summary>
    public List<MdInline> Inlines { get; } = new();

    /// <summary>
    /// 按行分段（译文预览口径）时，这一块在原来那一段里的位置。
    /// 默认口径恒为 <see cref="MdLineBreakRole.None"/> —— 资料栏 / 原文的渲染不受影响。
    /// </summary>
    public MdLineBreakRole LineBreakRole { get; set; } = MdLineBreakRole.None;

    /// <summary>列表项（只有 List 块用）。</summary>
    public List<MdItem> Items { get; } = new();

    /// <summary>表格（只有 Table 块用）。</summary>
    public MdTable? Table { get; set; }
}

/// <summary>一整篇解析结果。顺序就是文档顺序。</summary>
public sealed class MdDoc
{
    public List<MdBlock> Blocks { get; } = new();
}
