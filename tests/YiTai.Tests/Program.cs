// ============================================================
// Program.cs —— 冒烟测试（不开窗口，只测 Core）
// ============================================================
//
// 这套测试守的是"软件作为软件"该守的规矩，而不是某个界面的样子：
//   1-6   项目与译文：新建、收稿（同名不覆盖）、保存（换行/BOM/空稿）、
//         冲突不覆盖、改名、新建译文与自然排序
//   7     设置读写（含旧字段、null 字段的兼容）
//   8     仓库自带样例项目的完整性
//   9-10  旧项目兼容、归档按需建、栏位折叠、读不了的文件不崩
//   11-14 Markdown 渲染：==高亮==、软换行、GFM 管道表；句子切分
//   12    进度键绑文件路径（含旧裸键的迁移与认领）
//   15-16 滚动条样式、进度高亮图层的回归断言（读源码文本，不画窗口）
//   17    区间运算 RangeMath（底带给 ==高亮== 让路）
//   18    工作计时落盘 WorkTimeStore（缺文件/坏 JSON/往返/多日/原子写）+ 源码锚
//   19    省略号单边界（.../……/。。。整段一个句末，T-30）
//   20    ==高亮== 整句合并 MergeSpansOverZones（T-31）+ 渲染层接线源码锚
//   21    快捷键重排 + 复制当前句（T-32 / T-33，源码锚）
//   22    工作计时显示挪状态栏（T-34，源码锚）
//   23    英文省略号边界：点间带空格 . . . / 三点收尾引号括号 / 大写新句（T-35）
//   24    复制当前句的成功/失败反馈：提示音波形 + 反馈接线源码锚（T-37）
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

// 备份名必须保留原扩展名：.md 与 .txt 同名时，不能抢同一个备份名（后保存的那份会当天没有备份）
string bkDir = Path.Combine(box, "备份-同名不同扩展名");
Directory.CreateDirectory(bkDir);
string sameMd = Path.Combine(box, "同名.md");
string sameTxt = Path.Combine(box, "同名.txt");
File.WriteAllText(sameMd, "md 版\n");
File.WriteAllText(sameTxt, "txt 版\n");
var backupDay = new DateTime(2026, 11, 16, 10, 0, 0);
string? bkMd = TextFile.BackupOncePerDay(sameMd, bkDir, backupDay);
string? bkTxt = TextFile.BackupOncePerDay(sameTxt, bkDir, backupDay);
Check("同名不同扩展名各备份一份",
      bkMd != null && bkTxt != null && Directory.GetFiles(bkDir).Length == 2,
      Directory.GetFiles(bkDir).Length.ToString());
Check("备份保留原扩展名",
      bkMd?.EndsWith(".md", StringComparison.Ordinal) == true
      && bkTxt?.EndsWith(".txt", StringComparison.Ordinal) == true,
      (bkMd ?? "(null)") + " / " + (bkTxt ?? "(null)"));

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
Check("新建译文后「上次打开」指向清单里真实存在的那一条",
      reopened.Meta.LastTranslationId == fresh.Id
      && reopened.Meta.Translations.Exists(t => t.Id == reopened.Meta.LastTranslationId),
      reopened.Meta.LastTranslationId ?? "(null)");

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

// 集合字段被写成 null（手改、或别的版本写坏）：读回来必须还是空集合，不能是 null
File.WriteAllText(AppSettings.DefaultPath,
    "{\n  \"LastProjectRoot\": null,\n  \"RecentRefFiles\": null,\n" +
    "  \"PanesByProject\": null,\n  \"PaneWeightsByProject\": null\n}");
var stNull = AppSettings.Load();
Check("settings.json 的集合字段为 null 也不炸",
      stNull.LastProjectRoot == "" && stNull.RecentRefFiles != null
      && stNull.PanesByProject != null && stNull.PaneWeightsByProject != null,
      stNull.RecentRefFiles == null ? "RecentRefFiles 是 null" : "ok");

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

// 目录：File.Exists(目录) 恒为 false，所以必须先判 Directory.Exists；
// 否则用户把栏位指到文件夹，只会看到"文件不在了"，真正的错因被盖掉。
var dirInfo = TextFile.Read(box);
Check("路径是文件夹时明确报 InvalidPath（不是「文件不在了」）",
      dirInfo.State == TextReadState.InvalidPath && dirInfo.Problem.Contains("文件夹", StringComparison.Ordinal),
      dirInfo.State + " / " + dirInfo.Problem);

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
Check("跨行：两对 == 都原样留着", Flat(crossDoc.Blocks[0].Inlines) == "第一行 == 开头第二行 结尾 ==",
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

// 11.10 段落内软换行：中日韩文字两侧直接拼（不插半角空格），西文之间仍补一个空格。
// 这是文件头写明的承诺，之前实现里无条件补空格，中文按行折行的资料会每行多一个空格。
var cjkWrap = MdParser.Parse("第一行中文\n第二行中文\n");
Check("中文软换行直接拼，不插半角空格",
      Flat(cjkWrap.Blocks[0].Inlines) == "第一行中文第二行中文", Flat(cjkWrap.Blocks[0].Inlines));

var latinWrap = MdParser.Parse("first line\nsecond line\n");
Check("西文软换行仍补一个空格",
      Flat(latinWrap.Blocks[0].Inlines) == "first line second line", Flat(latinWrap.Blocks[0].Inlines));

var mixedWrap = MdParser.Parse("hello\n中文\n");
Check("一侧是中文就直接拼",
      Flat(mixedWrap.Blocks[0].Inlines) == "hello中文", Flat(mixedWrap.Blocks[0].Inlines));

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

                // 点轨道翻页：RepeatButton 默认绑的是竖向命令 PageUp/PageDown，
                // 横向条上 ScrollViewer 不认它们 —— 真机表现就是"点轨道毫无反应"。
                // 横向触发器必须把两个按钮换成 PageLeft/PageRight（T-28 真机验证时发现的）。
                var decrease = horizTrack.Elements(ns + "Setter")
                    .FirstOrDefault(s => (string?)s.Attribute("TargetName") == "PART_Decrease");
                Check("点轨道左段 → 绑 PageLeftCommand（真会向左翻一页）",
                      decrease != null
                      && ((string?)decrease.Attribute("Value") ?? "").Contains("PageLeftCommand"),
                      decrease == null ? "(没设 PART_Decrease)" : (string?)decrease.Attribute("Value"));

                var increase = horizTrack.Elements(ns + "Setter")
                    .FirstOrDefault(s => (string?)s.Attribute("TargetName") == "PART_Increase");
                Check("点轨道右段 → 绑 PageRightCommand（真会向右翻一页）",
                      increase != null
                      && ((string?)increase.Attribute("Value") ?? "").Contains("PageRightCommand"),
                      increase == null ? "(没设 PART_Increase)" : (string?)increase.Attribute("Value"));
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

// ---------------------------------------------------------------- 17. 区间运算（底带给 ==高亮== 让路）
// 为什么测这个：一句话里可能只有三五个字被 ==...== 标过。
// 原先的写法是"只要碰到一个高亮字，整句都不画底带、只画下划线"，
// 结果那一句里没标过的字也跟着不亮了（用户截图报的就是这个）。
// 正确做法是把句子切成"没标过的"和"标过的"，各画各的，所以先把区间减法测死。
Console.WriteLine();
Console.WriteLine("== 17. 区间运算 RangeMath ==");

void Same((int Start, int End)[] got, (int Start, int End)[] want, string name)
{
    bool ok = got.Length == want.Length;
    if (ok)
    {
        for (int i = 0; i < got.Length; i++)
            if (got[i] != want[i]) { ok = false; break; }
    }

    Check(name, ok, ok ? "" : $"得到 [{string.Join(", ", got.Select(g => $"{g.Start}..{g.End}"))}]");
}

Same(RangeMath.Subtract(10, 20, null).ToArray(), new[] { (10, 20) }, "没有高亮 → 整句保留");
Same(RangeMath.Subtract(10, 20, new[] { (13, 16) }).ToArray(),
     new[] { (10, 13), (16, 20) }, "中间一段高亮 → 断成左右两段");

Same(RangeMath.Subtract(10, 20, new[] { (10, 13) }).ToArray(),
     new[] { (13, 20) }, "高亮压在句首 → 只留后半段");

Same(RangeMath.Subtract(10, 20, new[] { (17, 20) }).ToArray(),
     new[] { (10, 17) }, "高亮压在句尾 → 只留前半段");

Same(RangeMath.Subtract(10, 20, new[] { (5, 25) }).ToArray(),
     Array.Empty<(int, int)>(), "整句都在高亮里 → 不留底带");

Same(RangeMath.Subtract(10, 20, new[] { (0, 12), (18, 30) }).ToArray(),
     new[] { (12, 18) }, "高亮越过句子两端 → 只留中间");

Same(RangeMath.Subtract(10, 20, new[] { (13, 16), (14, 18) }).ToArray(),
     new[] { (10, 13), (18, 20) }, "两段高亮重叠 → 先合并再减");

Same(RangeMath.Subtract(10, 20, new[] { (16, 18), (13, 14) }).ToArray(),
     new[] { (10, 13), (14, 16), (18, 20) }, "高亮区间乱序传入 → 先排序再减");

Same(RangeMath.Subtract(10, 20, new[] { (13, 13) }).ToArray(),
     new[] { (10, 20) }, "空高亮区间不产生影响");

Same(RangeMath.Subtract(20, 10, new[] { (13, 16) }).ToArray(),
     Array.Empty<(int, int)>(), "空句子 → 什么都不返回");

Same(RangeMath.Intersect(10, 20, new[] { (5, 14), (18, 25) }).ToArray(),
     new[] { (10, 14), (18, 20) }, "交集只取落在句内的部分（下划线画在这里）");

Same(RangeMath.Intersect(10, 20, null).ToArray(),
     Array.Empty<(int, int)>(), "没有高亮 → 交集为空");

// ============================================================
// 18. 工作计时落盘（T-29）
// ============================================================
// 为什么测这个：计时器每 30 秒往 .yitai\worktime.json 记一笔，读坏、写坏、
// 崩溃断电都在这个文件上发生。它坏了绝不能连累项目打不开，所以"缺文件 / 内容坏"
// 必须是空账而不是异常；"原子写"必须真的不留 .tmp。这些都是纯 Core 逻辑，
// 不开窗口就能钉住。另加几条源码锚：T-28 的 IsDocumentEnabled=true、计时口径的
// 180 秒与 30 秒落盘——光靠"我记得改过"守不住，下一个人顺手改回去也不会有人发现。
Console.WriteLine();
Console.WriteLine("== 18. 工作计时落盘（T-29）==");

WorkTimeLedger LoadNoThrow(string rootPath, out bool threw)
{
    threw = false;
    try { return WorkTimeStore.Load(rootPath); }
    catch (Exception) { threw = true; return new WorkTimeLedger(); }
}

string wbox = NewSandbox();
string wroot = Path.Combine(wbox, "旧项目");
ProjectStore.CreateNew(wroot, "旧项目");

// 18.1 旧项目：只有 project.json，从来没有 .yitai\worktime.json
Check("旧项目本来没有 worktime.json（前提）", !File.Exists(ProjectLayout.WorkTimeFile(wroot)));
bool threw;
var emptyLedger = LoadNoThrow(wroot, out threw);
Check("缺文件 → 空账、不抛异常", !threw && emptyLedger.Days.Count == 0,
      threw ? "抛了异常" : $"{emptyLedger.Days.Count} 天");
Check("空账的版本号仍是 1（写回去结构不漂）", emptyLedger.Version == 1, emptyLedger.Version.ToString());

// 18.2 文件内容坏掉：读不懂就当没记过，不能连累打开项目
Directory.CreateDirectory(ProjectLayout.Private(wroot));
File.WriteAllText(ProjectLayout.WorkTimeFile(wroot), "{ 这不是 JSON");
var brokenLedger = LoadNoThrow(wroot, out threw);
Check("损坏 JSON → 空账、不抛异常", !threw && brokenLedger.Days.Count == 0,
      threw ? "抛了异常" : $"{brokenLedger.Days.Count} 天");

// 18.3 Add / Save / Load 往返（坏文件被这一次 Save 覆盖掉）
var ledger = WorkTimeStore.Load(wroot);
WorkTimeStore.Add(ledger, "2026-11-17", 3600);
WorkTimeStore.Save(wroot, ledger);
var back = WorkTimeStore.Load(wroot);
Check("Add/Save/Load 往返：3600 秒原样回来",
      WorkTimeStore.SecondsOf(back, "2026-11-17") == 3600,
      WorkTimeStore.SecondsOf(back, "2026-11-17").ToString());
Check("往返后账本里只有记过的那一天", back.Days.Count == 1, back.Days.Count.ToString());

// 18.4 多日累加：同一天累加不断段，不同日各记各的（跨午夜就靠这个）
WorkTimeStore.Add(back, "2026-11-17", 240);
WorkTimeStore.Add(back, "2026-11-18", 1275);
WorkTimeStore.Add(back, "2026-11-19", 60);
WorkTimeStore.Save(wroot, back);
var back2 = WorkTimeStore.Load(wroot);
Check("多日累加：同一天两次 Add 累加（3600+240=3840）",
      WorkTimeStore.SecondsOf(back2, "2026-11-17") == 3840,
      WorkTimeStore.SecondsOf(back2, "2026-11-17").ToString());
Check("多日累加：不同自然日各记各的（18 日 1275、19 日 60）",
      WorkTimeStore.SecondsOf(back2, "2026-11-18") == 1275
      && WorkTimeStore.SecondsOf(back2, "2026-11-19") == 60,
      WorkTimeStore.SecondsOf(back2, "2026-11-18") + "/" + WorkTimeStore.SecondsOf(back2, "2026-11-19"));
Check("多日累加：账本一共 3 天", back2.Days.Count == 3, back2.Days.Count.ToString());

// 18.5 原子写：.tmp 只是过程，落完不能留在目录里
Check("Save 后 .yitai 里没有 .tmp 残留",
      Directory.GetFiles(ProjectLayout.Private(wroot), "*.tmp").Length == 0,
      string.Join(", ", Directory.GetFiles(ProjectLayout.Private(wroot), "*.tmp")));

// 18.6 只新增文件：旧结构一个字不动，旧项目照常打开
string projBefore = File.ReadAllText(ProjectLayout.ProjectFile(wroot));
bool reopenedOk = false;
try { reopenedOk = ProjectStore.Open(wroot).Meta.Name == "旧项目"; } catch { reopenedOk = false; }
Check("有 worktime.json 的项目照常打开", reopenedOk);
Check("计时账本没动 project.json 一个字",
      File.ReadAllText(ProjectLayout.ProjectFile(wroot)) == projBefore);

// 18.7 边界：没项目时不计时、不落盘；秒数 ≤0 不动账
Check("空项目根 Load → 空账、不抛", WorkTimeStore.Load("").Days.Count == 0);
bool saveNoThrow = true;
try { WorkTimeStore.Save("", new WorkTimeLedger()); } catch { saveNoThrow = false; }
Check("空项目根 Save 不抛异常", saveNoThrow);
WorkTimeStore.Add(back2, "2026-11-17", 0);
WorkTimeStore.Add(back2, "2026-11-17", -5);
Check("秒数 ≤0 不动账（0 与负数都不记）",
      WorkTimeStore.SecondsOf(back2, "2026-11-17") == 3840,
      WorkTimeStore.SecondsOf(back2, "2026-11-17").ToString());

// 18.8 源码锚：T-28 的根因那一行 + 计时口径
string? panesPath = FindUp("src\\YiTai.App\\MainWindow.Panes.cs");
Check("MainWindow.Panes.cs 找得到（源码锚）", panesPath != null, panesPath ?? "(没找到)");

if (panesPath != null)
{
    string panes = File.ReadAllText(panesPath);
    Check("源码锚：IsDocumentEnabled = true（T-28 的根因就这一行）",
          panes.Contains("IsDocumentEnabled = true", StringComparison.Ordinal));
    Check("源码锚：没有写回 IsDocumentEnabled = false",
          !panes.Contains("IsDocumentEnabled = false", StringComparison.Ordinal),
          "写回 false，表格横向滚动条又会变成看得见、点不动");
}

string? timerPath = FindUp("src\\YiTai.App\\WorkTimer.cs");
Check("WorkTimer.cs 找得到（源码锚）", timerPath != null, timerPath ?? "(没找到)");

if (timerPath != null)
{
    string timer = File.ReadAllText(timerPath);

    var idle = System.Text.RegularExpressions.Regex.Match(timer,
        @"IdleThreshold\s*=\s*TimeSpan\.FromSeconds\((\d+)\)");
    Check("源码锚：IdleThreshold = 180 秒（闲置即停的验收口径）",
          idle.Success && idle.Groups[1].Value == "180",
          idle.Success ? idle.Groups[1].Value : "(没匹配上)");

    var flush = System.Text.RegularExpressions.Regex.Match(timer,
        @"FlushEverySeconds\s*=\s*(\d+)");
    Check("源码锚：每 30 秒落一次盘（崩溃最多丢 30 秒）",
          flush.Success && flush.Groups[1].Value == "30",
          flush.Success ? flush.Groups[1].Value : "(没匹配上)");

    Check("源码锚：活动只认键按下 / 鼠标按下 / 滚轮（不挂 PreviewMouseMove）",
          timer.Contains("PreviewKeyDown") && timer.Contains("PreviewMouseDown")
          && timer.Contains("PreviewMouseWheel")
          && !timer.Contains("PreviewMouseMove"),
          "纯鼠标移动不算活动，别顺手挂上去");

    // T-36：计时只在状态栏一处显示，窗口标题恢复成「译台 — 项目名」。
    // 原来这条锚钉的是"标题带 h:mm:ss"，现在改钉状态栏口径 —— 别再把时长拼回标题。
    Check("源码锚：状态栏时长格式仍是 h:mm:ss（口径不变）",
          timer.Contains("{t.Minutes:D2}:{t.Seconds:D2}"), "");

    Check("源码锚：窗口标题不再拼时长（没有「· 工作」后缀）",
          !timer.Contains("· 工作"),
          "标题栏只留「译台 — 项目名」，整窗只有一个计时（状态栏）");

    Check("源码锚：状态栏口径是「工作 h:mm:ss」（OnDisplay 送 Format）",
          timer.Contains("OnDisplay?.Invoke(\"工作 \" + Format(SessionElapsed))"),
          "状态栏那一处的文案口径一字不动");

    Check("源码锚：回到前台先等一次新活动才算（_needsActivity 门闩）",
          timer.Contains("!_needsActivity") && timer.Contains("_needsActivity = false"),
          "否则切回来的瞬间就抢着数秒，与「激活后有新活动才续算」的口径不符");
}

try { Directory.Delete(wbox, recursive: true); } catch { }

// 清理
try { Directory.Delete(box, recursive: true); } catch { }

// ============================================================
// 19. 省略号单边界（T-30）
// ============================================================
// 为什么测这个：省略号以前只合并"连续半角点（≥2）"与"连续 …"两种，
// 「。。。」按单个。逐个收句，一个。。。出 3 个句边界；混合写法（...…、……。、。。。！）
// 也各多切一次。现在按"省略号停顿"判定：E = { '.' , '…' , '。' } 的极大连续 run
// （不跨行），run 中 …≥1 或 .≥2 或 。≥2 → 整段 run 连同紧随的 T 类收尾只产生
// **一个**句末边界；单个 '.' / '。' 行为不变。
Console.WriteLine();
Console.WriteLine("== 19. 省略号单边界（T-30）==");

void OneBoundary(string text, string what)
{
    var si = SentenceIndex.FromPlain(text);
    bool ok = si.Count == 1;
    string detail = si.Count.ToString();
    if (ok) ok = text.Substring(si.At(0).Start, si.At(0).Length) == text;   // 无空句、内容对上
    Check(what, ok, detail);
}

OneBoundary("等一下...", "「...」只切一个句末边界");
OneBoundary("等一下....", "「....」只切一个句末边界");
OneBoundary("等一下…", "单个「…」只切一个句末边界");
OneBoundary("等一下……", "「……」只切一个句末边界");
OneBoundary("等一下。。。", "「。。。」整段只切一个句末边界（不再一。一句）");
OneBoundary("等一下。。", "「。。」整段只切一个句末边界");
OneBoundary("等一下...…", "「...…」混合写法只切一个句末边界");
OneBoundary("……。", "「……。」混合写法只切一个句末边界");
OneBoundary("等一下。。。！", "「。。！」run 连同紧随的！只切一个句末边界");
OneBoundary("等一下……）", "「……）」收尾括号归上一句，只切一个句末边界");
OneBoundary("他说：“……”？", "引号里的省略号 + 句末问号收尾只切一个句末边界");

Check("「走吧。。。回家。」2 句（run 之后照常开新句）",
      SentenceIndex.FromPlain("走吧。。。回家。").Count == 2,
      SentenceIndex.FromPlain("走吧。。。回家。").Count.ToString());
Check("省略号 run 不跨行：「等一下。\n。。。」仍是 2 句",
      SentenceIndex.FromPlain("等一下。\n。。。").Count == 2,
      SentenceIndex.FromPlain("等一下。\n。。。").Count.ToString());

// 既有守卫回归：小数点 / 缩写 / 单个句号一个字都不能变
Check("回归：Mr. Smith 仍 2 句", SentenceIndex.FromPlain("Mr. Smith went home. He slept.").Count == 2);
Check("回归：3.5 小数点仍不切", SentenceIndex.FromPlain("It is 3.5 meters. Fine.").Count == 2);
Check("回归：A. B（单个大写字母缩写）仍 1 句", SentenceIndex.FromPlain("A. B").Count == 1);
Check("回归：普通句号一行三句不变", SentenceIndex.FromPlain("One. Two. Three.").Count == 3);

// ============================================================
// 20. ==高亮== 整句合并（T-31）
// ============================================================
// 为什么测这个：切句在渲染后文本上进行，== 标记被剥掉，高亮内容混在普通文字里按标点切；
// 高亮里含句号/省略号时照样被切开。现在把合并算法下沉为 Core 纯函数
// MergeSpansOverZones：任一句区间与任一高亮区间相交 → 并成一句，可传递合并；
// 高亮区本身以并集进入输出（"紧邻文字并入"靠重叠自然带上）。
// 边界口径逐条钉死（与方案文档第二节的边界表一一对应）。
Console.WriteLine();
Console.WriteLine("== 20. ==高亮== 整句合并（T-31）==");

void MergeIs((int, int)[] spans, (int, int)[] zones, (int, int)[] want, string what)
{
    var got = SentenceIndex.MergeSpansOverZones(spans, zones);
    bool ok = got.Count == want.Length;
    string detail = $"[{string.Join(", ", got.Select(g => $"{g.Item1}..{g.Item2}"))}]";
    for (int i = 0; ok && i < want.Length; i++) if (got[i] != want[i]) ok = false;
    Check(what, ok, ok ? "" : detail);
}

// 边界表：高亮在句首 / 句中 / 句尾 → 与所在句合并为一句，紧邻普通文字自然并入
MergeIs(new[] { (0, 10) }, new[] { (0, 3) }, new[] { (0, 10) }, "高亮在句首 → 仍是一句");
MergeIs(new[] { (0, 10) }, new[] { (4, 7) }, new[] { (0, 10) }, "高亮在句中 → 仍是一句");
MergeIs(new[] { (0, 10) }, new[] { (7, 10) }, new[] { (0, 10) }, "高亮在句尾 → 仍是一句");

// 一句里多段高亮 → 都并入该句
MergeIs(new[] { (0, 10) }, new[] { (2, 3), (5, 6) }, new[] { (0, 10) }, "一句里多段高亮 → 都并入该句");

// 高亮跨两个"原句"（==A。B==）→ 两句并成一句（覆盖区间并集）
MergeIs(new[] { (0, 10), (10, 20) }, new[] { (5, 15) }, new[] { (0, 20) }, "高亮跨两个原句 → 两句并成一句");

// 可传递合并：zone → span → zone 收敛成一句
MergeIs(new[] { (5, 10) }, new[] { (0, 6), (9, 15) }, new[] { (0, 15) }, "传递合并：高亮→句→高亮 收敛成一句");

// 高亮含省略号/很长（同段内）仍是一句：用 FromPlain 造两个原句，整段高亮 → 一句
string ellipText = "第一句……第二句。";
var ellipSpans = SentenceIndex.FromPlain(ellipText);
Check("前提：含省略号的文本切成 2 句", ellipSpans.Count == 2, ellipSpans.Count.ToString());
var ellipZones = new (int, int)[ellipSpans.Count];
for (int i = 0; i < ellipSpans.Count; i++) ellipZones[i] = (ellipSpans.At(i).Start, ellipSpans.At(i).End);
MergeIs(ellipZones, new[] { (0, ellipText.Length) }, new[] { (0, ellipText.Length) },
        "高亮含标点/省略号/很长（整段）→ 仍是一句");

// 未闭合 ==：解析层就不产生高亮区 → 等价于 zones 为空 → 区间原样返回
var openSpans = SentenceIndex.FromPlain("==没闭合。仍按标点切。");
Check("前提：未闭合 == 的文本仍切成 2 句", openSpans.Count == 2, openSpans.Count.ToString());
var untouched = SentenceIndex.MergeSpansOverZones(
    new[] { (openSpans.At(0).Start, openSpans.At(0).End), (openSpans.At(1).Start, openSpans.At(1).End) },
    Array.Empty<(int, int)>());
Check("未闭合 ==（无高亮区）→ 不触发整句口径，区间原样返回",
      untouched.Count == 2
      && untouched[0] == (openSpans.At(0).Start, openSpans.At(0).End)
      && untouched[1] == (openSpans.At(1).Start, openSpans.At(1).End));

// 行内代码 / 表格不参与：Core 层没有这两样，用 FromPlain 组合说明——
// 代码块整块是一句（块内句号不切），块里的"高亮区"并入整块后仍是一句、不外溢；
// 表格在渲染层整块不进切句文本（BlockUIContainer），天然不参与，见源码锚。
var codeSpans = SentenceIndex.FromPlain("```\na. b. c.\n```\nDone.");
Check("前提：代码块整块一句 + Done 一句", codeSpans.Count == 2, codeSpans.Count.ToString());
MergeIs(new[] { (codeSpans.At(0).Start, codeSpans.At(0).End), (codeSpans.At(1).Start, codeSpans.At(1).End) },
        new[] { (4, 10) },
        new[] { (codeSpans.At(0).Start, codeSpans.At(0).End), (codeSpans.At(1).Start, codeSpans.At(1).End) },
        "代码块内的高亮区并入整块，仍是两句（表格在渲染层整块不参与）");

// zone 与 span 不相交 → zone 原样输出、按 Start 排序
MergeIs(new[] { (0, 5) }, new[] { (10, 15) }, new[] { (0, 5), (10, 15) }, "不相交的高亮区 → 原样输出并排序");

// 相邻可保留（相邻两句是两句），重叠才并
MergeIs(new[] { (0, 5), (5, 10) }, new[] { (20, 30) },
        new[] { (0, 5), (5, 10), (20, 30) }, "相邻句区间保留相邻、重叠才并");

// zone 空 / span 空 → 原样返回（不重排、不归并、不改输入）
var messy = new[] { (3, 8), (0, 2) };
var noZones = SentenceIndex.MergeSpansOverZones(messy, Array.Empty<(int, int)>());
Check("zone 空 → spans 原样返回", noZones.Count == 2 && noZones[0] == (3, 8) && noZones[1] == (0, 2));
var noSpans = SentenceIndex.MergeSpansOverZones(Array.Empty<(int, int)>(), messy);
Check("span 空 → zones 原样返回", noSpans.Count == 2 && noSpans[0] == (3, 8) && noSpans[1] == (0, 2));

// App 侧接线源码锚：SentenceIndexFor 必须真的调合并函数
string? mdRenderPath = FindUp("src\\YiTai.App\\Markdown\\MdRender.cs");
Check("MdRender.cs 找得到（源码锚）", mdRenderPath != null, mdRenderPath ?? "(没找到)");

if (mdRenderPath != null)
{
    string mdRender = File.ReadAllText(mdRenderPath);

    Check("源码锚：SentenceIndexFor 调用了 MergeSpansOverZones（高亮整句口径在渲染层接线）",
          mdRender.Contains("SentenceIndex.MergeSpansOverZones", StringComparison.Ordinal),
          "没接线的话高亮区白算了，整句口径不会生效");

    Check("源码锚：拼接文本时递归识别 Tag=hl 的 Span 并记高亮区",
          mdRender.Contains("\"hl\"", StringComparison.Ordinal)
          && mdRender.Contains("hlZones", StringComparison.Ordinal));
}

// ============================================================
// 21. 快捷键重排 + 复制当前句（T-32 / T-33，源码锚）
// ============================================================
// 为什么测这个：Ctrl+Alt 三键从"X 下一句 / C 居中"重排成"Z 下一句 / X 居中 / C 复制"。
// 纯界面行为，CI 起不来窗口 —— 靠源码锚钉住键位接线，防止以后悄悄改回去。
Console.WriteLine();
Console.WriteLine("== 21. 快捷键重排 + 复制当前句（T-32 / T-33，源码锚）==");

string? markerPath21 = FindUp("src\\YiTai.App\\MainWindow.Marker.cs");
Check("MainWindow.Marker.cs 找得到（源码锚）", markerPath21 != null, markerPath21 ?? "(没找到)");

if (markerPath21 != null)
{
    string marker21 = File.ReadAllText(markerPath21);

    Check("源码锚：Ctrl+Alt+Z → StepMarker(+1)（下一句）",
          marker21.Contains("Key.Z) { StepMarker(+1)", StringComparison.Ordinal));

    Check("源码锚：Ctrl+Alt+X → CenterOnActivePane()（居中找回）",
          marker21.Contains("Key.X) { CenterOnActivePane()", StringComparison.Ordinal));

    Check("源码锚：Ctrl+Alt+C → CopyCurrentSentence()（复制当前句）",
          marker21.Contains("Key.C) { CopyCurrentSentence()", StringComparison.Ordinal));

    Check("源码锚：X 不再绑下一句（无双套并行）",
          !marker21.Contains("Key.X) { StepMarker", StringComparison.Ordinal));

    Check("源码锚：CopyCurrentSentence 用 TextRange 取纯文本并写剪贴板",
          marker21.Contains("new TextRange(", StringComparison.Ordinal)
          && marker21.Contains("Clipboard.SetText", StringComparison.Ordinal));
}

string? winXamlPath = FindUp("src\\YiTai.App\\MainWindow.xaml");
Check("MainWindow.xaml 找得到（源码锚）", winXamlPath != null, winXamlPath ?? "(没找到)");

string? winCsPath = FindUp("src\\YiTai.App\\MainWindow.xaml.cs");
Check("MainWindow.xaml.cs 找得到（源码锚）", winCsPath != null, winCsPath ?? "(没找到)");

if (winXamlPath != null && winCsPath != null)
{
    string winXaml = File.ReadAllText(winXamlPath);
    string winCs = File.ReadAllText(winCsPath);

    Check("源码锚：帮助菜单有「快捷键对照…」入口",
          winXaml.Contains("OnShowShortcuts", StringComparison.Ordinal));

    Check("源码锚：xaml.cs 实现了 OnShowShortcuts（快捷键对照对话框）",
          winCs.Contains("private void OnShowShortcuts", StringComparison.Ordinal));
}

// ============================================================
// 22. 工作计时显示挪状态栏（T-34，源码锚）
// ============================================================
// 为什么测这个：计时显示从"只在标题"扩到"状态栏常显"。计时口径（180 秒/30 秒落盘/
// _needsActivity 门闩，第 18 节）一字不动，只加一个显示回调 —— 源码锚钉住三处接线。
Console.WriteLine();
Console.WriteLine("== 22. 工作计时显示挪状态栏（T-34，源码锚）==");

string? timerPath22 = FindUp("src\\YiTai.App\\WorkTimer.cs");
Check("WorkTimer.cs 找得到（源码锚）", timerPath22 != null, timerPath22 ?? "(没找到)");

string? winXamlPath22 = FindUp("src\\YiTai.App\\MainWindow.xaml");
Check("MainWindow.xaml 找得到（源码锚）", winXamlPath22 != null, winXamlPath22 ?? "(没找到)");

string? winCsPath22 = FindUp("src\\YiTai.App\\MainWindow.xaml.cs");
Check("MainWindow.xaml.cs 找得到（源码锚）", winCsPath22 != null, winCsPath22 ?? "(没找到)");

if (timerPath22 != null && winXamlPath22 != null && winCsPath22 != null)
{
    string timer22 = File.ReadAllText(timerPath22);
    string winXaml22 = File.ReadAllText(winXamlPath22);
    string winCs22 = File.ReadAllText(winCsPath22);

    Check("源码锚：WorkTimer 有 OnDisplay 显示回调",
          timer22.Contains("OnDisplay", StringComparison.Ordinal));

    Check("源码锚：状态栏有 TxtWorkTime 常显文本",
          winXaml22.Contains("TxtWorkTime", StringComparison.Ordinal));

    Check("源码锚：MainWindow 把 OnDisplay 接到状态栏",
          winCs22.Contains("TxtWorkTime.Text = s", StringComparison.Ordinal)
          || winCs22.Contains("TxtWorkTime.Text =", StringComparison.Ordinal));
}

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

// ============================================================
// 23. 英文省略号边界（T-35）
// ============================================================
// 为什么测这个：T-30 只把"**连续** E 字符 run"（...、……、。。。）并成一个句末边界，
// 它的 17 条断言全是连续写法；英文资料里常见的点间带空格写法 `. . .`
// （三个句号中间各夹一个空格）run 在空格处就断了，于是每个 `.` 各自收句 ——
// 一段 `He paused . . . then he left.` 被切成 4 句，进度线要按三次才跨过去。
// 现在把"点间带空格"的省略号也算一个停顿：E 段之间只隔空格/制表符、
// 累计 ≥3 个点 → 整段只产生一个句末边界（收尾引号/括号照旧并入上一句）。
// 连续写法的口径与既有守卫（Mr./e.g./3.14/A. B/普通句号）一个字都不动。
Console.WriteLine();
Console.WriteLine("== 23. 英文省略号边界（T-35）==");

string SpanText(string text, int i)
{
    var si = SentenceIndex.FromPlain(text);
    var sp = si.At(i);
    return text.Substring(sp.Start, sp.Length);
}

void TwoSentences(string text, string wantFirst, string what)
{
    var si = SentenceIndex.FromPlain(text);
    bool ok = si.Count == 2;
    string detail = si.Count.ToString();
    if (ok) ok = SpanText(text, 0) == wantFirst;
    Check(what, ok, ok ? "" : $"句数={detail}，第一句=「{(si.Count > 0 ? SpanText(text, 0) : "")}」");
}

// 23.1 连续三点：英文句中 / 句尾（T-30 已覆盖的写法，这里把英文上下文补齐）
Check("英文：He paused... then he left. → 2 句（... 只算一个句末边界）",
      SentenceIndex.FromPlain("He paused... then he left.").Count == 2,
      SentenceIndex.FromPlain("He paused... then he left.").Count.ToString());
Check("英文：He said goodbye... Then the door closed. → 2 句（句尾三点 + 大写新句）",
      SentenceIndex.FromPlain("He said goodbye... Then the door closed.").Count == 2,
      SentenceIndex.FromPlain("He said goodbye... Then the door closed.").Count.ToString());
Check("英文：三点跨段不并（...\\n\\nThen 仍是 2 句）",
      SentenceIndex.FromPlain("He paused...\n\nThen he left.").Count == 2,
      SentenceIndex.FromPlain("He paused...\n\nThen he left.").Count.ToString());

// 23.2 点间带空格：这就是用户中招的写法（三个句号各夹一个空格）
TwoSentences("He paused . . . then he left.", "He paused . . .",
             "英文：He paused . . . then he left. → 2 句（点间空格也是一个边界）");
TwoSentences("He paused . . . Then he left.", "He paused . . .",
             "英文：点间空格的三点后接大写新句 → 2 句");
TwoSentences("等一下 。 。 。 继续。", "等一下 。 。 。",
             "中文：。 。 。（点间空格）同样只算一个句末边界");
Check("英文：空格点三点不改数目枚举（Item 1. Item 2. Item 3. 仍 3 句）",
      SentenceIndex.FromPlain("Item 1. Item 2. Item 3.").Count == 3,
      SentenceIndex.FromPlain("Item 1. Item 2. Item 3.").Count.ToString());

// 23.3 三点紧跟引号 / 括号（连续与点间空格两种写法都要收进上一句）
TwoSentences("She said \"...\" and left.", "She said \"...\"",
             "英文：三点紧跟收尾引号 → 引号归上一句（2 句）");
TwoSentences("He paused (or so I thought...) and left.", "He paused (or so I thought...)",
             "英文：三点紧跟收尾括号 → 括号归上一句（2 句）");
TwoSentences("He paused . . . \" she said.", "He paused . . . \"",
             "英文：点间空格的三点后紧跟引号 → 引号归上一句（2 句）");
TwoSentences("He paused (maybe . . .) and left.", "He paused (maybe . . .)",
             "英文：点间空格的三点后紧跟括号 → 括号归上一句（2 句）");

// 23.4 既有守卫回归：缩写 / 小数点 / 单大写字母 / 普通句号，一个都不能被新规则带坏
Check("回归：Mr. Smith waited... then → 2 句（Mr. 守卫与省略号不打架）",
      SentenceIndex.FromPlain("Mr. Smith waited... then he left.").Count == 2,
      SentenceIndex.FromPlain("Mr. Smith waited... then he left.").Count.ToString());
Check("回归：e.g. / i.e. 后接省略号不误切",
      SentenceIndex.FromPlain("He cited e.g. two works... then stopped.").Count == 2,
      SentenceIndex.FromPlain("He cited e.g. two works... then stopped.").Count.ToString());

// 缩写表里的词后面**紧跟**省略号（etc... / U.S...）：缩写守卫不能把整段点吞掉，
// 否则句末边界直接丢失（整句连下一句并成一句，按「下一句」会跨过头）。
TwoSentences("He brought pens, etc... Then he left.", "He brought pens, etc...",
             "回归：etc... 后面的省略号照样收句（守 2 句，不是并成 1 句）");
TwoSentences("In the U.S... Then he left.", "In the U.S...",
             "回归：U.S... 后面的省略号照样收句（缩写点与省略号互不吃掉）");
Check("回归：3.14 小数点不被点间空格规则吃掉",
      SentenceIndex.FromPlain("It cost 3.14 dollars. Fine.").Count == 2,
      SentenceIndex.FromPlain("It cost 3.14 dollars. Fine.").Count.ToString());
Check("回归：Mr. / e.g. / 3.14 全在一句里仍按句号切 3 句",
      SentenceIndex.FromPlain("Well, Mr. Smith waited... e.g. nothing happened. It cost 3.14 dollars.").Count == 3,
      SentenceIndex.FromPlain("Well, Mr. Smith waited... e.g. nothing happened. It cost 3.14 dollars.").Count.ToString());
Check("回归：A. B 单大写字母缩写仍不切（1 句）",
      SentenceIndex.FromPlain("A. B").Count == 1,
      SentenceIndex.FromPlain("A. B").Count.ToString());
Check("回归：普通句号 One. Two. Three. 仍 3 句（点间空格规则不误并）",
      SentenceIndex.FromPlain("One. Two. Three.").Count == 3,
      SentenceIndex.FromPlain("One. Two. Three.").Count.ToString());

// ============================================================
// 24. 复制当前句的成功/失败反馈（T-37）
// ============================================================
// 为什么测这个：反馈是"声音 + 颜色"，看着最像纯界面、最容易被以后的人改坏：
//   · 音是 Core 合成的，波形走向（上滑=成功 / 下滑=失败）、时长、音量都能算；
//   · "失败也必须有反馈"这条靠源码锚守：连 Clipboard.SetText 只允许出现一次，
//     失败路径不可能偷偷写剪贴板，也不会再退化成静默 return；
//   · 浮条不吃鼠标（IsHitTestVisible=False）、成功文案与 T-33 一字不差，都钉住。
Console.WriteLine();
Console.WriteLine("== 24. 复制当前句的成功/失败反馈（T-37）==");

byte[] successWav = CopyCue.Wav(CopyCueKind.Success);
byte[] failureWav = CopyCue.Wav(CopyCueKind.Failure);

int SampleCount(byte[] wav) => (wav.Length - 44) / 2;
short SampleAt(byte[] wav, int i)
{
    int p = 44 + i * 2;
    return (short)(wav[p] | (wav[p + 1] << 8));
}

Check("成功提示音是合法 WAV（RIFF/WAVE/fmt /data 四个块都在）",
      successWav.Length > 44
      && Encoding.ASCII.GetString(successWav, 0, 4) == "RIFF"
      && Encoding.ASCII.GetString(successWav, 8, 4) == "WAVE"
      && Encoding.ASCII.GetString(successWav, 12, 4) == "fmt "
      && Encoding.ASCII.GetString(successWav, 36, 4) == "data");

Check("提示音格式：PCM / 单声道 / 44100 / 16 位（不用重采样就能直接放）",
      successWav[20] == 1 && successWav[22] == CopyCue.Channels
      && BitConverter.ToInt32(successWav, 24) == CopyCue.SampleRate
      && BitConverter.ToInt16(successWav, 34) == CopyCue.BitsPerSample);

Check("成功音 = 100ms（100 毫秒 × 44100 = 4410 个采样）",
      SampleCount(successWav) == CopyCue.SampleRate * CopyCue.SuccessMs / 1000,
      SampleCount(successWav).ToString());

Check("失败音 = 140ms（比成功略长，留出读懂「为什么」的时间）",
      SampleCount(failureWav) == CopyCue.SampleRate * CopyCue.FailureMs / 1000,
      SampleCount(failureWav).ToString());

// 零交叉数 ≈ 频率 × 2：上半段与下半段一比，走向就出来了。
// 上是"办成了"、下是"没成"—— 这是两种音最本质的区别，闭着眼也分得开。
int Crossings(byte[] wav, int from, int to)
{
    int n = 0;
    short prev = SampleAt(wav, from);
    for (int i = from + 1; i < to; i++)
    {
        short cur = SampleAt(wav, i);
        if ((prev < 0 && cur >= 0) || (prev >= 0 && cur < 0)) n++;
        prev = cur;
    }
    return n;
}

int half = SampleCount(successWav) / 2;
int successFirst = Crossings(successWav, 0, half);
int successSecond = Crossings(successWav, half, SampleCount(successWav));
Check("成功音是上滑（后半段比前半段更密）",
      successSecond > successFirst, $"{successFirst} → {successSecond}");

int fHalf = SampleCount(failureWav) / 2;
int failFirst = Crossings(failureWav, 0, fHalf);
int failSecond = Crossings(failureWav, fHalf, SampleCount(failureWav));
Check("失败音是下滑（后半段比前半段更疏）",
      failSecond < failFirst, $"{failFirst} → {failSecond}");

// 音量：峰值落在 0.15～0.5 满量程 —— 听得到，但不至于在安静房间里吓人一跳。
int Peak(byte[] wav)
{
    int m = 0;
    for (int i = 0; i < SampleCount(wav); i++)
    {
        int a = Math.Abs((int)SampleAt(wav, i));
        if (a > m) m = a;
    }
    return m;
}

double successPeak = Peak(successWav) / (double)short.MaxValue;
double failurePeak = Peak(failureWav) / (double)short.MaxValue;
Check("成功音峰值在 0.15～0.5 之间（够听清，不刺耳）",
      successPeak is > 0.15 and < 0.5, successPeak.ToString("0.00"));
Check("失败音峰值在 0.15～0.5 之间，且不比成功更轻（更容易被注意到）",
      failurePeak is > 0.15 and < 0.5 && failurePeak >= successPeak,
      $"{failurePeak:0.00} vs {successPeak:0.00}");

// 首尾必须归零：sin² 包络的意义就是"不爆音"。硬切开头会"啪"一声 —— 那才是真烦。
bool StartsAndEndsSilent(byte[] wav)
{
    int n = SampleCount(wav);
    for (int i = 0; i < 10; i++)
    {
        if (Math.Abs((int)SampleAt(wav, i)) > 400) return false;          // < 1.2% 满量程
        if (Math.Abs((int)SampleAt(wav, n - 1 - i)) > 400) return false;
    }
    return true;
}

Check("两种音的首尾都归零（起止不爆音，连按也不会「啪啪」响）",
      StartsAndEndsSilent(successWav) && StartsAndEndsSilent(failureWav));

// 逐字节稳定：真机放的那一声，和交付给审查的 .wav 是同一段，可以复听比对。
Check("同一 kind 两次生成逐字节一致（真机听到的 = 交付的这份 wav）",
      CopyCue.Wav(CopyCueKind.Success).SequenceEqual(CopyCue.Wav(CopyCueKind.Success))
      && CopyCue.Wav(CopyCueKind.Failure).SequenceEqual(CopyCue.Wav(CopyCueKind.Failure)));

// ---- App 侧接线源码锚 ----
string? cueSrc = FindUp("src\\YiTai.Core\\CopyCue.cs");
string? feedbackSrc = FindUp("src\\YiTai.App\\MainWindow.CopyFeedback.cs");
string? markerSrc = FindUp("src\\YiTai.App\\MainWindow.Marker.cs");
string? toastXaml = FindUp("src\\YiTai.App\\MainWindow.xaml");

Check("CopyCue.cs 找得到（源码锚）", cueSrc != null, cueSrc ?? "(没找到)");
Check("MainWindow.CopyFeedback.cs 找得到（源码锚）", feedbackSrc != null, feedbackSrc ?? "(没找到)");
Check("MainWindow.Marker.cs 找得到（源码锚）", markerSrc != null, markerSrc ?? "(没找到)");

if (feedbackSrc != null)
{
    string fb = File.ReadAllText(feedbackSrc);

    Check("源码锚：反馈声音真的交给播放器（SoundPlayer.Play）",
          fb.Contains("new SoundPlayer(", StringComparison.Ordinal)
          && fb.Contains(".Play()", StringComparison.Ordinal));

    Check("源码锚：音源来自 Core 的 CopyCue.Wav（不是仓库外的 wav 文件）",
          fb.Contains("CopyCue.Wav(", StringComparison.Ordinal));

    Check("源码锚：声音失败只记日志，不抛给复制流程（catch + Diag.Error）",
          fb.Contains("catch (Exception ex)", StringComparison.Ordinal)
          && fb.Contains("Diag.Error(", StringComparison.Ordinal));

    Check("源码锚：浮条每次触发都重新压入时间轴（BeginAnimation 替换上一轮）",
          fb.Contains("BeginAnimation(UIElement.OpacityProperty", StringComparison.Ordinal));

    Check("源码锚：成功绿 ✓、失败橙 ！（两种反馈不会看混）",
          fb.Contains("\"✓\"", StringComparison.Ordinal)
          && fb.Contains("\"！\"", StringComparison.Ordinal)
          && fb.Contains("\"Warn\"", StringComparison.Ordinal));
}

if (markerSrc != null)
{
    string mk = File.ReadAllText(markerSrc);

    Check("源码锚：成功反馈在写剪贴板之后才给（反馈不先于事实）",
          mk.IndexOf("Clipboard.SetText(text)", StringComparison.Ordinal) >= 0
          && mk.IndexOf("ShowCopyFeedback(true", StringComparison.Ordinal)
             > mk.IndexOf("Clipboard.SetText(text)", StringComparison.Ordinal));

    Check("源码锚：失败路径也一定有反馈（CopyFailed → ShowCopyFeedback(false)",
          mk.Contains("ShowCopyFeedback(false", StringComparison.Ordinal)
          && mk.Contains("private void CopyFailed(string reason)", StringComparison.Ordinal));

    Check("源码锚：剪贴板只写一次，失败路径不可能写进去（不串句、不改复制结果）",
          mk.Split(new[] { "Clipboard.SetText" }, StringSplitOptions.None).Length - 1 == 1);

    Check("源码锚：成功文案与 T-33 一字不差（已复制当前句（N 字））",
          mk.Contains("$\"已复制当前句（{text.Length} 字）\"", StringComparison.Ordinal));
}

if (toastXaml != null)
{
    string xaml = File.ReadAllText(toastXaml);

    Check("源码锚：浮条不吃鼠标（IsHitTestVisible=False），连续按不会误点正文",
          xaml.Contains("x:Name=\"CopyToastLayer\"", StringComparison.Ordinal)
          && xaml.Contains("IsHitTestVisible=\"False\"", StringComparison.Ordinal));

    Check("源码锚：浮条文本与图标都在 XAML 里（视觉反馈不是靠状态栏那行灰字）",
          xaml.Contains("TxtCopyToastText", StringComparison.Ordinal)
          && xaml.Contains("TxtCopyToastIcon", StringComparison.Ordinal));
}

// ============================================================
// 25. 译文批注（T-a451712a）
// ============================================================
// 为什么测这组：批注的命门有两处，都是"看起来没事、出事就丢数据"：
//   ① 文件本身 —— 新项目没有它、坏 JSON、写一半断电，都不能连累项目打不开；
//   ② 锚点 —— 正文被改过之后，偏移对不上时"回贴"还是"硬贴"，
//      选错就会把批注焊到别的句子上。这里把两条路都钉死。
// 渲染那半截（Span / ToolTip / 与高亮合成后的实际像素）在 WPF 里，冒烟测试跑不到，
// 由真机探针 + 下面的源码锚守；纯区间合成由 AnnotationMath 在这里直接算。
Console.WriteLine();
Console.WriteLine("== 25. 译文批注（annotations.json + 区间合成）==");

string annBox = NewSandbox();
string annRoot = Path.Combine(annBox, "批注译本");
var annStore = ProjectStore.CreateNew(annRoot, "批注译本");

Check("新项目没有 annotations.json（旧项目天然兼容）",
      !File.Exists(ProjectLayout.AnnotationsFile(annRoot)));

var annEmpty = AnnotationStore.Load(annRoot);
Check("没有文件时 Load 返回空表、不抛", annEmpty.Items.Count == 0);

File.WriteAllText(ProjectLayout.AnnotationsFile(annRoot), "{ 这不是 JSON");
var annBad = AnnotationStore.Load(annRoot);
bool annProjectOpens = false;
try { annProjectOpens = ProjectStore.Open(annRoot) != null; } catch (Exception) { annProjectOpens = false; }
Check("坏 JSON 当空表、项目照样打开（绝不连累）", annBad.Items.Count == 0 && annProjectOpens);

File.Delete(ProjectLayout.AnnotationsFile(annRoot));

string annTr = Path.Combine(ProjectLayout.Translations(annRoot), "ch01.md");
string annText = "第一章\n\n他站在门口，没有说话。\n";
File.WriteAllText(annTr, annText);

string annKey = ProgressStore.KeyFor(annRoot, annTr);
Check("项目内译文用 rel: 键（整个文件夹搬走不断链）",
      annKey.StartsWith("rel:", StringComparison.Ordinal), annKey);

string annNorm = AnnotationStore.Normalize(annText);
int annLen = "他站在门口".Length;   // 5 个字，别数错（原来按 6 算，锚点全歪一格）
int annAt = annNorm.IndexOf("他站在门口", StringComparison.Ordinal);
Check("译文里找得到要批注的片段", annAt >= 0 && annNorm.Substring(annAt, annLen) == "他站在门口");

var annState = AnnotationStore.Load(annRoot);
var annItem = AnnotationStore.Create(annKey, annAt, annAt + annLen, "他站在门口", "这句太硬", DateTime.Now);
AnnotationStore.Add(annState, annItem);
AnnotationStore.Save(annRoot, annState);

Check("落盘位置就是 .yitai\\annotations.json", File.Exists(ProjectLayout.AnnotationsFile(annRoot)));
Check("原子写不留 .tmp（先 .tmp 再 move）", !File.Exists(ProjectLayout.AnnotationsFile(annRoot) + ".tmp"));

string annJson = File.ReadAllText(ProjectLayout.AnnotationsFile(annRoot));
Check("中文不转 \\uXXXX（UnsafeRelaxedJsonEscaping）",
      annJson.Contains("这句太硬", StringComparison.Ordinal)
      && !annJson.Contains("\\u", StringComparison.Ordinal));

var annBack = AnnotationStore.Load(annRoot);
var annRound = AnnotationStore.ItemsFor(annBack, annKey);
Check("往返：条数 / 键 / 偏移 / quote / 正文都对",
      annRound.Count == 1
      && annRound[0].FileKey == annKey
      && annRound[0].Start == annAt && annRound[0].End == annAt + annLen
      && annRound[0].Quote == "他站在门口" && annRound[0].Body == "这句太硬");

var annEdit = annRound[0];
annEdit.Body = "这句太硬，建议改成「他没有说话」";
annEdit.Modified = DateTime.Now.AddMinutes(1).ToString("o");
AnnotationStore.Save(annRoot, annBack);

var annBack2 = AnnotationStore.Load(annRoot);
Check("改正文之后落盘往返仍对（Id 不变）",
      AnnotationStore.Find(annBack2, annEdit.Id)?.Body == "这句太硬，建议改成「他没有说话」");

AnnotationStore.Remove(annBack2, annEdit.Id);
AnnotationStore.Save(annRoot, annBack2);
var annBack3 = AnnotationStore.Load(annRoot);
Check("删批注后表里没有它", AnnotationStore.ItemsFor(annBack3, annKey).Count == 0);
Check("删批注没碰译文正文一个字", File.ReadAllText(annTr) == annText);

// ---- 锚点：原位 / 漂移回贴 / 找不到就如实未定位 ----
var probe = AnnotationStore.Create(annKey, annAt, annAt + annLen, "他站在门口", "", DateTime.Now);
var atHome = AnnotationStore.Anchor(probe, annNorm);
Check("锚点原位：偏移处就是 quote，原样返回",
      atHome.Start == annAt && atHome.End == annAt + annLen,
      atHome.Start.ToString());

string shifted = "楔子\n\n" + annNorm;
var atShifted = AnnotationStore.Anchor(probe, shifted);
Check("正文前面插了字：按 quote 回贴到新位置",
      atShifted.Start == shifted.IndexOf("他站在门口", StringComparison.Ordinal)
      && atShifted.End == atShifted.Start + annLen,
      atShifted.Start.ToString());

string rewritten = annNorm.Replace("他站在门口", "她离开了房间");
var atLost = AnnotationStore.Anchor(probe, rewritten);
Check("原文被改掉：如实返回未定位，绝不硬贴到别的文字上",
      atLost.Start == -1 && atLost.End == -1, $"{atLost.Start}/{atLost.End}");

string twice = "他站在门口。\n\n他站在门口。\n";
var probeSecond = AnnotationStore.Create(annKey, twice.LastIndexOf("他站在门口", StringComparison.Ordinal),
                                         twice.LastIndexOf("他站在门口", StringComparison.Ordinal) + annLen,
                                         "他站在门口", "", DateTime.Now);
string prefixed = "序\n" + twice;   // 前缀短一点，两处距原偏移不等远，才测得出"取最近"
var atNearest = AnnotationStore.Anchor(probeSecond, prefixed);
Check("附近有两处同文：取离原偏移最近的一处（不乱跳）",
      atNearest.Start == prefixed.LastIndexOf("他站在门口", StringComparison.Ordinal),
      atNearest.Start.ToString());

// ---- 键不串：同名不同目录 / 项目外 ----
string annOtherDir = Path.Combine(annRoot, "sources");
Directory.CreateDirectory(annOtherDir);
string annOther = Path.Combine(annOtherDir, "ch01.md");
File.WriteAllText(annOther, "同名但另一份");
Check("同名不同目录 → 不同键（在 A 上标的批注不会跑到 B 上）",
      ProgressStore.KeyFor(annRoot, annOther) != annKey);

string annOutside = Path.Combine(annBox, "项目外.md");
File.WriteAllText(annOutside, "项目外的一份");
Check("项目外文件用 abs: 键（不冒充项目内）",
      ProgressStore.KeyFor(annRoot, annOutside).StartsWith("abs:", StringComparison.Ordinal));

var annTwoKeys = new AnnotationState();
AnnotationStore.Add(annTwoKeys, AnnotationStore.Create(annKey, 0, 3, "第一章", "一", DateTime.Now));
AnnotationStore.Add(annTwoKeys, AnnotationStore.Create(ProgressStore.KeyFor(annRoot, annOther), 0, 3, "同名", "二", DateTime.Now));
Check("按文件键取批注：两份同名文件互不串",
      AnnotationStore.ItemsFor(annTwoKeys, annKey).Count == 1
      && AnnotationStore.ItemsFor(annTwoKeys, ProgressStore.KeyFor(annRoot, annOther)).Count == 1);

// ---- 区间合成：批注 + 既有 ==高亮== 都要在 ----
var annRanges = new List<(string Id, int Start, int End)> { ("a", 5, 15) };
var hlRanges = new List<(int Start, int End)> { (10, 12) };

var slices = AnnotationMath.Slice(0, 20, annRanges, hlRanges);
Check("切段：批注/高亮的边界都切开（5 段：0-5 5-10 10-12 12-15 15-20）",
      slices.Count == 5 && slices[0].Start == 0 && slices[4].End == 20, slices.Count.ToString());

bool SliceAt(int from, int to, bool hasAnn, bool hl)
{
    foreach (var s in slices)
        if (s.Start == from && s.End == to)
            return s.HasAnnotation == hasAnn && s.Highlighted == hl;
    return false;
}

Check("批注盖住、无高亮的那段：有批注、无高亮（可以铺浅底）",
      SliceAt(5, 10, true, false));
Check("批注与高亮重叠的那段：两者都在（高亮没被吃掉，改画下划线）",
      SliceAt(10, 12, true, true));
Check("批注尾巴（高亮之后）：有批注、无高亮",
      SliceAt(12, 15, true, false));

var noAnn = AnnotationMath.Slice(0, 20, null, hlRanges);
Check("没有批注：原区间一字不切，全部 HasAnnotation=false（渲染层可以完全不动）",
      noAnn.Count == 3 && !noAnn[0].HasAnnotation && !noAnn[1].HasAnnotation && !noAnn[2].HasAnnotation
      && noAnn[1].Highlighted);

var twoAnn = new List<(string Id, int Start, int End)> { ("a", 2, 8), ("b", 6, 12) };
var overlap = AnnotationMath.Slice(0, 20, twoAnn, null);
bool BothAt(int from, int to)
{
    foreach (var s in overlap)
        if (s.Start == from && s.End == to)
            return s.ItemIds.Count == 2 && s.ItemIds[0] == "a" && s.ItemIds[1] == "b";
    return false;
}
Check("两条批注叠在同一截：计划里两条都记着（不会只画一条）", BothAt(6, 8));

// ---- App 侧接线源码锚 ----
string? mdRenderSrc = FindUp("src\\YiTai.App\\Markdown\\MdRender.cs");
string? annRenderSrc = FindUp("src\\YiTai.App\\AnnotationRender.cs");
string? annUiSrc = FindUp("src\\YiTai.App\\MainWindow.Annotations.cs");
string? annStoreSrc = FindUp("src\\YiTai.Core\\Project\\AnnotationStore.cs");
string? annXamlSrc = FindUp("src\\YiTai.App\\MainWindow.xaml");

Check("MdRender.cs / AnnotationRender.cs / MainWindow.Annotations.cs / MainWindow.xaml 都在",
      mdRenderSrc != null && annRenderSrc != null && annUiSrc != null && annXamlSrc != null);

if (mdRenderSrc != null)
{
    string md = File.ReadAllText(mdRenderSrc);
    Check("源码锚：偏移反查 OffsetAtPointer 与 PointerAtOffset 同一套坐标（互逆）",
          md.Contains("public static int? OffsetAtPointer(", StringComparison.Ordinal)
          && md.Contains("public static TextPointer? PointerAtOffset(", StringComparison.Ordinal));
}

if (annRenderSrc != null)
{
    string ar = File.ReadAllText(annRenderSrc);
    Check("源码锚：批注在正文里包 Span（Tag=ann:），不是只在面板里列",
          ar.Contains("Tag = \"ann:\"", StringComparison.Ordinal));
    Check("源码锚：悬停即显示（Span.ToolTip = 批注正文）",
          ar.Contains("ToolTip = body", StringComparison.Ordinal));
    Check("源码锚：橙色虚线下划线（与进度高亮的实线/底带区分开）",
          ar.Contains("TextDecorationLocation.Underline", StringComparison.Ordinal)
          && ar.Contains("DashStyles.Dash", StringComparison.Ordinal));
    Check("源码锚：重叠高亮处只画下划线、不铺底（两片半透明色不叠脏）",
          ar.Contains("if (!slice.Highlighted) inner.Background = AnnBand;", StringComparison.Ordinal));
}

if (annUiSrc != null)
{
    string au = File.ReadAllText(annUiSrc);
    Check("源码锚：预览右键加批注（选中文字即可）",
          au.Contains("OnPreviewContextMenuOpening", StringComparison.Ordinal)
          && au.Contains("SelectedRangeInPreview", StringComparison.Ordinal));
    Check("源码锚：加/改/删都落盘后重画（不是只改内存）",
          au.Contains("SaveAnnotations();", StringComparison.Ordinal)
          && au.Contains("RefreshPreview();", StringComparison.Ordinal));
    Check("源码锚：对照栏里的译文也画批注（切栏/改字号后不丢）",
          au.Contains("ApplyPaneAnnotations(", StringComparison.Ordinal)
          && au.Contains("IsTranslationPath", StringComparison.Ordinal));
}

if (annStoreSrc != null)
{
    string ast = File.ReadAllText(annStoreSrc);
    Check("源码锚：批注只写 annotations.json（不看也不写 progress/project）",
          ast.Contains("ProjectLayout.AnnotationsFile", StringComparison.Ordinal)
          && !ast.Contains("ProgressFile(", StringComparison.Ordinal)
          && !ast.Contains("ProjectFile(", StringComparison.Ordinal));
    Check("源码锚：原子写（先 .tmp 再 move）",
          ast.Contains("string tmp = file + \".tmp\";", StringComparison.Ordinal)
          && ast.Contains("File.Move(tmp, file, overwrite: true)", StringComparison.Ordinal));
}

if (annXamlSrc != null)
{
    string ax = File.ReadAllText(annXamlSrc);
    Check("源码锚：译文栏有「批注」按钮（打开本页批注列表）",
          ax.Contains("x:Name=\"BtnAnnotate\"", StringComparison.Ordinal)
          && ax.Contains("OnShowAnnotationPanel", StringComparison.Ordinal));
}

// ============================================================
// 26. 译文预览按行分段（T-fdbe0163）
// ============================================================
// 为什么测这个：编辑页 TbxTranslation 是 TextBox，一个 \n 就是一个看得见的换行；
// 预览页走 MdParser.Parse，默认把段内软换行拼成一整段 —— 译文若是"每句一行、句间只有单换行"，
// 预览就挤成一整块（董事会反馈）。这里钉住两件事：
//   ① 默认口径（资料栏 / 原文）一个字不动；
//   ② 译文口径按行成段：段数、分段位置、每段锚点都与编辑页逐行对得上，且不吞字不错位。

string softText = "第一句。\n第二句。\n第三句。\n";

var byDefault = MdParser.Parse(softText);
Check("默认口径：单换行仍并成一段（开关默认关）",
      byDefault.Blocks.Count == 1 && byDefault.Blocks[0].LineBreakRole == MdLineBreakRole.None,
      byDefault.Blocks.Count + " 块");
Check("默认口径：拼回去的文字与以前一致",
      Flat(byDefault.Blocks[0].Inlines) == "第一句。第二句。第三句。", Flat(byDefault.Blocks[0].Inlines));

var byLine = MdParser.Parse(softText, softBreakAsParagraph: true);
Check("译文口径：三行分成三段", byLine.Blocks.Count == 3, byLine.Blocks.Count + " 块");
Check("译文口径：三块都是段落", byLine.Blocks.All(b => b.Kind == MdBlockKind.Paragraph));
Check("译文口径：每段文字逐字等于该行、一个字不多不少",
      string.Join("|", byLine.Blocks.Select(b => Flat(b.Inlines))) == "第一句。|第二句。|第三句。",
      string.Join("|", byLine.Blocks.Select(b => Flat(b.Inlines))));
Check("译文口径：每段锚在该行行首（0 / 5 / 10，含行尾那个 \n）",
      string.Join("/", byLine.Blocks.Select(b => b.SourceOffset)) == "0/5/10",
      string.Join("/", byLine.Blocks.Select(b => b.SourceOffset)));
Check("译文口径：段内第一截的偏移也落在行首（批注/进度线靠它）",
      byLine.Blocks.All(b => b.Inlines.Count > 0 && b.Inlines[0].SourceOffset == b.SourceOffset));
Check("译文口径：首行标成段首、其余是续行",
      string.Join("/", byLine.Blocks.Select(b => b.LineBreakRole.ToString()))
          == "GroupStart/Continuation/Continuation",
      string.Join("/", byLine.Blocks.Select(b => b.LineBreakRole.ToString())));

// 空行 + 单换行混排：空行仍然是"段"的分界，连续空行不产生空块
string mixedText = "一段一。\n一段二。\n\n二段一。\n二段二。\n";
var mixedDefault = MdParser.Parse(mixedText);
Check("同一份稿子的默认口径仍是两段（只有空行才分段）",
      mixedDefault.Blocks.Count == 2, mixedDefault.Blocks.Count + " 块");

var mixedLine = MdParser.Parse(mixedText, softBreakAsParagraph: true);
Check("译文口径：空行 + 单换行 → 四段", mixedLine.Blocks.Count == 4, mixedLine.Blocks.Count + " 块");
Check("译文口径：空行前后各成一组（段首/续行成对）",
      string.Join("/", mixedLine.Blocks.Select(b => b.LineBreakRole.ToString()))
          == "GroupStart/Continuation/GroupStart/Continuation");

var blanks = MdParser.Parse("甲\n\n\n\n乙\n", true);
Check("译文口径：三连空行也只分两段，不多出空块", blanks.Blocks.Count == 2, blanks.Blocks.Count + " 块");

// 行内标记：==高亮== 的 SourceOffset 两种口径必须同一个数（分段方式不能挪锚）
string hlText = "前==重点==后\n下一句\n";
var hlDefault = MdParser.Parse(hlText);
var hlLine = MdParser.Parse(hlText, true);
Check("高亮偏移两种口径一个数（分段不挪锚）",
      First(hlDefault.Blocks[0].Inlines, MdInlineKind.Highlight)?.SourceOffset == 3
      && First(hlLine.Blocks[0].Inlines, MdInlineKind.Highlight)?.SourceOffset == 3,
      First(hlLine.Blocks[0].Inlines, MdInlineKind.Highlight)?.SourceOffset.ToString());
Check("译文口径：高亮那一段文字不缺不多", Flat(hlLine.Blocks[0].Inlines) == "前重点后",
      Flat(hlLine.Blocks[0].Inlines));
Check("译文口径：下一句自成一段",
      hlLine.Blocks.Count == 2 && Flat(hlLine.Blocks[1].Inlines) == "下一句");

// 行首缩进的行：块的锚点是行首（进度线立在这一行），文字那一截的锚点按 trim 后第一个字算
var indented = MdParser.Parse("  缩进的行\n下一行\n", true);
Check("译文口径：缩进行块锚在行首、文字锚在第一个字（0 / 2）",
      indented.Blocks.Count == 2 && indented.Blocks[0].SourceOffset == 0
      && indented.Blocks[0].Inlines[0].SourceOffset == 2
      && Flat(indented.Blocks[0].Inlines) == "缩进的行",
      indented.Blocks.Count > 0 ? indented.Blocks[0].SourceOffset + " / " + indented.Blocks[0].Inlines[0].SourceOffset : "没有块");

// 源码锚：两条译文渲染链路都得显式开这个口径，资料栏不许开
string? mainWinSrc = FindUp("src\\YiTai.App\\MainWindow.xaml.cs");
string? panesLineSrc = FindUp("src\\YiTai.App\\MainWindow.Panes.cs");
string? mdParserSrc = FindUp("src\\YiTai.Core\\Markdown\\MdParser.cs");

if (mainWinSrc != null)
{
    string mw = File.ReadAllText(mainWinSrc);
    Check("源码锚：译文预览按行成段（softBreakAsParagraph: true）",
          mw.Contains("MdParser.Parse(TbxTranslation.Text, softBreakAsParagraph: true)", StringComparison.Ordinal));
}

if (panesLineSrc != null)
{
    string ps = File.ReadAllText(panesLineSrc);
    Check("源码锚：对照栏只在译文文件上按行成段，资料栏仍走默认 Parse",
          ps.Contains("softBreakAsParagraph: isTranslation", StringComparison.Ordinal)
          && ps.Contains("IsTranslationPath(", StringComparison.Ordinal));
}

if (mdParserSrc != null)
{
    string mp = File.ReadAllText(mdParserSrc);
    Check("源码锚：默认 Parse 的软换行合并原样在（补空格 / 中文直拼没被改）",
          mp.Contains("var gap = MdInline.Plain(tight ? \"\" : \" \");", StringComparison.Ordinal)
          && mp.Contains("softBreakAsParagraph = false", StringComparison.Ordinal));
}

// ============================================================
// 27. 行号口径（T-1cd1d1b7）
// ============================================================
// 为什么测这个：行号有两处显示（编辑页 TextBox、预览页 FlowDocument），
// 只要两边各算一套，迟早会分家（"预览第 12 行"和"编辑第 12 行"对不上）。
// 这里钉住三件事：
//   ① 口径只有一条：归一化原文（\r\n→\n）的逻辑行号，从 1 起；
//   ② 排版折出来的视觉行不另编号（同一逻辑行里任意位置都是同一个号）；
//   ③ 预览页的每段 SourceOffset 反查出来的号，正好等于编辑页的行序。

string normText = LineMap.Normalize("第一句。\r\n第二句。\r\n第三句。\r\n");
Check("行号口径：\r\n 归一成 \n，行数与行首一致",
      normText == "第一句。\n第二句。\n第三句。\n" && LineMap.CountLines(normText) == 4,
      normText.Replace("\n", "\\n"));

Check("行号口径：行首偏移（0 / 5 / 10 / 15）",
      string.Join("/", LineMap.LineStarts(normText)) == "0/5/10/15",
      string.Join("/", LineMap.LineStarts(normText)));

Check("行号口径：偏移 → 行号（行首是 1 / 2 / 3，末尾空行是 4）",
      LineMap.LineOfOffset(normText, 0) == 1
      && LineMap.LineOfOffset(normText, 5) == 2
      && LineMap.LineOfOffset(normText, 10) == 3
      && LineMap.LineOfOffset(normText, 15) == 4,
      $"{LineMap.LineOfOffset(normText, 0)}/{LineMap.LineOfOffset(normText, 5)}/{LineMap.LineOfOffset(normText, 10)}");

Check("行号口径：同一逻辑行里往后数几个字，还是同一个号（折行不另编号）",
      LineMap.LineOfOffset(normText, 1) == 1
      && LineMap.LineOfOffset(normText, 4) == 1
      && LineMap.LineOfOffset(normText, 9) == 2);

Check("行号口径：空行各占一号，连续空行也各算一行",
      string.Join("/", LineMap.LineStarts("甲\n\n乙\n")) == "0/2/3/5"
      && LineMap.LineOfOffset("甲\n\n乙\n", 2) == 2,
      string.Join("/", LineMap.LineStarts("甲\n\n乙\n")));

Check("行号口径：越界/负数不炸（负数给 1、超长给最后一行）",
      LineMap.LineOfOffset(normText, -3) == 1
      && LineMap.LineOfOffset(normText, 9999) == 4);

// 预览段锚 → 行号，必须与编辑页逐行对上（末尾那个空行预览里没有内容，不给号）
var numByLine = MdParser.Parse(normText, true);
Check("预览段锚反查行号 = 编辑页行序（1/2/3）",
      string.Join("/", numByLine.Blocks.Select(b => LineMap.LineOfOffset(normText, b.SourceOffset))) == "1/2/3",
      string.Join("/", numByLine.Blocks.Select(b => LineMap.LineOfOffset(normText, b.SourceOffset))));

string longLine = new string('长', 60) + "。";
string wrapText = LineMap.Normalize($"短行\r\n{longLine}\r\n最后一行\r\n");
var wrapLines = MdParser.Parse(wrapText, true);
Check("窄栏折行的长句仍只有一个号（不按像素切行）",
      string.Join("/", wrapLines.Blocks.Select(b => LineMap.LineOfOffset(wrapText, b.SourceOffset))) == "1/2/3"
      && LineMap.LineOfOffset(wrapText, 1) == 1,
      string.Join("/", wrapLines.Blocks.Select(b => LineMap.LineOfOffset(wrapText, b.SourceOffset))));

// ---- 源码锚：两处行号共用一套口径、位置来自正文真实排版 ----
string? lineMapSrc = FindUp("src\\YiTai.Core\\Text\\LineMap.cs");
string? lineNumSrc = FindUp("src\\YiTai.App\\MainWindow.LineNumbers.cs");
string? railSrc = FindUp("src\\YiTai.App\\LineNumRail.cs");
string? themeSrc = FindUp("src\\YiTai.App\\Theme.xaml");

Check("LineMap.cs / MainWindow.LineNumbers.cs / LineNumRail.cs 都在",
      lineMapSrc != null && lineNumSrc != null && railSrc != null && themeSrc != null);

if (lineNumSrc != null)
{
    string ln = File.ReadAllText(lineNumSrc);

    Check("源码锚：编辑页行号位置来自正文实测（GetRectFromCharacterIndex），不是自己估折行",
          ln.Contains("GetRectFromCharacterIndex(", StringComparison.Ordinal)
          && !ln.Contains("ViewportWidth - 1", StringComparison.Ordinal));

    Check("源码锚：预览页行号由段锚反查（LineMap.LineOfOffset），与编辑页同号",
          ln.Contains("LineMap.LineOfOffset(text, anchor)", StringComparison.Ordinal)
          && ln.Contains("GetCharacterRect(LogicalDirection.Forward)", StringComparison.Ordinal));

    Check("源码锚：滚动只平移不重算（行号不再靠抄 VerticalOffset 硬追）",
          ln.Contains("ScrollTo(e.VerticalOffset", StringComparison.Ordinal));

    Check("源码锚：量坐标前先排完版（栏位刚变宽变窄时量到的是上一版布局）",
          ln.Contains("TbxTranslation.UpdateLayout();", StringComparison.Ordinal));

    Check("源码锚：量不全不发布半套号码（缺号就排队重量，最多 3 次）",
          ln.Contains("if (missed > 0 && _editorBuildAttempt < 3)", StringComparison.Ordinal));

    Check("源码锚：滚到已摆窗口外面就重建（位置过期时自愈，不拿旧坐标平移）",
          ln.Contains("Covers(e.VerticalOffset", StringComparison.Ordinal));
}

if (railSrc != null)
{
    string rail = File.ReadAllText(railSrc);
    Check("源码锚：行号层是 Canvas + 平移（位置与正文同源，不再补空行）",
          rail.Contains("TranslateTransform", StringComparison.Ordinal)
          && rail.Contains("Canvas.SetTop(t, row.Y)", StringComparison.Ordinal));

    // 号码的 Y 是"内容坐标"（整篇稿子的 Y），靠平移进视口；Canvas 自己按局部坐标裁剪，
    // 一开 ClipToBounds 就会把平移后才进视口的号裁掉（滚一下就整栏空白）。
    Check("源码锚：行号 Canvas 不自己裁剪（裁剪归父 Border，按平移后的坐标裁）",
          !rail.Contains("_canvas.ClipToBounds = true", StringComparison.Ordinal)
          && rail.Contains("public bool Covers(", StringComparison.Ordinal));
}

if (themeSrc != null)
{
    string th = File.ReadAllText(themeSrc);
    Check("源码锚：没有为了对齐去改正文口径（DocBox 仍 15 号 / 行高 29 / Padding 18,8,14,18）",
          th.Contains("<Setter Property=\"FontSize\" Value=\"15\"/>", StringComparison.Ordinal)
          && th.Contains("<Setter Property=\"Padding\" Value=\"18,8,14,18\"/>", StringComparison.Ordinal));
}

if (annXamlSrc != null)
{
    string ax2 = File.ReadAllText(annXamlSrc);
    Check("源码锚：编辑页与预览页各有一条行号栏（预览那条有自己的一套）",
          ax2.Contains("x:Name=\"LineNumCanvas\"", StringComparison.Ordinal)
          && ax2.Contains("x:Name=\"PreviewLineNumCanvas\"", StringComparison.Ordinal));
    Check("源码锚：预览页不压住行号栏（PreviewDoc 只占第 1 列）",
          ax2.Contains("x:Name=\"PreviewDoc\" Grid.Column=\"1\"", StringComparison.Ordinal));
    Check("源码锚：两条行号 Canvas 都不开 ClipToBounds（否则滚动后整栏空白）",
          !ax2.Contains("x:Name=\"LineNumCanvas\" ClipToBounds=\"True\"", StringComparison.Ordinal)
          && !ax2.Contains("x:Name=\"PreviewLineNumCanvas\" ClipToBounds=\"True\"", StringComparison.Ordinal));
}

if (annRenderSrc != null)
{
    string ar2 = File.ReadAllText(annRenderSrc);
    Check("源码锚：批注处有橙色角标（只补一个，悬停同样给正文）",
          ar2.Contains("Run(\"◆\")", StringComparison.Ordinal)
          && ar2.Contains("Tag = \"annbadge\"", StringComparison.Ordinal)
          && ar2.Contains("badge.ToolTip = body", StringComparison.Ordinal));
    Check("源码锚：角标 Tag 不是 int（偏移反查/切句只认 int Tag，锚点不被污染）",
          !ar2.Contains("Tag = slice.End", StringComparison.Ordinal));
}

Console.WriteLine();
if (failed == 0) Console.WriteLine($"全部通过 ✓（{passed} 项）");else Console.WriteLine($"失败 {failed} 项 ✗（通过 {passed} 项）");

return failed == 0 ? 0 : 1;


