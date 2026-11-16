<#
维护体检脚本 · YiTai（译台）

一条命令回答"这个仓库现在还健康吗"：
  1. 关键文件在不在（构建脚本、docs/MAINTENANCE.md、docs/TODO.md）；
  2. 跑一遍 build.ps1，断言退出码 0、Debug 构建 0 警告 0 错误、冒烟测试全过；
  3. 单独跑一次 Release 构建，断言 0 警告 0 错误（build.ps1 不带 -Release 时只编 Debug）；
  4. git 工作区：代码 / 工程文件有没有未提交改动（-AllowDirty 跳过）；
  5. 打一份人类可读摘要，同时落一份机器可读 JSON 报告。

用法：
  .\tools\maintenance-check.ps1                  # 体检，报告写 %TEMP%
  .\tools\maintenance-check.ps1 -Json            # 同上，并把 JSON 打到 stdout
  .\tools\maintenance-check.ps1 -AllowDirty      # 跳过 git 检查
  .\tools\maintenance-check.ps1 -BuildScript .\nope.ps1   # 造失败用例

退出码：0 全部通过；1 有必检项失败；2 连体检都做不起来（仓库根/参数不对）。

几条刻意的取舍（改之前先读）：
  - 只用 Windows 自带的 PowerShell 能力：不引任何第三方模块，脚本自己也不联网
    （构建时的包还原是 build.ps1 / dotnet 的事，不是本脚本行为）。
  - 报告默认写 %TEMP%，不落仓库：体检产物不该混进 git status。
  - git 检查只看"代码 / 工程文件"（src、tests、dist、.github、tools、build.ps1、
    YiTai.exe、*.csproj/*.slnx/*.ps1/*.yml/*.xaml 等）有没有未提交改动。
    文档改动（*.md）与未跟踪文件只作提示、不算失败——原因很实在：接手时仓库本来
    就可能带着没提交的 docs 草稿，写文档也不该把体检搞红。要看严格的，自己看
    JSON 里的 docChanges / untracked。
  - 冒烟测试项数按"只增不减"处理：少于 -ExpectedTestCount（默认 176）算失败，
    多于则通过并在摘要里标出来（手册第 3 节的口径）。
#>

[CmdletBinding()]
param(
    # 仓库根。默认按脚本位置推（tools\ 的上一层），一般不用给。
    [string]$RepoRoot,

    # 被体检的构建脚本，相对仓库根解析。给一个不存在的路径就能造出失败用例。
    [string]$BuildScript = 'build.ps1',

    # 冒烟测试项数下限：少于它就判失败（手册：项数只增不减）。
    [int]$ExpectedTestCount = 176,

    # 跳过 git 工作区检查（在有意的临时改动时用）。
    [switch]$AllowDirty,

    # 把 JSON 报告同时写到 stdout（stdout 上就只有 JSON，方便 ConvertFrom-Json）。
    [switch]$Json,

    # 报告落盘位置。不给就写 %TEMP%\yitai-maintenance-<时间戳>.json。
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 基础设置

$checkStarted = Get-Date
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$stamp = $checkStarted.ToString('yyyyMMdd-HHmmss')

$checks   = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]
$notes    = New-Object System.Collections.Generic.List[string]

# 定位仓库根
if ($RepoRoot) {
    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
        Write-Host "体检做不起来：仓库根不存在：$RepoRoot" -ForegroundColor Red
        exit 2
    }
    $root = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
}
else {
    $root = Split-Path -Parent $PSScriptRoot
}

if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    Write-Host "体检做不起来：没能定位仓库根（脚本应在 tools\ 下）：$root" -ForegroundColor Red
    exit 2
}

# 报告路径：默认 %TEMP%，绝不默认落仓库
if ($OutputPath) {
    $reportPath = $OutputPath
    if (-not [IO.Path]::IsPathRooted($reportPath)) {
        $reportPath = Join-Path (Get-Location).ProviderPath $reportPath
    }
}
else {
    $tempDir = $env:TEMP
    if (-not $tempDir) { $tempDir = [IO.Path]::GetTempPath() }
    $reportPath = Join-Path $tempDir "yitai-maintenance-$stamp.json"
}
$reportDir = Split-Path -Parent $reportPath
if ($reportDir -and -not (Test-Path -LiteralPath $reportDir)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}

# 报告若落在仓库里，提醒一句（体检产物不该被提交）
$reportInsideRepo = $reportPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
if ($reportInsideRepo) {
    $notes.Add("报告写在了仓库里（$reportPath），注意别提交")
}

# 构建日志也放 %TEMP%，失败时用得上
$logDir = $env:TEMP
if (-not $logDir) { $logDir = [IO.Path]::GetTempPath() }
$buildLogPath   = Join-Path $logDir "yitai-maintenance-$stamp-build.log"
$releaseLogPath = Join-Path $logDir "yitai-maintenance-$stamp-release.log"

# ---------------------------------------------------------------- 小工具

function New-Assertion {
    param(
        [string]$Name,
        [object]$Expected,
        [object]$Actual,
        [bool]$Pass,
        [string]$Message
    )
    return [ordered]@{
        name     = $Name
        expected = $Expected
        actual   = $Actual
        pass     = $Pass
        message  = $Message
    }
}

# 用子进程跑被体检的命令：拿到真实退出码，也不怕对方脚本里 exit 把我们带走。
function Invoke-Child {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory
    )

    $savedLocation = (Get-Location).ProviderPath
    $thrown = $false
    $text = ''
    $code = -1

    try {
        if ($WorkingDirectory) { Set-Location -LiteralPath $WorkingDirectory }
        $text = (& $FilePath @Arguments 2>&1 | Out-String)
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = -1 }
    }
    catch {
        $thrown = $true
        $text = $text + "`n" + $_.Exception.Message
        $code = -1
    }
    finally {
        try { Set-Location -LiteralPath $savedLocation } catch { }
    }

    return [pscustomobject]@{
        ExitCode = $code
        Output   = $text
        Threw    = $thrown
    }
}

# 从 dotnet 构建输出里抠"X 个警告 / X 个错误"（中英文都要认，取最后一次统计行）
function Get-BuildCounts {
    param([string]$Text)

    $warnings = $null
    $errors = $null

    foreach ($m in [regex]::Matches($Text, '(?m)^\s*(\d+)\s*(?:个警告|Warning\(s\))')) {
        $warnings = [int]$m.Groups[1].Value
    }
    foreach ($m in [regex]::Matches($Text, '(?m)^\s*(\d+)\s*(?:个错误|Error\(s\))')) {
        $errors = [int]$m.Groups[1].Value
    }

    return [pscustomobject]@{ Warnings = $warnings; Errors = $errors }
}

# 冒烟测试结尾：全过 "全部通过 ✓（176 项）"，有失败 "失败 N 项 ✗（通过 M 项）"
function Get-SmokeResult {
    param([string]$Text)

    $allPass = $Text -match '全部通过'
    $count = $null
    $m = [regex]::Match($Text, '全部通过[^\r\n（(]*[（(]\s*(\d+)\s*项')
    if ($m.Success) { $count = [int]$m.Groups[1].Value }

    $failedCount = $null
    $fm = [regex]::Match($Text, '(?m)^失败\s*(\d+)\s*项')
    if ($fm.Success) { $failedCount = [int]$fm.Groups[1].Value }

    return [pscustomobject]@{ AllPass = $allPass; Count = $count; FailedCount = $failedCount }
}

function Get-TailText {
    param([string]$Text, [int]$Lines = 12)
    $split = ($Text -split "`r?`n") | Where-Object { $_.Trim().Length -gt 0 }
    $take = @($split | Select-Object -Last $Lines)
    return ($take -join "`n")
}

function Add-Check {
    param(
        [string]$Id,
        [string]$Title,
        [string]$Status,
        [string]$Detail,
        [object]$Assertions = @(),
        [double]$Seconds = 0,
        [hashtable]$Extra = @{}
    )

    $entry = [ordered]@{
        id              = $Id
        title           = $Title
        status          = $Status
        detail          = $Detail
        durationSeconds = [math]::Round($Seconds, 1)
        assertions      = @($Assertions)
    }
    foreach ($key in $Extra.Keys) { $entry[$key] = $Extra[$key] }

    $checks.Add($entry)

    if ($Status -eq 'fail') { $failures.Add("$Title：$Detail") }
}

# ---------------------------------------------------------------- 检查 1：关键文件

$filesWatch = [Diagnostics.Stopwatch]::StartNew()

$buildScriptPath = $BuildScript
if (-not [IO.Path]::IsPathRooted($buildScriptPath)) {
    $buildScriptPath = Join-Path $root $buildScriptPath
}

$requiredFiles = @(
    [pscustomobject]@{ Name = 'buildScript';      Label = $BuildScript;                     Path = $buildScriptPath },
    [pscustomobject]@{ Name = 'maintenanceDoc';   Label = 'docs/MAINTENANCE.md';            Path = (Join-Path $root 'docs\MAINTENANCE.md') },
    [pscustomobject]@{ Name = 'todoDoc';          Label = 'docs/TODO.md';                   Path = (Join-Path $root 'docs\TODO.md') }
)

$fileAssertions = @()
$missingLabels = @()
foreach ($item in $requiredFiles) {
    $exists = Test-Path -LiteralPath $item.Path -PathType Leaf
    if (-not $exists) { $missingLabels += $item.Label }
    $fileAssertions += New-Assertion -Name $item.Name -Expected 'file exists' -Actual ($(if ($exists) { 'found' } else { 'missing' })) -Pass $exists -Message ("{0} -> {1}" -f $item.Label, $item.Path)
}

$filesWatch.Stop()

if ($missingLabels.Count -eq 0) {
    $filesDetail = "构建脚本、docs/MAINTENANCE.md、docs/TODO.md 都在（3/3）"
    Add-Check -Id 'files' -Title '关键文件存在' -Status 'pass' -Detail $filesDetail -Assertions $fileAssertions -Seconds $filesWatch.Elapsed.TotalSeconds
}
else {
    $filesDetail = "缺少：" + ($missingLabels -join '、')
    Add-Check -Id 'files' -Title '关键文件存在' -Status 'fail' -Detail $filesDetail -Assertions $fileAssertions -Seconds $filesWatch.Elapsed.TotalSeconds
}

# ---------------------------------------------------------------- 检查 2：构建 + 冒烟测试

$buildWatch = [Diagnostics.Stopwatch]::StartNew()
$buildOk = $false

if (-not (Test-Path -LiteralPath $buildScriptPath -PathType Leaf)) {
    $buildWatch.Stop()
    Add-Check -Id 'build' -Title '构建 + 冒烟测试' -Status 'fail' `
        -Detail "构建脚本不存在，没能执行：$buildScriptPath" `
        -Assertions @((New-Assertion -Name 'buildScriptExists' -Expected 'exists' -Actual 'missing' -Pass $false -Message $buildScriptPath)) `
        -Seconds $buildWatch.Elapsed.TotalSeconds
}
elseif ($buildScriptPath -notmatch '\.ps1$') {
    $buildWatch.Stop()
    Add-Check -Id 'build' -Title '构建 + 冒烟测试' -Status 'fail' `
        -Detail "构建脚本不是 .ps1，没法用 -File 跑：$buildScriptPath" `
        -Assertions @((New-Assertion -Name 'buildScriptKind' -Expected '.ps1' -Actual ([IO.Path]::GetExtension($buildScriptPath)) -Pass $false -Message $buildScriptPath)) `
        -Seconds $buildWatch.Elapsed.TotalSeconds
}
else {
    # 用当前这套 PowerShell 的子进程跑：退出码真实，日志能留下
    if ($PSVersionTable.PSEdition -eq 'Core') {
        $shellExe = Join-Path $PSHOME 'pwsh.exe'
        if (-not (Test-Path -LiteralPath $shellExe)) { $shellExe = 'pwsh' }
    }
    else {
        $shellExe = Join-Path $PSHOME 'powershell.exe'
        if (-not (Test-Path -LiteralPath $shellExe)) { $shellExe = 'powershell' }
    }

    $buildRun = Invoke-Child -FilePath $shellExe `
        -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NonInteractive', '-File', $buildScriptPath) `
        -WorkingDirectory $root

    # 完整日志留一份在 %TEMP%
    try { [IO.File]::WriteAllText($buildLogPath, $buildRun.Output, [Text.UTF8Encoding]::new($false)) } catch { }

    $counts = Get-BuildCounts -Text $buildRun.Output
    $smoke = Get-SmokeResult -Text $buildRun.Output

    $exitPass = ($buildRun.ExitCode -eq 0)
    $warnPass = ($null -ne $counts.Warnings -and $counts.Warnings -eq 0)
    $errPass = ($null -ne $counts.Errors -and $counts.Errors -eq 0)
    $countPass = ($null -ne $smoke.Count -and $smoke.Count -ge $ExpectedTestCount)
    $smokePass = ($smoke.AllPass -and $countPass)

    $assertions = @(
        (New-Assertion -Name 'exitCode' -Expected 0 -Actual $buildRun.ExitCode -Pass $exitPass -Message 'build.ps1 退出码'),
        (New-Assertion -Name 'debugWarnings' -Expected 0 -Actual $counts.Warnings -Pass $warnPass -Message 'Debug 构建警告数'),
        (New-Assertion -Name 'debugErrors' -Expected 0 -Actual $counts.Errors -Pass $errPass -Message 'Debug 构建错误数'),
        (New-Assertion -Name 'smokeAllPass' -Expected $true -Actual $smoke.AllPass -Pass $smoke.AllPass -Message '冒烟测试是否全过'),
        (New-Assertion -Name 'smokeCount' -Expected ">=$ExpectedTestCount" -Actual $smoke.Count -Pass $countPass -Message '冒烟测试项数（只增不减）')
    )

    $buildWatch.Stop()
    $buildOk = ($exitPass -and $warnPass -and $errPass -and $smokePass)

    if ($buildOk) {
        $warnText = '未取到'
        if ($null -ne $counts.Warnings) { $warnText = $counts.Warnings }
        $errText = '未取到'
        if ($null -ne $counts.Errors) { $errText = $counts.Errors }

        $smokeText = "冒烟 $($smoke.Count) 项全过"
        if ($smoke.Count -gt $ExpectedTestCount) {
            $smokeText = "冒烟 $($smoke.Count) 项全过（比基线 $ExpectedTestCount 多 $(($smoke.Count - $ExpectedTestCount)) 项）"
        }

        $buildDetail = "退出码 0；Debug $warnText 警告 / $errText 错误；$smokeText"
        Add-Check -Id 'build' -Title '构建 + 冒烟测试' -Status 'pass' -Detail $buildDetail `
            -Assertions $assertions -Seconds $buildWatch.Elapsed.TotalSeconds `
            -Extra @{ exitCode = $buildRun.ExitCode; log = $buildLogPath; smokeCount = $smoke.Count }
    }
    else {
        $reasons = @()
        if (-not $exitPass) { $reasons += "退出码 $($buildRun.ExitCode)（要 0）" }
        if (-not $warnPass) {
            if ($null -eq $counts.Warnings) { $reasons += '输出里找不到警告统计' } else { $reasons += "Debug 有 $($counts.Warnings) 个警告（要 0）" }
        }
        if (-not $errPass) {
            if ($null -eq $counts.Errors) { $reasons += '输出里找不到错误统计' } else { $reasons += "Debug 有 $($counts.Errors) 个错误（要 0）" }
        }
        if (-not $smokePass) {
            if (-not $smoke.AllPass) {
                if ($null -ne $smoke.FailedCount) { $reasons += "冒烟测试有 $($smoke.FailedCount) 项失败" }
                else { $reasons += '没看到「全部通过 …项」的收尾行（构建可能中途就挂了）' }
            }
            elseif ($null -eq $smoke.Count) { $reasons += "冒烟测试没报项数（要 ≥$ExpectedTestCount 项）" }
            elseif ($smoke.Count -lt $ExpectedTestCount) { $reasons += "冒烟测试只有 $($smoke.Count) 项（要 ≥$ExpectedTestCount）" }
            else { $reasons += '冒烟测试没全过' }
        }

        $buildDetail = ($reasons -join '；') + "（日志：$buildLogPath）"
        Add-Check -Id 'build' -Title '构建 + 冒烟测试' -Status 'fail' -Detail $buildDetail `
            -Assertions $assertions -Seconds $buildWatch.Elapsed.TotalSeconds `
            -Extra @{ exitCode = $buildRun.ExitCode; log = $buildLogPath; outputTail = (Get-TailText -Text $buildRun.Output) }
    }
}

# ---------------------------------------------------------------- 检查 3：Release 构建

$releaseWatch = [Diagnostics.Stopwatch]::StartNew()

$slnx = @(Get-ChildItem -LiteralPath $root -Filter '*.slnx' -File -ErrorAction SilentlyContinue)
if (-not $buildOk) {
    $releaseWatch.Stop()
    Add-Check -Id 'release' -Title 'Release 构建' -Status 'skipped' `
        -Detail 'Debug 构建没通过，跳过 Release（先修 Debug）' -Seconds 0
}
elseif ($slnx.Count -eq 0) {
    $releaseWatch.Stop()
    Add-Check -Id 'release' -Title 'Release 构建' -Status 'fail' `
        -Detail '仓库根找不到 *.slnx 解决方案文件' `
        -Assertions @((New-Assertion -Name 'solutionFile' -Expected '*.slnx' -Actual 'none' -Pass $false -Message $root)) `
        -Seconds $releaseWatch.Elapsed.TotalSeconds
}
elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $releaseWatch.Stop()
    Add-Check -Id 'release' -Title 'Release 构建' -Status 'fail' `
        -Detail '找不到 dotnet 命令（需要 .NET SDK 10+）' `
        -Assertions @((New-Assertion -Name 'dotnetAvailable' -Expected $true -Actual $false -Pass $false -Message 'dotnet 不在 PATH 里')) `
        -Seconds $releaseWatch.Elapsed.TotalSeconds
}
else {
    $solutionPath = $slnx[0].FullName
    $releaseRun = Invoke-Child -FilePath 'dotnet' `
        -Arguments @('build', $solutionPath, '-c', 'Release', '--nologo') `
        -WorkingDirectory $root

    try { [IO.File]::WriteAllText($releaseLogPath, $releaseRun.Output, [Text.UTF8Encoding]::new($false)) } catch { }

    $counts = Get-BuildCounts -Text $releaseRun.Output
    $exitPass = ($releaseRun.ExitCode -eq 0)
    $warnPass = ($null -ne $counts.Warnings -and $counts.Warnings -eq 0)
    $errPass = ($null -ne $counts.Errors -and $counts.Errors -eq 0)

    $assertions = @(
        (New-Assertion -Name 'exitCode' -Expected 0 -Actual $releaseRun.ExitCode -Pass $exitPass -Message 'Release 构建退出码'),
        (New-Assertion -Name 'releaseWarnings' -Expected 0 -Actual $counts.Warnings -Pass $warnPass -Message 'Release 构建警告数'),
        (New-Assertion -Name 'releaseErrors' -Expected 0 -Actual $counts.Errors -Pass $errPass -Message 'Release 构建错误数')
    )

    $releaseWatch.Stop()

    if ($exitPass -and $warnPass -and $errPass) {
        $warnText = '未取到'
        if ($null -ne $counts.Warnings) { $warnText = $counts.Warnings }
        $errText = '未取到'
        if ($null -ne $counts.Errors) { $errText = $counts.Errors }
        Add-Check -Id 'release' -Title 'Release 构建' -Status 'pass' `
            -Detail "退出码 0；Release $warnText 警告 / $errText 错误" `
            -Assertions $assertions -Seconds $releaseWatch.Elapsed.TotalSeconds `
            -Extra @{ exitCode = $releaseRun.ExitCode; log = $releaseLogPath }
    }
    else {
        $reasons = @()
        if (-not $exitPass) { $reasons += "退出码 $($releaseRun.ExitCode)（要 0）" }
        if (-not $warnPass) {
            if ($null -eq $counts.Warnings) { $reasons += '输出里找不到警告统计' } else { $reasons += "Release 有 $($counts.Warnings) 个警告（要 0）" }
        }
        if (-not $errPass) {
            if ($null -eq $counts.Errors) { $reasons += '输出里找不到错误统计' } else { $reasons += "Release 有 $($counts.Errors) 个错误（要 0）" }
        }
        Add-Check -Id 'release' -Title 'Release 构建' -Status 'fail' `
            -Detail (($reasons -join '；') + "（日志：$releaseLogPath）") `
            -Assertions $assertions -Seconds $releaseWatch.Elapsed.TotalSeconds `
            -Extra @{ exitCode = $releaseRun.ExitCode; log = $releaseLogPath; outputTail = (Get-TailText -Text $releaseRun.Output) }
    }
}

# ---------------------------------------------------------------- 检查 4：git 工作区

# 哪些算"代码 / 工程文件"：碰了它们才算把仓库弄脏
$codePathPattern = '^(src|tests|dist|tools|\.github)/|^build\.ps1$|^YiTai\.exe$|\.(csproj|slnx|props|targets|cs|ps1|psm1|yml|yaml|xaml)$'

$gitWatch = [Diagnostics.Stopwatch]::StartNew()

if ($AllowDirty) {
    $gitWatch.Stop()
    Add-Check -Id 'git' -Title 'git 工作区' -Status 'skipped' -Detail '已用 -AllowDirty 跳过' -Seconds 0
}
elseif (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    $gitWatch.Stop()
    Add-Check -Id 'git' -Title 'git 工作区' -Status 'fail' `
        -Detail '找不到 git 命令，没法判断工作区状态' `
        -Assertions @((New-Assertion -Name 'gitAvailable' -Expected $true -Actual $false -Pass $false -Message 'git 不在 PATH 里')) `
        -Seconds $gitWatch.Elapsed.TotalSeconds
}
else {
    $gitRun = Invoke-Child -FilePath 'git' `
        -Arguments @('-c', 'core.quotePath=false', '-C', $root, 'status', '--porcelain=v1') `
        -WorkingDirectory $root

    $gitWatch.Stop()

    if ($gitRun.ExitCode -ne 0) {
        Add-Check -Id 'git' -Title 'git 工作区' -Status 'fail' `
            -Detail ("git status 失败（退出码 {0}）：{1}" -f $gitRun.ExitCode, (Get-TailText -Text $gitRun.Output -Lines 4)) `
            -Assertions @((New-Assertion -Name 'gitStatus' -Expected 0 -Actual $gitRun.ExitCode -Pass $false -Message '不是 git 仓库或 git 出错')) `
            -Seconds $gitWatch.Elapsed.TotalSeconds
    }
    else {
        $codeChanges = @()
        $docChanges = @()
        $untracked = @()

        foreach ($line in ($gitRun.Output -split "`r?`n")) {
            if ($line.Trim().Length -le 3) { continue }
            $code = $line.Substring(0, 2)
            $path = $line.Substring(3).Trim()

            # 重命名形如 "R  old -> new"
            $arrow = $path.IndexOf(' -> ')
            if ($arrow -ge 0) { $path = $path.Substring($arrow + 4).Trim() }
            $path = $path.Trim('"')

            if ($code -match '^\?\?') {
                $untracked += $path
            }
            elseif ($path -match $codePathPattern) {
                $codeChanges += ("{0} {1}" -f $code, $path)
            }
            else {
                $docChanges += ("{0} {1}" -f $code, $path)
            }
        }

        $assertions = @(
            (New-Assertion -Name 'codeChanges' -Expected 0 -Actual $codeChanges.Count -Pass ($codeChanges.Count -eq 0) -Message '代码 / 工程文件的未提交改动数')
        )

        $hintParts = @()
        if ($docChanges.Count -gt 0) { $hintParts += "文档改动 $($docChanges.Count) 处" }
        if ($untracked.Count -gt 0) { $hintParts += "未跟踪文件 $($untracked.Count) 个" }

        if ($codeChanges.Count -eq 0) {
            $detail = '代码 / 工程文件没有未提交改动'
            if ($hintParts.Count -gt 0) { $detail += '（仅提示：' + ($hintParts -join '，') + '）' }
            Add-Check -Id 'git' -Title 'git 工作区' -Status 'pass' -Detail $detail `
                -Assertions $assertions -Seconds $gitWatch.Elapsed.TotalSeconds `
                -Extra @{ codeChanges = @($codeChanges); docChanges = @($docChanges); untracked = @($untracked) }
        }
        else {
            $sample = @($codeChanges | Select-Object -First 5) -join '，'
            $more = ''
            if ($codeChanges.Count -gt 5) { $more = "…（共 $($codeChanges.Count) 处）" }
            Add-Check -Id 'git' -Title 'git 工作区' -Status 'fail' `
                -Detail ("代码 / 工程文件有 $($codeChanges.Count) 处未提交改动：$sample$more") `
                -Assertions $assertions -Seconds $gitWatch.Elapsed.TotalSeconds `
                -Extra @{ codeChanges = @($codeChanges); docChanges = @($docChanges); untracked = @($untracked) }
        }
    }
}

# ---------------------------------------------------------------- 汇总与报告

$stopwatch.Stop()

$passed = @($checks | Where-Object { $_.status -eq 'pass' }).Count
$failed = @($checks | Where-Object { $_.status -eq 'fail' }).Count
$skipped = @($checks | Where-Object { $_.status -eq 'skipped' }).Count
$ok = ($failed -eq 0)

$report = [ordered]@{
    tool             = 'yitai-maintenance-check'
    schemaVersion    = 1
    repoRoot         = $root
    buildScript      = $BuildScript
    expectedTestCount = $ExpectedTestCount
    startedAt        = $checkStarted.ToString('o')
    finishedAt       = (Get-Date).ToString('o')
    durationSeconds  = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    allowDirty       = [bool]$AllowDirty
    checks           = $checks.ToArray()
    summary          = [ordered]@{
        total   = $checks.Count
        passed  = $passed
        failed  = $failed
        skipped = $skipped
        ok      = $ok
    }
    failures         = @($failures)
    notes            = @($notes)
    reportPath       = $reportPath
}

$jsonText = $report | ConvertTo-Json -Depth 8
try {
    [IO.File]::WriteAllText($reportPath, $jsonText, [Text.UTF8Encoding]::new($false))
    $reportWritten = $true
}
catch {
    $reportWritten = $false
    $failures.Add("报告没写进磁盘：$($_.Exception.Message)")
}

# ---------------------------------------------------------------- 人类可读摘要

$line = '=' * 62
Write-Host ''
Write-Host $line -ForegroundColor DarkCyan
Write-Host ' YiTai 一键体检' -ForegroundColor Cyan
Write-Host (" 仓库：{0}" -f $root)
Write-Host (" 时间：{0}" -f $checkStarted.ToString('yyyy-MM-dd HH:mm:ss'))
Write-Host $line -ForegroundColor DarkCyan

foreach ($check in $checks) {
    $tag = '[SKIP ]'
    $color = 'Yellow'
    if ($check.status -eq 'pass') { $tag = '[ PASS ]'; $color = 'Green' }
    elseif ($check.status -eq 'fail') { $tag = '[ FAIL ]'; $color = 'Red' }

    Write-Host ("{0} {1,-16} {2}" -f $tag, $check.title, $check.detail) -ForegroundColor $color
}

Write-Host ('-' * 62) -ForegroundColor DarkGray
Write-Host (" 耗时 {0} 秒" -f [math]::Round($stopwatch.Elapsed.TotalSeconds, 1))

if ($ok) {
    Write-Host (" 结论：全部通过（{0}/{1}{2}）" -f $passed, $checks.Count, $(if ($skipped -gt 0) { "，跳过 $skipped" } else { '' })) -ForegroundColor Green
}
else {
    Write-Host (" 结论：不通过（通过 {0}，失败 {1}，跳过 {2}）" -f $passed, $failed, $skipped) -ForegroundColor Red
    Write-Host ' 失败项：' -ForegroundColor Red
    $i = 0
    foreach ($f in $failures) {
        $i++
        Write-Host ("   {0}. {1}" -f $i, $f) -ForegroundColor Red
    }
}

foreach ($n in $notes) {
    Write-Host (" 提示：{0}" -f $n) -ForegroundColor Yellow
}

if ($reportWritten) {
    Write-Host (" 报告：{0}" -f $reportPath) -ForegroundColor DarkGray
}
else {
    Write-Host ' 报告没写出来（见上面的失败项）' -ForegroundColor Red
}
Write-Host $line -ForegroundColor DarkCyan
Write-Host ''

# -Json 时 stdout 上只放 JSON，方便 ConvertFrom-Json 直接吃
if ($Json) {
    Write-Output $jsonText
}

if ($ok -and $reportWritten) { exit 0 }
exit 1
