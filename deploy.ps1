# sopAccess 一键部署脚本
# 用法（管理员 PowerShell）:
#   .\deploy.ps1 -GameDir "F:\Steam\steamapps\common\Sister Other Paranoia"
# 作用:
#   1. 若游戏目录尚无 BepInEx，则复制 BepInEx 运行时（winhttp.dll、doorstop_config.ini、BepInEx\）
#   2. 部署最新编译的 SopAccess.dll 到 BepInEx\plugins\
#   3. 复制 NVDA 插件到用户 NVDA 插件目录（下次 NVDA 重启生效）
param(
    [string]$GameDir = "F:\Steam\steamapps\common\Sister Other Paranoia",
    [switch]$SkipBepInEx,
    [switch]$SkipNvda
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$BepInExZip = Join-Path $Root "game-mod\thirdparty\BepInEx_win_x64_5.4.23.3.zip"
$BepInExDir = Join-Path $Root "game-mod\thirdparty\BepInEx-5.4.23.3"
$PluginDll = Join-Path $Root "game-mod\SopAccess\bin\Release\SopAccess.dll"
$AddonSrc = Join-Path $Root "nvda-addon\sopAccess"
$NvdaAddons = Join-Path $env:APPDATA "nvda\addons"

Write-Host "=== 游戏目录: $GameDir ==="
if (-not (Test-Path $GameDir)) { throw "游戏目录不存在: $GameDir" }

if (-not $SkipBepInEx) {
    if (-not (Test-Path (Join-Path $GameDir "winhttp.dll"))) {
        Write-Host "安装 BepInEx 运行时..."
        if (-not (Test-Path $BepInExDir)) {
            if (-not (Test-Path $BepInExZip)) { throw "缺少 $BepInExZip，请先运行 game-mod\build.ps1 下载" }
            Expand-Archive $BepInExZip -DestinationPath $BepInExDir -Force
        }
        Copy-Item "$BepInExDir\winhttp.dll" $GameDir -Force
        Copy-Item "$BepInExDir\doorstop_config.ini" $GameDir -Force
        Copy-Item "$BepInExDir\.doorstop_version" $GameDir -Force
        Copy-Item "$BepInExDir\BepInEx" $GameDir -Recurse -Force
    } else {
        Write-Host "BepInEx 已存在，跳过"
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx\plugins") | Out-Null
    Copy-Item $PluginDll (Join-Path $GameDir "BepInEx\plugins\SopAccess.dll") -Force
    Write-Host "插件已部署: $PluginDll"
}

if (-not $SkipNvda) {
    $dst = Join-Path $NvdaAddons "sopAccess"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Get-ChildItem $AddonSrc | Copy-Item -Destination $dst -Recurse -Force
    Write-Host "NVDA 插件已复制到 $dst（重启 NVDA 生效）"
}

Write-Host "=== 完成 ==="
Write-Host "提示：若游戏正在运行，请先退出游戏再执行部署；NVDA 需重启以加载插件。"
