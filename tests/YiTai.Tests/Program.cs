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

// ============================================================ 1. 新建项目
Console.WriteLine("== 1. 新建项目 ==");
string box = NewSandbox();

// 测试的"设置读写"必须落在这里，不能碰用户真实的 %APPDATA%\YiTai\settings.json。
// 之前没隔离，每跑一次测试就把用户上次的项目、栏位记忆、最近文件整份覆盖成测试数据。
Environment.SetEnvironmentVariable("YITAI_CONFIG_DIR", Path.Combine(box, "config"));

string root = Path.Combine(box, "我的译本");
var store = ProjectStore.CreateNew(root, "我的译本");

Check("project.json 建出来了", File.Exists(ProjectLayout.ProjectFile(root)));
Check("sources 目录存在", Directory.Exists(ProjectLayout.Sources(root)));
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
Check("原文件还在原地（是复制不是搬走）", File.Exists(src1));
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
Check("没有 BOM（沿用原文件）", !(raw.Length >= 3 && raw[1] == 0xBB), "");
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
var st = new AppSettings { LastProjectRoot = root, LastSourceId = "abc", AutoSaveDelayMs = 1500 };
st.Save();
var st2 = AppSettings.Load();
Check("设置回读正确", st2.LastProjectRoot == root && st2.AutoSaveDelayMs == 1500, st2.LastProjectRoot);

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

// 清理
try { Directory.Delete(box, recursive: true); } catch { }

Console.WriteLine();
if (failed == 0) Console.WriteLine($"全部通过 ✓（{passed} 项）");
else Console.WriteLine($"失败 {failed} 项 ✗（通过 {passed} 项）");

return failed == 0 ? 0 : 1;
