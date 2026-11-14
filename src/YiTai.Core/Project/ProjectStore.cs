// ============================================================
// ProjectStore.cs —— 项目的新建 / 打开 / 保存 / 收译文
// ============================================================
//
// 这是唯一改 project.json 的地方。界面拿到的永远是"数据 + 事件"，
// 不自己动文件。
//
// 方法边界：
//   CreateNew          建目录 + 空清单（不导入任何东西）
//   Open               读 project.json（自动修个别缺字段，不因小毛病打不开）
//   Save               原子写 project.json
//   SyncTranslations   让清单和 translations\ 目录对齐
//   NewTranslation     新建一篇空译文
//   ImportTranslation  把已有稿子复制进来当译文
//   RenameTranslation  改译文的文件名
//
// 这里曾经有一整套"把资料复制进 sources\、登记进清单、按章号配对译文"的旧链路
// （导入、移除、按顺序/按章号关联、给源文件改译文位置）。界面改成
// 「译文 + 若干只读资料栏」之后，那些方法已经没有调用方，连同
// `SourceEntry` 与 `TranslationPath` 一起删掉 —— 免得字段还在，
// 让后来的人以为"一份原文必须配一份译文"。
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

        // 旧项目里若还带着 "Sources" / "LastSourceId"，反序列化时会被忽略（不认识的键不报错），
        // 下次 Save 就不再写出来。
        var meta = JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(file), JsonOptions)
                   ?? new ProjectMeta();

        // 缺字段兜底：旧文件读进来也能用
        if (meta.Schema < 2) meta.Schema = 2;   // 旧格式（还带 Sources / TranslationPath）→ 标记成新格式
        meta.Translations ??= new List<TranslationEntry>();
        foreach (var t in meta.Translations)
        {
            if (t.Id.Length == 0) t.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            t.FileName ??= "";
            t.Note ??= "";
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

    // ================================================================ 译文（独立文档）

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
    /// 这是"一个译文都没有时也能直接开始写"的入口 —— 不需要先有资料文件。
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
