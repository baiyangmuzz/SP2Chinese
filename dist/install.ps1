<#
================================================================
 SimplePlanes 2 简体中文汉化 · 安装器

 作者    白杨木 (baiyangmuzz)
 QQ 群   1128992783
 项目    https://github.com/baiyangmuzz/SP2Chinese

 功能：
   [1] 检查更新并安装  —— 14 个镜像源竞速拉取 manifest 与词库
   [2] 卸载汉化        —— 清理插件文件，BepInEx 本体不受影响
   [3] 回滚到历史版本  —— 自动保留最近 3 次安装记录

 说明：
   - 网络请求一律走系统自带 curl.exe（PowerShell 的 .NET TLS 栈
     与 hosts 劫持型加速器反代握手会失败，curl 无此问题）
   - 所有下载均以 manifest 记录的 SHA256 校验完整性
   - 游戏目录自动定位：注册表 → 全部 Steam 库 → 弹窗 → 手输
================================================================
#>

param(
    [string]$GameDir,
    [switch]$Yes,
    [switch]$Silent,
    [switch]$Uninstall,
    [switch]$TestUpdate
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackRoot = $ScriptDir
$Payload = Join-Path $PackRoot 'payload'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# ================================================================ UI 输出

function Wait-Key([string]$msg = '按回车关闭窗口') {
    Write-Host ''
    Read-Host ("  " + $msg)
}

function Write-Head($text) {
    $line = '  ' + ([string][char]0x2550) * 54
    Write-Host ''
    Write-Host $line -ForegroundColor DarkCyan
    Write-Host ('    >>  ' + $text) -ForegroundColor Cyan
    Write-Host $line -ForegroundColor DarkCyan
}

function Write-Ok($text)    { Write-Host ('  +  ' + $text) -ForegroundColor Green }
function Write-Warn2($text) { Write-Host ('  !  ' + $text) -ForegroundColor Yellow }
function Write-Err($text)   { Write-Host ('  x  ' + $text) -ForegroundColor Red }

# 单行原地刷新进度条：20 格，每 5% 刷新一次，文件间 25ms 间隔使推进平滑
$script:LastBarPct = -1
function Show-Bar([int]$pct, [string]$text) {
    if ($script:Silent) { return }
    if ($pct -gt 100) { $pct = 100 }
    $step = [int]($pct / 5) * 5
    if ($step -eq $script:LastBarPct) { return }
    $script:LastBarPct = $step
    $w = 20
    $filled = [int][math]::Round($w * $pct / 100)
    $bar = ([string][char]0x2588) * $filled + ([string][char]0x2591) * ($w - $filled)
    $label = $text
    if ($label.Length -gt 40) { $label = $label.Substring(0, 40) }
    $label = $label.PadRight(42)
    Write-Host ("`r  {0} {1,3}%  {2}  " -f $bar, $pct, $label) -NoNewline -ForegroundColor Cyan
}
function Hide-Bar {
    if ($script:Silent) { return }
    $script:LastBarPct = -1
    Write-Host ""
}

# ================================================================ 游戏定位

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

# ================================================================ 状态检查

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

# ================================================================ 在线更新

# 14 个源：官方 raw（Watt Toolkit 加速生效时可用）、jsDelivr 四个节点、
# ghproxy 系与 moeyy/akams 等前缀加速站。任何一个能通即完成检查。
function Get-SourceUrls([string]$relPath) {
    $raw = "https://raw.githubusercontent.com/baiyangmuzz/SP2Chinese/main/" + $relPath
    $jd  = "gh/baiyangmuzz/SP2Chinese@main/" + $relPath
    return @(
        $raw,
        "https://fastly.jsdelivr.net/" + $jd,
        "https://cdn.jsdelivr.net/" + $jd,
        "https://gcore.jsdelivr.net/" + $jd,
        "https://testingcf.jsdelivr.net/" + $jd,
        "https://gh-proxy.com/" + $raw,
        "https://v6.gh-proxy.org/" + $raw,
        "https://ghproxy.net/" + $raw,
        "https://ghproxy.homeboyc.cn/" + $raw,
        "https://gh.llkk.cc/" + $raw,
        "https://hub.gitmirror.com/" + $raw,
        "https://github.akams.cn/" + $raw,
        "https://mirror.ghproxy.com/" + $raw,
        "https://github.moeyy.xyz/" + $raw
    )
}

# 文本下载：manifest 等小型 JSON
function Get-TextViaCurl([string]$url) {
    # --connect-timeout 3：连不上的源 3 秒内放弃，不让单个节点卡死流程
    return ((& curl.exe -sk --connect-timeout 3 -m 20 $url 2>$null) -join "`n")
}

# 文件下载：字典等较大文件（写盘 + 存在且非空才认成功）
function Save-FileViaCurl([string]$url, [string]$outFile) {
    & curl.exe -sk --connect-timeout 3 -m 120 -o $outFile $url 2>$null
    return ($LASTEXITCODE -eq 0 -and (Test-Path $outFile) -and (Get-Item $outFile).Length -gt 0)
}

function Invoke-UpdateCheck {
    # 依次尝试全部源；全部失败时静默降级到安装包内版本，绝不阻塞安装
    $urls = Get-SourceUrls 'manifest.json'
    $mf = $null
    $i = 0
    foreach ($u in $urls) {
        $i++
        Show-Bar ([int](100 * $i / $urls.Count)) ("[{0}/{1}] {2}" -f $i, $urls.Count, ([uri]$u).Host)
        $json = Get-TextViaCurl $u
        if ($json) {
            try { $mf = $json | ConvertFrom-Json; break } catch { continue }
        }
    }
    Hide-Bar
    if (-not $mf) {
        Write-Host ''
        Write-Warn2 '检查更新失败（所有镜像源均不可达）。'
        Write-Host '  如果你用 Watt Toolkit / Steam++：开着 GitHub 加速再选一次 [1]，' -ForegroundColor DarkGray
        Write-Host '  它对 curl 直连生效，无需其他设置。' -ForegroundColor DarkGray
        Write-Host '  或到 QQ 群 / Releases 下载最新完整包。本次使用安装包内版本。' -ForegroundColor DarkGray
        return
    }
    Write-Host ''

    # 插件本体更新：只提示，不自动替换 DLL
    $verFile = Join-Path $Payload 'VERSION.txt'
    $localVer = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '' }
    if ($mf.modVersion -and $mf.modVersion -ne $localVer) {
        Write-Warn2 ("  GitHub 上有插件新版本 v{0}（安装包内 v{1}）。" -f $mf.modVersion, $localVer)
        Write-Host ("  建议先到 {0} 下载完整包；本次继续安装包内版本。" -f $mf.update.release) -ForegroundColor DarkGray
    }

    # 词库更新：下载并 SHA256 校验后替换
    $dictFile = Join-Path $Payload 'BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv'
    if (-not (Test-Path $dictFile)) { return }
    $sha = (Get-FileHash $dictFile -Algorithm SHA256).Hash.ToLower()
    if ($mf.dictVersion -and -not ($sha.StartsWith($mf.dictVersion))) {
        Write-Host '  发现新词库，正在下载……' -ForegroundColor Gray
        $bak = $dictFile + '.bak-inpkg'
        Copy-Item $dictFile $bak -Force
        $okDl = $false
        $i = 0
        $dictUrls = Get-SourceUrls 'dictionary_zh_cn.tsv'
        foreach ($u in $dictUrls) {
            $i++
            Show-Bar ([int](100 * $i / $dictUrls.Count)) ("[{0}/{1}] {2}" -f $i, $dictUrls.Count, ([uri]$u).Host)
            if (Save-FileViaCurl $u $dictFile) {
                $got = (Get-FileHash $dictFile -Algorithm SHA256).Hash.ToLower()
                if (-not $mf.dictSha256 -or $got -eq $mf.dictSha256.ToLower()) {
                    $okDl = $true
                    Write-Ok ("  词库已更新到 {0}（{1} 条）" -f $mf.dictVersion, $mf.dictRows)
                    break
                }
            }
        }
        Hide-Bar
        if (-not $okDl) {
            Move-Item $bak $dictFile -Force
            Write-Host '  词库下载或校验失败，使用安装包内版本。' -ForegroundColor DarkGray
        }
        else { Remove-Item $bak -Force -ErrorAction SilentlyContinue }
    }
    else {
        Write-Ok '词库已是最新'
    }
}

# ================================================================ 历史版本

function Get-History([string]$dir) {
    $root = Join-Path $dir 'BepInEx\_mod_backups'
    if (-not (Test-Path $root)) { return @() }
    return @(Get-ChildItem $root -Directory -Filter 'SP2Chinese-*' |
        Sort-Object Name -Descending)
}

function Prune-History([string]$dir) {
    # 只保留最近 3 个历史版本，更早的自动清理
    $old = Get-History $dir | Select-Object -Skip 3
    if ($old) { $old | Remove-Item -Recurse -Force }
}

function Show-Rollback([string]$dir) {
    $hist = Get-History $dir | Select-Object -First 3
    if (-not $hist -or $hist.Count -eq 0) {
        Write-Warn2 '没有可回滚的历史版本（每次安装都会自动记录一个）。'
        return
    }
    Write-Host ''
    Write-Host '  可回滚的历史版本（最近 3 个）：' -ForegroundColor White
    for ($i = 0; $i -lt $hist.Count; $i++) {
        $dll = Join-Path $hist[$i].FullName 'SP2Chinese.dll'
        $size = if (Test-Path $dll) { "{0} KB" -f [math]::Round((Get-Item $dll).Length / 1KB, 1) } else { '?' }
        Write-Host ("    [{0}] {1}   {2}" -f ($i + 1), $hist[$i].Name, $size)
    }
    Write-Host '    [0] 返回主菜单'
    $c = Read-Host '  回滚到哪一个'
    $n = 0
    if (-not [int]::TryParse($c, [ref]$n) -or $n -lt 1 -or $n -gt $hist.Count) {
        Write-Host '  已返回。' -ForegroundColor DarkGray
        return
    }
    # 回滚部署：直接用目标版本覆盖当前，不再额外记录历史
    $pluginDir = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
    if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force }
    Copy-Item $hist[$n - 1].FullName $pluginDir -Recurse -Force
    Prune-History $dir
    Write-Ok ("已回滚到 {0}。重启游戏生效。" -f $hist[$n - 1].Name)
}

# ================================================================ 安装

function Install-Payload([string]$dir) {
    $info = Get-InstalledInfo $dir

    # --- 组装复制清单（逐文件复制以便显示进度条）---
    # BepInEx 前置只在缺失时进清单；core/config 显式列出，绝不整目录拷贝：
    # payload\BepInEx 还带着 plugins\SP2Chinese，整目录拷会让旧版立刻出现、
    # 紧接着被备份步骤挪走，顺序上非常混乱。
    $copyList = @()
    $needsBep = -not $info.HasBepInEx
    if ($needsBep) {
        foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
            $src = Join-Path $Payload $f
            if (Test-Path $src) { $copyList += @{ src = $src; dst = $dir; rel = $f } }
        }
        $coreSrc = Join-Path $Payload 'BepInEx\core'
        Get-ChildItem $coreSrc -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($Payload.Length + 1)
            $copyList += @{ src = $_.FullName; dst = Join-Path $dir $rel; rel = $rel }
        }
        $cfgSrc = Join-Path $Payload 'BepInEx\config'
        if ((Test-Path $cfgSrc) -and -not (Test-Path (Join-Path $dir 'BepInEx\config'))) {
            Get-ChildItem $cfgSrc -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($Payload.Length + 1)
                $copyList += @{ src = $_.FullName; dst = Join-Path $dir $rel; rel = $rel }
            }
        }
    }
    $plugSrc = Join-Path $Payload 'BepInEx\plugins\SP2Chinese'
    Get-ChildItem $plugSrc -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Payload.Length + 1)
        $copyList += @{ src = $_.FullName; dst = Join-Path $dir $rel; rel = $rel }
    }

    # --- 逐文件复制并刷新进度条 ---
    $total = $copyList.Count
    $i = 0
    foreach ($c in $copyList) {
        $i++
        Show-Bar ([int](100 * $i / $total)) $c.rel
        $dstDir = Split-Path $c.dst -Parent
        if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
        Copy-Item $c.src $c.dst -Force
        Start-Sleep -Milliseconds 25
    }
    Hide-Bar

    if ($needsBep) { Write-Ok 'BepInEx 运行环境已就位' }
    else           { Write-Ok '检测到已安装 BepInEx，沿用现有版本（不改动 core）' }

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
        Write-Ok "已记录当前版本，可在菜单 [3] 回滚"
        Prune-History $dir
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

# ================================================================ 哈希校验

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

    $fileProps = @($data.files.PSObject.Properties)
    foreach ($prop in $fileProps) {
        $rel = $prop.Name.Replace('/', '\')
        $target = Join-Path $dir $rel
        $checked++
        if (-not (Test-Path $target)) {
            $bad.Add("$rel (缺失)")
            continue
        }
        Show-Bar ([int](100 * $checked / $fileProps.Count)) ("SHA256 " + ($prop.Name.Split('/')[-1]))
        $actual = (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLower()
        $expect = [string]$prop.Value.sha256
        if ($actual -ne $expect.ToLower()) {
            $bad.Add("$rel (内容不符)")
        }
    }
    Hide-Bar

    if ($bad.Count -eq 0) {
        Write-Ok ("哈希校验通过（{0} 个文件）" -f $checked)
        if ($data.dictRows) { Write-Host ("         词典版本 {0}，{1} 条" -f $data.dictVersion, $data.dictRows) -ForegroundColor DarkGray }
        return $true
    }

    Write-Err ("{0} 个文件校验失败：" -f $bad.Count)
    foreach ($b in $bad) { Write-Host ("         " + $b) -ForegroundColor DarkGray }
    return $false
}

# ================================================================ 卸载

function Uninstall-Plugin([string]$dir) {
    $pluginDir = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
    if (-not (Test-Path $pluginDir)) {
        Write-Warn2 '没有找到已安装的汉化插件，无需卸载。'
        return
    }
    Remove-Item $pluginDir -Recurse -Force
    Write-Ok '汉化已卸载，相关文件已清理。'
    Write-Host '  BepInEx 本体保留，其他模组不受影响。' -ForegroundColor DarkGray
}

# ================================================================ 主流程

Write-Head 'SimplePlanes 2 简体中文汉化'

# 项目信息：优先读包内 manifest，读不到用默认
$mfAuthor = '白杨木'
$mfGroup  = '1128992783'
$mfRepo   = 'github.com/baiyangmuzz/SP2Chinese'
$mfPath0  = Join-Path $PackRoot 'manifest.json'
if (Test-Path $mfPath0) {
    try {
        $mf0 = Get-Content $mfPath0 -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($mf0.author)     { $mfAuthor = $mf0.author }
        if ($mf0.qqGroup)    { $mfGroup  = $mf0.qqGroup }
        if ($mf0.repository) { $mfRepo   = ($mf0.repository -replace '^https://', '') }
    } catch {}
}
Write-Host ''
Write-Host ('    >  作者      {0}' -f $mfAuthor) -ForegroundColor Gray
Write-Host ('    >  QQ 群     {0}' -f $mfGroup) -ForegroundColor Gray
Write-Host ('    >  项目      {0}' -f $mfRepo) -ForegroundColor Gray

if (-not (Test-Path $Payload)) {
    Write-Err '安装包不完整：缺少 payload 目录。请重新解压整个压缩包。'
    Wait-Key
    exit 1
}

# -TestUpdate：只跑更新检查（网络诊断），不安装任何文件
if ($TestUpdate) {
    Invoke-UpdateCheck
    $d = Join-Path $Payload 'BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv'
    Write-Host ''
    Write-Host ("  字典行数  {0}" -f (Get-Content $d -Encoding UTF8 | Measure-Object -Line).Lines)
    Write-Host ("  字典型号  {0}" -f (Get-FileHash $d -Algorithm SHA256).Hash.ToLower().Substring(0, 12))
    exit 0
}

function Ensure-GameDir {
    # 定位游戏目录，整个会话只定位一次
    if ($script:GameDir) { return $script:GameDir }
    $d = Find-GameDir
    if (-not (Test-GameDir $d)) { $d = Ask-GameDir }
    if (-not $d) {
        Write-Err '无法确定游戏目录。'
        Wait-Key
        exit 1
    }
    $script:GameDir = $d
    Write-Host ''
    Write-Host ("  游戏目录  {0}" -f $d) -ForegroundColor White
    return $d
}

function Stop-RunningGame([string]$dir) {
    $proc = Get-GameProcess $dir
    if ($proc.Count -eq 0) { return $true }
    Write-Host ''
    Write-Warn2 '游戏正在运行，插件文件会被占用。'
    $answer = Read-Host '  关闭游戏后继续？(Y/n)'
    if ($answer -match '^[Nn]') { return $false }
    Write-Host '  正在关闭游戏……' -ForegroundColor Gray
    $proc | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    return $true
}

function Invoke-InstallFlow([string]$dir) {
    try {
        $pluginDir = Install-Payload $dir
        if (-not (Test-Install $pluginDir)) {
            Write-Err '安装校验未通过，请把上面的输出发给群主。'
            return
        }
        $hashOk = Verify-WithManifest $dir
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
        Write-Host ('   3. 检查 BepInEx\LogOutput.log 里有没有 "SP2 chinese loaded"') -ForegroundColor DarkGray
    }
    catch {
        Write-Err $_.Exception.Message
        Write-Host ''
        Write-Host '  如果提示文件被占用，请彻底关闭游戏（含任务栏图标）后重试。' -ForegroundColor Yellow
    }
}

if (-not $Silent -and -not $Yes -and -not $Uninstall) {
    # ---------------- 交互主循环：数字 + 回车选择，0 返回上级 ----------------
    while ($true) {
        $tri = '>'
        $sep = '  ' + ([string][char]0x2500) * 54
        Write-Host ''
        Write-Host $sep -ForegroundColor DarkGray
        Write-Host ''
        Write-Host ('    [1] ' + $tri + '  检查更新并安装') -ForegroundColor White
        Write-Host ('    [2] ' + $tri + '  卸载汉化') -ForegroundColor White
        Write-Host ('    [3] ' + $tri + '  回滚到历史版本') -ForegroundColor White
        Write-Host ('    [0] ' + $tri + '  退出') -ForegroundColor DarkGray
        Write-Host ''
        Write-Host $sep -ForegroundColor DarkGray
        $c = Read-Host ('    ' + $tri + ' 请选择 (0-3)')

        if ($null -eq $c -or $c -eq '' -or $c -eq '0') { break }  # 0 或输入耗尽(EOF)退出

        if ($c -eq '1') {
            $d = Ensure-GameDir
            if (-not (Stop-RunningGame $d)) { continue }
            Invoke-UpdateCheck
            Invoke-InstallFlow $d
            Wait-Key "`n  按回车返回菜单"
        }
        elseif ($c -eq '2') {
            $d = Ensure-GameDir
            if (-not (Stop-RunningGame $d)) { continue }
            Uninstall-Plugin $d
            Wait-Key "`n  按回车返回菜单"
        }
        elseif ($c -eq '3') {
            $d = Ensure-GameDir
            if (-not (Stop-RunningGame $d)) { continue }
            Show-Rollback $d
            Wait-Key "`n  按回车返回菜单"
        }
    }
    exit 0
}

# ---------------- 单次执行（-Silent / -Uninstall / -Yes）----------------

$GameDir = Ensure-GameDir
Stop-RunningGame $GameDir | Out-Null

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
        Write-Host ''
    }
}
catch {
    Write-Err $_.Exception.Message
    Wait-Key
    exit 1
}

if (-not $Silent) { Wait-Key '按回车关闭窗口' }
