# 把发布产物打成规范发布包：dist\SliderSorter_v<版本>.zip。
# 包内是一个 SliderSorter/ 目录：程序文件 + LICENSE + 英文快速上手（README.md）+
# 第三方许可清单（THIRD-PARTY-NOTICES.md）。每次都会先跑 publish.ps1 重新发布。
#
# 用法: packaging\pack.ps1
param()

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "src\SliderSorter.Wpf\SliderSorter.Wpf.csproj"

# 版本号只认 csproj 这一处：exe 元数据、zip 文件名、Nexus 版本号都照它走
$m = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>')
if (-not $m.Success) { throw "无法从 $csproj 读取 <Version>" }
$version = $m.Groups[1].Value

& (Join-Path $root "publish.ps1")

$dist = Join-Path $root "dist"
$stageDir = Join-Path $dist "package"
$stageApp = Join-Path $stageDir "SliderSorter"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageApp | Out-Null

# 程序文件只搬 dist 下的散文件（package 暂存目录本身自然排除）
Get-ChildItem $dist -File | Copy-Item -Destination $stageApp
Copy-Item (Join-Path $root "LICENSE") $stageApp
Copy-Item (Join-Path $PSScriptRoot "README.md") $stageApp
Copy-Item (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.md") $stageApp

$zip = Join-Path $dist ("SliderSorter_v{0}.zip" -f $version)
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stageApp -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stageDir -Recurse -Force

Write-Host ""
Write-Host "Package: $zip" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length
