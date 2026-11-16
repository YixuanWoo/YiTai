<#
发布脚本 · YiTai（译台）—— 一条命令把版本编出来、验清楚、打包挂校验和（T-14）

做什么：
  1. 校验版本号格式（MAJOR.MINOR.PATCH，可带 -预发布 后缀）；
  2. 查 git 工作区：脏且没给 -AllowDirty 就中止，并列出是哪些改动；
  3. 跑门禁 build.ps1（Debug 构建 + 冒烟测试），断言 0 警告 0 错误、测试全过、退出码 0；
  4. dotnet publish 出 win-x64 自包含单文件到 dist\<Version>\；
  5. 打包 zip，逐份产物生成 .sha256；
  6. 打印产物清单（路径 / 大小 / sha256）与各阶段耗时。

用法：
  .\tools\release.ps1 -Version 0.2.0                     # 正式发版：要求工作区干净
  .\tools\release.ps1 -Version 0.2.0 -AllowDirty         # 手上有意留着的改动时
  .\tools\release.ps1 -Version 0.0.0-drill -AllowDirty   # 发布演练

退出码：
  0 成功；1 门禁 / 发布 / 打包失败；2 前置条件不满足（版本号非法、工作区脏、仓库根不对）。

几条刻意的边界（别在本脚本里加回来，要加先拍板）：
  - 不打 tag、不 push、不跑 gh release create：远端发布要等 T-13（CI 凭据 scope）与 T-15（签名）定下来再接，
    本脚本只负责"本地出一份可校验的产物"；也因此脚本里没有任何 git 写操作。
  - 不签名（T-15 未定）。
  - 不覆盖仓库根的 YiTai.exe：那是日常双击入口，发布/演练不该把正在用的入口换成新版本；
    build.ps1 -Release 会同步根目录，本脚本刻意不做这一步。
  - dist\ 已在 .gitignore 里，产物不进 git（所以也没有"提交产物"这一步）。
  - 只用 Windows 自带能力，不引任何第三方模块，脚本不联网（包还原是 dotnet 的事）。
#>

[CmdletBinding()]
param(
    # 版本号，形如 0.2.0；演练可用 0.0.0-drill。同时决定 dist\ 下的产物目录名。
    [string]$Version,

    # 放行已存在的未提交改动（演练、或确认这些改动与发布无关时用）。
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $repoRoot -PathType Container)) {
    Write-Host "发布中止（退出码 2）：没能定位仓库根（脚本应在 tools\ 下）：$repoRoot" -ForegroundColor Red
    exit 2
}

# 从哪个目录调都按仓库根跑（与 build.ps1 / T-27 一个口径）
Set-Location -LiteralPath $repoRoot

$totalWatch = [Diagnostics.Stopwatch]::StartNew()
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$logDir = $env:TEMP
if (-not $logDir) { $logDir = [IO.Path]::GetTempPath() }

# ---------------------------------------------------------------- 小工具

function Write-Stage {
    param([string]$Text)
    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

# 中止：说清原因 + 一句怎么办，然后按码退出（不抛异常，免得被外面当成"没说原因"）
function Stop-Release {
    param(
        [int]$Code,
        [string]$Reason,
        [string]$Hint
    )
    Write-Host ''
    Write-Host ("发布中止（退出码 {0}）：{1}" -f $Code, $Reason) -ForegroundColor Red
    if ($Hint) { Write-Host ("  怎么办：{0}" -f $Hint) -ForegroundColor Yellow }
    Write-Host ''
    exit $Code
}

function Format-Size {
    param([int64]$Bytes)
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return ("$Bytes B")
}

function Get-TailText {
    param([string]$Text, [int]$Lines = 12)
    $split = ($Text -split "`r?`n") | Where-Object { $_.Trim().Length -gt 0 }
    return (@($split | Select-Object -Last $Lines) -join "`n")
}

# ---------------------------------------------------------------- 1. 版本号

if ([string]::IsNullOrWhiteSpace($Version)) {
    Stop-Release -Code 2 -Reason '缺少 -Version 参数' `
        -Hint '用法：.\tools\release.ps1 -Version 0.2.0 [-AllowDirty]'
}

# 允许 0.2.0 与 0.0.0-drill 这类带后缀的；不许前导零（SemVer 口径），
# 字符集里刻意不含空格、斜杠、反斜杠、通配符 —— 版本号要拿来当目录名和文件名。
$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$'
if ($Version -notmatch $versionPattern) {
    Stop-Release -Code 2 -Reason ("版本号格式非法：'{0}'" -f $Version) `
        -Hint '要 MAJOR.MINOR.PATCH（如 0.2.0、1.10.3）；预发布/演练可带后缀（如 0.0.0-drill、0.3.0-rc.1）；不许前导零、空格与路径分隔符'
}

Write-Stage ("发布 {0}" -f $Version)
Write-Host (" 仓库：{0}" -f $repoRoot)

# csproj 里的版本号是"程序自己认的版本"；两处不一致时给个提醒，但不拦（演练版本本来就和 csproj 不同）
$csprojPath = Join-Path $repoRoot 'src\YiTai.App\YiTai.App.csproj'
$csprojVersion = $null
if (Test-Path -LiteralPath $csprojPath -PathType Leaf) {
    $m = [regex]::Match((Get-Content -LiteralPath $csprojPath -Raw -Encoding utf8), '<Version>([^<]+)</Version>')
    if ($m.Success) { $csprojVersion = $m.Groups[1].Value.Trim() }
}
if ($csprojVersion -and $csprojVersion -ne $Version) {
    Write-Host (" 注意：csproj 里的 <Version> 是 {0}，本次发的却是 {1}；exe 内部版本号仍取 csproj（要改请改 csproj，见 MAINTENANCE-PLAN 4.1）" -f $csprojVersion, $Version) -ForegroundColor Yellow
}

# git HEAD 只是记录进清单，方便对上"这次发的到底是哪一版代码"
$headRun = & git -c core.quotePath=false -C $repoRoot rev-parse --short HEAD 2>&1
$gitHead = if ($LASTEXITCODE -eq 0) { ("$headRun").Trim() } else { '未知' }
Write-Host (" git HEAD：{0}" -f $gitHead)

# ---------------------------------------------------------------- 2. git 工作区

Write-Stage 'git 工作区'

$statusRun = & git -c core.quotePath=false -C $repoRoot status --porcelain=v1 2>&1
$statusCode = $LASTEXITCODE
if ($statusCode -ne 0) {
    Stop-Release -Code 1 -Reason ("git status 失败（退出码 {0}）：{1}" -f $statusCode, (Get-TailText -Text "$statusRun" -Lines 4)) `
        -Hint '确认仓库根是 git 仓库、git 可用'
}

$dirtyLines = @()
foreach ($line in @($statusRun)) {
    if ($null -eq $line) { continue }
    if (("$line").Trim().Length -ge 3) { $dirtyLines += ("$line").TrimEnd() }
}

if ($dirtyLines.Count -eq 0) {
    Write-Host ' 工作区干净' -ForegroundColor Green
}
elseif ($AllowDirty) {
    Write-Host (" 工作区有 {0} 处未提交改动，已用 -AllowDirty 放行：" -f $dirtyLines.Count) -ForegroundColor Yellow
    foreach ($line in @($dirtyLines | Select-Object -First 10)) { Write-Host ("   {0}" -f $line) -ForegroundColor Yellow }
    if ($dirtyLines.Count -gt 10) { Write-Host ("   …（共 {0} 处）" -f $dirtyLines.Count) -ForegroundColor Yellow }
}
else {
    Write-Host (" 工作区不干净：{0} 处未提交改动" -f $dirtyLines.Count) -ForegroundColor Red
    foreach ($line in @($dirtyLines | Select-Object -First 10)) { Write-Host ("   {0}" -f $line) -ForegroundColor Red }
    if ($dirtyLines.Count -gt 10) { Write-Host ("   …（共 {0} 处）" -f $dirtyLines.Count) -ForegroundColor Red }
    Stop-Release -Code 2 -Reason '工作区不干净，且没给 -AllowDirty' `
        -Hint '先提交 / 收好这些改动；确认与本次发布无关时，加 -AllowDirty 重跑'
}

# ---------------------------------------------------------------- 3. 门禁

Write-Stage '门禁（build.ps1：Debug 构建 + 冒烟测试）'

$buildScript = Join-Path $repoRoot 'build.ps1'
if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    Stop-Release -Code 2 -Reason ("门禁脚本不存在：{0}" -f $buildScript) -Hint '确认仓库根或 tools\ 位置对不对'
}

$gateWatch = [Diagnostics.Stopwatch]::StartNew()

# 用当前这套 PowerShell 的子进程跑：退出码真实，日志留得住，也不会被对方脚本里的 exit 带跑
if ($PSVersionTable.PSEdition -eq 'Core') {
    $shellExe = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $shellExe)) { $shellExe = 'pwsh' }
}
else {
    $shellExe = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $shellExe)) { $shellExe = 'powershell' }
}

$gateLog = Join-Path $logDir "yitai-release-$Version-$stamp-gate.log"
$gateText = (& $shellExe -NoProfile -ExecutionPolicy Bypass -NonInteractive -File $buildScript 2>&1 | Out-String)
$gateCode = $LASTEXITCODE
try { [IO.File]::WriteAllText($gateLog, $gateText, [Text.UTF8Encoding]::new($false)) } catch { }

# 与 maintenance-check.ps1 同一套口径：从构建/测试输出里抠统计（中英文都认，取最后一次）
$warnings = $null
$errors = $null
foreach ($m in [regex]::Matches($gateText, '(?m)^\s*(\d+)\s*(?:个警告|Warning\(s\))')) { $warnings = [int]$m.Groups[1].Value }
foreach ($m in [regex]::Matches($gateText, '(?m)^\s*(\d+)\s*(?:个错误|Error\(s\))')) { $errors = [int]$m.Groups[1].Value }

$smokeAllPass = ($gateText -match '全部通过')
$smokeCount = $null
$m2 = [regex]::Match($gateText, '全部通过[^\r\n（(]*[（(]\s*(\d+)\s*项')
if ($m2.Success) { $smokeCount = [int]$m2.Groups[1].Value }

$gateWatch.Stop()

$gateReasons = @()
if ($gateCode -ne 0) { $gateReasons += "退出码 $gateCode（要 0）" }
if ($null -eq $warnings) { $gateReasons += '输出里找不到警告统计' } elseif ($warnings -ne 0) { $gateReasons += "有 $warnings 个警告（警告也算失败）" }
if ($null -eq $errors) { $gateReasons += '输出里找不到错误统计' } elseif ($errors -ne 0) { $gateReasons += "有 $errors 个错误" }
if (-not $smokeAllPass) { $gateReasons += '没看到「全部通过」收尾行（构建可能中途就挂了）' }
elseif ($null -eq $smokeCount) { $gateReasons += '冒烟测试没报项数' }

if ($gateReasons.Count -gt 0) {
    Write-Host (" 门禁不通过：" + ($gateReasons -join '；')) -ForegroundColor Red
    Write-Host (" 日志：{0}" -f $gateLog) -ForegroundColor Yellow
    Write-Host (Get-TailText -Text $gateText -Lines 12) -ForegroundColor DarkGray
    Stop-Release -Code 1 -Reason ('门禁不通过（' + ($gateReasons -join '；') + '）') `
        -Hint ("先修门禁再发版；完整输出见 {0}" -f $gateLog)
}

Write-Host (" 通过：Debug {0} 警告 / {1} 错误；冒烟 {2} 项全过（{3:N0} 秒）" -f $warnings, $errors, $smokeCount, $gateWatch.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host (" 日志：{0}" -f $gateLog) -ForegroundColor DarkGray

# ---------------------------------------------------------------- 4. 发布单文件

Write-Stage "发布单文件（win-x64 / 自包含 / 单文件）"

$distRoot = Join-Path $repoRoot 'dist'
$outDir = Join-Path $distRoot $Version

if (-not (Test-Path -LiteralPath $distRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
}

# 重复发同一版本时先清干净这一层的旧产物，免得旧 pdb / 旧 zip 混进新包里。
# 只动 dist\<Version> 这一层：路径先解析成绝对路径，确认仍落在 dist\ 里面再删。
if (Test-Path -LiteralPath $outDir) {
    $outFull = (Resolve-Path -LiteralPath $outDir).ProviderPath
    $distFull = (Resolve-Path -LiteralPath $distRoot).ProviderPath
    if (-not $outFull.StartsWith($distFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Release -Code 1 -Reason ("产物目录不在 dist\ 里，拒绝清理：{0}" -f $outFull) -Hint '检查 -Version 与仓库布局'
    }
    Remove-Item -LiteralPath $outFull -Recurse -Force
    Write-Host (" 清掉上一次的同版本产物：{0}" -f $outFull) -ForegroundColor DarkGray
}

$publishWatch = [Diagnostics.Stopwatch]::StartNew()
$publishArgs = @(
    'publish', $csprojPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-o', $outDir,
    '--nologo'
)
$publishText = (& dotnet @publishArgs 2>&1 | Out-String)
$publishCode = $LASTEXITCODE
$publishLog = Join-Path $logDir "yitai-release-$Version-$stamp-publish.log"
try { [IO.File]::WriteAllText($publishLog, $publishText, [Text.UTF8Encoding]::new($false)) } catch { }
$publishWatch.Stop()

if ($publishCode -ne 0) {
    Write-Host (Get-TailText -Text $publishText -Lines 12) -ForegroundColor DarkGray
    Stop-Release -Code 1 -Reason ("dotnet publish 失败（退出码 {0}）" -f $publishCode) `
        -Hint ("看日志：{0}" -f $publishLog)
}

$exePath = Join-Path $outDir 'YiTai.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Stop-Release -Code 1 -Reason ("发布完了却没看到 {0}" -f $exePath) -Hint ("看日志：{0}" -f $publishLog)
}
Write-Host (" 已产出：{0}（{1:N0} 秒）" -f $exePath, $publishWatch.Elapsed.TotalSeconds) -ForegroundColor Green

# ---------------------------------------------------------------- 5. 打包 + 校验和

Write-Stage '打包 zip + 生成 .sha256'

$packageWatch = [Diagnostics.Stopwatch]::StartNew()

# 真正的产品就是那一个 exe —— 单文件自包含发布的意义就在这儿：拷给别人只需要这一个文件。
# publish 还会在同一层留下 pdb 与 WebView2 的 xml 说明文件，那些是构建附带物：
# 不进 zip、不出校验和，只在清单里如实列出来（免得下一个人以为漏打了）。
# 让下面这段代码简化成"只压 exe"的前提，是发布参数里必须有 PublishSingleFile（见第 4 段的 $publishArgs）。
$exeItem = Get-Item -LiteralPath $exePath

$zipName = "YiTai-$Version-win-x64.zip"
$zipPath = Join-Path $outDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath $exePath -DestinationPath $zipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    Stop-Release -Code 1 -Reason ("打包没生成 zip：{0}" -f $zipPath) -Hint '检查磁盘空间与文件占用（程序若在运行会压不动）'
}

# 每份产物各写一个 <文件名>.sha256（内容 `<小写哈希>  <文件名>`，sha256sum -c 与 Get-FileHash 都能复算）
$hashTargets = @($exeItem, (Get-Item -LiteralPath $zipPath))
$shaFiles = @()
foreach ($file in $hashTargets) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLower()
    $shaPath = "$($file.FullName).sha256"
    [IO.File]::WriteAllText($shaPath, ("{0}  {1}" -f $hash, $file.Name), [Text.UTF8Encoding]::new($false))
    $shaFiles += (Get-Item -LiteralPath $shaPath)
    Write-Host (" {0}`n   sha256 {1}" -f $file.Name, $hash) -ForegroundColor Green
}
Write-Host (" 已写校验和：{0} 个 .sha256 文件" -f $shaFiles.Count) -ForegroundColor Green

$packageWatch.Stop()

# ---------------------------------------------------------------- 6. 产物清单

$totalWatch.Stop()

$line = '=' * 62
Write-Host ''
Write-Host $line -ForegroundColor DarkCyan
Write-Host (" YiTai 发布产物 · {0}" -f $Version) -ForegroundColor Cyan
Write-Host (" 仓库：{0}  HEAD {1}" -f $repoRoot, $gitHead)
Write-Host (" 产物目录：{0}" -f $outDir)
Write-Host $line -ForegroundColor DarkCyan

$listed = @($exeItem, (Get-Item -LiteralPath $zipPath)) + $shaFiles
Write-Host (" {0,-44} {1,12}  {2}" -f '文件', '大小', 'sha256') -ForegroundColor DarkGray
foreach ($file in $listed) {
    $hash = '-'
    if ($file.Extension -ne '.sha256') {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLower()
    }
    Write-Host (" {0,-44} {1,12}  {2}" -f $file.Name, (Format-Size -Bytes $file.Length), $hash)
    Write-Host ("   {0}" -f $file.FullName) -ForegroundColor DarkGray
}

# publish 附带出来的 pdb / xml：不发布，但如实列出来（省的别人对着目录猜哪些该拷走）
$auxFiles = @(Get-ChildItem -LiteralPath $outDir -File |
    Where-Object { $_.Name -ne $exeItem.Name -and $_.Extension -notin @('.zip', '.sha256') } |
    Sort-Object Name)
if ($auxFiles.Count -gt 0) {
    $auxText = (@($auxFiles | ForEach-Object { '{0}（{1}）' -f $_.Name, (Format-Size -Bytes $_.Length) }) -join '，')
    Write-Host (" 另有发布附带文件 {0} 份（不进 zip、不出校验和）：{1}" -f $auxFiles.Count, $auxText) -ForegroundColor DarkGray
}

Write-Host ('-' * 62) -ForegroundColor DarkGray
Write-Host (" 版本：{0}（git HEAD {1}）" -f $Version, $gitHead)
Write-Host (" 门禁：Debug {0} 警告 / {1} 错误；冒烟 {2} 项全过" -f $warnings, $errors, $smokeCount)
Write-Host (" 耗时：门禁 {0:N1}s · 发布 {1:N1}s · 打包 {2:N1}s · 合计 {3:N1}s" -f `
    $gateWatch.Elapsed.TotalSeconds, $publishWatch.Elapsed.TotalSeconds, $packageWatch.Elapsed.TotalSeconds, $totalWatch.Elapsed.TotalSeconds)
Write-Host (' 复算：Get-FileHash -Algorithm SHA256 -LiteralPath <上面任一文件>') -ForegroundColor DarkGray
Write-Host (' 注意：本脚本不打 tag、不 push、不签名、不覆盖仓库根 exe；dist\ 已在 .gitignore 里，产物不进 git') -ForegroundColor Yellow
Write-Host $line -ForegroundColor DarkCyan
Write-Host ''
Write-Host ("发布完成：{0}" -f $zipPath) -ForegroundColor Green

exit 0
