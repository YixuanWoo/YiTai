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

        # dist 那份是"拷给别人"用的；根目录这份才是平时双击的入口。
        # 两处不同步，就会出现"代码改了、打开的还是老版本"这种假象（已经吃过一次）。
        # 根目录那份若正在运行，覆盖不了也不该让发布失败，所以只警告。
        $rootExe = Join-Path $root 'YiTai.exe'
        try {
            Copy-Item -LiteralPath $exe -Destination $rootExe -Force -ErrorAction Stop
            Write-Host "已同步到 $rootExe" -ForegroundColor Green
        }
        catch {
            Write-Host "根目录那份正在运行，没能覆盖：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}
else {
    $exe = Join-Path $root 'src\YiTai.App\bin\Debug\net10.0-windows\YiTai.exe'
    Write-Host "完成：$exe" -ForegroundColor Green
}
