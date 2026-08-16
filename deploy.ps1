# sopAccess 一键部署脚本
# 用法（PowerShell 5.1+，必要时管理员运行）:
#   .\deploy.ps1 -GameDir "F:\Steam\steamapps\common\Sister Other Paranoia"
# 不带 -GameDir 时，脚本会尝试从 Steam 注册表/库配置自动定位游戏目录。
# 作用:
#   1. 若游戏目录尚无 BepInEx，则自动下载并部署 BepInEx 运行时（winhttp.dll、doorstop_config.ini、BepInEx\）
#   2. 若未编译 SopAccess.dll，则自动调用 dotnet build 编译并部署到 BepInEx\plugins\
#   3. 复制 NVDA 插件到用户 NVDA 插件目录（下次 NVDA 重启生效）
param(
    [string]$GameDir = "",
    [switch]$SkipBepInEx,
    [switch]$SkipNvda
)

$ErrorActionPreference = "Stop"

# 脚本位于仓库根目录：$PSScriptRoot 即仓库根，不要再次取父级
$Root = $PSScriptRoot
$BepInExVersion = "5.4.23.3"
$ThirdPartyDir = Join-Path $Root "game-mod\thirdparty"
$BepInExZip = Join-Path $ThirdPartyDir "BepInEx_win_x64_$BepInExVersion.zip"
$BepInExDir = Join-Path $ThirdPartyDir "BepInEx-$BepInExVersion"
$BepInExCoreDir = Join-Path $BepInExDir "BepInEx\core"
$PluginDll = Join-Path $Root "game-mod\SopAccess\bin\Release\SopAccess.dll"
$Csproj = Join-Path $Root "game-mod\SopAccess\SopAccess.csproj"
$AddonSrc = Join-Path $Root "nvda-addon\sopAccess"
$NvdaAddons = Join-Path $env:APPDATA "nvda\addons"

# GitHub API 需要 TLS 1.2+（Windows PowerShell 5.1 默认不是）
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-SteamGameDir {
    # 从 Steam 注册表与 libraryfolders.vdf 中查找游戏安装目录；找不到返回空字符串
    $candidates = @()
    try {
        $steamPath = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -ErrorAction Stop).SteamPath
        $steamPath = $steamPath -replace "/", "\"
        $candidates += (Join-Path $steamPath "steamapps\common\Sister Other Paranoia")
        $vdf = Join-Path $steamPath "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            $content = Get-Content $vdf -Raw
            $pathMatches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
            foreach ($m in $pathMatches) {
                $lib = $m.Groups[1].Value -replace '\\+', '\'
                $candidates += (Join-Path $lib "steamapps\common\Sister Other Paranoia")
            }
        }
    } catch {
        # 注册表读取失败则跳过，走占位兜底
    }
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return ""
}

function Ensure-BepInEx {
    # 已解压则跳过
    if (Test-Path (Join-Path $BepInExCoreDir "BepInEx.dll")) { return }
    # 已有 zip 则直接解压
    if (Test-Path $BepInExZip) {
        Write-Host "解压 BepInEx $BepInExVersion ..."
        Expand-Archive $BepInExZip -DestinationPath $BepInExDir -Force
        return
    }
    # 否则自动从官方 Releases 下载
    Write-Host "未找到 $BepInExZip，尝试从 GitHub 自动下载 BepInEx $BepInExVersion ..."
    New-Item -ItemType Directory -Force -Path $ThirdPartyDir | Out-Null
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/BepInEx/BepInEx/releases/tags/v$BepInExVersion" `
            -Headers @{ "User-Agent" = "sop-nvda-deploy" }
        $asset = $release.assets | Where-Object { $_.name -match "win.*x64.*\.zip$" -or $_.name -match "x64.*\.zip$" } | Select-Object -First 1
        if (-not $asset) { throw "发布页未找到 Windows x64 安装包" }
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $BepInExZip
        Expand-Archive $BepInExZip -DestinationPath $BepInExDir -Force
        Write-Host "BepInEx $BepInExVersion 下载并解压完成。"
    } catch {
        Write-Host "自动下载失败：$($_.Exception.Message)"
        throw "请手动下载 BepInEx $BepInExVersion（https://github.com/BepInEx/BepInEx/releases/tag/v$BepInExVersion）的 Windows x64 zip，放到 $BepInExZip 后重新运行本脚本。"
    }
}

function Ensure-PluginDll {
    if (Test-Path $PluginDll) { return }
    Write-Host "未找到编译产物 $PluginDll，尝试自动编译 ..."
    $managed = Join-Path $GameDir "SisterOtherParanoia_Data\Managed"
    if (-not (Test-Path $managed)) {
        throw "未找到游戏程序集目录: $managed（请确认 -GameDir 指向正确的游戏根目录）"
    }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw "未检测到 dotnet SDK。请先安装 .NET SDK（https://dotnet.microsoft.com/download），或手动编译：dotnet build `"$Csproj`" -c Release -p:GameManaged=`"$managed`""
    }
    & $dotnet.Source build $Csproj -c Release -p:GameManaged="$managed" -p:BepInEx="$BepInExCoreDir"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $PluginDll)) {
        throw "编译失败。可尝试手动编译：dotnet build `"$Csproj`" -c Release -p:GameManaged=`"$managed`""
    }
}

# ---- 主流程 ----

# 定位游戏目录：优先使用传入参数，其次自动探测
if (-not $GameDir) {
    $GameDir = Get-SteamGameDir
}
if (-not $GameDir) {
    Write-Host "未找到游戏安装目录。请通过参数指定：.\deploy.ps1 -GameDir `"<游戏根目录>`""
    exit 1
}
Write-Host "=== 游戏目录: $GameDir ==="
if (-not (Test-Path $GameDir)) {
    Write-Host "游戏目录不存在: $GameDir"
    exit 1
}

# 权限提示：Steam 装在用户目录时无需管理员；Program Files 等位置则需要
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "提示：当前非管理员运行。若游戏目录位于需要管理员权限的位置（如 C:\Program Files (x86)\Steam），部署可能失败，请改用管理员 PowerShell 重试。"
}

if (-not $SkipBepInEx) {
    Ensure-BepInEx
    if (-not (Test-Path (Join-Path $GameDir "winhttp.dll"))) {
        Write-Host "安装 BepInEx 运行时..."
        Copy-Item "$BepInExDir\winhttp.dll" $GameDir -Force
        Copy-Item "$BepInExDir\doorstop_config.ini" $GameDir -Force
        Copy-Item "$BepInExDir\.doorstop_version" $GameDir -Force
        Copy-Item "$BepInExDir\BepInEx" $GameDir -Recurse -Force
    } else {
        Write-Host "BepInEx 已存在，跳过"
    }
    Ensure-PluginDll
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir "BepInEx\plugins") | Out-Null
    Copy-Item $PluginDll (Join-Path $GameDir "BepInEx\plugins\SopAccess.dll") -Force
    Write-Host "插件已部署: $PluginDll"
}

if (-not $SkipNvda) {
    if (-not (Test-Path $AddonSrc)) {
        Write-Host "NVDA 插件源码目录不存在（跳过）: $AddonSrc"
    } else {
        $dst = Join-Path $NvdaAddons "sopAccess"
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Get-ChildItem $AddonSrc | Copy-Item -Destination $dst -Recurse -Force
        Write-Host "NVDA 插件已复制到 $dst（重启 NVDA 生效）"
    }
}

Write-Host "=== 完成 ==="
Write-Host "提示：若游戏正在运行，请先退出游戏再执行部署；NVDA 需重启以加载插件。"
