# 一键构建 / 打包（Windows，需要 dotnet SDK 10+）
#
#   普通构建：  .\build.ps1
#   发布单文件：.\build.ps1 -Release
#
# -Release 产出 dist\YiTai.exe：自包含、免安装、可直接拷给别人，
# 代价是体积约 70 到 90MB（.NET 运行时一起打进去了）。

param(
    [switch]$Release
)

$ErrorPolicy = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "== 构建 ==" -ForegroundColor Cyan
& dotnet build (Join-Path $root 'YiTai.slnx') -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

Write-Host "== 冒烟测试 ==" -ForegroundColor Cyan
& dotnet run --project (Join-Path $root 'tests\YiTai.Tests') -c Debug --no-build
if ($LASTEXITCODE -ne 0) { throw '测试失败（见上面的输出）' }

if ($Release) {
    Write-Host "== 发布单文件 ==" -ForegroundColor Cyan
    $dist = Join-Path $root 'dist'
    if (-not (Test-Path -LiteralPath $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

    & dotnet publish (Join-Path $root 'src\YiTai.App\YiTai.App.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw '发布失败' }

    $exe = Join-Path $dist 'YiTai.exe'
    if (Test-Path -LiteralPath $exe) {
        $mb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
        Write-Host "完成：$exe（$mb MB）" -ForegroundColor Green
    }
}
else {
    $exe = Join-Path $root 'src\YiTai.App\bin\Debug\net10.0-windows\YiTai.exe'
    Write-Host "完成：$exe" -ForegroundColor Green
}
