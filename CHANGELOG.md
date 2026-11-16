# 变更记录 · YiTai（译台）

> 2026-11-16 起才有这份文件（此前变更沿革只看 `docs/MAINTENANCE.md` 第 8 节与 `git log`）。
> 只记"用户看得见的行为、发布方式、工具链"这三类变化；一条一个版本块，**最新的放最上面**。
> 提交信息里的 `[T-编号]` 与 `docs/TODO.md` 一一对应，回退时能对上。

## 未发布 · 2026-11-16

### 新增

- **T-14 发布脚本 `tools\release.ps1`**：一条命令做完发布里"本地那半段"——
  校验版本号 → 查 git 工作区（脏且没给 `-AllowDirty` 就中止并列出改动）→ 跑门禁 `build.ps1`
  （0 警告 0 错误 + 冒烟测试全过）→ `dotnet publish` 出 win-x64 自包含单文件到 `dist\<Version>\`
  → 打 zip → 逐份产物写 `.sha256` → 打印清单（路径 / 大小 / sha256 / 各阶段耗时）。
  参数 `-Version`（如 `0.2.0`，演练可用 `0.0.0-drill`）与 `-AllowDirty`；
  退出码 `0` 成功 / `1` 门禁或发布失败 / `2` 前置条件不满足。
  **刻意不做**：不打 tag、不 push、不签名、不跑 `gh release create`、不覆盖仓库根 `YiTai.exe`；
  远端挂产物待 T-13（CI 凭据）与 T-15（签名）定下来再接。`dist\` 在 `.gitignore` 里，产物不进 git。

### 文档

- `docs/reports/2026-11-16-release-drill.md`：`0.0.0-drill` 发布演练记录
  （命令、产物路径 / 字节数 / sha256 / 耗时，含两条失败用例的退出码与屏幕原话）。
- 本文件（`CHANGELOG.md`）建立；`docs/MAINTENANCE-PLAN.md` 里"仓库没有独立 CHANGELOG"的口径同步更新。
- `docs/TODO.md`：T-14 勾为完成（写了日期与验收证据，并标明"远端挂 Release 仍未做"）。

## 2026-11-16 之前

沿革以 `docs/MAINTENANCE.md` 第 8 节「交接验收记录」与 `git log` 为准，本文件不回头补抄（免得两边各写一套、互相对不上）。
