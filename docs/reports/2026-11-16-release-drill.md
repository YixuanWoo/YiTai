# YiTai（译台）发布演练记录 · 2026-11-16（T-14）

> 任务编号：T-653c3bf5。本报告记录 `tools\release.ps1` 的落地与一次**真实跑通**的本地发布演练，
> 只做"出产物 + 可校验"，不涉及任何远端动作（不打 tag、不 push、不签名、不碰 `.github/workflows`）。
> 产物留档在仓库 `dist\0.0.0-drill\`（`dist\` 已被 `.gitignore` 忽略，不进 git）。

## 1. 本次做了什么

| 项 | 内容 |
|---|---|
| 新增脚本 | `tools\release.ps1` |
| 参数 | `-Version <版本号>`（必填，如 `0.2.0`；演练用 `0.0.0-drill`）、`-AllowDirty`（放行已存在的未提交改动） |
| 流程 | 版本号校验 → git 工作区校验 → 门禁 `build.ps1`（Debug 构建 + 冒烟测试）→ `dotnet publish`（win-x64 / 自包含 / 单文件）→ 打 zip → 逐份产物写 `.sha256` → 打印清单（路径 / 大小 / sha256 / 耗时） |
| 退出码 | `0` 成功；`1` 门禁 / 发布 / 打包失败；`2` 前置条件不满足（版本号非法、工作区脏、仓库根不对） |
| 刻意不做 | 不打 tag、不 push、不跑 `gh release create`、不签名（T-15 未定）、不覆盖仓库根 `YiTai.exe`、不改 `.github/workflows`、不提交 `dist/` |

脚本头部注释里写明了这些边界，避免下一个人把它"补全"成自动发 Release 的链路。

## 2. 环境与版本

| 项 | 值 |
|---|---|
| 仓库路径 | `D:\开源\YiTai` |
| 分支 / HEAD | `master`，`89dafbe`（与 `origin/master` 一致，演练前后无提交、无 push） |
| 系统 | Windows |
| PowerShell | `7.6.4`（`pwsh`，Core） |
| .NET SDK | `10.0.401`，目标框架 `net10.0-windows` |
| git | `2.53.0.windows.2` |
| 演练版本号 | `0.0.0-drill`（刻意用 `0.0.0` 一眼能看出不是正式版） |

## 3. 本次执行的命令

正式演练（成功的一次）：

```powershell
Set-Location -LiteralPath 'D:\开源\YiTai'
.\tools\release.ps1 -Version 0.0.0-drill -AllowDirty
```

为什么要 `-AllowDirty`：接手时工作区**本来就**带着上一单留下的改动
（`M docs/MAINTENANCE.md`、`?? docs/MAINTENANCE-PLAN.md`、`?? docs/reports/`、`?? tools/`、`?? .github/`），
另外本单新增的 `tools\release.ps1` 也是未跟踪文件。这些与发布产物无关，故演练放行 —— **正式发版不该这么干**。

反例（验证"该拦的拦住"）：

```powershell
.\tools\release.ps1                                  # 缺 -Version
.\tools\release.ps1 -Version 1.2                     # 少一段
.\tools\release.ps1 -Version abc                     # 非数字
.\tools\release.ps1 -Version v1.2.3                  # 带 v 前缀
.\tools\release.ps1 -Version 1.2.3.4                 # 四段
.\tools\release.ps1 -Version 01.2.3                  # 前导零
.\tools\release.ps1 -Version 0.2.0                   # 版本号合法，但工作区脏且没给 -AllowDirty
```

## 4. 门禁结果（脚本内第 3 段）

脚本调 `build.ps1`（Debug 构建 + 冒烟测试），口径与 `tools\maintenance-check.ps1` 一致：退出码 0、0 警告、0 错误、冒烟全过。

| 断言 | 要求 | 实测 | 结论 |
|---|---|---|---|
| `build.ps1` 退出码 | 0 | 0 | 通过 |
| Debug 警告数 | 0 | 0 | 通过 |
| Debug 错误数 | 0 | 0 | 通过 |
| 冒烟测试 | 全部通过 | `全部通过 ✓（176 项）` | 通过 |

门禁输出原文（尾部）：

```
全部通过 ✓（176 项）
完成：D:\开源\YiTai\src\YiTai.App\bin\Debug\net10.0-windows\YiTai.exe
```

完整门禁日志（本轮）：`%TEMP%\yitai-release-0.0.0-drill-20261116-183538-gate.log`
发布日志（本轮）：`%TEMP%\yitai-release-0.0.0-drill-20261116-183538-publish.log`
（两份都按惯例写 `%TEMP%`，不落仓库；下次跑文件名里的时间戳会变。）

## 5. 产物清单

产物目录：`D:\开源\YiTai\dist\0.0.0-drill\`

| 文件 | 字节 | 大小 | sha256 |
|---|---|---|---|
| `YiTai.exe` | 62,146,152 | 59.3 MB | `eb94cab251d0af8d100577ee7d33183c1020c0c8c166f314df4b61b692a7eca9` |
| `YiTai-0.0.0-drill-win-x64.zip` | 56,659,736 | 54.0 MB | `a49dd8997f03af7695ccf57bcd619b47fd9698b6349918dedb61125b49853764` |
| `YiTai.exe.sha256` | 75 | 75 B | —（校验和文件本身） |
| `YiTai-0.0.0-drill-win-x64.zip.sha256` | 95 | 95 B | —（校验和文件本身） |

产物形态：

- `YiTai.exe`：`win-x64` 自包含单文件（`--self-contained true` + `PublishSingleFile=true`
  + `IncludeNativeLibrariesForSelfExtract=true` + `EnableCompressionInSingleFile=true`），59.3 MB 与基线量级一致。
- `YiTai-0.0.0-drill-win-x64.zip`：**内含且仅含** `YiTai.exe` 一份（单文件发布本来就是一个可拷走的文件），
  解压即用；压缩后 54.0 MB。
- 两份 `.sha256` 内容为 `<小写哈希>  <文件名>`（两个空格，`sha256sum -c` 与 `Get-FileHash` 都能吃）。

publish 还在同一层留下 5 份构建附带物（**不进 zip、不出校验和**，脚本清单里如实列出）：
`Microsoft.Web.WebView2.Core.xml`（600.8 KB）、`Microsoft.Web.WebView2.WinForms.xml`（40.9 KB）、
`Microsoft.Web.WebView2.Wpf.xml`（140.3 KB）、`YiTai.Core.pdb`（27.9 KB）、`YiTai.pdb`（46.6 KB）。

## 6. 校验和复算（不依赖脚本自报）

```powershell
$dir = 'D:\开源\YiTai\dist\0.0.0-drill'
(Get-FileHash -LiteralPath "$dir\YiTai.exe" -Algorithm SHA256).Hash.ToLower()
(Get-FileHash -LiteralPath "$dir\YiTai-0.0.0-drill-win-x64.zip" -Algorithm SHA256).Hash.ToLower()
Get-Content -LiteralPath "$dir\YiTai.exe.sha256" -Raw
Get-Content -LiteralPath "$dir\YiTai-0.0.0-drill-win-x64.zip.sha256" -Raw
```

实测：

| 文件 | 复算得到 | `.sha256` 里声明 | 一致 |
|---|---|---|---|
| `YiTai.exe` | `eb94cab251d0af8d100577ee7d33183c1020c0c8c166f314df4b61b692a7eca9` | `eb94cab251d0af8d100577ee7d33183c1020c0c8c166f314df4b61b692a7eca9` | 是 |
| `YiTai-0.0.0-drill-win-x64.zip` | `a49dd8997f03af7695ccf57bcd619b47fd9698b6349918dedb61125b49853764` | `a49dd8997f03af7695ccf57bcd619b47fd9698b6349918dedb61125b49853764` | 是 |

zip 内容核对（`System.IO.Compression.ZipFile` 打开）：

```
YiTai.exe  62146152 bytes
```

即包内只有一份 exe，且字节数与 `dist\0.0.0-drill\YiTai.exe` 相同。

## 7. 耗时

| 阶段 | 耗时（脚本自报） |
|---|---|
| 门禁（Debug 构建 + 176 项冒烟） | 9.3 s |
| `dotnet publish`（win-x64 自包含单文件） | 16.7 s |
| 打包 zip + 写 2 份 `.sha256` | 3.1 s |
| **合计（脚本内计时）** | **29.5 s** |
| 外壳计时（含 pwsh 启动等） | 30.7 s |

（本机增量构建、包已还原，所以门禁只花 9 秒；干净环境首次跑会慢得多，别拿这个数当机器性能基线。）

## 8. 反例结果（该拦的都拦住了）

| 命令 | 退出码 | 屏幕原话（摘要） |
|---|---|---|
| 缺 `-Version` | 2 | `发布中止（退出码 2）：缺少 -Version 参数` + 用法提示 |
| `-Version 1.2` | 2 | `版本号格式非法：'1.2'` |
| `-Version abc` | 2 | `版本号格式非法：'abc'` |
| `-Version v1.2.3` | 2 | `版本号格式非法：'v1.2.3'` |
| `-Version 1.2.3.4` | 2 | `版本号格式非法：'1.2.3.4'` |
| `-Version 01.2.3` | 2 | `版本号格式非法：'01.2.3'`（不许前导零） |
| `-Version 0.2.0`（工作区脏，未加 `-AllowDirty`） | 2 | `工作区不干净：5 处未提交改动` 逐条列出，然后 `发布中止（退出码 2）：工作区不干净，且没给 -AllowDirty` |

> 说明：那个"5 处"是**演练当时**的实况（`M docs/MAINTENANCE.md`、`?? .github/`、`?? docs/MAINTENANCE-PLAN.md`、
> `?? docs/reports/`、`?? tools/`）。演练之后本单又新增了 `CHANGELOG.md` 与本报告，同样的命令再跑会显示 7 处 ——
> 结论不变（退出码仍是 2）。

版本号规则（脚本里的正则口径）：`MAJOR.MINOR.PATCH`，可带 `-预发布` 后缀（`0.0.0-drill`、`0.3.0-rc.1` 都认）；
不许前导零、空格、路径分隔符 —— 因为版本号要直接当目录名与文件名用（这也是不让它把 `dist\..\` 之类的东西拼出来的原因）。

## 9. 过程中踩到并修掉的问题（如实记录）

1. **第一次跑直接崩在第 6 段（打印清单）**：格式串写成了 `"{1,>12}"`，PowerShell 只认
   `{index[,alignment]}`（负号表示左对齐），不认 `>` 前缀 —— 报 `Error formatting a string: Input string was not in a correct format`，
   脚本以退出码 1 结束（当时产物已产出、zip 已打好，只是清单没打出来）。改成 `{1,12}` 后重跑，退出码 0。
   **教训**：`-f` 的格式串要在"能跑到那一刻"之前就验，别把唯一一次演练留给它当试验品。
2. **第一版把 publish 目录里所有文件都打进 zip**：连带 pdb 与 WebView2 的 xml 说明文件一起压，
   包里成 6 份文件、还各配了一个 `.sha256`。单文件自包含发布的意义就是"一个文件拷走即用"，
   改成**只压 `YiTai.exe`**，附带物只在清单里列出。
3. **重复发同一版本会留旧文件**：脚本现在先清空 `dist\<Version>\` 再发布；
   清理前把路径解析成绝对路径，确认仍落在 `dist\` 里面才删（`-LiteralPath`，不用通配符）。
4. **`dist\` 根上还留着上一单 `build.ps1 -Release` 的产物**（`dist\YiTai.exe` 等）。本脚本按版本分目录，
   不动 `dist\` 根的那些文件，也不覆盖仓库根 `YiTai.exe`（那是日常双击入口，不该被演练版替换）。

## 10. 已知遗留（没做的，别当成已完成）

- **exe 内部版本号仍是 `0.1.0`**（取自 `src\YiTai.App\YiTai.App.csproj` 的 `<Version>`）。
  本次发的是 `0.0.0-drill`，脚本会打印一行提醒但不改 csproj；按 `MAINTENANCE-PLAN` 4.1 的口径，
  要改版本号应改 csproj。发布目录 / zip / `.sha256` 的命名与内容对得上，但"文件属性里的版本"目前对不上发版号。
- **不打 tag、不 push、不签名、不跑 `gh release create`**：远端挂产物仍是手动一步，
  要等 T-13（CI 凭据 `workflow` scope）与 T-15（签名拍板）之后再接。
- **zip 里只有 exe**，没有 `README` / `LICENSE` / 校验和文件；要"解压就能看到校验和"，得再改打包口径。
- **`dist\0.0.0-drill\` 保留在本机**（`dist\` 被 `.gitignore` 忽略，不进 git，也不会被提交）；
  需要清掉时直接删这一层目录即可，脚本下次跑同一版本会自己重建。

## 11. 本次操作边界（自证）

- 只新增 / 修改了这些文件：`tools\release.ps1`（新增）、`docs\TODO.md`（T-14 勾完成）、`CHANGELOG.md`（新增）、
  `docs\MAINTENANCE-PLAN.md`（同步"仓库无独立 CHANGELOG"这句已过期的事实）；本报告为新增。
- **未改**任何 `.cs` / `.xaml` / `.csproj` / `build.ps1`，未改 `Core` 业务逻辑，未引入任何依赖。
- **未改** `.github\workflows\ci.yml`（仍是 2026-11-15 12:14 那份未跟踪文件）。
- 未打 tag（`git tag` 输出为空）、未 push（`git status -sb` 仍是 `## master...origin/master`，本地无领先提交）、未 commit。
- 未碰用户真实翻译项目数据；演练只读源码、只在 `dist\` 与 `%TEMP%` 写文件。
- 构建产物落在 `dist\`（已被 `.gitignore` 第 4 行 `dist/` 忽略，`git check-ignore` 三条路径均命中）。
