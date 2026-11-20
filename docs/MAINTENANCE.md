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
| 冒烟测试 | **378 项全过**（`.\build.ps1`，退出码 0） |
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
7. **出可运行产物（硬性，每次更新都要做）**：**每次软件更新都必须重新编译出可双击运行的 exe**，
   只改源码不重编等于没改——董事会拿到的还是上次那份老程序（T-4292b0fa 就断在这一层）。
   - 跑 `.\build.ps1 -Release`：先过 Debug 门禁与全部冒烟测试，再 `dotnet publish` 出自包含单文件
     `dist\YiTai.exe`，脚本自动同步一份到仓库根 `YiTai.exe`（日常双击入口）。
   - 同步失败只警告不报错（exe 被占用时）。必须亲眼看到输出里出现「已同步到 …\YiTai.exe」那一行；
    没出现就关掉占用程序重跑，不能当没事。
   - 收工核对新 exe 的 mtime 与 sha256 确实变了（旧值记进任务报告作对比），
    别让「代码改了、双击还是老版本」再发生。
   - 任务报告必须写明四件事：产物绝对路径、构建时间、对应 commit（`git rev-parse HEAD`）、
    真机验证结论。**缺这四项的更新不算完成**。
   - 例外仅限纯文档/纯测试改动、且不改变任何用户可见行为；只要动了 `src\` 下的代码
    （哪怕一行）或改了用户可见行为，就必须重编。

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

> **先决条件**：本次更新已按第 2 节第 7 步重编出可双击 exe（含根目录 `YiTai.exe` 同步成功）；
> 没有新产物的更新不得进入本节流程。发布前先核对产物 mtime / sha256 与构建 commit。

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
| `Project\AnnotationStore.cs` | `.yitai\annotations.json`：批注的读写（原子写）、按 quote 就近回贴锚点 |
| `Text\AnnotationMath.cs` | 批注区间与 `==高亮==` 的切段（纯函数，无界面概念） |
| `Project\ProjectMeta.cs` | 项目数据模型（只有译文清单，没有源文件清单） |
| `AppSettings.cs` | `%APPDATA%\YiTai\settings.json`（`YITAI_CONFIG_DIR` 可重定向） |
| `Markdown\MdDoc.cs` / `MdParser.cs` | Markdown → 中间结构；唯一解析入口 |
| `Text\SentenceIndex.cs` / `RangeMath.cs` / `TextStats.cs` | 切句、区间运算、字数 |
| `Text\LineMap.cs` | 行号口径（纯函数）：`\r\n→\n` 归一化、行首下标、偏移→逻辑行号（1 起）。行号栏与预览行号共用这一份，别再各算各的 |
| `CopyCue.cs` | 复制成功/失败的提示音（纯计算合成 16 位单声道 WAV，零依赖） |

`src\YiTai.App`（只碰界面）：

| 文件 | 职责 |
|---|---|
| `MainWindow.xaml.cs` | 窗口骨架、打开项目、译文切换 |
| `MainWindow.Panes.cs` | 对照栏（资料/PDF/别的译文），只读 |
| `MainWindow.Marker.cs` | 进度高亮：点击落线、快捷键、活动栏 |
| `MainWindow.LineNumbers.cs` | 逻辑行号：编辑页按 `GetRectFromCharacterIndex` 的真 Y 坐标摆号（不问折几行），预览页按块锚点反查行号；两份共用 `Text\LineMap` |
| `LineNumRail.cs` | 行号栏的 `Canvas` 轨道：行号池化复用、整体平移随滚动、滚出缓冲窗才重建 |
| `Markdown\MdRender.cs` | 中间结构 → `FlowDocument`；**只有这里碰 WPF 渲染** |
| `ProgressHighlightLayers.cs` | 底带/下划线/起笔标三层浮层 |
| `MainWindow.CopyFeedback.cs` | 复制当前句的当场确认：浮条动画（`CopyToast`）+ 提示音播放器（`SoundPlayer`） |
| `AnnotationRender.cs` | 批注 → `Span` 包层（橙色虚线下划线 + 极浅底 + ToolTip）；与 `==高亮==` 合成的唯一处 |
| `MainWindow.Annotations.cs` | 批注的加/改/删、本页面板、译文预览与对照栏的批注挂载与回贴 |
| `AnnotationWindows.cs` | 「加批注 / 改批注」与「本页批注」两个窗 |
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

## 8.6 交接验收记录（2026-11-18，T-37：复制当前句的成功/失败反馈）

- **症状（用户实测）**：按 `Ctrl+Alt+C` 之后"毫无反应"。原来的唯一反馈是状态栏一行 11.5px 浅灰字，
  还和自动保存的「已保存 hh:mm:ss」抢同一个位置；更硬的断点是 `CopyCurrentSentence` 里
  四条提前 `return`（没正文 / 指针取不到 / 空句 / `Clipboard.SetText` 抛异常）连那行灰字都没有。
- **设计**（形式由执行岗定，逐条理由写在 `MainWindow.CopyFeedback.cs` 与 `CopyCue.cs` 的注释里）：
  · **声音**：Core 新增 `CopyCue`（纯计算合成 16 位单声道 WAV，零第三方依赖）——
    成功 100ms 上滑 780→1180Hz、峰值 0.28；失败 140ms 下滑 560→340Hz、峰值 0.34；
    sin² 包络两端归零（不爆音）。一声就完、不排队：连按是同一个音重来，不叠加、不拖长。
  · **视觉**：`MainWindow.xaml` 新增 `CopyToastLayer`（正文区底部、状态栏上方居中，`IsHitTestVisible=False`）：
    成功绿 `✓` +「已复制当前句（N 字）」，失败橙 `！` + 一句原因；淡入 120ms，
    成功停 1.15s、失败停 2.3s 后淡出。只动透明度与 6px 上浮（RenderTransform）——
    不重排布局、不动焦点、不挡鼠标，连续按不会牵着正文跳。
  · **失败可区分且不误导**：颜色（绿/橙）、符号（✓/！）、音调走向（上滑/下滑）、停留时长四处都不同；
    失败路径**不写剪贴板**（`Clipboard.SetText` 全文件只出现一处），不会"报失败却把别的句子塞进去"。
  · **反馈在写成功之后**：多了"按了没成"的明说，复制本身的三步（取句区间 → 渲染文档 TextRange 取纯文本 →
    写剪贴板）一个字没动；提示音放不出来只记日志（`Diag.Error("复制提示音")`），不影响复制与浮条。
- **测试**：**275 → 299 项**（第 24 节 24 条：WAV 头/格式/时长与采样数吻合、上滑与下滑的零交叉比较、
  峰值区间、首尾归零、逐字节可复现，以及"成功反馈在 `Clipboard.SetText` 之后""失败必有反馈"
  "`Clipboard.SetText` 只出现一次""浮条不吃鼠标"等源码锚）。先写断言后改代码，原有 275 条零删改零放宽。
- **门禁**：`.\build.ps1` 退出码 0、Debug `0 个警告 / 0 个错误`、冒烟 **275 → 299 项全过**；
  `dotnet build YiTai.slnx -c Release` 同样 0 警告 0 错误。
- **真机**（`%TEMP%` 沙箱项目 + `YITAI_CONFIG_DIR` 重定向，未碰真实译稿/配置）：
  · 成功两例（剪贴板与当前高亮句逐字对照）：第 1 句 →「第一段：夜里他把窗推开一条缝，海风灌了进来。」（22 字）；
    第 2 句 `==灯亮着，屋里却没有人说话。==` →「第二段：灯亮着，屋里却没有人说话。」（17 字，无 `==`）。
    浮条文字与状态栏同步（UIA 读数：「已复制当前句（22 字）」/「（17 字）」），
    截图 `output/evidence/01…`、`02…`。
  · 连按 10 次：诊断日志 `copy 10 fail 0`（一次不漏），第 2 下与第 10 下截图都能看到浮条（`03…`、`04…`）。
  · 失败两例：切到空白栏 →「这一栏还没标进度，先点一下正文」；
    另一进程用 `OpenClipboard` 占住剪贴板再按 →「剪贴板被别的程序占着，没能复制，请再按一次」，
    放开后剪贴板仍是占住前那句（失败不污染剪贴板）。截图 `05…`、`06…`。
  · 其它行为未受影响：状态栏计时照常走（截图 0:00:08 → 0:00:50 → 0:01:45）；
    `Ctrl+Alt+Z` 仍推进（栏底第 1/5 句 → 第 2/5 句）。

## 8.7 交接验收记录（2026-11-19，T-a451712a：译文批注）

- **要的东西**：译文渲染视图里选中文字就能加批注；批注处正文有看得见的标记
  （橙色虚线下划线 + 极浅底），鼠标悬停直接把批注正文显示出来；能改、能删；
  关掉重开、切栏、改字号、滚动之后批注仍钉在原来那几个字上。
- **不碰的东西**（硬约束）：只新增 `<项目根>\.yitai\annotations.json`；
  `project.json` / `progress.json` / `panes.json` / `worktime.json` 结构与 `translations\*.md` 正文一字不动；
  不引入第三方依赖；导出 / 共享 / 云同步一律不做。
- **锚点怎么钉**：批注存 `FileKey`（`rel:…` 或 `abs:…`）+ 归一化字符偏移 `[Start,End)` + `Quote`。
  读档时先原位校验（该区间文字是否仍等于 `Quote`）；不等就在 ±600 字窗口内找同一段文字就近回贴；
  仍找不到就标成"未定位"，**绝不硬贴到别的文字上**。偏移坐标系与进度高亮、`Run.Tag` 同一套
  （`\n` 归一化后的源文档偏移），所以 `Navigation`/切句的既有口径不受影响。
- **怎么画**：新增 `AnnotationRender.Apply` 在 `MdRender.Build` 之后把区间包成 `Span`（`Tag="ann:<id>"`），
  样式是橙色（`#C2410C`）`Dash` 虚线（1.5px）+ 非高亮段 `#30` 极浅底；与既有 `==高亮==` 重叠时
  **只画下划线、不铺底**——两片半透明色不叠脏，高亮的粉底还看得见。`ToolTip` 就是批注正文本身。
  改的是渲染文档，**不碰译文正文**：冒烟断言里有一条专门比"画批注前后纯文本逐字相同"。
- **入口**：译文栏头新增「批注 N」按钮（`BtnAnnotate`）开「本页批注」窗（每条带 定位 / 改 / 删）；
  译文预览与对照栏里对选中文字右键 →「为选中文字加批注…」，落在批注上右键 →「改这条批注…」「删这条批注」；
  对话框多行输入，`Ctrl+Enter` 保存、`Esc` 取消，空正文有明确拦截（「批注正文不能是空的。」）。
- **测试**：**299 → 336 项**（第 25 节 37 条：缺文件/坏 JSON 当空表、原子写先 `.tmp` 再 move、
  中文不转义、增改删往返、原位命中 / 前置插字回贴 / 原文改掉后标未定位 / 两处同文取最近、
  `rel:` 与 `abs:` 键不串、同名不同目录不串、`Slice` 与高亮切段、`OffsetAtPointer` 与 `PointerAtOffset` 互逆、
  `Span` 的 `Tag`/`ToolTip`/虚线、重叠处不铺底、只写 `annotations.json` 等源码锚）。原有 299 条零删改零放宽。
- **门禁**：`.\build.ps1` 退出码 0、Debug `0 个警告 / 0 个错误`、冒烟 **336 项全过**；
  `.\build.ps1 -Release` 同样 0/0 → `dist\YiTai.exe`（59.3 MB）并打印「已同步到 D:\开源\YiTai\YiTai.exe」；
  根 exe 62,162,628 B、mtime 2026-11-19 23:51:49、sha256 `4D7FE8AB3B97C2B8A5C4A3E3F3C876B13E697175CB6D30BF2D33E05EF891A0A2`。
  **注意**：HEAD 仍是 `9fb4906`，这批改动尚未提交（按规矩由工程岗归口提交后再重编一次）。
- **真机**（`%TEMP%\yitai-ann-T-a451712a` 沙箱项目 + `YITAI_CONFIG_DIR` 重定向，真实译稿与
  `%APPDATA%\YiTai\settings.json` 未碰；沙箱译文里预置了 `==站在门口，用==` 制造"批注压着高亮"的情形）：
  · 编辑态对照栏已见批注（`gui-02-editmode-pane.png`），切到预览后同一段字仍在（`gui-03-preview.png`）；
  · 预览里拖选「守塔人一天爬两趟那一百」→ 右键菜单（`复制 / 为选中文字加批注… / 改这条批注… /
    删这条批注 / 本页批注列表…`，未选中文字时加批注项呈灰）→ 对话框引文与选区一致 → 填正文保存
    （`gui-10-dialog.png`、`gui-11-dialog-filled.png`），保存后正文立即出现橙色虚线下划线与浅底，
    `批注 2 → 3`、状态栏「已加批注（本页 3 条）」（`gui-12-added.png`）；
  · 悬停批注：ToolTip 原文「这句太白了：礁石的尽头立着灯塔，比「灯塔立在礁石尽头」更冷一点。」
    （`gui-13-tooltip.png`，UIA 亦读到同一句）；
  · 「本页批注」面板列出 3 条（引文 + 正文 + 定位/改/删，`gui-14-panel.png`）→ 改第 2 条 → 引文预填正确
    （`gui-15-edit-dialog.png`）→ 保存后面板与落盘同步（`gui-17-edited-panel.png`）→ 删同一条 → 面板剩 2 条
    （`gui-18-deleted.png`）；
  · 关掉重开：两条批注仍在原字上、`批注 2`（`gui-19-reopen.png`，重启后先回到编辑态再切预览）；
  · 改字号 + 滚动：对照栏 `100% → 150% → 250%`，滚轮翻到中段，"站在门口，用"仍精确压在
    `==高亮==` 上（`gui-22-zoom150.png`、`gui-23-scrolled.png`）；
  · 切栏：预览 ⇄ 回到编辑来回切，批注不丢（`gui-24-backtoedit.png`、`gui-25-previewback.png`）；
  · 旧项目兼容：拿没有任何 `.yitai\annotations.json` 的原样副本打开，正常显示、`批注` 按钮无计数、
    **不会凭空创建 annotations.json**（`gui-26-oldproject.png`）。
- **已知边界**（别当 bug 提）：批注只在能选中文字的渲染视图里加（译文编辑态的 `TextBox` 里没有）；
  `==` 标记本身被选中时会落在标记字符上；原文删掉那一句后批注会在面板里显示"未定位"而不是消失；
  批注不做导出/共享，也不进 `project.json`。

## 8.8 交接验收记录（2026-11-20，T-fdbe0163：译文预览按行分段）

- **要的东西**：董事会反馈预览页"完全没有分行分段、文字挤成一整块"。同一份译文在预览页的
  段落划分 / 换行要与编辑页一致、可顺畅阅读，批注标记仍钉在原文位置；并按发布流程编出最新 exe。
- **根因（复核属实）**：编辑页 `TbxTranslation` 是 `TextBox`，一个 `\n` 就是看得见的一行；
  预览页 `RefreshPreview()` 走 `MdParser.Parse`，默认把段内软换行（单个 `\n`、行间无空行）
  拼成一个 `Paragraph`（中文两侧直拼、西文补空格），只有空行才真正分段。
  译文若是"每句一行"的常见排版，预览就被拼成一整块。demo 样本恰好是空行分段，所以只在
  单换行稿子上复现 —— 用 HEAD 版 `MdParser` 单独跑同一份三行稿子，实测**只有 1 段**。
- **改法（默认口径零变化）**：`MdParser.Parse(text, softBreakAsParagraph = false)` 新增开关；
  `FlushPara` 在开关打开时逐行成段，不插 gap inline、不拼字，每行的 `SourceOffset` 本来就是行首。
  `MdBlock.LineBreakRole` 只标"段首 / 续行"（Core 不碰像素）；`MdRender.ParagraphBlock` 里
  默认口径仍是"段间距挂段尾"（一个字没改），译文口径把段间距挪到段首上方、续行贴紧上一行，
  于是空行分开的段仍有间隔、同段续行正好是一行接一行。
- **接线（只有两条译文链路）**：`MainWindow.xaml.cs` 的 `RefreshPreview()` 传 `true`；
  `MainWindow.Panes.cs` 的 `RefreshOne()` 先用 `IsTranslationPath` 判断这一栏放的是不是本项目
  `translations\` 里的译文，只有是才传 `true` —— 资料栏 / 原文 / PDF 一律默认口径。
- **测试**：**336 → 355 项**（第 26 节 19 条：默认口径 1 段且角色为 None、译文口径 3 段逐字一致、
  段锚 0/5/10、段内首截偏移=行首、空行+单换行 4 段且段首/续行成对、三连空行不出空块、
  `==高亮==` 两种口径偏移同为 3、缩进行的块锚 0 / 字锚 2、两条链路的源码锚）。原有 336 条零删改。
- **门禁**：`.\build.ps1` 退出码 0、Debug `0 个警告 / 0 个错误`、冒烟 **355 项全过**；
  `.\build.ps1 -Release` 同样 0/0 → `dist\YiTai.exe`（59.3 MB）并打印「已同步到 D:\开源\YiTai\YiTai.exe」。
  根 exe 62,162,947 B、mtime 2026-11-20 01:57:02、sha256 `5197E9311D5F1C22A23466433754579F514B7C7D6A98411867BC29F26C8B7995`；
  **HEAD 仍是 `c0305a6`，本轮改动尚未提交**（按规矩由工程岗归口提交后再重编一次）。
- **真机**（`%TEMP%\yitai-preview-T-fdbe0163` 沙箱项目 + `YITAI_CONFIG_DIR` 重定向，
  真实 `%APPDATA%\YiTai\settings.json` 的 mtime/哈希收工核对未变）：
  沙箱译文 11 行"每句一行"、其中一句带 `==高亮==`、两句预置批注；对照栏直接放这份译文。
  · 编辑页 UIA 读到 11 行正文；预览页文档读到 **11 行**、逐行文字与编辑页一致；对照栏同样 11 行
    （旧代码下这里只会是 3 行 = 7+3+1）；空行处的段间距在截图里看得见（`gui-04-preview.png`）；
  · 批注：悬停读出正文（`gui-05-hover-tooltip.png`）；面板 2 条「都钉在正文上」→ 改第 2 条
    （`gui-07-panel-edited.png`）→ 删第 1 条（`gui-08-panel-deleted.png`），落盘只剩 1 条且偏移未变；
  · 关掉重开：预览仍 11 行、批注仍钉原字、`annotations.json` 哈希不变（说明是原位校验通过，
    没有走回贴改写）；在译文开头插一行（整体后移 21 字）再重开 → 面板仍显示"钉在正文上"、
    偏移自动 156 → 177、悬停照常（quote 回贴有效，`gui-13-rebased-tooltip.png`）；
  · 发版产物：根目录 `YiTai.exe`（新 sha256/大小）在同一沙箱里打开，预览 12 行、批注悬停正常
    （`gui-15-release-preview.png`、`gui-16-release-tooltip.png`）。
- **已知边界**：预览只在译文上按行成段；资料栏 / 原文仍按 Markdown 合并软换行（有意不同口径）。
  译文里"一行写半句、下一行接着写"的稿子，预览也会照样分成两行 —— 这正是编辑页看到的样子。

## 8.9 交接验收记录（2026-11-20，T-1cd1d1b7：行号对齐 / 预览行号 / 批注角标）

- **要的东西**：①译文栏拉窄、滚动时行号不错行；②预览页加只读行号栏，与编辑页同一逻辑行同号；
  ③有批注处加显眼角标，悬停即显示批注正文；④预览页右键加注保存后角标立即出现、不切回编辑页；
  ⑤批注锚定与持久不回退；并按发布流程重编根目录 exe。
- **根因（复核属实）**：旧 `MainWindow.LineNumbers.cs` 用隐藏 `TextBlock` 估算每个逻辑行折了几行、
  往行号里补 `\n`、再抄 `VerticalOffset` 对齐；估算宽度取 `_editorScroller.ViewportWidth - 1`，
  **没扣 `DocBox` 的 `Padding=18,8,14,18`（左右 32px）**，所以栏一窄、长句一折行，
  估算行数与实际行数就差一截，行号整体往上漂。
- **改法（不问"折几行"，改问"正文在哪"）**：新增 `src\YiTai.Core\Text\LineMap.cs` 统一行号口径
  （`\r\n→\n` 归一化、行首下标、偏移→1 起逻辑行号、末尾空行也给号、越界不炸）；
  新增 `src\YiTai.App\LineNumRail.cs`（`Canvas` + `TranslateTransform`，行号出自池子、滚动只平移，
  滚出 320px 缓冲窗才重建）。编辑页行号位置来自 `TextBox.GetRectFromCharacterIndex` 的**视口坐标**
  实测（行首 X/Y 都是真的），末尾空行用"最后一行 Y + 行高"补；预览页行号由块
  （`MdBlock` / `ListItem` / `Paragraph` 首 `Run`）的 `SourceOffset` 经 `LineMap.LineOfOffset`
  反查，两条链路同一个号。`DocBox` 字号 15 / 行高 29 / Padding 一字未改（冒烟有源码锚钉着）。
- **踩到的坑（都已修）**：`Canvas` 自己的 `ClipToBounds` 挡不住平移后的越界号码，必须父
  `Border` 也裁剪（否则窗口外会飘一个"1"）；预览文档初始 `Collapsed` 时模板没建，
  `_previewScroller` 为 null 就 return，导致首屏一个号都没有 —— 改成先 `AttachPreviewScroller()`
  再判断，并对"刚换文档还没排完"补一次排队。
- **批注角标**：`AnnotationRender.Apply` 用 `HashSet<string>` 保证每条批注在 slice 末尾只补一个
  `Run("◆")`（`FontSize=10`、`BaselineAlignment=Superscript`、橙色 `AnnLine`、
  `Tag="annbadge"`、`ToolTip=正文`）。角标 `Tag` 不是 `int`，而 `MdRender.MakeRun` /
  `PointerAtOffset` / `OffsetAtPointer` / `HighlightRanges` 全部只认 `int Tag`，
  所以角标不会污染偏移；`==高亮==` 会截断批注 slice、角标仍落在批注真正的末尾。
- **预览右键加注**：确认链路本来就是"落盘 → `RefreshPreview()` → `ScrollPreviewTo`"，
  本轮在 `RefreshPreview()` 末尾补 `QueuePreviewLineNumbers()`，于是角标、浅底虚线、行号
  同帧出现，仍停在预览页（按钮仍是「回到编辑」）。注意加批注窗的保存键是「保存（Ctrl+Enter）」，
  单发 `Enter` 只换行。
- **测试**：**355 → 378 项**（第 27 节 23 条：LineMap 归一化/行首 0/5/10/15、偏移→行号、
  折行不另编号、空行各占一号、越界不炸、预览段锚反查 1/2/3，以及源码锚——编辑页位置来自
  `GetRectFromCharacterIndex` 且不再出现 `ViewportWidth - 1`、预览走 `LineMap.LineOfOffset`、
  滚动只 `ScrollTo` 平移、行号层是 `Canvas` + `Canvas.SetTop(t, row.Y)`、`DocBox` 口径未改、
  两套行号栏都在、`PreviewDoc` 只在第 1 列、角标 `Run("◆")` + `Tag="annbadge"` +
  `badge.ToolTip`、`Tag` 不是 `slice.End`、量之前先 `UpdateLayout()`、量不全重试 3 次、
  滚出已摆窗口就重建、两条行号 `Canvas` 都不开 `ClipToBounds`）。原有 355 条一条没删、没放宽。
- **门禁**：`.\build.ps1 -Release` 退出码 **0**，Debug **0 个警告 / 0 个错误**、冒烟
  **378 项全过**；另以 `dotnet build -c Release --warnaserror` 复核 Release **0/0**；
  `dotnet publish` 出 `dist\YiTai.exe`（59.3 MB）并同步根目录。
  根 exe 62,165,562 B、mtime 2026-11-20 19:51:43、
  sha256 `0E6BA159328F6304E845AD1F8ADD4BD5FE1DD3AF00F5DFACCDC28F2B41357D22`（与 `dist` 同字节）。
  **本轮未做任何 git 操作**，提交由工程岗归口。
- **真机**（`%TEMP%\yitai-sandbox\proj` 沙箱项目 + `YITAI_CONFIG_DIR` 重定向；探针
  `%TEMP%\yitai-ln-probe` 读 UIA 矩形 + 逐行扫墨迹带，按"行号 Y 与正文行首 Y"的
  `delta` 量化对齐）：沙箱译文 120 行"每句一行"，其中 4 行预置批注。
  · 编辑页宽栏（译文栏 755px）13 个行号 `delta 3–4`、`spread 1`；把分隔条拖到 505px 窄栏
    后仍 `delta 3–4 / spread 1`；滚轮下滚两档、关掉重开后滚动，同样 `delta 3–4 / spread 1`
    —— 窄栏下行距真变成 87px（=3×29，长句折三行），行号跟着走，不再错行。
  · 预览页 14 个行号 `delta 2–4 / spread 2`，1..14 逐行递增，与编辑页同一逻辑行同号。
  · 预览内拖选 → 右键 → 菜单里「为选中文字加批注…」`enabled=true` → 加批注窗写正文 →
    `Ctrl+Enter` 保存 → `annotations.json` 3→4 条、状态栏「已加批注（本页 4 条）」、
    按钮仍显示「回到编辑」（即仍在预览），新批注处浅底 + 虚线 + 角标同帧出现；再测行号
    仍 `paired 14 / unpaired 0`。
  · 角标悬停：在第 04 行批注末尾的 `◆` 上悬停，UIA 弹出 tooltip
    「这一句的动词太轻了：爬台阶该有吃力的感觉。」（截图与 `tips` 结构化读数都留了）。
  · 批注持久：删掉进程重开沙箱项目，4 条批注、行号 1..14 全部照旧（`14`/`15` 两张图）。
- **复查（2026-11-20 傍晚，屋主拿真项目当场验）**：在真项目（第 10 章、3 栏、译文栏约 470px）里
  **滚过一屏后行号整栏空白**，栏顶只剩 22/23/24 三个号。查出两个真 bug，都不是"坐标算得不准"：
  1. **行号 `Canvas` 自己开了 `ClipToBounds`**：号码的 `Canvas.Top` 是内容坐标（整篇稿子的 Y，
     几千像素），靠 `TranslateTransform` 平移到视口里；`ClipToBounds` 按**局部**坐标裁、
     且在平移之前生效，滚过一屏后所有号码都落在 0..视口高 之外，被整批裁掉。
     改法：两条行号 `Canvas` 都不开裁剪，裁剪只留给外面那层 `Border`（它按平移后的坐标裁）。
  2. **首次构建量到的是上一版布局**：窗口刚起来 / 三栏刚套上时分栏宽度还没定，
     `GetRectFromCharacterIndex` 量到的是旧宽度的 Y；量不到的行被静默跳过，于是"只画出前 24 个号"。
     改法：量之前先 `TbxTranslation.UpdateLayout()`；`missed > 0` 就排队重量（最多 3 次）、
      **不发布半套号码**；滚动时用 `LineNumRail.Covers()` 判断"已摆的号盖不住视口"，盖不住就重建，
      不再拿过期坐标平移（这条让任何原因造成的过期位置都能自愈）。
  复测（根目录 Release exe + `%TEMP%\yitai-ch10-copy`，译文栏 470px）：首屏 1..12 与各行齐平；
  拖滚动条到中部 25..35、再到底部 30..43，`delta 3–4`（行高 29px）；预览页滚到 30..43 同样对齐。
  冒烟相应补 5 条源码锚（`UpdateLayout()`、缺号重试、`Covers` 自愈、两处 `Canvas` 不许
  `ClipToBounds`），355 → **378** 项。
- **第 10 章 29 条回归（只在 `%TEMP%` 副本上跑，真项目只读）**：29 条中 **22 条钉住**
  （14 条原位、8 条按 quote 就近回贴），**7 条未定位**。逐条核对引文后确认：这 7 条的
  `Quote`（如"对他们来说，所有人都是他的臣民""我没别的事儿了。""我，\"小王子回答，…"）
  **在当前 `translations\第10章.md` 正文里根本不存在**（用户后来改过这一章的措辞，
  现文是"对国王们来说，所有人都是臣民""我在这儿没别的事儿了。"…），属**内容漂移**，
  不是本轮回归：`AnnotationStore.cs`（11-19 23:40）与 `AnnotationMath.cs`（11-19 23:46）
  本轮**一个字未改**，`Normalize` / `DriftWindow` / 回贴口径全部原样。再在副本上做
  "改正文 → 删一条 → 加一条 → 落盘重载"，条目数与锚点仍稳定（未定位数不增加）。
  这 7 条要恢复只能重打批注（或改回原措辞），属于内容决策，不在本轮范围。
- **已知边界**：行号只给逻辑行（软换行不另编号，与 README 口径一致）；角标落在批注末尾，
  若批注正好结束在行尾换行处，角标会出现在下一行行首；角标是 10px 上标小菱形，
  4K/高缩放下比正文小但仍是橙色可辨。

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