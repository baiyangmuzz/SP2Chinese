#Requires -Version 5.1
<#
    SimplePlanes 2 简体中文汉化 - 一键安装程序

    设计原则（按 Room 先生的要求）：
      * 只往游戏目录里写 BepInEx 和 plugins\SP2Chinese，绝不碰存档和其他模组
      * 覆盖前一律改名备份，从不删除
      * 找不到游戏目录时不做任何猜测，交给人来指定
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [switch]$Uninstall,
    [switch]$Yes,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$PackRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Payload  = Join-Path $PackRoot 'payload'
$Stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'

function Wait-Key {
    param([string]$Message = '按回车退出')
    if (-not $Silent) { Read-Host $Message | Out-Null }
}

function Write-Head($text) {
    Write-Host ''
    Write-Host ('=' * 62) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 62) -ForegroundColor DarkCyan
}

function Write-Ok($text)   { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "  [注意] $text" -ForegroundColor Yellow }
function Write-Err($text)  { Write-Host "  [错误] $text" -ForegroundColor Red }

# ---------------------------------------------------------------- 定位游戏

function Get-SteamLibraries {
    $libs = New-Object System.Collections.Generic.List[string]
    $steam = $null

    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        if (-not (Test-Path $key)) { continue }
        $props = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($props -and $props.SteamPath)  { $steam = $props.SteamPath;  break }
        if ($props -and $props.InstallPath){ $steam = $props.InstallPath; break }
    }

    if ($steam) {
        # 注册表习惯给正斜杠（d:/steam），vdf 给反斜杠，统一后再去重，免得同一个库试两遍
        $steam = $steam.Replace('/', '\').TrimEnd('\')
        if (-not $libs.Contains($steam)) { $libs.Add($steam) }

        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $text = Get-Content $vdf -Raw -Encoding UTF8
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $p = $m.Groups[1].Value.Replace('\\', '\').TrimEnd('\')
                if ($p -and -not $libs.Contains($p)) { $libs.Add($p) }
            }
        }
    }
    return $libs
}

function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    if (-not (Test-Path (Join-Path $dir 'SimplePlanes 2.exe')))  { return $false }
    if (-not (Test-Path (Join-Path $dir 'SimplePlanes 2_Data'))) { return $false }
    return $true
}

function Find-GameDir {
    $tried = New-Object System.Collections.Generic.List[string]

    # 1) Steam 库（注册表 + libraryfolders.vdf）
    foreach ($lib in Get-SteamLibraries) {
        $candidate = Join-Path $lib 'steamapps\common\SimplePlanes 2'
        $tried.Add($candidate) | Out-Null
        if (Test-GameDir $candidate) { return $candidate }
    }

    # 2) 所有本地盘符下的常见位置
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        foreach ($rel in @('steamapps\common\SimplePlanes 2', 'SteamLibrary\steamapps\common\SimplePlanes 2',
                           'Steam\steamapps\common\SimplePlanes 2', 'Games\SimplePlanes 2',
                           'SimplePlanes 2')) {
            $candidate = Join-Path $drive.Root $rel
            $tried.Add($candidate) | Out-Null
            if (Test-GameDir $candidate) { return $candidate }
        }
    }
    return $null
}

# ---------------------------------------------------------------- 手动定位

function Select-GameFolderViaDialog {
    <#
      原生文件夹选择窗口。比让人去找地址栏粘贴友好得多，尤其是群友里
      不知道自己游戏装在哪块盘的那些。

      两条实现路径：先试 WinForms（Windows 10/11 的现代对话框），
      失败再退回 Shell.Application 的 BrowseForFolder；都不可用时返回 $null，
      由调用方转成粘贴路径。
    #>
    $start = $null
    foreach ($lib in Get-SteamLibraries) {
        $candidate = Join-Path $lib 'steamapps\common'
        if (Test-Path $candidate) { $start = $candidate; break }
    }

    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = '请选择 SimplePlanes 2 的安装文件夹（里面应当有 SimplePlanes 2.exe）'
        $dialog.ShowNewFolderButton = $false
        if ($start) { $dialog.SelectedPath = $start }
        $result = $dialog.ShowDialog()
        $chosen = $dialog.SelectedPath
        $dialog.Dispose()
        if ($result -eq [System.Windows.Forms.DialogResult]::OK) { return $chosen }
        return $null                       # 用户取消
    }
    catch {
        # WinForms 装不出来（精简系统 / 被策略拦住），换 COM
    }

    try {
        $shell = New-Object -ComObject Shell.Application
        $folder = $shell.BrowseForFolder(0, '请选择 SimplePlanes 2 的安装文件夹', 0, $start)
        if ($folder -ne $null) { return $folder.Self.Path }
    }
    catch {
        # 两条路都不可用
    }
    return $null
}

function Ask-GameDir {
    # 无人值守（测试 / 脚本调用）时绝不弹窗、绝不等待输入，直接失败交给调用方处理
    if ($Silent) { return $null }

    Write-Host ''
    Write-Host '  没能自动找到游戏目录。' -ForegroundColor Yellow
    Write-Host '  不知道装在哪？Steam 里右键游戏 → 管理 → 浏览本地文件，地址栏就是。' -ForegroundColor DarkGray

    if (-not $Silent) {
        Write-Host ''
        Write-Host '  回车打开文件夹选择窗口，或输入 p 改为粘贴路径：' -ForegroundColor Yellow -NoNewline
        $choice = Read-Host
        if ($choice -notmatch '^[Pp]') {
            for ($i = 0; $i -lt 3; $i++) {
                $picked = Select-GameFolderViaDialog
                if (-not $picked) { break }                 # 用户点了取消
                if (Test-GameDir $picked) { return $picked }
                Write-Err '选中的文件夹里没有 SimplePlanes 2.exe，请选游戏根目录。'
                Write-Host '  （不是里面的 SimplePlanes 2_Data，也不是 Steam 的 common 文件夹）' -ForegroundColor DarkGray
            }
            if (-not $picked) { Write-Host '  已取消选择，改为手动粘贴。' -ForegroundColor DarkGray }
        }
    }

    Write-Host ''
    Write-Host '  把游戏文件夹路径粘贴进来（拖动文件夹到窗口也行）：' -ForegroundColor Gray
    for ($i = 0; $i -lt 5; $i++) {
        $typed = Read-Host '  游戏目录'
        if ([string]::IsNullOrWhiteSpace($typed)) { continue }
        $typed = $typed.Trim('"').Trim("'").TrimEnd('\')
        if (Test-GameDir $typed) { return $typed }
        Write-Err '这个目录里没有 SimplePlanes 2.exe，再试一次。'
    }
    return $null
}

# ---------------------------------------------------------------- 状态检查

function Get-GameProcess([string]$dir) {
    $procs = @(Get-Process -Name 'SimplePlanes 2' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return @() }

    # Only touch the instance that actually lives in the target folder. A blanket
    # Stop-Process would also kill a second copy the user is flying right now.
    $full = [System.IO.Path]::GetFullPath($dir).TrimEnd('\')
    $hit = @()
    foreach ($p in $procs) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if (-not $path) { $hit += $p; continue }        # no access, assume it is ours
        if ($path.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase)) { $hit += $p }
    }
    return $hit
}

function Get-InstalledInfo([string]$dir) {
    $pluginDir = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
    $info = [ordered]@{
        HasBepInEx = Test-Path (Join-Path $dir 'BepInEx\core\BepInEx.dll')
        HasPlugin  = Test-Path (Join-Path $pluginDir 'SP2Chinese.dll')
        HasDict    = Test-Path (Join-Path $pluginDir 'dictionary_zh_cn.tsv')
        PluginDir  = $pluginDir
    }
    return $info
}

# ---------------------------------------------------------------- 安装

function Install-Payload([string]$dir) {
    $info = Get-InstalledInfo $dir

    # --- BepInEx 前置 ---
    if (-not $info.HasBepInEx) {
        Write-Host '  未检测到 BepInEx，正在安装运行环境……' -ForegroundColor Gray
        foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
            $src = Join-Path $Payload $f
            if (Test-Path $src) {
                Copy-Item $src (Join-Path $dir $f) -Force
            }
        }
        # Copy core (and the default config) explicitly, never the whole BepInEx folder:
        # payload\BepInEx also carries plugins\SP2Chinese, which would land here first and
        # then immediately look like an "old version" for the backup step to move aside.
        $bepDst = Join-Path $dir 'BepInEx'
        New-Item -ItemType Directory -Path $bepDst -Force | Out-Null
        Copy-Item (Join-Path $Payload 'BepInEx\core') (Join-Path $bepDst 'core') -Recurse -Force
        $cfgSrc = Join-Path $Payload 'BepInEx\config'
        if ((Test-Path $cfgSrc) -and -not (Test-Path (Join-Path $bepDst 'config'))) {
            Copy-Item $cfgSrc (Join-Path $bepDst 'config') -Recurse -Force
        }
        Write-Ok 'BepInEx 运行环境已就位'
    }
    else {
        Write-Ok '检测到已安装 BepInEx，沿用现有版本（不改动 core）'
    }

    # --- 备份旧的汉化插件 ---
    # 备份必须放在 plugins 之外：BepInEx 会递归扫描 plugins 下所有目录，
    # 把 plugins\SP2Chinese.bak-xxx\SP2Chinese.dll 也当成插件加载一遍，
    # 同一个 GUID 被加载两次会引发 Harmony 补丁冲突。
    $pluginDir = $info.PluginDir
    if (Test-Path $pluginDir) {
        $backupRoot = Join-Path $dir 'BepInEx\_mod_backups'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $backup = Join-Path $backupRoot "SP2Chinese-$Stamp"
        Move-Item $pluginDir $backup
        Write-Ok "旧版已备份到 BepInEx\_mod_backups\SP2Chinese-$Stamp"
    }

    # --- 放置插件与字典 ---
    $srcPlugin = Join-Path $Payload 'BepInEx\plugins\SP2Chinese'
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    Copy-Item (Join-Path $srcPlugin 'SP2Chinese.dll')      $pluginDir -Force
    Copy-Item (Join-Path $srcPlugin 'dictionary_zh_cn.tsv') $pluginDir -Force

    # 顺带清掉 BepInEx 的旧缓存，避免仍加载旧链
    $cache = Join-Path $dir 'BepInEx\cache'
    if (Test-Path $cache) {
        Get-ChildItem $cache -Filter '*.dat' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    return $pluginDir
}

function Test-Install([string]$pluginDir) {    $dll  = Join-Path $pluginDir 'SP2Chinese.dll'
    $dict = Join-Path $pluginDir 'dictionary_zh_cn.tsv'

    if (-not (Test-Path $dll))  { Write-Err 'SP2Chinese.dll 缺失';  return $false }
    if (-not (Test-Path $dict)) { Write-Err 'dictionary_zh_cn.tsv 缺失'; return $false }

    $rows = (Get-Content $dict -Encoding UTF8 | Measure-Object -Line).Lines
    $size = [math]::Round((Get-Item $dll).Length / 1KB, 1)

    $verFile = Join-Path $Payload 'VERSION.txt'
    if (Test-Path $verFile) {
        Write-Ok ("汉化版本               v{0}" -f (Get-Content $verFile -Raw).Trim())
    }

    Write-Host ''
    Write-Ok ("SP2Chinese.dll          {0} KB" -f $size)
    Write-Ok ("dictionary_zh_cn.tsv   {0} 条译文" -f $rows)

    if ($rows -lt 1000) { Write-Warn2 '译文条数偏少，字典可能不完整'; return $false }
    return $true
}


# ---------------------------------------------------------------- 哈希校验

function Verify-WithManifest([string]$dir) {
    <#
      按 manifest.json 里记录的 SHA256 逐个核对装完的文件。
      目的是把"装歪了/下载不完整/被杀软改过"这类问题当场暴露，
      而不是让群友进游戏发现"怎么还是英文"再回来问。
    #>
    $mf = Join-Path $PackRoot 'manifest.json'
    if (-not (Test-Path $mf)) {
        Write-Warn2 '安装包内没有 manifest.json，跳过哈希校验'
        return $true
    }
    try {
        $data = Get-Content $mf -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Write-Warn2 'manifest.json 读取失败，跳过哈希校验'
        return $true
    }

    $checked = 0
    $bad = New-Object System.Collections.Generic.List[string]

    foreach ($prop in $data.files.PSObject.Properties) {
        $rel = $prop.Name.Replace('/', '\')
        $target = Join-Path $dir $rel
        if (-not (Test-Path $target)) {
            $bad.Add("$rel (缺失)")
            continue
        }
        $actual = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLower()
        $expect = [string]$prop.Value.sha256
        $checked++
        if ($actual -ne $expect.ToLower()) {
            $bad.Add("$rel (内容不符)")
        }
    }

    if ($bad.Count -eq 0) {
        Write-Ok ("哈希校验通过（{0} 个文件）" -f $checked)
        if ($data.dictRows) { Write-Host ("         词典版本 {0}，{1} 条" -f $data.dictVersion, $data.dictRows) -ForegroundColor DarkGray }
        return $true
    }

    Write-Err ("{0} 个文件校验失败：" -f $bad.Count)
    foreach ($b in $bad) { Write-Host ("         " + $b) -ForegroundColor DarkGray }
    return $false
}

# ---------------------------------------------------------------- 卸载

function Uninstall-Plugin([string]$dir) {
    $pluginDir = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
    if (-not (Test-Path $pluginDir)) {
        Write-Warn2 '没有找到已安装的汉化插件，无需卸载。'
        return
    }
    $backupRoot = Join-Path $dir 'BepInEx\_mod_backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $moved = Join-Path $backupRoot "SP2Chinese-uninstalled-$Stamp"
    Move-Item $pluginDir $moved
    Write-Ok "汉化已卸载（挪到 BepInEx\_mod_backups\SP2Chinese-uninstalled-$Stamp，没有删除）"
    Write-Host '  BepInEx 本体保留，其他模组不受影响。' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 主流程

Write-Head 'SimplePlanes 2 简体中文汉化'

if (-not (Test-Path $Payload)) {
    Write-Err "安装包不完整：缺少 payload 目录。请重新解压整个压缩包。"
    Wait-Key
    exit 1
}

# 定位目录
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = Find-GameDir
}
if (-not (Test-GameDir $GameDir)) {
    $GameDir = Ask-GameDir
}
if (-not $GameDir) {
    Write-Err '无法确定游戏目录，安装中止。'
    Wait-Key
    exit 1
}

Write-Host ''
Write-Host "  游戏目录：$GameDir" -ForegroundColor White

# 给群友一个自己按下开始的瞬间，而不是双击完就无声无息地跑完
if (-not $Silent) {
    $ver = '1.4.4'
    $verFile = Join-Path $Payload 'VERSION.txt'
    if (Test-Path $verFile) { $ver = (Get-Content $verFile -Raw).Trim() }
    Write-Host ''
    Write-Host '  ---------------------------------------------' -ForegroundColor DarkGray
    Write-Host ("   即将安装简体中文汉化 v{0}" -f $ver) -ForegroundColor White
    if ($Uninstall) { Write-Host '   动作：卸载汉化（会保留备份，可随时恢复）' -ForegroundColor White }
    else            { Write-Host '   动作：安装 / 更新汉化（旧版会自动备份）' -ForegroundColor White }
    Write-Host '  ---------------------------------------------' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  按回车开始，或直接关闭窗口取消……' -ForegroundColor Yellow -NoNewline
    [void](Read-Host)
    Write-Host ''
}

# 游戏必须关掉，否则 DLL 被锁
$proc = Get-GameProcess $GameDir
if ($proc.Count -gt 0) {
    Write-Host ''
    Write-Warn2 '游戏正在运行，插件文件会被占用。'
    if (-not $Yes) {
        $answer = Read-Host '  关闭游戏后继续？(Y/n)'
        if ($answer -ne '' -and $answer -notmatch '^[Yy]') {
            Write-Err '安装已取消。'
            Wait-Key
            exit 1
        }
    }
    Write-Host '  正在关闭游戏……' -ForegroundColor Gray
    $proc | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

try {
    if ($Uninstall) {
        Uninstall-Plugin $GameDir
    }
    else {
        $pluginDir = Install-Payload $GameDir
        if (-not (Test-Install $pluginDir)) {
            Write-Err '安装校验未通过，请把上面的输出发给群主。'
            Wait-Key
            exit 1
        }

        $hashOk = Verify-WithManifest $GameDir

        Write-Host ''
        if ($hashOk) { Write-Head '安装完成' }
        else         { Write-Head '安装完成（但有文件校验不一致）' }
        Write-Host '  启动游戏即可，界面应为中文。' -ForegroundColor Green
        if (-not $hashOk) {
            Write-Host '  如果进游戏仍是英文，重新解压安装包再装一次。' -ForegroundColor Yellow
        }
        Write-Host ''
        Write-Host '  首次进游戏如果仍是英文：' -ForegroundColor Gray
        Write-Host '   1. 确认是从 Steam 正常启动（不要用别的快捷方式）' -ForegroundColor DarkGray
        Write-Host '   2. 完全退出游戏后重新打开' -ForegroundColor DarkGray
        Write-Host '   3. 检查 BepInEx\LogOutput.log 里有没有 "SP2 chinese loaded"' -ForegroundColor DarkGray
    }
}
catch {
    Write-Err $_.Exception.Message
    Write-Host ''
    Write-Host '  如果提示文件被占用，请彻底关闭游戏（含任务栏图标）后重试。' -ForegroundColor Yellow
    Wait-Key
    exit 1
}

Write-Host ''
Wait-Key '按回车关闭窗口'
