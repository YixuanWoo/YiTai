# 维护手册 · YiTai（译台）

> 交接日期：2026-11-16。这份文件回答"以后怎么管这个仓库"，是维护入口。
> 动手前按顺序读：本文件 → `README.md`（产品边界）→ `docs/TODO.md`（待办与历史决策）。

## 0. 一分钟状态（交接时）

| 项 | 现状 |
|---|---|
| 平台 | Windows / WPF / .NET SDK 10（`net10.0-windows`） |
| 依赖 | Core 零第三方依赖；App 只有 `Microsoft.Web.WebView2`（PDF 原件渲染，运行时随 Edge 安装） |
| 分支 | `master`（CI 已上线：`build-and-test` workflow，push/PR 到 master 自动跑，见 T-13；`ci-pending` 是旧分支，不合并） |
| 构建 | 0 警告 0 错误 |
| 冒烟测试 | **273 项全过**（`.\build.ps1`，退出码 0） |
| 发布 | `.\build.ps1 -Release` → `dist\YiTai.exe`（约 60MB 单文件，自包含） |
| 未完成 | T-15 签名、T-16 检查更新、T-17 大文件性能、T-18 英文界面（T-14 release.ps1 已完成，远端挂 Release 未接） |
| 用户数据 | **不在仓库里**。真实译本在用户自己的项目文件夹；仓库只有 `samples\demo` |

## 1. 红线（改动前先确认没踩）

1. **Core 不知道界面存在**：`YiTai.Core` 只允许项目、文件、文本、落盘；不许出现 WPF 类型。
2. **App 不知道旧模型**：界面层只认"一份文件"，不许出现"章节 / 注解 / 一份原文配一份译文"的推导。
3. **路径只在 `ProjectLayout` 里拼**：别在业务代码里手拼 `project.json`、`translations\`、`.yitai\`。
4. **Core 零第三方依赖；App 只保留 `Microsoft.Web.WebView2`**（PDF 按原件显示）。
   要加新包，先按 T-12 的方式把代价写清楚，再让人拍板。
5. **落盘四条规矩**：换行/BOM 照源文件、原子替换（先 `.tmp` 再 move）、每天首次保存前备份、空稿拒绝落盘。

## 2. 每次改动的工作流

1. **看状态**：`git status`；有未提交改动先弄清是谁的。`docs/TODO.md` 里找对应条目（没有就先加条目）。
2. **先复现**：在 `tests\YiTai.Tests\Program.cs` 里写一条会失败的 `Check(...)`，再改代码。
   - 测试没有框架，`Check(说明, 条件)` 计数，退出码 0 = 全过。
   - 数据类测试一律用 `NewSandbox()` 的临时目录；设置类测试必须设 `YITAI_CONFIG_DIR`。
3. **小步改**：一项一提交，提交信息带条目号（如 `[T-22] 读目录不再误报文件缺失`）。
4. **过门禁**：`.\build.ps1` —— 构建 0 警告 0 错误 + 全部测试通过。**警告也算失败**。
5. **补文档**：用户可见行为变了 → 更新 README「已知边界」；没做的写进 TODO，别写成承诺。
6. **精确提交**：`git add <具体文件>`，禁止 `git add .`。

## 3. 定期体检（建议每周一次；每次接手的第一次必做）

一条命令：

```powershell
.\build.ps1
```

清单：

- [ ] 构建 0 警告 0 错误
- [ ] 测试全过；项数与上次相比**只增不减**（当前 202；删测试要说明理由）
- [ ] `git status` 没有意外的未跟踪文件
- [ ] TODO 里 `[ ]` 项没有过期承诺；勾掉的项写了**验证方式**（命令 + 看到的结果）
- [ ] 依赖没长歪：Core 无 `PackageReference`；App 只有 `Microsoft.Web.WebView2`
- [ ] `samples\demo` 能打开；改过栏位/渲染的版本要真开窗口看一眼
- [ ] 发布前：`.\build.ps1 -Release` 能出 exe，根目录 `YiTai.exe` 已同步

为什么冒烟测试不开窗口：WPF 窗口在 CI/无桌面环境起不来。所以界面回归只能靠人；
测试守的是 Core 与"读源码文本"的样式断言（第 15/16 节），不是像素。

## 4. 发布流程

1. `.\build.ps1 -Release` → `dist\YiTai.exe`，脚本自动把同一份复制到仓库根的 `YiTai.exe`
   （根目录这份是日常双击入口；复制失败只警告，因为程序可能正在运行）。
2. 用 `samples\demo` 真机走一遍：栏位增减、行号、字数、进度高亮、PDF、GFM 表格、预览。
3. 打 tag（`vX.Y.Z`），`gh release create` 挂 `dist\YiTai.exe`（T-14 未做，目前手动）。
4. README 的下载链接按 `latest`，不写死版本号。

## 5. 数据安全（最容易出事的区域）

- **测试绝不碰用户真实数据**：项目用 `%TEMP%` 沙箱；`AppSettings.DefaultPath` 必须被
  `YITAI_CONFIG_DIR` 指到沙箱。2026-11-13 出过一次事故：冒烟测试把用户真实的
  `%APPDATA%\YiTai\settings.json` 整份覆盖了（上次项目、栏位记忆、最近文件全丢）。
- **改 `project.json` / `.yitai\` 结构前**：先整目录备份，保留旧文件读取路径，`Schema` 升版，
  并写"旧文件读进来会怎样"的测试。T-9 / T-11 是范例。
- **迁移只做有把握的**：旧进度键只在"唯一对上"时搬；拿不准就留着等认领。宁可重标，不可错标。
- **删除可恢复**：删文件走回收站；大件先复制到备份目录。禁止通配符删除。
- 用户项目可能正被程序占用：跑引擎落盘前看 `deliverBlockers`（`YiTai*`）。

## 6. 已知坑与风险登记

| 事项 | 现状 / 处理 |
|---|---|
| CI 红灯（T-13 已绿） | 2026-11-17 起 CI 真跑且绿（run 44001556402，success）。以后若变红：先看是真警告/真测试失败还是 runner 的 .NET SDK 版本差异，**修问题，不许改 yml 断言凑绿**（`-warnaserror` 与「≥202」是底线） |
| 根目录 exe 与 dist 不同步 | `build.ps1 -Release` 已自动复制；程序运行中复制失败只警告，别当发布失败 |
| 测试 15/16 找不到源码 | 这两节从**当前目录往上**找 `Theme.xaml`。`build.ps1` 已 `Set-Location` 到仓库根；直接 `dotnet run` 时要在仓库根跑 |
| 行号只标逻辑行 | 设计取舍，见 README；不是 bug |
| `==高亮==` 编辑区无色 | 故意保持纯文本（撤销、IME、复制干净）；别改成富文本 |
| PDF 不抽文字层 | T-12 选了方案 A；要文字模式先读 README「想加功能的时候」的代价清单 |
| 进度键 `rel:` / `abs:` | 同名不同目录必须互不覆盖；旧裸键迁移规则在 `ProgressStore` 文件头 |
| 大文件卡顿（T-17） | 未实测；行号每次输入重建，必要时改增量 + 可见区节流 |
| settings.json 可能被手改坏 | `AppSettings.Load` 已兜 null 集合；新增字段继续按"缺省即可用"设计 |

## 7. 代码地图

`src\YiTai.Core`（不知道界面）：

| 文件 | 职责 |
|---|---|
| `TextFile.cs` | 读（**永不抛异常**，原因进 `TextFileInfo`）/ 原子写 / 每日备份 |
| `Storage\TranslationStore.cs` | 译文读写 + sha 冲突检测 |
| `Project\ProjectStore.cs` | `project.json`、译文的新建/收进/改名、自然排序 |
| `Project\ProjectLayout.cs` | **唯一拼路径的地方** |
| `Project\ProgressStore.cs` | 进度键、旧键迁移与认领 |
| `Project\PaneLayoutStore.cs` | `.yitai\panes.json` |
| `Project\ProjectMeta.cs` | 项目数据模型（只有译文清单，没有源文件清单） |
| `AppSettings.cs` | `%APPDATA%\YiTai\settings.json`（`YITAI_CONFIG_DIR` 可重定向） |
| `Markdown\MdDoc.cs` / `MdParser.cs` | Markdown → 中间结构；唯一解析入口 |
| `Text\SentenceIndex.cs` / `RangeMath.cs` / `TextStats.cs` | 切句、区间运算、字数 |

`src\YiTai.App`（只碰界面）：

| 文件 | 职责 |
|---|---|
| `MainWindow.xaml.cs` | 窗口骨架、打开项目、译文切换 |
| `MainWindow.Panes.cs` | 对照栏（资料/PDF/别的译文），只读 |
| `MainWindow.Marker.cs` | 进度高亮：点击落线、快捷键、活动栏 |
| `MainWindow.LineNumbers.cs` | 逻辑行号 |
| `Markdown\MdRender.cs` | 中间结构 → `FlowDocument`；**只有这里碰 WPF 渲染** |
| `ProgressHighlightLayers.cs` | 底带/下划线/起笔标三层浮层 |
| `Theme.xaml` | 样式单一真源（改公共样式先想清楚影响面） |
| `Diag.cs` / `App.xaml.cs` | 全局异常兜底 + `diag.log` |

`tests\YiTai.Tests\Program.cs`：冒烟测试，无测试框架，靠 `Check` 计数与退出码。

## 8. 交接验收记录（2026-11-16）

- 基线：构建 0 警告 0 错误；冒烟测试 **168 项**全过。
- 本轮修复后：构建 0 警告 0 错误；冒烟测试 **176 项**全过（新增 8 项回归断言）。
- 本轮修掉的缺陷（对应 TODO 第七批 T-22 ~ T-27）：
  1. 栏位指向文件夹时被误报成"文件不在了"（`TextFile.Read` 判断顺序）；
  2. 备份名写死 `.md`，`.md` 与 `.txt` 同名时互相挤掉当天的备份；
  3. 新建译文后 `LastTranslationId` 指向一个清单里不存在的 Id；
  4. 手改坏的 `settings.json` / `project.json` 里 null 集合或 null Id 会让软件空引用；
  5. Markdown 软换行无条件补空格，与文件头"中日韩直接拼"的承诺不符；
  6. `build.ps1` 依赖调用者的当前目录，换目录跑会误报 3 项测试失败。
- 验证证据：`build.ps1` 输出（0 警告 0 错误、176 项全过）；测试覆盖见
  `tests\YiTai.Tests\Program.cs` 第 3、6、7、10、11 节新增断言。

## 8.1 交接验收记录（2026-11-17，T-28 / T-29）

- 接手状态：工作区已有工程岗未提交改动（`MainWindow.Panes.cs`、`MainWindow.xaml.cs`、
  `ProjectLayout.cs`、新增 `WorkTimer.cs` / `WorkTimeStore.cs`），本单在其上继续，未还原未重写。
- 结果：构建 **0 警告 0 错误**；冒烟测试 **176 → 202 项**全过；退出码 0；
  `.\build.ps1 -Release` 出 `dist\YiTai.exe`（59.3 MB）并同步仓库根 `YiTai.exe`。
- 本单修的缺陷（TODO 第八批 T-28 / T-29）：
  1. 只读渲染控件 `IsDocumentEnabled=false` 让表格内嵌滚动条收不到鼠标（看得见点不动）；
  2. 横向滚动条轨道翻页按钮绑了竖向 `PageUp/PageDown`，横向条上点轨道毫无反应（真机验证时发现）；
  3. 工作计时切回前台即开始数秒，与"激活后有新活动才续算"的口径不符（真机验证时发现）。
- 新增断言：`tests` 第 15 节 2 条（横向两段轨道分别绑 PageLeft/PageRight）、
  第 18 节 24 条（工作计时落盘的缺文件/坏 JSON/往返/多日/无 `.tmp`/旧项目/源码锚等）。
- 真机验收：`samples\demo` + 宽表材料；滚动条拖 Thumb / 点轨道左右翻页 / 拖到两端全部生效；
  计时 0:00:00 → 活动后增长 → 失焦冻结 → 回来动一下续算；关窗时账本 360 → 384 秒。
- 数据安全：验证前备份 `%APPDATA%\YiTai\settings.json` 与 `samples\demo`，
  验证后逐字节恢复（sha256 与 mtime 均与备份一致，`.yitai` 新增目录已删除）。
- 未提交：按红线不做 git 提交/推送，改动留给工程岗审查后统一提交。

## 8.2 交接验收记录（2026-11-17，T-13 CI 推进）

- 推送状态核查：本地 HEAD = `c6c6604`，远端实际 sha（`git ls-remote`）= `c6c6604`，
  ahead/behind 0/0，已跟踪文件无改动 —— **昨晚已推送、无需再推**（未造空提交）。
- CI：`.github/workflows/ci.yml` 以 commit `b663114`（信息带 [T-13]）在本地 master 入库。
  门禁只加严不加宽：`dotnet build -warnaserror`（警告按错误处理）＋
  冒烟测试断言收尾行「全部通过 ✓（N 项）」且 N ≥ 202；无跳过测试、无 continue-on-error、
  无屏蔽警告。
- 本地留证：Debug 构建 `0 个警告 / 0 个错误`、退出码 0；冒烟 202 项全过；
  CI 冒烟步骤脚本在本地逐字模拟跑通（正则命中 202 项、退出码 0）。
- 推送实测被拒（本单唯一卡点）：`! [remote rejected] … refusing to allow an OAuth App to
  create or update workflow '.github/workflows/ci.yml' without 'workflow' scope`。
  推送 token scopes 仅 repo / gist / read:org。不绕安全边界，等授权。
- **授权后会发生什么（写明以后代码推送自动跑的检查）**：push / PR 到 master 自动触发
  `build-and-test` workflow —— ① `dotnet restore YiTai.slnx`；② `dotnet build -warnaserror`
  （0 警告 0 错误，有警告即红）；③ 冒烟测试（非 0 退出即红，且项数 < 202 即红）。
  红绿以 Actions 页面为准，不再依赖"本地跑没跑门禁"的口头声明。
- 文档同步：TODO 的 T-13 条目与"前置动作表"已按本记录更新。
- **勘误（2026-11-17 复核）**：本节首行的「本地 HEAD = origin = c6c6604、0/0、昨晚已推送」
  有误——复核时实测本地 HEAD 已是 `815759d`、ahead 2（b663114 + 815759d 均未推）。
  以 8.3 与 Actions 页面为准。

## 8.3 交接验收记录（2026-11-17，T-13 CI 闭环：授权 → 推送 → CI 全绿）

- 授权：屋主授权执行席位操作命令行，`gh auth refresh -h github.com -s workflow`
  （设备码流程，一次性代码 1771-540B，约 1 分钟）；token scopes 由 `gist, read:org, repo`
  增至含 **`workflow`**。
- 推送：`git push origin master` 成功，`c6c6604..815759d`（含 ci.yml 入库与文档同步两个提交）。
- **首个 CI run 全绿**：run `44001556402`（event=push，sha `815759d`），
  2026-11-17 02:14:53Z → 02:15:59Z，**conclusion=success**，用时 66 秒，
  https://github.com/YixuanWoo/YiTai/actions/runs/44001556402 。
  CI 日志末行原文：「全部通过 ✓（202 项）」「冒烟测试 202 项全过（基线 202 项）✓」。
  编译步骤 `-warnaserror` 下零警告零错误。门禁未放宽（无跳过测试 / continue-on-error / 屏蔽警告）。
- 本地等效留证（推送前）：Debug 构建 `0 个警告 / 0 个错误`、冒烟 202 项全过、退出码 0；
  CI 冒烟步骤脚本本地逐字模拟通过。日志备查：任务 output 目录 `logs/gate-20261117-t465.log`。
- 本记录随 commit `[T-13] CI 首跑全绿` 推送，会再触发一次 CI（文档改动，预期同样绿）。
- 文档同步：TODO 的 T-13 已勾选并写明 run 编号与链接；§0 / §6 / §9 同步为已闭环状态。

## 8.4 交接验收记录（2026-11-17，T-906d3686）

- 分工：工程岗出方案与拆卡（`AICompany\work-prod\tasks\T-906d3686\output\技术方案与拆卡.md`），
  执行岗（豆包）按卡写代码与失败断言；工程岗出方案与拆卡、审查 diff、跑门禁与真机验证、提交推送。
- 本轮五项改动（对应 TODO 第九批 T-30 ~ T-34）：
  1. **T-30 省略号单边界**：`SentenceIndex.FromPlain` 引入省略号停顿——
     `.`/`…`/`。` 的极大连续 run（不跨行），`…`≥1 或 `.`≥2 或 `。`≥2 →
     整段 run 连同紧随的 T 类收尾只产生一个句末边界；单个 `.`/`。`、缩写、小数点守卫不变。
  2. **T-31 高亮整句**：Core 新增纯函数 `SentenceIndex.MergeSpansOverZones`（零依赖），
     任一句区间与任一高亮区相交即合并成一句、可传递；`MdRender.SentenceIndexFor`
     拼接时识别 `Tag=="hl"` 的 Span 记高亮区，切句后先合并再换算文档偏移。
  3. **T-32 快捷键重排**：`Ctrl+Alt+Z` 下一句、`Ctrl+Alt+X` 居中找回、`Ctrl+Alt+C` 复制当前句；
     X 不再绑下一句；帮助菜单新增「快捷键对照…」对话框，「关于译台」同步。
  4. **T-33 复制当前句**：`Ctrl+Alt+C` 取活动栏当前句区间，经渲染文档 `TextRange` 取纯文本
     写剪贴板，状态栏提示「已复制当前句（N 字）」。
  5. **T-34 计时显示挪状态栏**：`WorkTimer` 加 `OnDisplay` 回调，状态栏常显「工作 h:mm:ss」；
     计时口径与状态机一字不动（第 18 节断言原样全过），标题后缀保留。
- 测试：冒烟测试 **202 → 254 项**（新增 52 条：第 19 节省略号 17 条、
  第 20 节高亮合并 17 条 + 渲染接线源码锚 2 条、第 21 节快捷键与复制源码锚 9 条、
  第 22 节计时显示源码锚 4 条；均先写先红、后改后绿）。
- 门禁结果（工程岗终审实测）：`.\build.ps1` 退出码 0、Debug 编译 `0 个警告 / 0 个错误`
  （另以 `-warnaserror` 复核 Debug 与 Release 均 0/0）、冒烟 **202 → 254 项全过**；
  真机五项验证见任务报告（省略号一次跨过 / 高亮整句复制 / 旧 X 不跳句 / 复制反馈 / 状态栏计时 0:00:18→0:00:22）。
- CI：推送 `d804a79` 触发 `build-and-test` run **44015319618**，**conclusion=success**（1m5s），
  `-warnaserror` 编译 0/0、冒烟 254 项全过（门禁未放宽）；
  https://github.com/YixuanWoo/YiTai/actions/runs/44015319618 。
- 数据安全：验证前后真实 `%APPDATA%\YiTai\settings.json` 与 `samples\demo` 的 sha256/mtime
  与开工基线逐字节一致；真机走 `%TEMP%` 沙箱 + `YITAI_CONFIG_DIR`，沙箱账本落盘 42 秒（口径未改的旁证）。
- 文档同步：README「用起来」快捷键行、「已知边界」新增省略号与高亮整句两条、
  计时口径表"显示"行补状态栏；TODO 第九批 T-30~T-34 已勾选并写验证方式；CHANGELOG 追加五项。

## 8.5 交接验收记录（2026-11-17，T-1301cc93：T-35 / T-36）

- **缺陷一（用户实测）**：英文资料里点间带空格的省略号 `. . .` 仍被切成多句。
  复现（`%TEMP%` 沙箱 4 栏英文材料 + `YITAI_CONFIG_DIR` 重定向）：
  `He paused . . . then he left.` 把进度线落在 `. . .` 上，栏底读「第 1/4 句」，
  要连按「下一句」几次才跨得过去；高亮只覆盖单个 `.`（截图
  `output\repro-before\02-c-spaced-after3z.png`，读数 `readings-before.txt`）。
  根因：`SentenceIndex.FromPlain` 的省略号 run 扫描（`src\YiTai.Core\Text\SentenceIndex.cs:122-131`，
  T-30 引入）只认**相邻**的 E 字符，`. . .` 在空格处断链 → 每个 `.` 落回单句号分支各自收句；
  T-30 那 17 条断言全是连续写法，没覆盖"点间空格"，所以上一轮"修过了"却没拦住英文场景。
- **修法（T-35）**：连续 run 之外再认"只隔空格/制表符的 E 段链"——
  累计 ≥3 个点即按一段省略号收句（连同紧随的 T 类收尾与引号括号）；
  段间夹数字/字母（`Item 1. Item 2.`、`A. B`）不算一串，行为照旧。
- **缺陷二（T-36）**：状态栏有计时后标题栏仍拼「· 工作 h:mm:ss」，整窗两个计时器。
  修法：`WorkTimer.RefreshTitle` 只写 `_baseTitle`（「译台 — 项目名」）；
  状态栏 `OnDisplay` 口径一字未动；原"标题时长格式 h:mm:ss"源码锚改钉到状态栏口径，
  并新增"标题不再拼时长"锚。
- **测试**：**254 → 275 项**（第 23 节新增 19 条英文省略号边界：英文句中/句尾三点、`. . .`、
  `。 。 。`、三点接引号/括号、大写新句、`Mr.`/`e.g.`/`3.14`/`A. B`/普通句号回归，
  以及 `etc...`/`U.S...` 这类"缩写词紧跟省略号"不再被缩写表吞掉边界；
  第 18 节 +2 条计时显示锚）。先写失败断言（8 条先红）后改代码，原有 254 条零删改零放宽。
- **门禁**（本机实测，UTF-8 原始日志落在任务 output：`gate-before-utf8.log` /
  `gate-after-debug-utf8.log` / `gate-after-release-utf8.log`）：`.\build.ps1` 退出码 0、
  Debug `0 个警告 / 0 个错误`、冒烟 **254 → 273 项全过**；
  `dotnet build YiTai.slnx -c Release` 同样 0 警告 0 错误。
- **真机（改后，同一份沙箱材料）**：`c-spaced` 栏「第 1/2 句」，按一次「下一句」→「第 2/2 句」，
  高亮从整段 `. . .` 跳到 `then he left.`；标题「译台 — T-1301 英文省略号沙箱」不含时长，
  状态栏「工作 0:00:0x」是整窗唯一计时（截图 `output\repro-after\00/01/02` 系列、
  读数 `readings-after7.txt`）。
- **CI**：代码提交 `175491a` → run **44037557117**（success）；
  文档提交 `23a10e9` → run **44037790261**（success）；
  补 `etc...` 守卫与同步数字后再推一次，run 号与结论见任务报告。
  https://github.com/YixuanWoo/YiTai/actions/runs/44037557117 。
- **数据安全**：真机全程 `%TEMP%` 沙箱 + `YITAI_CONFIG_DIR`；验证前后真实
  `%APPDATA%\YiTai\settings.json` 与 `samples\demo` 的 sha256/mtime 逐条一致
  （证据：任务 output `data-safety-pre.json` / `data-safety-post.json`）。
- **文档同步**：README「已知边界」省略号一条补点间空格口径、计时表"显示"行改为只在状态栏；
  TODO 新增第十批 T-35 / T-36（写验证方式）；CHANGELOG 修复/变更各一条；本文件 §0 测试数改 273。

## 9. 需要人拍板的事

1. **~~T-13 CI~~（已拍板并完成，2026-11-17）**：屋主授权 `workflow` scope
   （`gh auth refresh`，浏览器输一次性代码）后一次 `git push origin master` 即触发首个 run
   并全绿（run 44001556402）。T-13 闭环，不再是待拍板项。
2. **T-14 / T-15**：release.ps1 与代码签名做不做（签名要自备证书）。
3. **T-17**：大文件性能现在做还是等真实卡顿反馈。
4. **项目归属**：YiTai 目前不在 AICompany 的项目配置里。若要纳入公司项目管理，
   建议项目 id `yitai`、显示名「译台」、`rootDir` 指向本仓库（这需要改公司配置，留给董事会/屋主确认）。

## 10. 一键体检（`tools\maintenance-check.ps1`）

第 3 节那份清单有 7 条，人跑一遍容易漏。`tools\maintenance-check.ps1` 把能自动判的那几条合成一条命令：

```powershell
# 体检；报告默认写 %TEMP%\yitai-maintenance-<时间戳>.json
.\tools\maintenance-check.ps1

# 顺便把 JSON 打到 stdout（stdout 上只有 JSON）
.\tools\maintenance-check.ps1 -Json

# 指定报告位置
.\tools\maintenance-check.ps1 -OutputPath D:\tmp\yitai-check.json

# 手上有意留着的改动，先跳过 git 检查
.\tools\maintenance-check.ps1 -AllowDirty

# 造失败用例：指向一个不存在的构建脚本
.\tools\maintenance-check.ps1 -BuildScript .\nope.ps1
```

参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `-RepoRoot` | 脚本所在 `tools\` 的上一层 | 仓库根；一般不用给 |
| `-BuildScript` | `build.ps1` | 被体检的构建脚本，相对仓库根解析（造失败用例就改它） |
| `-ExpectedTestCount` | `176` | 冒烟测试项数下限；少于它判失败，多于它通过并在摘要里标出（当前实际 202，下限保持接手时的 176） |
| `-AllowDirty` | 关 | 跳过 git 工作区检查 |
| `-Json` | 关 | 把 JSON 报告同时写到 stdout |
| `-OutputPath` | `%TEMP%\yitai-maintenance-<时间戳>.json` | 报告落盘位置 |

4 项检查（任一失败即非 0 退出）：

1. **关键文件存在**：`build.ps1`（或 `-BuildScript` 指定的那个）、`docs/MAINTENANCE.md`、`docs/TODO.md`。
2. **构建 + 冒烟测试**：调 `build.ps1`（Debug），断言退出码 0、`0 个警告 / 0 个错误`、收尾行是 `全部通过 ✓（N 项）`，且项数 ≥ `-ExpectedTestCount`（默认 176 是接手时的下限，当前实际 **202**）。
3. **Release 构建**：单独跑 `dotnet build YiTai.slnx -c Release`（`build.ps1` 不带 `-Release` 时只编 Debug，Release 的警告没人看，所以这里补一遍），断言退出码 0、0 警告 0 错误。Debug 没过时这一项记「跳过」。
4. **git 工作区**：只看**代码 / 工程文件**（`src`、`tests`、`dist`、`tools`、`.github`、`build.ps1`、`YiTai.exe`、`*.csproj` / `*.slnx` / `*.ps1` / `*.yml` / `*.xaml` 等）有没有未提交改动。
   文档改动（`*.md`）和未跟踪文件只作提示、不算失败——接手时仓库本来就可能带着没提交的 docs 草稿，写文档也不该把体检搞红。要看细节读 JSON 里的 `docChanges` / `untracked`。

退出码：

| 码 | 含义 |
|---|---|
| `0` | 全部通过（`-AllowDirty` 的跳过项不算失败） |
| `1` | 有必检项失败；摘要末尾按编号列出失败项 |
| `2` | 连体检都做不起来（仓库根不存在、参数不对） |

JSON 报告（`schemaVersion` 1）：

- `checks[]`：每项的 `id` / `title` / `status`（`pass` / `fail` / `skipped`）/ `detail` / `durationSeconds` / `assertions[]`（`name` / `expected` / `actual` / `pass` / `message`）
- `summary`：`total` / `passed` / `failed` / `skipped` / `ok`
- `failures[]` 失败清单；`notes[]` 提示（比如报告被写进了仓库）
- git 项另有 `codeChanges` / `docChanges` / `untracked` 三个数组；构建项有 `log`（完整日志在 `%TEMP%`）

使用注意：

- 只用 Windows 自带 PowerShell 能力，不引第三方模块，脚本自身不联网（构建时的包还原是 `build.ps1` / `dotnet` 的事）。
- 报告和构建日志都写 `%TEMP%`，不落仓库。
- 完整构建输出不往屏幕上糊（近 200 项太长）；失败时 JSON 只带末尾 12 行 `outputTail`，要全的看 `log`。
- 它替不了人：`samples\demo` 真机开窗口看一眼、依赖没长歪、TODO 承诺没过期、`build.ps1 -Release` 出 exe 并同步，这几条还得照第 3 节人工过。