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

// ============================================================ 2. 导入源文件
Console.WriteLine("\n== 2. 导入源文件 ==");
string src1 = Path.Combine(box, "ch01.md");
string body = "# Chapter 1\n\nOnce when I was six years old I saw a magnificent picture.\n\nIt was a picture of a boa constrictor.\n";
File.WriteAllText(src1, body);

var added = store.ImportFiles(new[] { src1 });
Check("导入返回一条记录", added.Count == 1, added.Count.ToString());
Check("文件复制进了 sources", File.Exists(ProjectLayout.Sources(root) + "\\ch01.md"));
Check("源文件还在原地（是复制不是搬走）", File.Exists(src1));
Check("清单里登记了", store.Meta.Sources.Count == 1);

string tr = store.TranslationPath(store.Meta.Sources[0]);
Check("译文默认落在 translations\\ch01.md", tr.EndsWith(@"translations\ch01.md"), tr);
Check("空译文已建好（标题行）", File.Exists(tr) && File.ReadAllText(tr).Trim() == "ch01", File.Exists(tr) ? File.ReadAllText(tr) : "不存在");

// 同名再导入：不能盖掉已有副本
var again = store.ImportFiles(new[] { src1 });
Check("同名导入不覆盖", again.Count == 1 && again[0].FileName == "ch01 (2).md", again.Count > 0 ? again[0].FileName : "无");
Check("清单里变成两条", store.Meta.Sources.Count == 2);

// ============================================================ 3. 译文保存
Console.WriteLine("\n== 3. 译文保存 ==");
string tr1 = store.TranslationPath(store.Meta.Sources[0]);
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

// ============================================================ 5. 另存为
Console.WriteLine("\n== 5. 另存为 ==");
string tr2 = Path.Combine(ProjectLayout.Translations(root), "第01章-初稿.md");
TranslationStore.Save(tr2, "第一章\n\n另存的版本。\n", null, ProjectLayout.Backups(root), DateTime.Now);
store.SetTranslationPath(store.Meta.Sources[0], tr2);

var reopened = ProjectStore.Open(root);
string reTr = reopened.TranslationPath(reopened.Meta.Sources[0]);
Check("重开项目后译文指向新位置", reTr.EndsWith("第01章-初稿.md"), reTr);
Check("另存文件存在", File.Exists(reTr));

// ============================================================ 6. 关联旧译文（按顺序配对）
Console.WriteLine("\n== 6. 关联旧译文 ==");
string oldTrDir = Path.Combine(box, "旧译文");
Directory.CreateDirectory(oldTrDir);
File.WriteAllText(Path.Combine(oldTrDir, "第01章.md"), "第一章\n\n旧稿。\n");
File.WriteAllText(Path.Combine(oldTrDir, "第02章.md"), "第二章\n\n旧稿。\n");
File.Delete(ProjectLayout.Translations(reopened.Root) + @"\第01章-初稿.md");   // 先把另存那份移开，凑成 2 对 2
int paired = reopened.LinkTranslationsByOrder(oldTrDir);
Check("两份源文件配到两份旧译文", paired == 2, paired.ToString());
Check("配对结果指向旧译文（两份不同）",
      reopened.TranslationPath(reopened.Meta.Sources[0]).EndsWith("章.md") &&
      reopened.TranslationPath(reopened.Meta.Sources[1]).EndsWith("章.md") &&
      reopened.TranslationPath(reopened.Meta.Sources[1]) != reopened.TranslationPath(reopened.Meta.Sources[0]),
      reopened.TranslationPath(reopened.Meta.Sources[0]));

// 数量不等时必须一份都不动
string mism = Path.Combine(box, "数量不符");
Directory.CreateDirectory(mism);
File.WriteAllText(Path.Combine(mism, "只有一份.md"), "x\n");
int notPaired = reopened.LinkTranslationsByOrder(mism);
Check("数量不等时拒绝配对", notPaired == 0, notPaired.ToString());

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

// ============================================================ 9. 按章号配对（真实目录里的坑）
Console.WriteLine("\n== 9. 按章号配对 ==");
string nb = Path.Combine(box, "章号配对");
Directory.CreateDirectory(nb);
var s3 = ProjectStore.CreateNew(Path.Combine(nb, "proj"), "章号配对");

// 造三份源文件与译文：02 章有个"二校建议"附件，03 章有两个分不出主次的候选
foreach (var (name, text) in new[] { ("ch01.md", "one"), ("ch02.md", "two"), ("ch03.md", "three") })
    File.WriteAllText(Path.Combine(nb, name), text);
s3.ImportFiles(new[] { Path.Combine(nb, "ch01.md"), Path.Combine(nb, "ch02.md"), Path.Combine(nb, "ch03.md") });

string trDirReal = Path.Combine(nb, "旧译文");
Directory.CreateDirectory(trDirReal);
File.WriteAllText(Path.Combine(trDirReal, "第01章.md"), "一\n");
File.WriteAllText(Path.Combine(trDirReal, "第02章.md"), "二\n");
File.WriteAllText(Path.Combine(trDirReal, "第02章-二校建议.md"), "二校\n");
File.WriteAllText(Path.Combine(trDirReal, "第03章-甲.md"), "三甲\n");
File.WriteAllText(Path.Combine(trDirReal, "第03章-乙.md"), "三乙\n");

var (linked, skipped) = s3.LinkTranslationsByNumber(trDirReal);
Check("02 章的附件不抢位（挑干净的 第02章.md）", linked == 2, "linked=" + linked);
Check("03 章两个候选分不出 → 跳过不猜", skipped.Count == 1 && skipped[0] == 3, string.Join(",", skipped));


Console.WriteLine("\n== 8. 项目自带内容 ==");
// 每个导入的源文件都自带一份空译文（ch01.md、ch01 (2).md），另存为又多一份
Check("sources 里两份", Directory.GetFiles(ProjectLayout.Sources(root)).Length == 2,
      Directory.GetFiles(ProjectLayout.Sources(root)).Length.ToString());
Check("translations 里两份（另存那份已移开）",
      Directory.GetFiles(ProjectLayout.Translations(root)).Length == 2,
      Directory.GetFiles(ProjectLayout.Translations(root)).Length.ToString());

// ============================================================ 10. 归档按需建 / 栏位折叠 / 文件读不了
Console.WriteLine("\n== 10. 归档按需建 / 栏位折叠 / 文件读不了 ==");

// T-4：从零建的项目不带空的 sources\；第一次真往里放东西时才建
string t4 = Path.Combine(box, "t4");
var s4 = ProjectStore.CreateNew(t4, "t4");
Check("新项目不预建 sources\\", !Directory.Exists(ProjectLayout.Sources(t4)));

string t4src = Path.Combine(box, "t4-src.md");
File.WriteAllText(t4src, "一个字\n");
s4.ImportFiles(new[] { t4src });
Check("导入时 sources\\ 按需建出来且文件放进去",
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

// 清理
try { Directory.Delete(box, recursive: true); } catch { }

Console.WriteLine();
if (failed == 0) Console.WriteLine($"全部通过 ✓（{passed} 项）");
else Console.WriteLine($"失败 {failed} 项 ✗（通过 {passed} 项）");

return failed == 0 ? 0 : 1;
