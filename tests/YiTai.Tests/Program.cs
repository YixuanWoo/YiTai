// ============================================================
// Program.cs —— 核心逻辑冒烟测试（真实文件 + 临时沙箱）
// ============================================================
//
// 这个测试有两个目的：
//   1. 证明"剥注 / 切句"在真实原文上是对的（不是理论上的对）；
//   2. 证明"读 → 写 → 再读"能原样还回去（幂等），不会破坏已有译稿。
//
// ★ 所有写操作只发生在临时目录里。真实译稿全程只读。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YiTai.Core;

internal static class Program
{
    private static int _fail;

    /// <summary>真实项目根（只读用）。可用环境变量 YITAI_FIXTURE 覆盖。</summary>
    private static readonly string Fixture =
        Environment.GetEnvironmentVariable("YITAI_FIXTURE")
        ?? @"D:\Benny\20_项目\小王子翻译";

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("== 1. 剥注与切句（真实原文）==");
        TestParse("03");

        Console.WriteLine();
        Console.WriteLine("== 2. 全书 27 章扫描 ==");
        TestAllChapters();

        Console.WriteLine();
        Console.WriteLine("== 2b. 反向检查：有没有把两句粘成一句 ==");
        TestGlue();

        Console.WriteLine();
        Console.WriteLine("== 3. 译稿读写幂等（换行/BOM/段落）==");
        TestRoundTrip();

        Console.WriteLine();
        Console.WriteLine("== 4. 一章的完整闭环（沙箱）==");
        TestChapterLifecycle();

        Console.WriteLine();
        Console.WriteLine("== 5. 整篇原样写回（右边一整篇编辑区走的就是这条路径）==");
        TestRawSave();

        Console.WriteLine();
        Console.WriteLine("== 6. 文末卡片注（我早期章节的写法，第一版根本读不进来）==");
        TestCardNotes("01");
        TestCardNotes("02");

        Console.WriteLine();
        Console.WriteLine("== 7. 格式探测与其它切句口径 ==");
        TestProbe();
        TestOtherModes();

        Console.WriteLine();
        if (_fail == 0)
        {
            Console.WriteLine("全部通过 ✓");
            return 0;
        }

        Console.WriteLine($"失败 {_fail} 项 ✗");
        return 1;
    }

    // ---------------------------------------------------------------- 1

    private static void TestParse(string ch)
    {
        string path = Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", $"ch{ch}.md");
        var info = TextFile.Read(path);
        Check(info.Exists, $"原文存在：{path}");

        var parsed = SourceParser.Parse(info.Text);

        Check(parsed.Segments.Count >= 30, $"ch{ch} 切出 {parsed.Segments.Count} 句（应 ≥30）");
        Check(parsed.MergedFragments >= 0, $"并入碎片 {parsed.MergedFragments} 个");

        // 剥注必须干净：正文里不能残留锚点或粗体标记
        int residue = parsed.Segments.Count(s => s.En.Contains('〔') || s.En.Contains("**"));
        Check(residue == 0, $"正文无注残留（残留 {residue} 句）");

        // 纯标点碎片必须被并掉
        int junk = parsed.Segments.Count(s => s.En.Trim().Length <= 2 && !s.En.Any(char.IsLetter));
        Check(junk == 0, $"无纯标点碎片（{junk} 个）");

        // 真括号必须保留（这是正文，不是注）
        bool keptParen = parsed.Segments.Any(s => s.En.Contains("I shall not draw my airplane"));
        Check(keptParen, "正文里的真括号被保留（未被当成注吃掉）");

        // 注要被抓出来单独存，且不能污染正文
        int withNote = parsed.Segments.Count(s => s.Note.Length > 0);
        Check(withNote > 0, $"随文注已单独捕获（{withNote} 条）");
        Check(parsed.Segments.All(s => s.En.IndexOf('\u0001') < 0), "正文里没有残留占位标记");
        foreach (var s in parsed.Segments.Where(x => x.Note.Length > 0).Take(2))
            Console.WriteLine($"   注样本（挂在这句：{Trunc(s.En, 46)}）→ {Trunc(s.Note, 80)}");

        // 每条都以句末标点收尾
        int noEnd = parsed.Segments.Count(s =>
        {
            char c = s.En.TrimEnd().LastOrDefault();
            return c is not ('.' or '!' or '?' or '…' or '"' or '”' or '’' or ')');
        });
        Check(noEnd <= 1, $"句末标点完整（例外 {noEnd} 句）");

        // 复制给 AI 的文本必须干净：标点前不能挂空格
        int straySpace = parsed.Segments.Count(s =>
            System.Text.RegularExpressions.Regex.IsMatch(s.En, @"[ \t][.,;:!?]"));
        Check(straySpace == 0, $"标点前无游离空格（{straySpace} 句）");

        Console.WriteLine($"   前三句：");
        foreach (var s in parsed.Segments.Take(3))
            Console.WriteLine($"     · {Trunc(s.En, 78)}");
    }

    // ---------------------------------------------------------------- 2

    private static void TestAllChapters()
    {
        int total = 0;
        int residueTotal = 0;
        int failChapters = 0;

        for (int n = 1; n <= 27; n++)
        {
            string path = Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", $"ch{n:00}.md");
            if (!File.Exists(path)) { Console.WriteLine($"   ch{n:00} 缺失，跳过"); failChapters++; continue; }

            var parsed = SourceParser.Parse(TextFile.Read(path).Text);
            if (parsed.Segments.Count == 0) { Console.WriteLine($"   ch{n:00} 切不出句子 ✗"); failChapters++; continue; }

            total += parsed.Segments.Count;
            residueTotal += parsed.Segments.Count(s => s.En.Contains('〔') || s.En.Contains("**"));
            Console.WriteLine($"   ch{n:00}  {parsed.Segments.Count,3} 句  碎片并入 {parsed.MergedFragments,2}");
        }

        Check(failChapters == 0, $"27 章全部可切（失败 {failChapters}）");
        Check(residueTotal == 0, $"全书无注残留（{residueTotal} 处）");
        Console.WriteLine($"   合计 {total} 句");
    }

    // ---------------------------------------------------------------- 2b

    /// <summary>
    /// 切句只会做两件事：切得太碎（碎片）、切得太粗（把两句粘成一句）。
    /// 这一节专门查后者：一个行里如果还藏着「句末标点 + 空格 + 大写」，
    /// 说明那其实是两句。缩写（Mr. / St. / Mme.）会造成误报，所以只看比例。
    /// </summary>
    private static void TestGlue()
    {
        var glue = new System.Text.RegularExpressions.Regex(@"[.!?][""”’]?\s+[""“'A-Z]");

        int totalSeg = 0;
        int glued = 0;
        var samples = new List<string>();

        for (int n = 1; n <= 27; n++)
        {
            string path = Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", $"ch{n:00}.md");
            if (!File.Exists(path)) continue;

            var parsed = SourceParser.Parse(TextFile.Read(path).Text);
            totalSeg += parsed.Segments.Count;

            foreach (var s in parsed.Segments)
            {
                if (!glue.IsMatch(s.En)) continue;
                glued++;
                if (samples.Count < 6) samples.Add($"ch{n:00} · {Trunc(s.En, 90)}");
            }
        }

        double pct = totalSeg == 0 ? 0 : glued * 100.0 / totalSeg;
        Check(pct < 6.0, $"粘句比例 {pct:F1}%（{glued}/{totalSeg}，缩写会造成少量误报，应 <6%）");

        foreach (string s in samples) Console.WriteLine("     样本 " + s);
    }

    // ---------------------------------------------------------------- 3

    private static void TestRoundTrip()
    {
        foreach (string ch in new[] { "01", "02" })
        {
            string path = Path.Combine(Fixture, "04-译稿", $"第{ch}章.md");
            var info = TextFile.Read(path);
            Check(info.Exists, $"译稿存在：第{ch}章.md");

            var paras = MarkdownText.SplitParagraphs(info.Text);
            Check(paras.Count >= 2, $"第{ch}章 拆出 {paras.Count} 段（含章名）");

            string title = paras[0];
            var rows = new List<Segment>();
            foreach (string p in paras.Skip(1)) rows.Add(new Segment { Zh = p });

            string rebuilt = MarkdownText.Render(title, rows);

            // 还原后与原文比较（两边统一成 \n 并去掉尾部空白）
            string a = info.Text.Replace("\r\n", "\n").Trim();
            string b = rebuilt.Replace("\r\n", "\n").Trim();
            Check(a == b, $"第{ch}章 读写幂等（原 {a.Length} 字 / 还原 {b.Length} 字）");
            if (a != b) PrintFirstDiff(a, b);

            Console.WriteLine($"   第{ch}章 换行={(info.Eol == "\r\n" ? "CRLF" : "LF")} BOM={info.HasBom} 段={paras.Count}");
        }

        // 中文数字
        Check(MarkdownText.ChineseNumber(3) == "三", "3 → 三");
        Check(MarkdownText.ChineseNumber(10) == "十", "10 → 十");
        Check(MarkdownText.ChineseNumber(21) == "二十一", "21 → 二十一");
        Check(MarkdownText.ChineseNumber(27) == "二十七", "27 → 二十七");
        Check(MarkdownText.NormalizeTitle(null, 3) == "第三章", "新章默认名 = 第三章");
    }

    // ---------------------------------------------------------------- 4

    private static void TestChapterLifecycle()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "yitai-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string srcDir = Path.Combine(sandbox, "01-原文", "按章", "英文-带标注");
        string zhDir = Path.Combine(sandbox, "04-译稿");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(zhDir);

        File.Copy(Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", "ch03.md"),
                  Path.Combine(srcDir, "ch03.md"));

        var paths = new DeskPaths
        {
            ProjectRoot = sandbox,
            SourcePattern = @"01-原文\按章\英文-带标注\ch{nn}.md",
            TranslationPattern = @"04-译稿\第{nn}章.md",
            BackupPattern = @"03-工作文件\备份",
            StatePattern = @"03-工作文件\工作台状态",
        };
        var store = new ChapterStore(paths);
        var now = new DateTime(2026, 11, 10, 23, 0, 0);

        // 首次打开：空章
        var load1 = store.Load(3, now);
        Check(load1.State.Rows.Count > 0, $"首次打开得到 {load1.State.Rows.Count} 行");
        Check(load1.State.DoneCount == 0, "首次打开一行未译");
        Check(load1.State.Title == "第三章", $"章名自动生成：{load1.State.Title}");
        Check(!load1.ReadOnly, "空章不进入只读模式");

        // 写两句，保存
        load1.State.Rows[0].Zh = "我花了很长时间才弄懂他从哪儿来。";
        load1.State.Rows[1].Zh = "小王子问了我那么多问题，却好像从没听见我问他的话。";

        var save1 = store.Save(load1.State, now);
        Check(save1.Ok, "保存成功");
        string mdPath = Path.Combine(zhDir, "第03章.md");
        Check(File.Exists(mdPath), "译稿文件已生成");

        string mdText = File.ReadAllText(mdPath);
        Check(mdText.StartsWith("第三章", StringComparison.Ordinal), $"md 首段是章名：{Trunc(mdText, 40)}");
        Check(mdText.Contains("我花了很长时间才弄懂他从哪儿来。"), "中文已写入 md");
        Check(!mdText.Contains("〔"), "md 里没有残留锚点");

        // 行数远多于段落数：md 里必须只有 2 段正文
        var paras = MarkdownText.SplitParagraphs(mdText);
        Check(paras.Count == 3, $"md 段落数 = 章名 + 2 段正文（实际 {paras.Count}）");

        // 重新打开：中文必须原样回来（靠状态文件，因为段落数 ≠ 行数）
        var load2 = store.Load(3, now);
        Check(load2.State.Rows[0].Zh == "我花了很长时间才弄懂他从哪儿来。", "第 1 行中文已装回");
        Check(load2.State.DoneCount == 2, $"已完成 {load2.State.DoneCount} 行（应 2）");

        // 外部改动 → 冲突
        File.AppendAllText(mdPath, "\n外部加了一段。\n", Encoding.UTF8);
        var conflict = store.Save(load2.State, now);
        Check(conflict.Conflict, "外部改动被识别为冲突");
        Check(!conflict.Ok, "冲突时不落盘");

        // 强制覆盖
        var forced = store.Save(load2.State, now, force: true);
        Check(forced.Ok, "force=true 时覆盖成功");
        Check(forced.BackupPath != null || Directory.Exists(Path.Combine(sandbox, "03-工作文件", "备份")), "备份目录已建立");

        // 红线：全空时不能清空已有译稿
        var wiped = store.Load(3, now).State;
        foreach (var r in wiped.Rows) r.Zh = "";
        var refused = store.Save(wiped, now, force: true);
        Check(!refused.Ok, "全空保存被拒绝（保护已有译稿）");
        Check(File.ReadAllText(mdPath).Contains("我花了很长时间"), "拒绝后磁盘内容未被破坏");

        Console.WriteLine($"   沙箱：{sandbox}");
        try { Directory.Delete(sandbox, recursive: true); } catch { /* 留着也不影响 */ }
    }

    // ---------------------------------------------------------------- 5

    /// <summary>
    /// SaveRaw 是界面真正用的那条路：右边一整篇文本，原样写回译稿。
    /// 这里验三件事：换行符照原文件、内容一字不差、红线还在。
    /// </summary>
    private static void TestRawSave()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "yitai-raw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string srcDir = Path.Combine(sandbox, "01-原文", "按章", "英文-带标注");
        string zhDir = Path.Combine(sandbox, "04-译稿");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(zhDir);

        File.Copy(Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", "ch01.md"),
                  Path.Combine(srcDir, "ch01.md"));

        // 造一份 CRLF 的旧译稿（模仿第01章.md 的实际情况：CRLF、无 BOM、以换行结尾）
        string mdPath = Path.Combine(zhDir, "第01章.md");
        string original = "第一章\r\n\r\n旧的一段。\r\n";
        File.WriteAllText(mdPath, original, new UTF8Encoding(false));

        var paths = new DeskPaths
        {
            ProjectRoot = sandbox,
            SourcePattern = @"01-原文\按章\英文-带标注\ch{nn}.md",
            TranslationPattern = @"04-译稿\第{nn}章.md",
            BackupPattern = @"03-工作文件\备份",
            StatePattern = @"03-工作文件\工作台状态",
        };
        var store = new ChapterStore(paths);
        var now = new DateTime(2026, 11, 10, 23, 30, 0);

        var before = TextFile.Read(mdPath);
        Check(before.Eol == "\r\n", $"原文件是 CRLF（实测 {Trunc(before.Eol.Replace("\r", "CR").Replace("\n", "LF"), 8)}）");

        // 编辑区里用 \n 换行（WPF 给的是 \r\n，SaveRaw 两种都吃）
        string edited = "第一章\n\n新的一段。\n\n第二段，中间还有空行。\n";
        var saved = store.SaveRaw(1, edited, before.Sha, now);
        Check(saved.Ok, "整篇写回成功");

        string disk = File.ReadAllText(mdPath);
        Check(disk.Contains("\r\n"), "换行符仍是 CRLF（没被改成 LF）");
        Check(!disk.Contains("\n\n"), "文件里没有裸 LF（换行统一成 CRLF）");
        Check(MarkdownText.SplitParagraphs(disk).Count == 3, $"段落数 3（实际 {MarkdownText.SplitParagraphs(disk).Count}）");
        Check(disk.EndsWith("\r\n", StringComparison.Ordinal), "文件以换行结尾");

        // 再存一次同样的内容：指纹一致，不该报冲突
        var before2 = TextFile.Read(mdPath);
        var again = store.SaveRaw(1, edited, before2.Sha, now);
        Check(again.Ok, "同样的内容再存一次不报冲突");

        // 外部改动后再存：必须报冲突且不落盘
        File.AppendAllText(mdPath, "外部插的一段。\r\n", new UTF8Encoding(false));
        var stale = store.SaveRaw(1, edited, before2.Sha, now);
        Check(stale.Conflict, "外部改动被识别为冲突（整篇路径）");
        Check(File.ReadAllText(mdPath).Contains("外部插的一段"), "冲突时磁盘没被覆盖");

        // 红线：不许把已有译稿写空
        var emptied = store.SaveRaw(1, "  \n \n", TextFile.Read(mdPath).Sha, now, force: true);
        Check(!emptied.Ok, "空文本保存被拒绝");
        Check(File.ReadAllText(mdPath).Contains("外部插的一段"), "拒绝后磁盘内容没被清空");

        Console.WriteLine($"   沙箱：{sandbox}");
        try { Directory.Delete(sandbox, recursive: true); } catch { /* 留着也不影响 */ }
    }

    // ---------------------------------------------------------------- 6

    /// <summary>
    /// 早期章节是「正文 + --- + 文末〔n〕卡片」。
    /// 第一版对这份文件只做"截断丢弃"，等于注释全丢；现在必须真的读进来。
    /// </summary>
    private static void TestCardNotes(string ch)
    {
        string path = Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", $"ch{ch}.md");
        var info = TextFile.Read(path);
        if (!info.Exists) { Check(false, $"ch{ch} 原文存在"); return; }

        var profile = new SourceProfile { Notes = NoteStyle.TrailingCards };
        var parsed = SourceParser.Parse(info.Text, profile);

        int noted = 0;
        foreach (var s in parsed.Segments) if (s.Note.Length > 0) noted++;

        Check(parsed.Segments.Count >= 20, $"ch{ch} 切出 {parsed.Segments.Count} 句（应 ≥20）");
        Check(noted >= 3, $"ch{ch} 有 {noted} 句挂上了卡片注（应 ≥3）");

        bool anyAnchorLeft = false;
        foreach (var s in parsed.Segments)
        {
            if (s.En.Contains("〔") || s.En.Contains("---")) { anyAnchorLeft = true; break; }
        }
        Check(!anyAnchorLeft, $"ch{ch} 正文里没有残留的〔n〕锚点或分隔线");

        // 卡片区本身不能漏进正文
        bool cardTextLeaked = false;
        foreach (var s in parsed.Segments)
            if (s.En.Contains("译注：", StringComparison.Ordinal)) { cardTextLeaked = true; break; }
        Check(!cardTextLeaked, $"ch{ch} 卡片区没漏进正文");

        Console.WriteLine($"   ch{ch}: {parsed.Segments.Count} 句 / {noted} 句带注 / 标题「{Trunc(parsed.Title, 24)}」");
    }

    // ---------------------------------------------------------------- 7

    private static void TestProbe()
    {
        // ch01 是卡片格式，ch03 是内嵌格式：探测器必须分得出来
        string p1 = Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", "ch01.md");
        string p3 = Path.Combine(Fixture, "01-原文", "按章", "英文-带标注", "ch03.md");

        if (File.Exists(p1))
        {
            var g1 = SourceProbe.Guess(TextFile.Read(p1).Text);
            Check(g1.Profile.Notes == NoteStyle.TrailingCards, $"ch01 探测为卡片注（实际 {g1.Profile.Notes}）");
            Check(g1.SentenceCount > 0, $"ch01 探测后能切出 {g1.SentenceCount} 句");
        }

        if (File.Exists(p3))
        {
            var g3 = SourceProbe.Guess(TextFile.Read(p3).Text);
            Check(g3.Profile.Notes == NoteStyle.InlineBoldParen, $"ch03 探测为内嵌注（实际 {g3.Profile.Notes}）");
            Check(g3.NotedSentenceCount >= 10, $"ch03 有 {g3.NotedSentenceCount} 句带注");
            Check(g3.Profile.Split == SplitMode.LatinSentence, "ch03 按拉丁标点切句");
            Console.WriteLine($"   探测解释：{g3.Explain}");
        }
    }

    private static void TestOtherModes()
    {
        // 中文原文：按。！？切
        const string zh = "第一句。第二句！第三句？";
        var p = SourceParser.Parse(zh, new SourceProfile { Notes = NoteStyle.None, Split = SplitMode.CjkSentence });
        Check(p.Segments.Count == 3, $"中文切句得到 {p.Segments.Count} 句（应 3）");

        // 按段落：空行分段，不切句
        const string paras = "第一段第一句。第一段第二句。\n\n第二段只有一句。\n\n第三段。";
        var p2 = SourceParser.Parse(paras, new SourceProfile { Notes = NoteStyle.None, Split = SplitMode.Paragraph });
        Check(p2.Segments.Count == 3, $"按段落得到 {p2.Segments.Count} 段（应 3）");

        // 按行
        const string lines = "第一行\n第二行\n第三行";
        var p3 = SourceParser.Parse(lines, new SourceProfile { Notes = NoteStyle.None, Split = SplitMode.Line });
        Check(p3.Segments.Count == 3, $"按行得到 {p3.Segments.Count} 行（应 3）");

        // 脚注
        const string foot = "A sentence here.[^1]\n\nAnother sentence.[^2]\n\n[^1]: first note\n[^2]: second note";
        var p4 = SourceParser.Parse(foot, new SourceProfile { Notes = NoteStyle.Footnotes, Split = SplitMode.LatinSentence });
        int noted = 0;
        foreach (var s in p4.Segments) if (s.Note.Length > 0) noted++;
        Check(p4.Segments.Count == 2, $"脚注文本切出 {p4.Segments.Count} 句（应 2）");
        Check(noted == 2, $"脚注挂上了 {noted} 句（应 2）");
        Check(!p4.CleanBody.Contains("[^"), "正文里没有脚注引用残留");
    }

    // ---------------------------------------------------------------- 辅助

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + what);
        if (!ok) _fail++;
    }

    private static string Trunc(string s, int n)
    {
        s = s.Replace("\n", "⏎");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    private static void PrintFirstDiff(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 30);
        Console.WriteLine($"     首个差异位置 {i}");
        Console.WriteLine($"     原文 …{Trunc(a.Substring(from, Math.Min(70, a.Length - from)), 70)}");
        Console.WriteLine($"     还原 …{Trunc(b.Substring(from, Math.Min(70, b.Length - from)), 70)}");
    }
}
