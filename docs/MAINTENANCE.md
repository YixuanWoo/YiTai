# 维护手册 · YiTai（译台）

> 交接日期：2026-11-16。这份文件回答"以后怎么管这个仓库"，是维护入口。
> 动手前按顺序读：本文件 → `README.md`（产品边界）→ `docs/TODO.md`（待办与历史决策）。

## 0. 一分钟状态（交接时）

| 项 | 现状 |
|---|---|
| 平台 | Windows / WPF / .NET SDK 10（`net10.0-windows`） |
| 依赖 | **零第三方依赖**（Core 与 App 的 csproj 都没有 PackageReference） |
| 分支 | `master`（另有 `ci-pending` 分支，见 T-13） |
| 构建 | 0 警告 0 错误 |
| 冒烟测试 | **176 项全过**（`.\build.ps1`，退出码 0） |
| 发布 | `.\build.ps1 -Release` → `dist\YiTai.exe`（约 60MB 单文件，自包含） |
| 未完成 | T-13 CI、T-14 release.ps1、T-15 签名、T-16 检查更新、T-17 大文件性能、T-18 英文界面 |
| 用户数据 | **不在仓库里**。真实译本在用户自己的项目文件夹；仓库只有 `samples\demo` |

## 1. 红线（改动前先确认没踩）

1. **Core 不知道界面存在**：`YiTai.Core` 只允许项目、文件、文本、落盘；不许出现 WPF 类型。
2. **App 不知道旧模型**：界面层只认"一份文件"，不许出现"章节 / 注解 / 一份原文配一份译文"的推导。
3. **路径只在 `ProjectLayout` 里拼**：别在业务代码里手拼 `project.json`、`translations\`、`.yitai\`。
4. **零第三方依赖**：要引包先按 T-12 的方式把代价写清楚，再让人拍板。
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
- [ ] 测试全过；项数与上次相比**只增不减**（当前 176；删测试要说明理由）
- [ ] `git status` 没有意外的未跟踪文件
- [ ] TODO 里 `[ ]` 项没有过期承诺；勾掉的项写了**验证方式**（命令 + 看到的结果）
- [ ] 依赖仍为零：两个 csproj 里没有 `PackageReference`
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
| CI 不绿（T-13） | `.github/workflows/ci.yml` 已写好，卡在 push token 缺 `workflow` scope；网页粘贴或 `gh auth refresh -s workflow` |
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

## 9. 需要人拍板的事

1. **T-13 CI**：要不要现在刷新 token 的 `workflow` 权限（否则 CI 只能停在 `ci-pending` 分支）。
2. **T-14 / T-15**：release.ps1 与代码签名做不做（签名要自备证书）。
3. **T-17**：大文件性能现在做还是等真实卡顿反馈。
4. **项目归属**：YiTai 目前不在 AICompany 的项目配置里。若要纳入公司项目管理，
   建议项目 id `yitai`、显示名「译台」、`rootDir` 指向本仓库（这需要改公司配置，留给董事会/屋主确认）。
