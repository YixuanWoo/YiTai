// ============================================================
// ProjectStore.cs —— 项目的新建 / 打开 / 保存 / 导入源文件
// ============================================================
//
// 这是唯一改 project.json 的地方。界面拿到的永远是"数据 + 事件"，
// 不自己动文件。
//
// 方法边界：
//   CreateNew   建目录 + 空清单（不导入任何东西）
//   Open        读 project.json（自动修个别缺字段，不因小毛病打不开）
//   Save        原子写 project.json
//   ImportFiles 把外部文件复制进 sources\，登记进清单，按需建空译文
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiTai.Core;

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转 \uXXXX
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ProjectMeta Meta { get; private set; } = new();

    /// <summary>项目根目录（绝对）。</summary>
    public string Root { get; private set; } = "";

    public bool IsOpen => Root.Length > 0;

    // ---------------------------------------------------------------- 新建

    /// <summary>
    /// 在 <paramref name="root"/> 建一个新项目。
    /// 目录已存在且非空时**不覆盖**：只有里没有 project.json 才继续，
    /// 免得把别人已有的稿子盖了。
    /// </summary>
    public static ProjectStore CreateNew(string root, string name)
    {
        if (File.Exists(ProjectLayout.ProjectFile(root)))
            throw new InvalidOperationException("这个文件夹里已经有项目了：" + root);

        ProjectLayout.EnsureCreated(root);

        var store = new ProjectStore
        {
            Root = Path.GetFullPath(root),
            Meta = new ProjectMeta
            {
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(Path.GetFullPath(root)) : name.Trim(),
                CreatedUtc = DateTime.UtcNow,
            },
        };

        store.Save();
        return store;
    }

    // ---------------------------------------------------------------- 打开

    /// <summary>可以传 project.json，也可以直接传项目文件夹。</summary>
    public static ProjectStore Open(string pathOrDir)
    {
        string file = Directory.Exists(pathOrDir)
            ? ProjectLayout.ProjectFile(pathOrDir)
            : pathOrDir;

        if (!File.Exists(file))
            throw new FileNotFoundException("找不到项目文件：" + file);

        var meta = JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(file), JsonOptions)
                   ?? new ProjectMeta();

        // 缺字段兜底：旧文件读进来也能用
        meta.Sources ??= new List<SourceEntry>();
        foreach (var s in meta.Sources)
        {
            if (s.Id.Length == 0) s.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            s.FileName ??= "";
            s.TranslationPath ??= "";
            s.Note ??= "";
        }

        string root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        ProjectLayout.EnsureCreated(root);

        return new ProjectStore { Root = root, Meta = meta };
    }

    // ---------------------------------------------------------------- 保存

    public void Save()
    {
        if (!IsOpen) throw new InvalidOperationException("还没有打开项目。");

        string file = ProjectLayout.ProjectFile(Root);
        string tmp = file + ".tmp";

        string json = JsonSerializer.Serialize(Meta, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }

    // ---------------------------------------------------------------- 源文件

    /// <summary>源文件绝对路径。</summary>
    public string SourcePath(SourceEntry entry) =>
        Path.Combine(ProjectLayout.Sources(Root), entry.FileName);

    /// <summary>译文绝对路径（entry 里指定过就用它，否则用默认约定）。</summary>
    public string TranslationPath(SourceEntry entry)
    {
        string p = string.IsNullOrWhiteSpace(entry.TranslationPath)
            ? ProjectLayout.DefaultTranslationPath(Root, entry.FileName)
            : ProjectLayout.ToAbsolute(Root, entry.TranslationPath);

        return p;
    }

    /// <summary>改译文位置（「另存为」用），记成相对项目根的路径。</summary>
    public void SetTranslationPath(SourceEntry entry, string absolutePath)
    {
        string full = Path.GetFullPath(absolutePath);
        entry.TranslationPath = full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)
            ? ProjectLayout.ToRelative(Root, full)
            : full;   // 放到项目外面也允许，只是不再随项目一起搬走
        Save();
    }

    /// <summary>清单里是否已有同名文件（避免重复导入）。</summary>
    public bool HasSourceFile(string fileName) =>
        Meta.Sources.Exists(s => string.Equals(s.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 导入外部文件：复制到 sources\，登记进清单，并为它准备一份空译文
    /// （空译文只有标题行，打开就能写，不会出现"没有对应译稿"的状态）。
    /// </summary>
    public List<SourceEntry> ImportFiles(IEnumerable<string> paths, bool createTranslation = true)
    {
        var added = new List<SourceEntry>();

        // 归档区是按需建的（见 ProjectLayout.EnsureCreated 的注释）：
        // 真要把文件往里放，先确保目录在，否则 File.Copy 会抛 DirectoryNotFoundException。
        foreach (string p in paths)
            if (File.Exists(p)) { ProjectLayout.EnsureSources(Root); break; }

        foreach (string raw in paths)
        {
            if (!File.Exists(raw)) continue;

            string name = Path.GetFileName(raw);
            string dest = Path.Combine(ProjectLayout.Sources(Root), name);

            if (HasSourceFile(name))
            {
                // 同名已存在：不悄悄覆盖（那可能是用户改过的副本），改名后加 (2)
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                int n = 2;
                while (HasSourceFile($"{stem} ({n}){ext}") || File.Exists(Path.Combine(ProjectLayout.Sources(Root), $"{stem} ({n}){ext}")))
                    n++;
                name = $"{stem} ({n}){ext}";
                dest = Path.Combine(ProjectLayout.Sources(Root), name);
            }

            File.Copy(raw, dest, overwrite: false);

            var entry = new SourceEntry
            {
                FileName = name,
                OriginPath = Path.GetFullPath(raw),
                ImportedUtc = DateTime.UtcNow,
            };
            Meta.Sources.Add(entry);
            added.Add(entry);

            if (createTranslation)
            {
                string tr = TranslationPath(entry);
                if (!File.Exists(tr))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(tr)!);
                    string title = Path.GetFileNameWithoutExtension(name);
                    TextFile.WriteAtomic(tr, title + "\n", "\n", withBom: false);
                }
            }
        }

        if (added.Count > 0) Save();
        return added;
    }

    /// <summary>从项目里移除一个源文件条目（不删磁盘上的副本，只从清单里去掉）。</summary>
    public void RemoveEntry(SourceEntry entry)
    {
        Meta.Sources.Remove(entry);
        if (Meta.LastSourceId == entry.Id) Meta.LastSourceId = Meta.Sources.Count > 0 ? Meta.Sources[0].Id : null;
        Save();
    }

    /// <summary>
    /// 按"章号"批量关联已有译文（推荐用这个）。
    ///
    /// 为什么不用文件顺序：真实目录里常有 `第02章-二校建议.md` 这种附件，
    /// 一排序就把后面的全错开一位，而且错得看不出来。所以按名字里的数字配对：
    ///   ch01.md ↔ 第01章.md      （两边都取第一个数字）
    /// 同一个章号下有多个候选时（第02章.md 与 第02章-二校建议.md），
    /// 优先名字"干净"的那个（^第N章$ / ^chN$），仍分不出来就**跳过这一章**，不猜。
    ///
    /// 返回 (配上的条数, 因歧义跳过的章号)。只记路径，不复制文件。
    /// </summary>
    public (int Linked, List<int> Skipped) LinkTranslationsByNumber(string translationFolder)
    {
        var linked = 0;
        var skipped = new List<int>();
        if (!Directory.Exists(translationFolder)) return (0, skipped);

        // 收集译文：章号 → 候选文件
        var byNumber = new Dictionary<int, List<string>>();
        foreach (string f in Directory.GetFiles(translationFolder))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".md" or ".txt" or ".markdown")) continue;

            int n = ChapterNumber(Path.GetFileNameWithoutExtension(f));
            if (n <= 0) continue;

            if (!byNumber.TryGetValue(n, out var list)) { list = new List<string>(); byNumber[n] = list; }
            list.Add(f);
        }

        foreach (var entry in Meta.Sources)
        {
            int n = ChapterNumber(Path.GetFileNameWithoutExtension(entry.FileName));
            if (n <= 0 || !byNumber.TryGetValue(n, out var candidates)) continue;

            string? pick = null;
            if (candidates.Count == 1) pick = candidates[0];
            else
            {
                // 多个候选：只认"干净名字"，认不出就跳过，绝不按顺序凑
                foreach (var c in candidates)
                {
                    string stem = Path.GetFileNameWithoutExtension(c);
                    if (System.Text.RegularExpressions.Regex.IsMatch(stem, $@"^(第0*{n}章|ch0*{n})$"))
                    { pick = c; break; }
                }
                if (pick == null) { skipped.Add(n); continue; }
            }

            SetTranslationPathRaw(entry, pick);
            linked++;
        }

        if (linked > 0) Save();
        return (linked, skipped);
    }

    /// <summary>从文件名里取章号：ch01 / 第01章 / 01 / 第一章 都认，取不到返回 0。</summary>
    private static int ChapterNumber(string stem)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"\d+");
        if (m.Success && int.TryParse(m.Value, out int n)) return n;

        // 中文数字："第一章"（只支持 1..99，够用）
        var c = System.Text.RegularExpressions.Regex.Match(stem, @"第([一二三四五六七八九十]+)章");
        if (c.Success)
        {
            string s = c.Groups[1].Value;
            if (s == "十") return 10;
            if (s.Length == 1) return "零一二三四五六七八九".IndexOf(s);
            if (s.StartsWith('十')) return 10 + "零一二三四五六七八九".IndexOf(s[1]);
            if (s.EndsWith('十')) return "零一二三四五六七八九".IndexOf(s[0]) * 10;
            if (s.Length == 3 && s[1] == '十')
                return "零一二三四五六七八九".IndexOf(s[0]) * 10 + "零一二三四五六七八九".IndexOf(s[2]);
        }
        return 0;
    }

    /// <summary>
    /// 按"文件名顺序"批量关联（保底手段，仅在按章号配不上时用）。
    /// 只有两边数量相等才动 —— 数量不等说明配对不可靠，宁可不动。
    /// </summary>
    public int LinkTranslationsByOrder(string translationFolder)
    {
        if (!Directory.Exists(translationFolder)) return 0;

        var sources = new List<SourceEntry>(Meta.Sources);
        sources.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));

        var files = new List<string>();
        foreach (string f in Directory.GetFiles(translationFolder))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".md" or ".txt" or ".markdown") files.Add(f);
        }
        files.Sort(string.CompareOrdinal);

        if (sources.Count != files.Count) return 0;

        for (int i = 0; i < sources.Count; i++)
            SetTranslationPathRaw(sources[i], files[i]);

        Save();
        return sources.Count;
    }

    /// <summary>SetTranslationPath 的"不立即存盘"版本，供批量操作。</summary>
    private void SetTranslationPathRaw(SourceEntry entry, string absolutePath)
    {
        string full = Path.GetFullPath(absolutePath);
        entry.TranslationPath = full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)
            ? ProjectLayout.ToRelative(Root, full)
            : full;
    }

    // ================================================================ 译文（独立文档，不挂在源文件上）

    /// <summary>译文目录（绝对路径）。所有译文都落在这一处。</summary>
    public string TranslationsDir => ProjectLayout.Translations(Root);

    /// <summary>一篇译文的绝对路径。</summary>
    public string TranslationAbsPath(TranslationEntry entry) =>
        Path.Combine(TranslationsDir, entry.FileName);

    /// <summary>
    /// 让清单和磁盘对齐：translations\ 里每个 md/txt 都有一条，磁盘上没的条目就丢弃。
    /// 打开项目时调一次 —— 这样"用户在外面拿编辑器新建了一篇"也能立刻出现在下拉里。
    /// </summary>
    public List<TranslationEntry> SyncTranslations()
    {
        string dir = TranslationsDir;
        Directory.CreateDirectory(dir);

        var onDisk = new List<string>();
        foreach (string f in Directory.GetFiles(dir))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".md" or ".markdown" or ".txt") onDisk.Add(Path.GetFileName(f));
        }
        onDisk.Sort(StringComparer.OrdinalIgnoreCase);

        Meta.Translations ??= new List<TranslationEntry>();

        // 磁盘上没有的，从清单里去掉（文件被删/改名了，下拉就不该再留着它）
        Meta.Translations.RemoveAll(t =>
            !onDisk.Exists(n => string.Equals(n, t.FileName, StringComparison.OrdinalIgnoreCase)));

        // 磁盘上有、清单里没有的，补进来
        foreach (string n in onDisk)
        {
            if (!Meta.Translations.Exists(t => string.Equals(t.FileName, n, StringComparison.OrdinalIgnoreCase)))
            {
                var e = new TranslationEntry { FileName = n };
                Meta.Translations.Add(e);
            }
        }

        foreach (var t in Meta.Translations)
        {
            if (t.Id.Length == 0) t.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            t.FileName ??= "";
            t.Note ??= "";
        }

        // 顺序 = 文件名"自然序"（第01章 / 第02章 / … / 第10章，不是 1、10、2）
        Meta.Translations.Sort((a, b) => NaturalCompare(a.FileName, b.FileName));

        return Meta.Translations;
    }

    /// <summary>
    /// 文件名自然排序：数字段按数值比，其余按字符比。
    /// 为什么要它："第10章"按纯字典序会排到"第2章"前面，而下拉里第一项就是默认打开的，
    /// 排错了人一开软件看到的就不是第一章。
    /// </summary>
    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];

            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                long na = 0, nb = 0;
                while (i < a.Length && char.IsDigit(a[i])) { na = na * 10 + (a[i] - '0'); i++; }
                while (j < b.Length && char.IsDigit(b[j])) { nb = nb * 10 + (b[j] - '0'); j++; }
                if (na != nb) return na < nb ? -1 : 1;
                continue;
            }

            if (ca != cb) return ca < cb ? -1 : 1;
            i++; j++;
        }

        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return 0;
    }

    /// <summary>
    /// 新建一篇译文（空文件，落进 translations\）。名字重复就加 (2)。
    /// 这是"一个译文都没有时也能直接开始写"的入口 —— 不需要先有源文件。
    /// </summary>
    public TranslationEntry NewTranslation(string? name = null)
    {
        Directory.CreateDirectory(TranslationsDir);

        string stem = Path.GetFileNameWithoutExtension((name ?? "").Trim());
        if (stem.Length == 0) stem = "未命名";
        foreach (char bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '_');

        string fileName = stem + ".md";
        int n = 2;
        while (File.Exists(Path.Combine(TranslationsDir, fileName)))
            fileName = $"{stem} ({n++}).md";

        TextFile.WriteAtomic(Path.Combine(TranslationsDir, fileName), "", "\n", withBom: false);

        var entry = new TranslationEntry { FileName = fileName };
        var list = SyncTranslations();
        Meta.LastTranslationId = entry.Id;
        Save();

        // SyncTranslations 可能新建了同名的另一条，返回清单里那一条，保证 Id 一致
        foreach (var t in list)
            if (string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase)) return t;

        return entry;
    }

    /// <summary>
    /// 把一个已有的 md/txt 收进项目当译文（复制进 translations\，源文件不动）。
    /// 同名已存在就加 (2)，绝不悄悄覆盖 —— 那可能是他自己改过的一版。
    /// </summary>
    public TranslationEntry ImportTranslation(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到这个文件：" + sourcePath);

        Directory.CreateDirectory(TranslationsDir);

        string name = Path.GetFileName(sourcePath);
        string dest = Path.Combine(TranslationsDir, name);

        if (File.Exists(dest))
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            int n = 2;
            while (File.Exists(Path.Combine(TranslationsDir, $"{stem} ({n}){ext}"))) n++;
            name = $"{stem} ({n}){ext}";
            dest = Path.Combine(TranslationsDir, name);
        }

        File.Copy(sourcePath, dest, overwrite: false);

        SyncTranslations();
        Save();

        foreach (var t in Meta.Translations)
            if (string.Equals(t.FileName, name, StringComparison.OrdinalIgnoreCase)) return t;

        throw new InvalidOperationException("译文收进来了，但清单没记上：" + name);
    }

    /// <summary>给译文改个显示名（改的就是磁盘文件名，避免清单和文件各说各话）。</summary>
    public TranslationEntry RenameTranslation(TranslationEntry entry, string newName)
    {
        string stem = Path.GetFileNameWithoutExtension((newName ?? "").Trim());
        if (stem.Length == 0) throw new InvalidOperationException("名字不能是空的。");
        foreach (char bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '_');

        string ext = Path.GetExtension(entry.FileName);
        if (ext.Length == 0) ext = ".md";

        string target = stem + ext;
        string src = TranslationAbsPath(entry);
        string dst = Path.Combine(TranslationsDir, target);

        if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            string unique = target;
            int n = 2;
            while (File.Exists(Path.Combine(TranslationsDir, unique))) unique = $"{stem} ({n++}){ext}";
            File.Move(src, Path.Combine(TranslationsDir, unique));
            entry.FileName = unique;
        }

        SyncTranslations();
        Save();
        return entry;
    }

    /// <summary>清单里是否已有这个译文文件名。</summary>
    public bool HasTranslationFile(string fileName) =>
        Meta.Translations.Exists(t => string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase));
}
