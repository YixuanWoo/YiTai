// ============================================================
// MdParser.cs —— 把 Markdown 文本拆成 MdDoc
// ============================================================
//
// 刻意写得"笨"：按行扫一遍，认几种前缀，不做完整的 CommonMark。
// 理由跟 README 一个口径：这是给人看资料/对照用的，认错了顶多难看，
// 不该出现"解析器自己崩了、稿子读不出来"这种事。所以：
//   · 任何没闭合的标记都当普通文字（不会吃掉后面的正文）
//   · 代码块没闭合，到文末就按代码块收尾（内容不丢）
//   · 认不出来的行一律进段落，绝不吞字
//
// 软换行（段落里连续几行）合并成一段：西文之间补一个空格，
// 只要有一侧是中日韩文字就直接拼 —— 免得中文段落里冒出半角空格。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace YiTai.Core;

public static class MdParser
{
    /// <summary>
    /// 把整篇 Markdown 拆成 <see cref="MdDoc"/>。
    /// </summary>
    /// <param name="text">整篇原文（CRLF 会先归一化成 \n）。</param>
    /// <param name="softBreakAsParagraph">
    /// 段内的单个软换行算不算段落边界。默认 <c>false</c>：照 Markdown 的规矩拼回一段
    /// （资料栏 / 原文都走这条，行为一个字不动）。
    /// 译文渲染链路（编辑页的预览、译文对照栏）传 <c>true</c>：编辑页是 TextBox，
    /// 一个 <c>\n</c> 就是一个看得见的换行，预览要逐行对上才不会挤成一整块。
    /// <para>无论哪个口径，每一截文字带的 SourceOffset 都是同一个坐标系（归一化原文下标），
    /// 批注 / 进度线的锚点因此不会因为分段方式不同而漂。</para>
    /// </param>
    public static MdDoc Parse(string text, bool softBreakAsParagraph = false)
    {
        var doc = new MdDoc();
        if (string.IsNullOrEmpty(text)) return doc;

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        var paraLines = new List<MdLine>();
        var code = new StringBuilder();
        var quote = new StringBuilder();
        bool inCode = false;
        MdBlock? list = null;

        // 每一块在文档里的起始偏移（"译到哪了"的绿线靠它定位，见 MdBlock.SourceOffset）
        int offset = 0;
        int paraStart = -1;
        int quoteStart = -1;
        int codeStart = -1;

        void FlushPara()
        {
            if (paraLines.Count == 0) return;

            // -------------------------------------------------- 译文口径：按行成段
            //
            // 译文稿常见"每句一行、句间只有单换行"，编辑页里那是一行行看得见的换行。
            // 这个开关只给译文渲染链路用：每行各自成段，预览才与编辑页逐行对上。
            // 不插 gap inline、也不拼字 —— 每行的 SourceOffset 本来就是行首，
            // 批注 / 进度线的锚点照旧落在原来那些字上（不吞字、不错位）。
            // 段首与续行分开标：空行分开的两段仍要有段间距，同一段里的续行则贴紧上一行。
            if (softBreakAsParagraph)
            {
                for (int i = 0; i < paraLines.Count; i++)
                {
                    var one = new MdBlock
                    {
                        Kind = MdBlockKind.Paragraph,
                        SourceOffset = paraLines[i].SourceOffset,
                        LineBreakRole = i == 0 ? MdLineBreakRole.GroupStart : MdLineBreakRole.Continuation,
                    };
                    one.Inlines.AddRange(paraLines[i].Inlines);
                    doc.Blocks.Add(one);
                }

                paraLines.Clear();
                paraStart = -1;
                return;
            }

            var b = new MdBlock { Kind = MdBlockKind.Paragraph, SourceOffset = paraStart };

            // 段内的软换行照 Markdown 的规矩拼回去（一个空格），排版与"不标记"时完全一样；
            // 但中日韩文字两侧直接拼，不补空格 —— 中文稿按行折行后，渲染出来不该每行之间多一个半角空格。
            // 每一截都带着自己的文档偏移，竖线因此能落在段中间某句话上。
            for (int i = 0; i < paraLines.Count; i++)
            {
                if (i > 0)
                {
                    bool tight = IsCjk(LastTextChar(paraLines[i - 1].Inlines))
                              || IsCjk(FirstTextChar(paraLines[i].Inlines));
                    var gap = MdInline.Plain(tight ? "" : " ");
                    gap.SourceOffset = paraLines[i].SourceOffset - 1;   // 那个换行符的位置
                    b.Inlines.Add(gap);
                }

                b.Inlines.AddRange(paraLines[i].Inlines);
            }

            paraLines.Clear();
            paraStart = -1;
            doc.Blocks.Add(b);
        }

        void FlushQuote()
        {
            if (quote.Length == 0) return;
            string s = quote.ToString().Trim();
            quote.Clear();
            int start = quoteStart;
            quoteStart = -1;
            if (s.Length == 0) return;

            var b = new MdBlock { Kind = MdBlockKind.Quote, SourceOffset = start };
            // 引用是几行合并的，位置只能大概对（+2 约等于行首的 "> "）
            b.Inlines.AddRange(ParseInlines(s, start + 2));
            doc.Blocks.Add(b);
        }

        void FlushCode()
        {
            if (code.Length == 0) return;
            doc.Blocks.Add(new MdBlock
            {
                Kind = MdBlockKind.CodeBlock,
                Text = code.ToString().TrimEnd('\n'),
                SourceOffset = codeStart,
            });
            code.Clear();
            codeStart = -1;
        }

        for (int li = 0; li < lines.Length; li++)
        {
            string rawLine = lines[li];
            int lineStart = offset;
            offset += rawLine.Length + 1;   // +1 是那一行结尾的 \n

            string line = rawLine.TrimEnd();
            string trim = line.Trim();

            // -------------------------------------------------- 代码块内
            if (inCode)
            {
                if (trim.StartsWith("```", StringComparison.Ordinal) || trim.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inCode = false;
                    FlushCode();
                }
                else
                {
                    if (code.Length == 0) codeStart = lineStart;
                    code.Append(rawLine);
                    code.Append('\n');
                }
                continue;
            }

            // -------------------------------------------------- 空行
            if (trim.Length == 0)
            {
                FlushPara();
                FlushQuote();
                list = null;
                continue;
            }

            // -------------------------------------------------- 代码块开始
            if (trim.StartsWith("```", StringComparison.Ordinal) || trim.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushPara();
                FlushQuote();
                list = null;
                inCode = true;
                code.Clear();
                continue;
            }

            // -------------------------------------------------- 分隔线
            if (IsDivider(trim))
            {
                FlushPara();
                FlushQuote();
                list = null;
                doc.Blocks.Add(new MdBlock { Kind = MdBlockKind.Divider, SourceOffset = lineStart });
                continue;
            }

            // -------------------------------------------------- 标题
            int level = HeadingLevel(trim);
            if (level > 0)
            {
                FlushPara();
                FlushQuote();
                list = null;

                var b = new MdBlock { Kind = MdBlockKind.Heading, Level = level, SourceOffset = lineStart };

                int hLead = line.Length - line.TrimStart().Length;
                int k = level;                                  // 跳过 "#" 与其后的空格
                while (k < trim.Length && trim[k] == ' ') k++;
                b.Inlines.AddRange(ParseInlines(trim.Substring(level).Trim(), lineStart + hLead + k));
                doc.Blocks.Add(b);
                continue;
            }

            // -------------------------------------------------- 引用（连续几行算一段）
            if (trim.StartsWith(">", StringComparison.Ordinal))
            {
                FlushPara();
                list = null;

                string body = trim.Substring(1).TrimStart();
                if (quote.Length == 0) quoteStart = lineStart;
                if (quote.Length > 0 && body.Length > 0) quote.Append(' ');
                quote.Append(body);
                continue;
            }
            FlushQuote();

            // -------------------------------------------------- 列表
            if (TryListItem(line, out bool ordered, out int number, out string itemText, out int indent))
            {
                FlushPara();

                if (list == null || list.Ordered != ordered)
                {
                    list = new MdBlock { Kind = MdBlockKind.List, Ordered = ordered, SourceOffset = lineStart };
                    doc.Blocks.Add(list);
                }

                var item = new MdItem { Number = number, Level = indent };
                // 在行里找一下正文从哪儿开始，记准位置（"- " / "1. " 这些记号不算）
                int at = line.IndexOf(itemText, StringComparison.Ordinal);
                item.Inlines.AddRange(ParseInlines(itemText, lineStart + Math.Max(0, at)));
                list.Items.Add(item);
                continue;
            }

            // -------------------------------------------------- 表格（GFM 管道表）
            // 表头行 + 分隔行（下一行整行是 :---: 之类）才算表格；行里没有竖线就不是。
            // 只在段落还没开始的时候认：竖线在普通段落里不该把整段吃掉。
            if (paraLines.Count == 0)
            {
                if (TryParseTable(lines, ref li, lineStart, out var tableBlock))
                {
                    list = null;
                    doc.Blocks.Add(tableBlock);
                    continue;
                }
            }

            // -------------------------------------------------- 普通段落
            //
            // 先收成一行一行的 MdLine（每行记自己的绝对偏移、每截文字也记），
            // 等段落结束时再拼回一整段（FlushPara）。这样渲染出来的段落与原来一模一样，
            // 而"译到哪了"的竖线知道该立在哪一句话前面。
            list = null;
            if (paraLines.Count == 0) paraStart = lineStart;

            int lead = rawLine.Length - rawLine.TrimStart().Length;   // 行首缩进（tab/空格）
            var mdLine = new MdLine
            {
                SourceOffset = lineStart,
                EndOffset = lineStart + line.Length,
            };
            mdLine.Inlines.AddRange(ParseInlines(trim, lineStart + lead));
            paraLines.Add(mdLine);
        }

        FlushPara();
        FlushQuote();
        if (inCode) FlushCode();   // 没闭合的代码块：内容照收

        return doc;
    }

    // ==================================================================== 行内

    /// <summary>
    /// 解析一段里的粗体/斜体/代码/删除线/链接。认不出的记号原样留着。
    /// <paramref name="baseOffset"/> 是这段文字在文档里的起点：
    /// 每一截都记下自己的绝对位置，绿线才能落在段落中间的某一句话上。
    /// </summary>
    public static List<MdInline> ParseInlines(string s, int baseOffset = 0)
    {
        var list = new List<MdInline>();
        if (string.IsNullOrEmpty(s)) return list;

        var buf = new StringBuilder();
        int bufStart = -1;   // 当前这段纯文字在 s 里的起点

        void FlushText()
        {
            if (buf.Length == 0)
            {
                bufStart = -1;
                return;
            }

            var inl = MdInline.Plain(buf.ToString());
            inl.SourceOffset = baseOffset + Math.Max(0, bufStart);
            list.Add(inl);

            buf.Clear();
            bufStart = -1;
        }

        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            // 反斜杠转义：\* \_ \` 之类
            if (c == '\\' && i + 1 < s.Length)
            {
                if (buf.Length == 0) bufStart = i + 1;   // 转义吃掉的反斜杠不算位置
                buf.Append(s[i + 1]);
                i += 2;
                continue;
            }

            // 行内代码 `xxx`
            if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushText();
                    list.Add(new MdInline
                    {
                        Kind = MdInlineKind.Code,
                        Text = s.Substring(i + 1, end - i - 1),
                        SourceOffset = baseOffset + i + 1,
                    });
                    i = end + 1;
                    continue;
                }
            }

            // 粗斜 ***xxx*** / ___xxx___
            if ((c == '*' || c == '_') && i + 2 < s.Length && s[i + 1] == c && s[i + 2] == c)
            {
                if (c != '_' || UnderscoreOk(s, i))
                {
                    string mark = new string(c, 3);
                    int end = s.IndexOf(mark, i + 3, StringComparison.Ordinal);
                    if (end > i + 3)
                    {
                        FlushText();
                        list.Add(new MdInline
                        {
                            Kind = MdInlineKind.BoldItalic,
                            Text = s.Substring(i + 3, end - i - 3),
                            SourceOffset = baseOffset + i + 3,
                        });
                        i = end + 3;
                        continue;
                    }
                }
            }

            // 粗体 **xxx** / __xxx__
            if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
            {
                if (c != '_' || UnderscoreOk(s, i))
                {
                    string mark = new string(c, 2);
                    int end = s.IndexOf(mark, i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        FlushText();
                        list.Add(new MdInline
                        {
                            Kind = MdInlineKind.Bold,
                            Text = s.Substring(i + 2, end - i - 2),
                            SourceOffset = baseOffset + i + 2,
                        });
                        i = end + 2;
                        continue;
                    }
                }
            }

            // 斜体 *xxx* / _xxx_
            if (c == '*' || c == '_')
            {
                if (c != '_' || UnderscoreOk(s, i))
                {
                    int end = s.IndexOf(c, i + 1);
                    if (end > i + 1)
                    {
                        FlushText();
                        list.Add(new MdInline
                        {
                            Kind = MdInlineKind.Italic,
                            Text = s.Substring(i + 1, end - i - 1),
                            SourceOffset = baseOffset + i + 1,
                        });
                        i = end + 1;
                        continue;
                    }
                }
            }

            // 高亮 ==xxx==（GFM / Obsidian / Typora / Pandoc 都认，事实标准）
            // 放在粗体之前判：'=' 和 '* _ ~' 不冲突，但顺序上先认更长的记号更稳
            if (c == '=' && i + 1 < s.Length && s[i + 1] == '=')
            {
                int end = s.IndexOf("==", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushText();
                    list.Add(new MdInline
                    {
                        Kind = MdInlineKind.Highlight,
                        Text = s.Substring(i + 2, end - i - 2),
                        SourceOffset = baseOffset + i + 2,
                    });
                    i = end + 2;
                    continue;
                }
                // 没闭合：当普通文字收着，绝不把 == 吞掉
            }

            // 删除线 ~~xxx~~
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
            {
                int end = s.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushText();
                    list.Add(new MdInline
                    {
                        Kind = MdInlineKind.Strike,
                        Text = s.Substring(i + 2, end - i - 2),
                        SourceOffset = baseOffset + i + 2,
                    });
                    i = end + 2;
                    continue;
                }
            }

            // 链接 [文字](地址)
            if (c == '[')
            {
                int close = s.IndexOf(']', i + 1);
                if (close > i + 1 && close + 1 < s.Length && s[close + 1] == '(')
                {
                    int paren = s.IndexOf(')', close + 2);
                    if (paren > close + 1)
                    {
                        FlushText();
                        list.Add(new MdInline
                        {
                            Kind = MdInlineKind.Link,
                            Text = s.Substring(i + 1, close - i - 1),
                            Url = s.Substring(close + 2, paren - close - 2),
                            SourceOffset = baseOffset + i + 1,
                        });
                        i = paren + 1;
                        continue;
                    }
                }
            }

            if (buf.Length == 0) bufStart = i;
            buf.Append(c);
            i++;
        }

        FlushText();
        return list;
    }

    // ==================================================================== 小判定

    /// <summary>`#` 的数量，1..6；后面必须跟空格或到行尾。</summary>
    private static int HeadingLevel(string trim)
    {
        int n = 0;
        while (n < trim.Length && trim[n] == '#') n++;
        if (n < 1 || n > 6) return 0;
        if (n == trim.Length) return n;
        return trim[n] == ' ' ? n : 0;
    }

    // ==================================================================== 表格（GFM 管道表）

    /// <summary>
    /// 当前行是表头、下一行是分隔行（:---: 之类）→ 把整张表吃掉，<paramref name="li"/> 停在最后一行。
    /// 列数由分隔行决定；表头/数据行里多出来的单元格丢弃，缺的补空。
    /// 不是表格就原样返回 false，一个字符都不碰。
    /// </summary>
    private static bool TryParseTable(string[] lines, ref int li, int headerStart, out MdBlock block)
    {
        block = new MdBlock();

        if (li + 1 >= lines.Length) return false;
        if (!HasTablePipe(lines[li]) || !HasTablePipe(lines[li + 1])) return false;

        var headerCells = SplitRow(lines[li]);
        if (headerCells.Count == 0) return false;
        if (!TryParseDelimiter(lines[li + 1], out var aligns)) return false;

        int cols = aligns.Count;
        var table = new MdTable();
        table.Aligns.AddRange(aligns);

        table.Rows.Add(MakeRow(headerCells, cols, isHeader: true, headerStart));

        int rowStart = headerStart + lines[li].Length + 1 + lines[li + 1].Length + 1;
        int i = li + 2;

        while (i < lines.Length)
        {
            string raw = lines[i];
            if (raw.Trim().Length == 0) break;     // 空行 = 表格结束
            if (!HasTablePipe(raw)) break;         // 没有竖线的行 = 表格结束

            table.Rows.Add(MakeRow(SplitRow(raw), cols, isHeader: false, rowStart));
            rowStart += raw.Length + 1;
            i++;
        }

        block.Kind = MdBlockKind.Table;
        block.SourceOffset = headerStart;
        block.Table = table;

        li = i - 1;   // 外层 for 的 li++ 之后，正好指向表格后面的那一行
        return true;
    }

    private static MdTableRow MakeRow(List<string> cells, int cols, bool isHeader, int rowStart)
    {
        var row = new MdTableRow { IsHeader = isHeader };
        for (int c = 0; c < cols; c++)
        {
            string text = c < cells.Count ? cells[c] : "";

            var cell = new MdTableCell();
            // 表格渲染成 BlockUIContainer，绿线进不到单元格里，这里偏移只求不空、不精算。
            cell.Inlines.AddRange(ParseInlines(text, rowStart));
            row.Cells.Add(cell);
        }
        return row;
    }

    /// <summary>把一行按竖线切成单元格：`\|` 与代码跨里的 `|` 不算分隔；首尾的空单元格去掉。</summary>
    private static List<string> SplitRow(string line)
    {
        string s = line.Trim();
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inCode = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (c == '`') { inCode = !inCode; sb.Append(c); }
            else if (c == '\\' && i + 1 < s.Length && s[i + 1] == '|') { sb.Append('|'); i++; }
            else if (c == '|' && !inCode) { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());

        if (cells.Count > 1 && s.StartsWith("|", StringComparison.Ordinal)) cells.RemoveAt(0);
        if (cells.Count > 1 && s.EndsWith("|", StringComparison.Ordinal)) cells.RemoveAt(cells.Count - 1);

        return cells;
    }

    /// <summary>行里有没有一个"真分隔竖线"（不含 `\|` 转义、不含代码跨里的）。</summary>
    private static bool HasTablePipe(string line)
    {
        bool inCode = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '`') inCode = !inCode;
            else if (c == '\\' && i + 1 < line.Length && line[i + 1] == '|') i++;
            else if (c == '|' && !inCode) return true;
        }
        return false;
    }

    /// <summary>分隔行（:---: 之类）。列数与对齐都从这里定。</summary>
    private static bool TryParseDelimiter(string line, out List<MdColumnAlign> aligns)
    {
        aligns = new List<MdColumnAlign>();

        foreach (string raw in SplitRow(line))
        {
            string t = raw.Trim();
            if (t.Length == 0) return false;

            bool left = t.StartsWith(":", StringComparison.Ordinal);
            bool right = t.EndsWith(":", StringComparison.Ordinal);
            string mid = t.Trim(':');

            if (mid.Length == 0) return false;
            foreach (char ch in mid) if (ch != '-') return false;   // 必须全是 -

            aligns.Add(left && right ? MdColumnAlign.Center
                      : right ? MdColumnAlign.Right
                      : MdColumnAlign.Left);
        }

        return aligns.Count > 0;
    }

    /// <summary>--- / *** / ___ （至少三个同样的符号，中间允许空格）。</summary>
    private static bool IsDivider(string trim)
    {
        if (trim.Length < 3) return false;

        char c = trim[0];
        if (c != '-' && c != '*' && c != '_') return false;

        int n = 0;
        foreach (char ch in trim)
        {
            if (ch == c) n++;
            else if (ch == ' ' || ch == '\t') continue;
            else return false;
        }
        return n >= 3;
    }

    private static bool TryListItem(string line, out bool ordered, out int number, out string text, out int indent)
    {
        ordered = false;
        number = 0;
        text = "";
        indent = 0;

        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i >= line.Length) return false;

        indent = Math.Min(i / 2, 4);

        // 无序：- / * / + 加一个空格
        if (i + 1 < line.Length && (line[i] == '-' || line[i] == '*' || line[i] == '+') && line[i + 1] == ' ')
        {
            string t = line.Substring(i + 2).Trim();
            if (t.Length == 0) return false;
            text = t;
            return true;
        }

        // 有序：数字 + . 或 ) 再加一个空格
        int j = i;
        while (j < line.Length && char.IsDigit(line[j])) j++;
        if (j > i && j + 1 < line.Length && (line[j] == '.' || line[j] == ')') && line[j + 1] == ' ')
        {
            string t = line.Substring(j + 2).Trim();
            if (t.Length == 0) return false;

            int.TryParse(line.Substring(i, j - i), out number);
            text = t;
            ordered = true;
            return true;
        }

        return false;
    }

    /// <summary>`_` 当强调符要站得住：得在行首或前面不是字母数字，否则 snake_case 会被吃掉。</summary>
    private static bool UnderscoreOk(string s, int i)
        => i == 0 || !char.IsLetterOrDigit(s[i - 1]);

    /// <summary>行内片段里第一个渲染出来的字；整行没有文字时返回 '\0'。</summary>
    private static char FirstTextChar(List<MdInline> inlines)
    {
        foreach (var inl in inlines)
            if (inl.Text.Length > 0) return inl.Text[0];
        return '\0';
    }

    /// <summary>行内片段里最后一个渲染出来的字；整行没有文字时返回 '\0'。</summary>
    private static char LastTextChar(List<MdInline> inlines)
    {
        for (int i = inlines.Count - 1; i >= 0; i--)
            if (inlines[i].Text.Length > 0) return inlines[i].Text[^1];
        return '\0';
    }

    /// <summary>
    /// 这个字是不是"可以直接和后一个字拼、不用补空格"的东亚文字。
    /// 判据是 Unicode 区段，不是语言：中日韩混排、全角标点都按同一套规矩走。
    /// </summary>
    private static bool IsCjk(char c)
        => (c >= '\u3000' && c <= '\u303F')     // CJK 标点
        || (c >= '\u3040' && c <= '\u30FF')     // 平假名 / 片假名
        || (c >= '\u3400' && c <= '\u4DBF')     // CJK 扩展 A
        || (c >= '\u4E00' && c <= '\u9FFF')     // CJK 基本区
        || (c >= '\uAC00' && c <= '\uD7AF')     // 谚文音节
        || (c >= '\uF900' && c <= '\uFAFF')     // CJK 兼容表意文字
        || (c >= '\uFF00' && c <= '\uFFEF');    // 全角字符
}
