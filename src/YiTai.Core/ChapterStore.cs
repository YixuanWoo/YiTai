// ============================================================
// ChapterStore.cs —— 一章的"打开 / 装填 / 回写"总调度
// ============================================================
//
// 分工：
//   SourceParser 只管把原文切成行（纯函数）
//   MarkdownText 只管 md 的段落拆合（纯函数）
//   TextFile     只管安全落盘（编码/换行/原子/备份）
//   本文件把它们串起来，并守住三条不破坏用户稿子的红线。
//
// 【三条红线】
//   1. 绝不把已有译稿清空：若内存里一行中文都没有、而磁盘上有内容 → 拒绝保存。
//   2. 绝不多写一个文件：整个程序唯一的写目标是「这一章的译稿 md」。
//   3. 绝不默默覆盖外部改动：保存前比对 md 的 sha，对不上就报冲突，让用户选。
//
// 【状态文件】
//   md 是"成品出口"（给排版流水线读），状态 json 才是"行 ↔ 段"的映射真源。
//   因为一旦你合并了两句，md 的段落数就少于行数 —— 光看 md 无法还原对应关系。
//   状态文件默认放在 `03-工作文件\工作台状态\chNN.json`，跟译稿分开，绝不混进正文。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace YiTai.Core;

/// <summary>目录与命名约定。开源版改这里就能适配别的书/别的项目结构。</summary>
public sealed class DeskPaths
{
    /// <summary>项目根目录（含原文与译稿两个目录）。</summary>
    public string ProjectRoot { get; set; } = ".";

    /// <summary>原文文件模式，支持 {nn}（两位补零）与 {n}。</summary>
    public string SourcePattern { get; set; } = @"01-原文\按章\英文-带标注\ch{nn}.md";

    /// <summary>译稿文件模式。</summary>
    public string TranslationPattern { get; set; } = @"04-译稿\第{nn}章.md";

    /// <summary>备份目录（每天首次保存前放一份上一版）。</summary>
    public string BackupPattern { get; set; } = @"03-工作文件\备份";

    /// <summary>状态目录（行↔段映射）。</summary>
    public string StatePattern { get; set; } = @"03-工作文件\工作台状态";

    public string SourcePath(int n) => Resolve(SourcePattern, n);

    public string TranslationPath(int n) => Resolve(TranslationPattern, n);

    public string BackupDir => ResolveDir(BackupPattern);

    public string StateDir => ResolveDir(StatePattern);

    public string StatePath(int n) => Path.Combine(StateDir, $"ch{n:00}.json");

    private string Resolve(string pattern, int n)
    {
        string rel = pattern.Replace("{nn}", n.ToString("00")).Replace("{n}", n.ToString());
        return Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(ProjectRoot, rel));
    }

    private string ResolveDir(string pattern)
    {
        return Path.GetFullPath(Path.IsPathRooted(pattern) ? pattern : Path.Combine(ProjectRoot, pattern));
    }
}

/// <summary>打开一章之后拿到的东西。</summary>
public sealed class LoadResult
{
    public ChapterState State { get; set; } = new();

    public ParsedSource Source { get; set; } = new();

    /// <summary>磁盘上的译稿（可能不存在）。</summary>
    public TextFileInfo Md { get; set; } = new();

    /// <summary>给人看的提示（警告），空 = 一切正常。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>是否"仅展示"：已有译稿与当前切分对不上，工作台不会自动拆解已完成章节。</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>保存的结果。</summary>
public sealed class SaveOutcome
{
    public bool Ok { get; set; }

    /// <summary>失败/拒绝的原因（中文，可直接弹给用户）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>true = 检测到磁盘被别人改过，需要用户决定。</summary>
    public bool Conflict { get; set; }

    public DateTime SavedAt { get; set; }

    /// <summary>写完后 md 的新指纹。</summary>
    public string NewSha { get; set; } = "";

    /// <summary>本次是否顺手做了每日备份，备份到哪。</summary>
    public string? BackupPath { get; set; }
}

public sealed class ChapterStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // 中文字符不转义成 \uXXXX，否则状态文件人眼不可读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public DeskPaths Paths { get; }

    public ChapterStore(DeskPaths paths) : this(paths, SourceProfile.Default) { }

    public ChapterStore(DeskPaths paths, SourceProfile profile)
    {
        Paths = paths;
        _profile = profile;
    }

    private readonly SourceProfile _profile;

    // ================================================================ 打开

    /// <summary>打开一章：读原文 → 切行 → 装填已有中文 → 报状态。</summary>
    public LoadResult Load(int number, DateTime now)
    {
        string srcPath = Paths.SourcePath(number);
        var srcInfo = TextFile.Read(srcPath);

        if (!srcInfo.Exists)
            throw new FileNotFoundException("找不到这一章的原文文件：" + srcPath, srcPath);

        var parsed = SourceParser.Parse(srcInfo.Text, _profile);
        if (parsed.Segments.Count == 0)
            throw new InvalidDataException("这一章的原文切不出任何句子（文件可能是空的或格式变了）：" + srcPath);

        var mdInfo = TextFile.Read(Paths.TranslationPath(number));
        var state = ReadState(number);

        var result = new LoadResult { Source = parsed, Md = mdInfo };

        // ---- 行：以最新切分为准
        var rows = new List<Segment>();
        foreach (var seg in parsed.Segments) rows.Add(seg.Clone());

        string title;
        var mdBody = new List<string>();
        if (mdInfo.Exists)
        {
            var paras = MarkdownText.SplitParagraphs(mdInfo.Text);
            if (paras.Count > 0)
            {
                title = MarkdownText.NormalizeTitle(paras[0], number);
                for (int i = 1; i < paras.Count; i++) mdBody.Add(paras[i]);
            }
            else title = MarkdownText.NormalizeTitle(null, number);
        }
        else
        {
            title = MarkdownText.NormalizeTitle(null, number);
        }

        // ---- 装填：优先信 md（用户的真实产物），其次信状态文件
        bool filledFromMd = false;

        if (mdBody.Count == rows.Count && mdBody.Count > 0)
        {
            for (int i = 0; i < rows.Count; i++) rows[i].Zh = mdBody[i];
            filledFromMd = true;
        }
        else if (state.Rows.Count == rows.Count && state.SourceSha == srcInfo.Sha)
        {
            for (int i = 0; i < rows.Count; i++) rows[i].Zh = state.Rows[i].Zh;
            filledFromMd = true;
        }

        if (!filledFromMd)
        {
            if (mdBody.Count > 0)
            {
                // 已有译稿，但段落数 ≠ 切分行数，且没有可用状态文件。
                // 坦白讲做不到自动还原对应关系 —— 不猜，直接只读展示，绝不碰这份稿。
                result.ReadOnly = true;
                result.Warnings.Add(
                    $"这一章已有译稿（{mdBody.Count} 段），但当前切分是 {rows.Count} 句，无法逐行对应。"
                    + "已切换为只读查看：工作台不会自动改写已完成的章节。");
            }
            else if (state.Rows.Count > 0 && state.Rows.Count != rows.Count)
            {
                result.Warnings.Add(
                    $"原文切分变了（上次 {state.Rows.Count} 行，这次 {rows.Count} 行），"
                    + "已按新切分重排；能按序号对上的中文已接回。");
                int n = Math.Min(state.Rows.Count, rows.Count);
                for (int i = 0; i < n; i++) rows[i].Zh = state.Rows[i].Zh;
            }
        }

        var st = new ChapterState
        {
            Number = number,
            Title = title,
            SourceSha = srcInfo.Sha,
            MdSha = mdInfo.Exists ? mdInfo.Sha : "",
            Rows = rows,
        };

        result.State = st;
        return result;
    }

    // ================================================================ 保存

    /// <summary>
    /// 回写这一章的译稿。force=true 表示用户已经在冲突弹窗里选了"覆盖磁盘版"。
    /// </summary>
    public SaveOutcome Save(ChapterState state, DateTime now, bool force = false)
    {
        string mdPath = Paths.TranslationPath(state.Number);
        var mdInfo = TextFile.Read(mdPath);

        // ---- 红线 1：不能把已有译稿写空
        if (state.DoneCount == 0 && mdInfo.Exists && mdInfo.Text.Trim().Length > 0)
        {
            return new SaveOutcome
            {
                Ok = false,
                Reason = "检测到这一章磁盘上已有译稿，而当前一行中文都没有 —— 为防误清空，已拒绝保存。",
            };
        }

        // ---- 红线 3：不能默默覆盖别人的改动
        if (!force && mdInfo.Exists && state.MdSha.Length > 0 && mdInfo.Sha != state.MdSha)
        {
            return new SaveOutcome
            {
                Ok = false,
                Conflict = true,
                Reason = "磁盘上的译稿被外部改过了（可能是编辑器或别的程序）。请选择：用磁盘版覆盖我 / 用我的覆盖磁盘。",
            };
        }

        // ---- 每日备份（放在真正改写之前）
        string? backup = TextFile.BackupOncePerDay(mdPath, Paths.BackupDir, now);

        // ---- 落盘
        string text = MarkdownText.Render(state.Title, state.Rows);
        string eol = mdInfo.Exists ? mdInfo.Eol : "\n";
        TextFile.WriteAtomic(mdPath, text, eol, mdInfo.HasBom, endsWithNewline: true);

        string newSha = TextFile.Sha16(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        state.MdSha = newSha;
        state.SourceSha = state.SourceSha;      // 保持原样
        WriteState(state);

        return new SaveOutcome
        {
            Ok = true,
            SavedAt = now,
            NewSha = newSha,
            BackupPath = backup,
        };
    }

    // ================================================================ 整篇回写

    /// <summary>
    /// 把编辑器里的整篇文本原样写回译稿。
    /// 右边是一整篇 Markdown，所见即所存 —— 不做任何"按段落重组"的加工，
    /// 所以你手写的空行、缩进、甚至临时批注都不会被程序改动。
    /// <paramref name="expectedSha"/> 是打开时磁盘的指纹，用来发现外部改动。
    /// </summary>
    public SaveOutcome SaveRaw(int number, string text, string expectedSha, DateTime now, bool force = false)
    {
        string mdPath = Paths.TranslationPath(number);
        var mdInfo = TextFile.Read(mdPath);

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 红线：不能把已有译稿写空
        if (normalized.Trim().Length == 0 && mdInfo.Exists && mdInfo.Text.Trim().Length > 0)
        {
            return new SaveOutcome
            {
                Ok = false,
                Reason = "编辑区是空的，而磁盘上这一章有内容 —— 为防误清空，已拒绝保存。",
            };
        }

        // 红线：不能默默覆盖别人的改动
        if (!force && mdInfo.Exists && expectedSha.Length > 0 && mdInfo.Sha != expectedSha)
        {
            return new SaveOutcome
            {
                Ok = false,
                Conflict = true,
                Reason = "磁盘上的译稿被外部改过了（可能是 MarkText、git 切分支或别的程序）。",
            };
        }

        string? backup = TextFile.BackupOncePerDay(mdPath, Paths.BackupDir, now);
        string eol = mdInfo.Exists ? mdInfo.Eol : "\n";

        TextFile.WriteAtomic(mdPath, normalized, eol, mdInfo.HasBom, endsWithNewline: true);

        return new SaveOutcome
        {
            Ok = true,
            SavedAt = now,
            NewSha = TextFile.Sha16(normalized),
            BackupPath = backup,
        };
    }

    // ================================================================ 目录扫描

    /// <summary>左侧目录要的一章简介（原文句数 / 已写字数 / 是否已有译稿）。</summary>
    public sealed class ChapterBrief
    {
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public int Total { get; set; }
        public int Chars { get; set; }
        public bool HasExisting { get; set; }
    }

    /// <summary>
    /// 扫一遍所有章（只读，不动任何文件）：每章原文切出多少句、译稿写了多少字。
    /// 27 个文件、每个几十 KB，同步扫完是几十毫秒级，不值得做缓存。
    /// </summary>
    public List<ChapterBrief> ScanAll(int count)
    {
        var list = new List<ChapterBrief>();

        for (int n = 1; n <= count; n++)
        {
            var brief = new ChapterBrief { Number = n };

            string src = Paths.SourcePath(n);
            if (File.Exists(src))
            {
                try { brief.Total = SourceParser.Parse(TextFile.Read(src).Text, _profile).Segments.Count; }
                catch { brief.Total = 0; }
            }

            var md = TextFile.Read(Paths.TranslationPath(n));
            var paras = md.Exists ? MarkdownText.SplitParagraphs(md.Text) : new List<string>();
            brief.HasExisting = paras.Count > 1;
            brief.Title = MarkdownText.NormalizeTitle(paras.Count > 0 ? paras[0] : null, n);
            brief.Chars = CountChars(md.Exists ? md.Text : "");

            list.Add(brief);
        }

        return list;
    }

    /// <summary>数中文/非空白字符（章名那行不算）。只用来给目录显示体量。</summary>
    public static int CountChars(string textLf)
    {
        if (textLf.Length == 0) return 0;

        var paras = MarkdownText.SplitParagraphs(textLf);
        int chars = 0;
        for (int i = 1; i < paras.Count; i++)     // 跳过第一段（章名）
        {
            foreach (char c in paras[i])
                if (!char.IsWhiteSpace(c)) chars++;
        }
        return chars;
    }

    // ================================================================ 状态文件

    public ChapterState ReadState(int number)
    {
        string path = Paths.StatePath(number);
        if (!File.Exists(path)) return new ChapterState { Number = number };

        try
        {
            string json = File.ReadAllText(path);
            var st = JsonSerializer.Deserialize<ChapterState>(json, Json);
            return st ?? new ChapterState { Number = number };
        }
        catch (Exception)
        {
            // 状态文件坏了不该让整章打不开 —— 退化成"没有状态"，由 md 装填
            return new ChapterState { Number = number };
        }
    }

    public void WriteState(ChapterState state)
    {
        Directory.CreateDirectory(Paths.StateDir);
        string json = JsonSerializer.Serialize(state, Json);
        TextFile.WriteAtomic(Paths.StatePath(state.Number), json, "\n", withBom: false, endsWithNewline: true);
    }
}
