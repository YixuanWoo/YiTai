// ============================================================
// PdfText.cs —— 从 PDF 里取出能读的文字
// ============================================================
//
// 只做一件事：把 PDF 的文字层按"段"捞出来，交给对照栏显示。
//
// 不做的：版面还原、表格、图片、双栏分栏、脚注编号的对位。
// 那些要么需要版面分析，要么在这软件里根本用不上 —— 这一栏是拿来
// 对着读的，不是拿来印书的。字号、栏宽、行距全由 YiTai 自己重排。
//
// 有没有文字层是关键：扫描件（一页一张图）读出来是空的，
// 这时要老实说"这是图片，读不出字"，而不是显示一片空白让人以为文件坏了。
//
// 用什么读：PdfPig（纯 .NET，Apache-2.0）。它是这个项目唯一的外部依赖，
// 因为手写 PDF 解析（xref / Flate / 字体编码 / ToUnicode CMap）在容错上
// 付出的代价远大于一个几 MB 的包。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using UglyToad.PdfPig.Content;

namespace YiTai.Core;

/// <summary>一次 PDF 读取的结果。Ok=false 时 Text 为空，Reason 说明为什么。</summary>
public sealed class PdfTextResult
{
    public bool Ok { get; init; }

    public string Text { get; init; } = "";

    /// <summary>读不出来时给人看的一句话。</summary>
    public string Reason { get; init; } = "";

    public int Pages { get; init; }

    public int WordCount { get; init; }
}

public static class PdfText
{
    /// <summary>读一份 PDF 的文字层。</summary>
    public static PdfTextResult Read(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new PdfTextResult { Ok = false, Reason = "文件不在" };

        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);

            int pages = doc.NumberOfPages;
            var text = new StringBuilder();
            int wordCount = 0;

            for (int i = 1; i <= pages; i++)
            {
                Page page = doc.GetPage(i);

                var words = page.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .ToList();

                wordCount += words.Count;
                if (words.Count == 0) continue;   // 这一页是图，跳过

                foreach (string para in BuildParagraphs(words))
                {
                    if (text.Length > 0) text.Append("\n\n");
                    text.Append(para);
                }
            }

            if (wordCount == 0)
            {
                return new PdfTextResult
                {
                    Ok = false,
                    Reason = pages > 0 ? "这份 PDF 是一页页图片（扫描件），没有文字层，读不出字" : "PDF 里没有页面",
                    Pages = pages,
                };
            }

            return new PdfTextResult
            {
                Ok = true,
                Text = text.ToString(),
                Pages = pages,
                WordCount = wordCount,
            };
        }
        catch (Exception ex)
        {
            // 加密、损坏、非 PDF：都归到"读不动"，把类型名带上便于判断
            return new PdfTextResult
            {
                Ok = false,
                Reason = "读不动这份 PDF（" + ex.GetType().Name + "）",
                Pages = 0,
            };
        }
    }

    /// <summary>把一页的词排成"段"。段与段之间空行，段内折行并成一句。</summary>
    private static List<string> BuildParagraphs(List<Word> words)
    {
        var result = new List<string>();

        // 1) 先分行：PDF 的 y 轴向上，所以 Bottom 大的在上面
        var sorted = words
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var rows = new List<List<Word>>();

        foreach (Word w in sorted)
        {
            double h = Math.Max(1.0, w.BoundingBox.Height);

            if (rows.Count > 0)
            {
                var last = rows[^1];
                double baseline = last[0].BoundingBox.Bottom;

                // 同一行的判据：基线差不到半个字高（上标、小字号不至于被拆成两行）
                if (Math.Abs(w.BoundingBox.Bottom - baseline) < h * 0.55)
                {
                    last.Add(w);
                    continue;
                }
            }

            rows.Add(new List<Word> { w });
        }

        foreach (var row in rows)
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));

        // 2) 再并段：行距明显比平时大 = 另起一段
        var para = new StringBuilder();
        double prevBottom = double.NaN;
        double prevHeight = 0;

        foreach (var row in rows)
        {
            string line = JoinRow(row);
            if (IsNoise(line)) continue;

            double bottom = row[0].BoundingBox.Bottom;
            double height = row.Max(w => w.BoundingBox.Height);

            if (para.Length > 0 && !double.IsNaN(prevBottom))
            {
                double lineGap = prevBottom - bottom;

                if (lineGap > Math.Max(prevHeight, height) * 1.7)
                {
                    result.Add(para.ToString());
                    para.Clear();
                }
            }

            if (para.Length > 0)
            {
                // 上一行末尾是排版加的连字符，这一行接着同一个词（lu- / cide = lucide）。
                // 这是 PDF 折行留下的，不接回去，满篇都是断词。
                if (para[^1] == '-' && char.IsLower(line[0]))
                    para.Length -= 1;        // 去掉连字符，直接粘
                else
                    para.Append(' ');        // 段内折行：并成一段
            }

            para.Append(line);

            prevBottom = bottom;
            prevHeight = height;
        }

        if (para.Length > 0) result.Add(para.ToString());

        return result;
    }

    /// <summary>一行之内把词接起来。西文之间补空格，中文之间不补。</summary>
    private static string JoinRow(List<Word> row)
    {
        var sb = new StringBuilder();
        Word? prev = null;

        foreach (Word w in row)
        {
            string t = w.Text.Trim();
            if (t.Length == 0) continue;

            if (prev != null && sb.Length > 0)
            {
                char before = sb[^1];
                char after = t[0];

                double gap = w.BoundingBox.Left - prev.BoundingBox.Right;

                // 两侧都是汉字/全角：PDF 里本来就不带空格，硬补会把「你好」写成「你 好」
                bool bothWide = IsWide(before) && IsWide(after);

                // 省音撇号：法语的 j'en / l'homme / d'abord，撇号两边本来就没有空格
                bool elided = IsApostrophe(before) || IsApostrophe(after);

                // 判词界只看间距：PDF 里词间那个空格是有宽度的，紧贴的（l’expérience、
                // word,）间距接近 0。按"是不是字母"来加空格，会把省音撇号全拆开。
                if (!bothWide && !elided && gap > 0.6)
                    sb.Append(' ');
            }

            sb.Append(t);
            prev = w;
        }

        return sb.ToString();
    }

    private static bool IsApostrophe(char c)
        => c is '\'' or '’' or '‘' or '´' or '‛' or '′';

    /// <summary>
    /// 该丢的行：页码、目录行。
    /// 目录行长这样：PREMIER CHAPITRE ........ 5 —— 一串点号收在页码上。
    /// 这些行放进正文只会占地方，读起来还得跳过。
    /// </summary>
    private static bool IsNoise(string line)
    {
        // 页码常被排成「— 6 —」「– 12 –」，先削掉这些装饰再看是不是孤零零一个数字
        string t = line.Trim().Trim('–', '—', '-', '·', '*', ' ', '\t');
        if (t.Length == 0) return true;

        // 单独一行只有页码
        if (t.Length <= 3 && t.All(char.IsDigit)) return true;

        // 一串点号 + 页码 = 目录
        if (t.Length >= 8 && char.IsDigit(t[^1]))
        {
            int dots = 0;
            foreach (char c in t)
                if (c == '.') dots++;

            if (dots >= 6) return true;
        }

        return false;
    }

    /// <summary>汉字、假名、全角标点（这些字符之间从来不加空格）。</summary>
    private static bool IsWide(char c)
    {
        return (c >= 0x3000 && c <= 0x303F)     // 中文标点
            || (c >= 0x3040 && c <= 0x30FF)     // 假名
            || (c >= 0x4E00 && c <= 0x9FFF)     // 汉字
            || (c >= 0xFF00 && c <= 0xFFEF)     // 全角字符
            || (c >= 0xAC00 && c <= 0xD7AF);    // 谚文
    }

    private static bool IsLatin(char c)
    {
        return (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9');
    }
}
