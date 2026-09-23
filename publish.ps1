# Publish SliderSorter (WPF).
# Default: multi-file framework-dependent folder into dist\ (零件摊开，
#   requires .NET 8 Desktop Runtime); add -SelfContained to bundle the
#   runtime (no prerequisites), and/or -SingleFile to bundle everything
#   back into one exe (compressed, ~61MB).
param(
    [switch]$SelfContained,
    [switch]$SingleFile
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist"
$project = Join-Path $root "src\SliderSorter.Wpf\SliderSorter.Wpf.csproj"

# dotnet publish 只覆盖同名文件、不删多余文件。换发布形态（自包含 ↔ 框架依赖）后旧文件
# 会原地留下，目录里同时有 246 项和 6 项，看着发布成功实则混杂，故先清空。
if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

$dotnetArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=none",
    "/p:AllowedReferenceRelatedFileExtensions=none"
)
if ($SingleFile) {
    $dotnetArgs += "/p:PublishSingleFile=true"
}
if ($SelfContained) {
    $dotnetArgs += "--self-contained", "true"
} else {
    $dotnetArgs += "--self-contained", "false"
}
$dotnetArgs += "-o", $out

dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    # dotnet 是原生命令，失败不会触发 ErrorActionPreference；这里必须显式拦截，
    # 否则下面的文件列表会把旧产物误当成发布成功
    throw "dotnet publish 失败（exit $LASTEXITCODE）"
}
Write-Host ""
Write-Host "Output: $out" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, Length
