// ============================================================
// Program.cs —— 冒烟测试（不开窗口，只测 Core）
// ============================================================
//
// 测试的是"软件作为软件"该守住的那些规矩，而不是某个界面的样子：
//   1. 新建项目 → 目录齐全、project.json 可读
//   2. 导入源文件 → 复制进 sources、登记、自动建空译文
//   3. 同名再导入 → 不覆盖，改名为 (2)
//   4. 保存译文 → 换行/BOM 保持，空稿拒绝
//   5. 外部改动 → 抛冲突，不静默覆盖
//   6. 另存为 → 项目清单里的指向跟着改
//   7. 设置读写
//
// 退出码 0 = 全过，1 = 有失败。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using YiTai.Core;

int passed = 0, failed = 0;

void Check(string what, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine("  ✓ " + what); }
    else { failed++; Console.WriteLine("  ✗ " + what + (detail == null ? "" : "  —— " + detail)); }
}

string NewSandbox()
{
    string dir = Path.Combine(Path.GetTempPath(), "yitai-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(dir);
    return dir;
}

// 行内解析的小帮手：数某类行内片段有几个、把所有片段拼回纯文字
int CountKind(List<MdInline> list, MdInlineKind kind)
{
    int n = 0;
    foreach (var x in list) if (x.Kind == kind) n++;
    return n;
}

string Flat(List<MdInline> list)
{
    var sb = new StringBuilder();
    foreach (var x in list) sb.Append(x.Text);
    return sb.ToString();
}

// ============================================================ 1. 新建项目
Console.WriteLine("== 1. 新建项目 ==");
string box = NewSandbox();

// 测试的"设置读写"必须落在这里，不能碰用户真实的 %APPDATA%\YiTai\settings.json。
// 之前没隔离，每跑一次测试就把用户上次的项目、栏位记忆、最近文件整份覆盖成测试数据。
Environment.SetEnvironmentVariable("YITAI_CONFIG_DIR", Path.Combine(box, "config"));

string root = Path.Combine(box, "我的译本");
var store = ProjectStore.CreateNew(root, "我的译本");

Check("project.json 建出来了", File.Exists(ProjectLayout.ProjectFile(root)));
Check("sources 目录不预先建（要往里放东西时才建）", !Directory.Exists(ProjectLayout.Sources(root)));
Check("translations 目录存在", Directory.Exists(ProjectLayout.Translations(root)));
Check("backups 目录存在", Directory.Exists(ProjectLayout.Backups(root)));
Check("项目名记对了", store.Meta.Name == "我的译本", store.Meta.Name);

// 再建一次：必须拒绝，不能覆盖已有项目
bool refused = false;
try { ProjectStore.CreateNew(root, "重复"); } catch (InvalidOperationException) { refused = true; }
Check("同一文件夹不能建第二个项目", refused);

// ============================================================ 2. 收已有稿当译文
Console.WriteLine("\n== 2. 收已有稿当译文 ==");
string src1 = Path.Combine(box, "ch01.md");
string body = "# Chapter 1\n\nOnce when I was six years old I saw a magnificent picture.\n\nIt was a picture of a boa constrictor.\n";
File.WriteAllText(src1, body);

var added = store.ImportTranslation(src1);
Check("收进来后返回一条译文记录", !string.IsNullOrEmpty(added.FileName), added.FileName);
Check("文件复制进了 translations", File.Exists(ProjectLayout.Translations(root) + "\\ch01.md"));
Check("源文件还在原地（是复制不是搬走）", File.Exists(src1));
Check("清单里登记了", store.Meta.Translations.Count == 1);

string tr = store.TranslationAbsPath(added);
Check("译文就落在 translations\\ch01.md", tr.EndsWith(@"translations\ch01.md"), tr);
Check("内容原样复制的", File.ReadAllText(tr) == body);

// 同名再收一次：不能盖掉已有那份
var again = store.ImportTranslation(src1);
Check("同名不覆盖，改叫 ch01 (2).md", again.FileName == "ch01 (2).md", again.FileName);
Check("清单里变成两条", store.Meta.Translations.Count == 2);

// ============================================================ 3. 译文保存
Console.WriteLine("\n== 3. 译文保存 ==");
string tr1 = store.TranslationAbsPath(added);
File.WriteAllText(tr1, "第一章\r\n\r\n我六岁那年看到一张了不起的画。\r\n");   // 故意写成 CRLF，验证"照原样还回去"
var doc = TranslationStore.Load(tr1);
Check("读到的换行是 CRLF", doc.Eol == "\r\n", doc.Eol == "\n" ? "LF" : doc.Eol);

var saved = TranslationStore.Save(tr1, "第一章\n\n我六岁那年看到一张了不起的画。\n\n它画的是一条蟒蛇。\n",
                                  doc.Sha, ProjectLayout.Backups(root), DateTime.Now);
Check("保存后 sha 变了", saved.Sha != doc.Sha);
Check("换行仍保持 CRLF", saved.Eol == "\r\n");
Check("字数统计为正", saved.Chars > 0, saved.Chars.ToString());

byte[] raw = File.ReadAllBytes(tr1);
Check("没有 BOM（沿用源文件）", !(raw.Length >= 3 && raw[1] == 0xBB), "");
Check("落盘内容可回读", File.ReadAllText(tr1).Contains("蟒蛇"));

// 空稿：拒绝
bool emptyRefused = false;
try { TranslationStore.Save(tr1, "   \n\n", saved.Sha, ProjectLayout.Backups(root), DateTime.Now); }
catch (InvalidOperationException) { emptyRefused = true; }
Check("空稿拒绝落盘", emptyRefused);

// ============================================================ 4. 冲突
Console.WriteLine("\n== 4. 冲突检测 ==");
File.WriteAllText(tr1, "第一章\n\n别的编辑器改过的内容。\n");
bool conflict = false;
try
{
    TranslationStore.Save(tr1, "第一章\n\n我以为的版本。\n", saved.Sha,
                          ProjectLayout.Backups(root), DateTime.Now);
}
catch (TranslationConflictException) { conflict = true; }
Check("磁盘被改过时抛冲突", conflict);
Check("冲突时没有覆盖磁盘", File.ReadAllText(tr1).Contains("别的编辑器改过"));

// 备份
Check("备份目录里出现了今天的备份",
      Directory.GetFiles(ProjectLayout.Backups(root)).Length > 0,
      Directory.GetFiles(ProjectLayout.Backups(root)).Length.ToString());

// ============================================================ 5. 改译文名
Console.WriteLine("\n== 5. 改译文名 ==");
var renamed = store.RenameTranslation(added, "第01章-初稿");
Check("改完文件名跟着改", renamed.FileName == "第01章-初稿.md", renamed.FileName);
Check("磁盘上真改了", File.Exists(Path.Combine(ProjectLayout.Translations(root), "第01章-初稿.md")));
Check("老文件名不再存在", !File.Exists(Path.Combine(ProjectLayout.Translations(root), "ch01.md")));

var reopened = ProjectStore.Open(root);
TranslationEntry reEntry = reopened.Meta.Translations[0];
foreach (var t in reopened.Meta.Translations)
    if (t.FileName == "第01章-初稿.md") reEntry = t;
Check("重开项目后译文还在清单里", reEntry.FileName == "第01章-初稿.md", reEntry.FileName);
Check("重开后能定位到文件", File.Exists(reopened.TranslationAbsPath(reEntry)));

// ============================================================ 6. 新建译文 / 外部新增 / 自然排序
Console.WriteLine("\n== 6. 新建译文 / 外部新增 / 自然排序 ==");
var fresh = reopened.NewTranslation("第二章");
Check("新建的译文落地了", File.Exists(reopened.TranslationAbsPath(fresh)), fresh.FileName);
Check("空译文就是空文件", File.ReadAllText(reopened.TranslationAbsPath(fresh)).Length == 0);

var dup = reopened.NewTranslation("第二章");
Check("同名的自动加 (2)", dup.FileName == "第二章 (2).md", dup.FileName);

// 用户在外面用别的编辑器新建了一篇，SyncTranslations 要能收进来
File.WriteAllText(Path.Combine(ProjectLayout.Translations(reopened.Root), "第10章.md"), "十\n");
File.WriteAllText(Path.Combine(ProjectLayout.Translations(reopened.Root), "第2章.md"), "二\n");
var synced = reopened.SyncTranslations();
Check("外部新增的文件被收进清单", synced.Exists(t => t.FileName == "第10章.md"));

// 自然序：第2章 要排在 第10章 前面（纯字典序会反过来）
int idx2 = -1, idx10 = -1;
for (int k = 0; k < synced.Count; k++)
{
    if (synced[k].FileName == "第2章.md") idx2 = k;
    if (synced[k].FileName == "第10章.md") idx10 = k;
}
Check("自然序：第2章 在前、第10章 在后", idx2 >= 0 && idx10 >= 0 && idx2 < idx10, idx2 + "/" + idx10);

// ============================================================ 7. 设置
Console.WriteLine("\n== 7. 设置 ==");
var st = new AppSettings { LastProjectRoot = root, AutoSaveDelayMs = 1500 };
st.Save();
var st2 = AppSettings.Load();
Check("设置回读正确", st2.LastProjectRoot == root && st2.AutoSaveDelayMs == 1500, st2.LastProjectRoot);

// 老版本的 settings.json 里还有 "LastSourceId"（旧模型"上次选中的源文件"）。
// 字段删掉之后，这种旧文件必须照读不误 —— 不能因为多了个不认识的键就打不开设置。
File.WriteAllText(AppSettings.DefaultPath,
    "{\n  \"LastProjectRoot\": \"" + root.Replace("\\", "\\\\") + "\",\n  \"LastSourceId\": \"abc\",\n  \"AutoSaveDelayMs\": 1500\n}");
var stLegacy = AppSettings.Load();
Check("旧 settings.json 多出 LastSourceId 也照读",
      stLegacy.LastProjectRoot == root && stLegacy.AutoSaveDelayMs == 1500, stLegacy.LastProjectRoot);

// ============================================================ 9. 旧项目兼容（T-9 / T-10）
Console.WriteLine("\n== 9. 旧项目兼容（T-9 / T-10）==");

// 造一份"旧模型"的 project.json：带 Sources 清单、每个源文件带 TranslationPath、带 LastSourceId
string legacy = Path.Combine(box, "legacy");
Directory.CreateDirectory(legacy);
string legacyJson =
    "{\n" +
    "  \"Name\": \"旧项目\",\n" +
    "  \"Schema\": 1,\n" +
    "  \"Sources\": [\n" +
    "    { \"Id\": \"abc\", \"FileName\": \"ch01.md\", \"OriginPath\": \"C:\\\\旧\\\\ch01.md\"," +
    " \"ImportedUtc\": \"2026-01-01T00:00:00Z\", \"TranslationPath\": \"translations\\\\第01章.md\", \"Note\": \"\" }\n" +
    "  ],\n" +
    "  \"Translations\": [ { \"Id\": \"t1\", \"FileName\": \"第01章.md\", \"Note\": \"\" } ],\n" +
    "  \"LastTranslationId\": \"t1\",\n" +
    "  \"LastSourceId\": \"abc\"\n" +
    "}";
File.WriteAllText(ProjectLayout.ProjectFile(legacy), legacyJson);
Directory.CreateDirectory(ProjectLayout.Translations(legacy));
File.WriteAllText(Path.Combine(ProjectLayout.Translations(legacy), "第01章.md"), "第一章\n");

bool openedOk = true;
ProjectStore? legacyStore = null;
try { legacyStore = ProjectStore.Open(legacy); }
catch (Exception) { openedOk = false; }
Check("旧 project.json 照常打开，不报错", openedOk);
Check("旧文件里的译文清单一篇不少", legacyStore is not null && legacyStore.Meta.Translations.Count == 1,
      legacyStore?.Meta.Translations.Count.ToString());

// 存一次：旧字段必须从文件里消失
legacyStore!.Save();
string afterSave = File.ReadAllText(ProjectLayout.ProjectFile(legacy));
Check("保存后不再写 Sources", !afterSave.Contains("\"Sources\""));
Check("保存后不再写 TranslationPath", !afterSave.Contains("TranslationPath"));
Check("保存后不再写 LastSourceId", !afterSave.Contains("LastSourceId"));
Check("保存后 Schema 升到 2", afterSave.Contains("\"Schema\": 2"));

// 新写的 project.json 一开始就不该带这些键
string newProj = File.ReadAllText(ProjectLayout.ProjectFile(root));
Check("新项目的 project.json 里没有 Sources", !newProj.Contains("\"Sources\""));
Check("新项目的 project.json 里没有 TranslationPath", !newProj.Contains("TranslationPath"));


Console.WriteLine("\n== 8. 项目自带内容 ==");
// translations 里此时应有 6 份：第01章-初稿.md、ch01 (2).md、第二章.md、第二章 (2).md、第2章.md、第10章.md
Check("translations 里六份", Directory.GetFiles(ProjectLayout.Translations(root)).Length == 6,
      Directory.GetFiles(ProjectLayout.Translations(root)).Length.ToString());
Check("sources 目录没被建出来（没人往里放归档）", !Directory.Exists(ProjectLayout.Sources(root)));

// ============================================================ 10. 归档按需建 / 栏位折叠 / 文件读不了
Console.WriteLine("\n== 10. 归档按需建 / 栏位折叠 / 文件读不了 ==");

// T-4：从零建的项目不带空的 sources\；第一次真往里放东西时才建
string t4 = Path.Combine(box, "t4");
var s4 = ProjectStore.CreateNew(t4, "t4");
Check("新项目不预建 sources\\", !Directory.Exists(ProjectLayout.Sources(t4)));

// T-4：sources\ 是可选归档区，没人往里放东西就永远不建
string t4src = Path.Combine(box, "t4-src.md");
File.WriteAllText(t4src, "一个字\n");
Check("没人归档时 sources\\ 一直不存在", !Directory.Exists(ProjectLayout.Sources(t4)));
ProjectLayout.EnsureSources(t4);
File.Copy(t4src, Path.Combine(ProjectLayout.Sources(t4), "t4-src.md"));
Check("真往里放东西时 sources\\ 才建出来",
      File.Exists(Path.Combine(ProjectLayout.Sources(t4), "t4-src.md")));

// T-5：老 panes.json 带着已删除的 "Kind"，读的时候必须忽略，其余字段照旧
string t5 = Path.Combine(box, "t5");
ProjectStore.CreateNew(t5, "t5");
File.WriteAllText(ProjectLayout.PaneLayoutFile(t5),
    "{\n  \"Version\": 1,\n  \"Panes\": [\n" +
    "    { \"Kind\": 0, \"Path\": \"sources\\\\ch01.md\", \"Label\": \"ch01.md\", \"Zoom\": 1.0 }\n" +
    "  ],\n  \"Weights\": [ 1.0, 1.0 ]\n}");
var lay = PaneLayoutStore.Load(t5);
Check("旧 panes.json 的 Kind 被忽略、Path 照读",
      lay.Panes.Count == 1 && lay.Panes[0].Path == @"sources\ch01.md",
      lay.Panes.Count == 0 ? "一条都没读到" : lay.Panes[0].Path);

// T-19a：读不了 ≠ 崩。文件不在、被独占、空路径，全部收成状态返回
string gone = Path.Combine(box, "根本没有.md");
var goneInfo = TextFile.Read(gone);
Check("读不存在的文件不抛异常，返回 Missing",
      goneInfo.State == TextReadState.Missing && goneInfo.Problem.Length > 0,
      goneInfo.State.ToString());

var goneDoc = TranslationStore.Load(gone);
Check("缺失译文 Writable=false，且带着原因",
      !goneDoc.Writable && goneDoc.State == TextReadState.Missing && goneDoc.Problem.Length > 0);

string lockPath = Path.Combine(box, "被占着.md");
File.WriteAllText(lockPath, "内容\n");
Check("正常文件读得到", TextFile.Read(lockPath).State == TextReadState.Ok);

bool lockedOk;
using (var hold = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    var lockedInfo = TextFile.Read(lockPath);
    lockedOk = lockedInfo.State == TextReadState.Locked && lockedInfo.Text.Length == 0;
}
Check("文件被独占时返回 Locked 而不是抛异常", lockedOk);

Check("空路径返回 InvalidPath", TextFile.Read("").State == TextReadState.InvalidPath);

// ============================================================ 11. ==高亮== 的解析
Console.WriteLine("\n== 11. ==高亮== 的解析 ==");

MdInline? First(List<MdInline> list, MdInlineKind kind)
{
    foreach (var x in list) if (x.Kind == kind) return x;
    return null;
}

// 11.1 正常配对：两对 == 被吃掉，中间那段成为 Highlight，位置指在文字上
var hl = MdParser.ParseInlines("前 ==重点== 后");
Check("正常配对：一处高亮", CountKind(hl, MdInlineKind.Highlight) == 1);
Check("正常配对：内容是「重点」", First(hl, MdInlineKind.Highlight)?.Text == "重点");
Check("正常配对：== 被吃掉，纯文字原样", Flat(hl) == "前 重点 后", Flat(hl));
Check("正常配对：SourceOffset 落在文字上（=2 是那对 ==，=4 才是字）",
      First(hl, MdInlineKind.Highlight)?.SourceOffset == 4,
      First(hl, MdInlineKind.Highlight)?.SourceOffset.ToString());

// 11.2 同一行两处高亮，各算各的
var hl2 = MdParser.ParseInlines("==一== 和 ==二==");
Check("同一行两处高亮", CountKind(hl2, MdInlineKind.Highlight) == 2);
Check("两处内容各归各", Flat(hl2) == "一 和 二", Flat(hl2));

// 11.3 未闭合：当普通文字收着，绝不吞掉 == 和后面的正文
var open = MdParser.ParseInlines("这是 ==没闭合");
Check("未闭合：不产生高亮", CountKind(open, MdInlineKind.Highlight) == 0);
Check("未闭合：== 原样保留", Flat(open) == "这是 ==没闭合", Flat(open));

// 11.4 跨行不配对：行内解析是一行一行来的，上一行开的 == 不会吃掉下一行
var crossDoc = MdParser.Parse("第一行 == 开头\n第二行 结尾 ==\n");
Check("跨行：并成一段", crossDoc.Blocks.Count == 1 && crossDoc.Blocks[0].Kind == MdBlockKind.Paragraph,
      crossDoc.Blocks.Count.ToString());
Check("跨行：不产生高亮", CountKind(crossDoc.Blocks[0].Inlines, MdInlineKind.Highlight) == 0);
Check("跨行：两对 == 都原样留着", Flat(crossDoc.Blocks[0].Inlines) == "第一行 == 开头 第二行 结尾 ==",
      Flat(crossDoc.Blocks[0].Inlines));

// 11.5 与粗体嵌套：先认到谁，就只解析那一层（当前行为，不做内层二次解析）
var nest1 = MdParser.ParseInlines("**==粗==**");
Check("嵌套一 **==x==**：只有粗体", CountKind(nest1, MdInlineKind.Bold) == 1 && CountKind(nest1, MdInlineKind.Highlight) == 0);
Check("嵌套一：高亮标记被当成粗体里的字", First(nest1, MdInlineKind.Bold)?.Text == "==粗==");

var nest2 = MdParser.ParseInlines("==**粗**==");
Check("嵌套二 ==**x**==：只有高亮", CountKind(nest2, MdInlineKind.Highlight) == 1 && CountKind(nest2, MdInlineKind.Bold) == 0);
Check("嵌套二：粗体标记被当成高亮里的字", First(nest2, MdInlineKind.Highlight)?.Text == "**粗**");

// 11.6 空标记 ====：里面一个字都没有，不当高亮（否则等于吃掉 4 个字符）
var empty = MdParser.ParseInlines("====");
Check("==== 不产生高亮", CountKind(empty, MdInlineKind.Highlight) == 0);
Check("==== 原样保留", Flat(empty) == "====", Flat(empty));

// 11.7 行内代码里的 == 不当高亮（代码优先）
var codeHl = MdParser.ParseInlines("`==a==`");
Check("代码里的 == 不解析成高亮",
      CountKind(codeHl, MdInlineKind.Code) == 1 && CountKind(codeHl, MdInlineKind.Highlight) == 0);

// 11.8 baseOffset：段落/引用里传进来的起点要叠加（绿线定位靠它）
var off = MdParser.ParseInlines("==x==", 10);
Check("baseOffset 叠加正确", First(off, MdInlineKind.Highlight)?.SourceOffset == 12,
      First(off, MdInlineKind.Highlight)?.SourceOffset.ToString());

// 11.9 转义：\== 就是两个普通等号，不配成高亮
var esc = MdParser.ParseInlines("\\==");
Check("转义后不当高亮", CountKind(esc, MdInlineKind.Highlight) == 0 && Flat(esc) == "==", Flat(esc));

// ============================================================ 12. 进度键绑文件路径（T-11）
Console.WriteLine("\n== 12. 进度键绑文件路径（T-11）==");

string p11 = Path.Combine(box, "进度");
ProjectStore.CreateNew(p11, "进度");
string dirA = Path.Combine(p11, "01-原文", "英文-带标注");
string dirB = Path.Combine(p11, "01-原文", "英文-纯文本");
Directory.CreateDirectory(dirA);
Directory.CreateDirectory(dirB);
File.WriteAllText(Path.Combine(dirA, "ch03.md"), "第一份\n");
File.WriteAllText(Path.Combine(dirB, "ch03.md"), "第二份\n");
string absA = Path.Combine(dirA, "ch03.md");
string absB = Path.Combine(dirB, "ch03.md");

string keyA = ProgressStore.KeyFor(p11, absA);
string keyB = ProgressStore.KeyFor(p11, absB);
Check("同一文件名、不同目录 → 两个不同的键", keyA != keyB, keyA + " / " + keyB);
Check("项目内文件用 rel: 前缀", keyA.StartsWith("rel:"), keyA);
Check("同一个路径永远同一个键", ProgressStore.KeyFor(p11, absA) == keyA);

// 项目外的文件：只能用绝对路径
string outside = Path.Combine(box, "项目外", "ch03.md");
Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
File.WriteAllText(outside, "外面的\n");
string keyOut = ProgressStore.KeyFor(p11, outside);
Check("项目外文件用 abs: 前缀", keyOut.StartsWith("abs:"), keyOut);

// 两份同名文件的进度互不干扰
var st11 = new ProgressState();
ProgressStore.Set(p11, st11, keyA, 4297);
ProgressStore.Set(p11, st11, keyB, 88);
Check("A 的线还是 4297", ProgressStore.Get(st11, keyA) == 4297, ProgressStore.Get(st11, keyA).ToString());
Check("B 的线是 88，没被 A 影响", ProgressStore.Get(st11, keyB) == 88, ProgressStore.Get(st11, keyB).ToString());

// 键 → 绝对路径，能反推回来
Check("rel: 键能还原成绝对路径",
      string.Equals(Path.GetFullPath(ProgressStore.ToAbsolutePath(p11, keyA)), Path.GetFullPath(absA),
                    StringComparison.OrdinalIgnoreCase));
Check("旧裸文件名认不出位置", ProgressStore.ToAbsolutePath(p11, "ch03.md") == "");

// 迁移一：唯一对得上 → 搬到新键，旧键删掉，偏移不变
var old11 = new ProgressState { Version = 1 };
old11.Files["ch03.md"] = 4297;
File.WriteAllText(ProjectLayout.ProgressFile(p11),
    "{\n  \"Version\": 1,\n  \"Files\": {\n    \"ch03.md\": 4297\n  }\n}");
var (mig, held) = ProgressStore.Migrate(p11, old11, new[] { absA });
Check("唯一对得上就搬 1 条", mig == 1 && held == 0, mig + "/" + held);
Check("新键下偏移没变", ProgressStore.Get(old11, keyA) == 4297);
Check("旧裸键已清掉", !old11.Files.ContainsKey("ch03.md"));
Check("迁移前留了一份 progress.json.bak", File.Exists(ProjectLayout.ProgressFile(p11) + ".bak"));
Check("落盘的也是新键", File.ReadAllText(ProjectLayout.ProgressFile(p11)).Contains("rel:"));

// 迁移二：同名两份都在"当前打开的栏位"里 → 拿不准，原样留着不动
var ambiguous = new ProgressState { Version = 1 };
ambiguous.Files["ch03.md"] = 4297;
var (mig2, held2) = ProgressStore.Migrate(p11, ambiguous, new[] { absA, absB });
Check("同名两份分不清 → 一条都不搬", mig2 == 0 && held2 == 1, mig2 + "/" + held2);
Check("拿不准时旧键原样留着", ambiguous.Files.ContainsKey("ch03.md") && ambiguous.Files["ch03.md"] == 4297);

// 迁移二·b：打开项目时没摆的栏 → 留着旧键，等这份文件被打开时现场认领
var adopt = new ProgressState { Version = 1 };
adopt.Files["ch03.md"] = 77;
var (migNo, heldNo) = ProgressStore.Migrate(p11, adopt, Array.Empty<string>());
Check("没打开的栏不瞎搬", migNo == 0 && heldNo == 1, migNo + "/" + heldNo);
bool adopted = ProgressStore.AdoptLegacy(p11, adopt, keyA, "ch03.md");
Check("打开这份文件时把旧键认领过来", adopted && ProgressStore.Get(adopt, keyA) == 77,
      ProgressStore.Get(adopt, keyA).ToString());
Check("认领后旧裸键没了", !adopt.Files.ContainsKey("ch03.md"));
Check("认领结果落了盘", File.ReadAllText(ProjectLayout.ProgressFile(p11)).Contains("rel:"));

// 迁移三：一份都对不上（文件已不在）→ 也留着，不猜
var orphan = new ProgressState { Version = 1 };
orphan.Files["没见过.md"] = 100;
var (mig3, held3) = ProgressStore.Migrate(p11, orphan, new[] { absA, absB });
Check("对不上任何文件时不动它", mig3 == 0 && held3 == 1, mig3 + "/" + held3);

// ============================================================ 13. 表格（GFM 管道表）
Console.WriteLine("\n== 13. 表格（GFM 管道表） ==");

// 13.1 基本表：表头 + 分隔行 + 两行数据
var md1 = MdParser.Parse("| 词 | 译法 |\n| --- | --- |\n| apple | 苹果 |\n| book | 书 |\n");
Check("基本表：1 个表格块", md1.Blocks.Count == 1 && md1.Blocks[0].Kind == MdBlockKind.Table,
      md1.Blocks.Count.ToString() + "/" + md1.Blocks[0].Kind);
var mt1 = md1.Blocks[0].Table!;
Check("基本表：2 列", mt1.Aligns.Count == 2);
Check("基本表：3 行（表头 + 2 数据）", mt1.Rows.Count == 3);
Check("基本表：表头行 IsHeader", mt1.Rows[0].IsHeader);
Check("基本表：数据行不是表头", !mt1.Rows[1].IsHeader);
Check("基本表：单元格内容正确", Flat(mt1.Rows[1].Cells[0].Inlines) == "apple");

// 13.2 对齐：:--- 左、:--: 中、---: 右
var md2 = MdParser.Parse("| a | b | c |\n| :-- | :--: | --: |\n| 1 | 2 | 3 |\n");
var mt2 = md2.Blocks[0].Table!;
Check("对齐：左中右", mt2.Aligns[0] == MdColumnAlign.Left && mt2.Aligns[1] == MdColumnAlign.Center
      && mt2.Aligns[2] == MdColumnAlign.Right);

// 13.3 没有分隔行 → 不是表格，是普通段落
var md3 = MdParser.Parse("| a | b |\n| c | d |\n");
Check("无分隔行：不认成表格", md3.Blocks.Count == 1 && md3.Blocks[0].Kind == MdBlockKind.Paragraph,
      md3.Blocks[0].Kind.ToString());

// 13.4 单列表格（表头、分隔、数据都带竖线）照认
var md4 = MdParser.Parse("| foo |\n| --- |\n| x |\n");
Check("单列表格：1 列", md4.Blocks[0].Kind == MdBlockKind.Table && md4.Blocks[0].Table!.Aligns.Count == 1,
      md4.Blocks[0].Kind.ToString());

// 13.5 转义的竖线不算分隔
var md5 = MdParser.Parse("| a\\|b | c |\n| --- | --- |\n| 1 | 2 |\n");
var mt5 = md5.Blocks[0].Table!;
Check("转义竖线：第一格内容含竖线", Flat(mt5.Rows[0].Cells[0].Inlines) == "a|b",
      Flat(mt5.Rows[0].Cells[0].Inlines));

// 13.6 列数不齐：表头多出的丢弃，数据行缺的补空
var md6 = MdParser.Parse("| a | b | c |\n| --- | --- |\n| 1 |\n");
var mt6 = md6.Blocks[0].Table!;
Check("列数：按分隔行 2 列", mt6.Aligns.Count == 2);
Check("表头多出的列被丢", Flat(mt6.Rows[0].Cells[1].Inlines) == "b");
Check("数据行缺的列补空", mt6.Rows[1].Cells[1].Inlines.Count == 0);

// 13.7 单元格里的粗体 / ==高亮== 照常解析
var md7 = MdParser.Parse("| a | b |\n| --- | --- |\n| **粗** | ==亮== |\n");
var mt7 = md7.Blocks[0].Table!;
Check("单元格粗体", CountKind(mt7.Rows[1].Cells[0].Inlines, MdInlineKind.Bold) == 1);
Check("单元格高亮", CountKind(mt7.Rows[1].Cells[1].Inlines, MdInlineKind.Highlight) == 1);

// 13.8 表格后跟普通段落：表格结束于空行
var md8 = MdParser.Parse("| a |\n| --- |\n| 1 |\n\n后面的段落\n");
Check("表格后接段落：两块", md8.Blocks.Count == 2);
Check("第二块是段落", md8.Blocks[1].Kind == MdBlockKind.Paragraph);

// ============================================================
// 14. 句子切分（进度高亮用）
// ============================================================
Console.WriteLine();
Console.WriteLine("== 14. 句子切分 ==");

// 14.1 基本：三句
var si1 = SentenceIndex.FromPlain("One. Two! Three? ");
Check("基本：切成 3 句", si1.Count == 3, si1.Count.ToString());
Check("基本：第一句是 One.", si1.Count > 0 && "One. Two! Three? ".Substring(si1.At(0).Start, si1.At(0).Length) == "One.");
Check("基本：第三句是 Three?", si1.Count > 2 && "One. Two! Three? ".Substring(si1.At(2).Start, si1.At(2).Length) == "Three?");

// 14.2 Mr. / Dr. 不切
var si2 = SentenceIndex.FromPlain("Mr. Smith went home. He slept.");
Check("缩写：Mr. 不切", si2.Count == 2, si2.Count.ToString());
Check("缩写：第二句从 He 起", si2.Count > 1 && "Mr. Smith went home. He slept.".Substring(si2.At(1).Start).StartsWith("He"));

// 14.3 e.g. / U.S. 不切
var si3 = SentenceIndex.FromPlain("Use e.g. this one. The U.S. Army came.");
Check("e.g. 不切", si3.Count == 2, si3.Count.ToString());
Check("U.S. 不切", si3.At(1).Start > 0 && "Use e.g. this one. The U.S. Army came.".Substring(si3.At(1).Start).StartsWith("The U.S."), si3.Count.ToString());

// 14.4 小数点不切
var si4 = SentenceIndex.FromPlain("It is 3.5 meters. Fine.");
Check("小数点：不切 3.5", si4.Count == 2, si4.Count.ToString());

// 14.5 省略号算一个句末
var si5 = SentenceIndex.FromPlain("Wait... then go. Done.");
Check("省略号：切 3 句", si5.Count == 3, si5.Count.ToString());
Check("省略号：第一句到 ... 为止", si5.Count > 0 && "Wait... then go. Done.".Substring(si5.At(0).Start, si5.At(0).Length) == "Wait...");
var si5b = SentenceIndex.FromPlain("等等……然后走。完了。");
Check("省略号：中文 3 句", si5b.Count == 3, si5b.Count.ToString());

// 14.6 收尾引号归上一句
var si6 = SentenceIndex.FromPlain("He said \"Hi!\" Then left.");
Check("引号：切 2 句", si6.Count == 2, si6.Count.ToString());
Check("引号：问号留在上一句", si6.Count > 0 && "He said \"Hi!\" Then left.".Substring(si6.At(0).Start, si6.At(0).Length) == "He said \"Hi!\"");

// 14.7 中文标点
var si7 = SentenceIndex.FromPlain("你好。世界！再见？");
Check("中文：3 句", si7.Count == 3, si7.Count.ToString());

// 14.8 空行硬边界
var si8 = SentenceIndex.FromPlain("abc\n\ndef");
Check("空行：2 句", si8.Count == 2, si8.Count.ToString());

// 14.9 标题行行尾就是边界
var si9 = SentenceIndex.FromPlain("# Title\nBody text.");
Check("标题：2 句", si9.Count == 2, si9.Count.ToString());

// 14.10 代码块整块一句
var si10 = SentenceIndex.FromPlain("```\na. b. c.\n```\nDone.");
Check("代码块：2 句（整块 + Done）", si10.Count == 2, si10.Count.ToString());

// 14.11 吸附与前后查找
var si11 = SentenceIndex.FromPlain("One. Two. Three.");
Check("吸附：句中偏移落到句首", si11.StartOf(2) == si11.At(0).Start);
Check("吸附：第二句内偏移", si11.StartOf(si11.At(1).Start + 1) == si11.At(1).Start);
Check("下一句", si11.NextStart(0) == si11.At(1).Start);
Check("上一句", si11.PrevStart(si11.At(2).Start) == si11.At(1).Start);
Check("最后一句没有下一句", si11.NextStart(si11.At(2).Start) == -1);
Check("第一句没有上一句（回 0）", si11.PrevStart(0) == si11.At(0).Start);

// 14.12 空文本
var si12 = SentenceIndex.FromPlain("");
Check("空文本：0 句", si12.Count == 0, si12.Count.ToString());

// 14.13 用现成区间建索引（界面在渲染结果上切句时走这条）
var si13 = SentenceIndex.FromSpans(new[] { (10, 20), (30, 40) });
Check("FromSpans：2 句", si13.Count == 2, si13.Count.ToString());
Check("FromSpans：IndexAt(12) → 第 1 句", si13.IndexAt(12) == 0);
Check("FromSpans：NextStart(12) → 30", si13.NextStart(12) == 30);
Check("FromSpans：StartOf(35) → 30", si13.StartOf(35) == 30);

// ============================================================
// 15. 主题里的滚动条口径（表格横滚的两次翻车点，钉成回归测试）
// ============================================================
// 为什么测这个：表格横向滚动条被报过两次——
//   ① 只有 9px 长的一小截（全局 Width=9 是给竖向用的，横向沿用了）
//   ② 鼠标始终是 I 形光标，点不住滑块（样式没设 Cursor，继承了 RichTextBox 的文本光标）
// 这两条都在 Theme.xaml 里，是纯静态数据，所以可以直接读文件钉住。
Console.WriteLine();
Console.WriteLine("== 15. 滚动条样式（表格横滚） ==");

string? themePath = FindUp("src\\YiTai.App\\Theme.xaml");
Check("Theme.xaml 找得到", themePath != null, themePath ?? "(没找到，测试可能不在仓库根跑)");

if (themePath != null)
{
    var themeRoot = System.Xml.Linq.XDocument.Load(themePath).Root!;
    var ns = themeRoot.Name.Namespace;

    var sbStyle = themeRoot.Elements(ns + "Style")
        .FirstOrDefault(s => (string?)s.Attribute("TargetType") == "ScrollBar");

    Check("存在全局 ScrollBar 样式", sbStyle != null);

    if (sbStyle != null)
    {
        var setters = sbStyle.Elements(ns + "Setter").ToList();

        var cursor = setters.FirstOrDefault(s => (string?)s.Attribute("Property") == "Cursor");
        Check("滚动条光标是箭头（不是继承来的 I 形）",
              cursor != null && (string?)cursor.Attribute("Value") == "Arrow",
              cursor == null ? "(没设 Cursor)" : (string?)cursor.Attribute("Value"));

        var styleTrig = sbStyle.Elements(ns + "Style.Triggers")
            .SelectMany(t => t.Elements(ns + "Trigger")).ToList();
        var horizontal = styleTrig.FirstOrDefault(t => (string?)t.Attribute("Property") == "Orientation"
                                                       && (string?)t.Attribute("Value") == "Horizontal");
        Check("有 Orientation=Horizontal 的样式触发器", horizontal != null);

        if (horizontal != null)
        {
            var hs = horizontal.Elements(ns + "Setter").ToList();

            var width = hs.FirstOrDefault(s => (string?)s.Attribute("Property") == "Width");
            Check("横向滚动条宽度回到 Auto（铺满整条轨道）",
                  width != null && (string?)width.Attribute("Value") == "Auto",
                  width == null ? "(没设 Width)" : (string?)width.Attribute("Value"));

            var height = hs.FirstOrDefault(s => (string?)s.Attribute("Property") == "Height");
            int h = height == null ? 0 : int.TryParse((string?)height.Attribute("Value"), out var v) ? v : 0;
            Check("横向滚动条高度 ≥ 10px（能抓得住）", h >= 10, h.ToString());
        }

        var tpl = sbStyle.Elements(ns + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == "Template");

        var controlTpl = tpl?.Elements(ns + "Setter.Value")
            .SelectMany(v => v.Elements(ns + "ControlTemplate")).FirstOrDefault();
        Check("ScrollBar 模板存在", controlTpl != null);

        if (controlTpl != null)
        {
            var tplTrig = controlTpl.Elements(ns + "ControlTemplate.Triggers")
                .SelectMany(t => t.Elements(ns + "Trigger")).ToList();
            var horizTrack = tplTrig.FirstOrDefault(t => (string?)t.Attribute("Property") == "Orientation"
                                                         && (string?)t.Attribute("Value") == "Horizontal");
            Check("模板里有横向触发器", horizTrack != null);

            if (horizTrack != null)
            {
                var track = horizTrack.Elements(ns + "Setter")
                    .FirstOrDefault(s => (string?)s.Attribute("TargetName") == "PART_Track");

                Check("横向 Track 方向不反转（滑块在左、往右拖）",
                      track != null && (string?)track.Attribute("Value") == "False",
                      track == null ? "(没设 PART_Track)" : (string?)track.Attribute("Value"));
            }
        }
    }
}

// ---------------------------------------------------------------- 16. 进度高亮的三层图层
// 为什么测这个：真机上被截图报过一次 —— 段落中间只剩一条细琥珀竖线，整句底带和下划线整片不见。
// 根因是图层 Canvas 套在浮层 Canvas 里：Canvas 不随父容器自适应，它的 DesiredSize 是 0×0，
// 再叠 ClipToBounds，整层内容被裁得干净；只有直接挂在浮层上的 3px 起笔标还看得见。
// 而"撑尺寸"的 EnsureSize 第一版也坏了：新建 Canvas 的 Width 是 double.NaN，
// `Math.Abs(NaN - w) > 0.5` 恒为假 —— 尺寸一次都没设上。
// 这两条都是静态就能钉住的规则，所以直接读源码断言，别再靠"我以为画上去了"。
Console.WriteLine();
Console.WriteLine("== 16. 进度高亮图层（防“只剩起笔标”） ==");

string? layerPath = FindUp("src\\YiTai.App\\ProgressHighlightLayers.cs");
Check("ProgressHighlightLayers.cs 找得到", layerPath != null, layerPath ?? "(没找到，测试可能不在仓库根跑)");

string? markerPath = FindUp("src\\YiTai.App\\MainWindow.Marker.cs");
Check("MainWindow.Marker.cs 找得到", markerPath != null, markerPath ?? "(没找到)");

if (layerPath != null && markerPath != null)
{
    string layers = File.ReadAllText(layerPath);
    string marker = File.ReadAllText(markerPath);

    Check("EnsureSize 先判 IsNaN（NaN 也当“尺寸不对”）",
          layers.Contains("IsNaN", StringComparison.Ordinal),
          "否则 NaN 参与比较恒假，尺寸一次都设不上");

    int ensureAt = marker.IndexOf("ProgressHighlightLayers.EnsureSize", StringComparison.Ordinal);
    int bandAt = marker.IndexOf("ProgressHighlightLayers.SyncBand", StringComparison.Ordinal);
    int underAt = marker.IndexOf("ProgressHighlightLayers.SyncUnderline", StringComparison.Ordinal);

    Check("画底带之前先撑尺寸",
          ensureAt >= 0 && bandAt > ensureAt,
          $"EnsureSize@{ensureAt} / SyncBand@{bandAt}");

    Check("画下划线之前先撑尺寸",
          ensureAt >= 0 && underAt > ensureAt,
          $"EnsureSize@{ensureAt} / SyncUnderline@{underAt}");

    // 脉冲：Opacity 上限是 1，写成 1.0 → 1.85 会被裁成 1 → 1，等于没闪
    int pulseAt = marker.IndexOf("private static void PulseBand", StringComparison.Ordinal);
    Check("找得到 PulseBand", pulseAt >= 0);

    if (pulseAt >= 0)
    {
        string pulseBody = marker.Substring(pulseAt, Math.Min(600, marker.Length - pulseAt));
        var toMatch = System.Text.RegularExpressions.Regex.Match(pulseBody, @"To\s*=\s*([0-9.]+)");
        double toVal = toMatch.Success ? double.Parse(toMatch.Groups[1].Value) : -1;

        Check("PulseBand 的亮度目标 ≤1（Opacity 上限就是 1，大于 1 等于不闪）",
              toVal > 0 && toVal <= 1,
              $"To={toVal:0.00}");
    }
}

// 清理
try { Directory.Delete(box, recursive: true); } catch { }

// 从当前目录往上找仓库里的某个文件（测试从仓库根跑，也能从工程目录跑）
string? FindUp(string rel)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());

    for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
    {
        string p = Path.Combine(dir.FullName, rel);
        if (File.Exists(p)) return p;
    }

    return null;
}

Console.WriteLine();
if (failed == 0) Console.WriteLine($"全部通过 ✓（{passed} 项）");
else Console.WriteLine($"失败 {failed} 项 ✗（通过 {passed} 项）");

return failed == 0 ? 0 : 1;
