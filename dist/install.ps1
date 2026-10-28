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

$script:DetectedGameDir = $null
$script:DetectDone = $false

function Get-DetectedGameDir {
    # 首页状态和安装流程共用一次扫描结果，避免每次重扫全部盘符
    if (-not $script:DetectDone) {
        $script:DetectedGameDir = Find-GameDir
        $script:DetectDone = $true
    }
    return $script:DetectedGameDir
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

function Read-BuildFile([string]$pluginDir) {
    $p = Join-Path $pluginDir 'dict.build'
    $v = 0
    if (Test-Path $p) {
        try { [void][long]::TryParse((Get-Content $p -Raw).Trim(), [ref]$v) } catch {}
    }
    return $v
}

function Get-PayloadBuild {
    return Read-BuildFile (Join-Path $Payload 'BepInEx\plugins\SP2Chinese')
}

function Read-LoadedVersion([string]$pluginDir) {
    $p = Join-Path $pluginDir 'loaded-build.txt'
    if (-not (Test-Path $p)) { return '' }
    try {
        $line = (Get-Content $p -Raw).Trim()
        if (-not $line) { return '' }
        return ($line -split "`t")[0].Trim()
    } catch { return '' }
}

function Get-InstalledInfo([string]$dir) {
    $pluginDir = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
    $info = [ordered]@{
        HasBepInEx     = Test-Path (Join-Path $dir 'BepInEx\core\BepInEx.dll')
        HasPlugin      = Test-Path (Join-Path $pluginDir 'SP2Chinese.dll')
        HasDict        = Test-Path (Join-Path $pluginDir 'dictionary_zh_cn.tsv')
        PluginDir      = $pluginDir
        InstalledBuild = Read-BuildFile $pluginDir
        InstalledVer   = Read-LoadedVersion $pluginDir
    }
    return $info
}

function Show-Status {
    $statusDir = $null
    if ($GameDir -and (Test-GameDir $GameDir)) { $statusDir = $GameDir }
    elseif ($script:DetectedGameDir -and (Test-GameDir $script:DetectedGameDir)) { $statusDir = $script:DetectedGameDir }
    else { $statusDir = Get-DetectedGameDir }

    $dirText = '未自动找到（选择功能后再定位）'
    $installedText = '未安装汉化'
    if (Test-GameDir $statusDir) {
        $dirText = $statusDir
        $ii = Get-InstalledInfo $statusDir
        if ($ii.HasPlugin) {
            $verText = if ($ii.InstalledVer) { 'v' + $ii.InstalledVer } else { '已安装（版本未记录）' }
            $buildText = if ($ii.InstalledBuild -gt 0) { '词库 build ' + $ii.InstalledBuild } else { '词库 build 未知' }
            $installedText = $verText + '，' + $buildText
        }
    }

    Write-Host ('    >  安装包    v{0}  词库 build {1}' -f $script:PackageVersion, $script:PackageBuild) -ForegroundColor Gray
    Write-Host ('    >  游戏目录  {0}' -f $dirText) -ForegroundColor Gray
    Write-Host ('    >  已装版本  {0}' -f $installedText) -ForegroundColor Gray
    Write-Host ''
}

# ================================================================ 在线更新

# 14 个源：官方 raw（Watt Toolkit 加速生效时可用）、jsDelivr 四个节点、
# ghproxy 系与 moeyy/akams 等前缀加速站。任何一个能通即完成检查。
function Get-SystemProxy {
    # 读 Windows 的 IE/WinINET 代理设置。Watt Toolkit 切到「系统代理模式」后会写这里。
    # 形如 "127.0.0.1:8080" 或 "http=127.0.0.1:8080;https=127.0.0.1:8080"
    try {
        $reg = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        if ($reg.ProxyEnable -ne 1 -or -not $reg.ProxyServer) { return $null }
        $sv = [string]$reg.ProxyServer
        if ($sv -match 'https=([^;]+)') { return ('http://' + $Matches[1]) }
        if ($sv -match 'http=([^;]+)')  { return ('http://' + $Matches[1]) }
        if ($sv -match '^[\d\.]+:\d+$') { return ('http://' + $sv) }
    } catch {}
    return $null
}

function Get-SourceUrls([string]$relPath) {
    # 注意：数组里每个元素都必须用括号包住。
    # PowerShell 会把 @("a" + $b, "c") 中的 "a" + $b 拆成两个元素，
    # 结果是数组长度翻倍、一半是无效值，所有请求都会失败。
    $raw = "https://raw.githubusercontent.com/baiyangmuzz/SP2Chinese/main/" + $relPath
    $jd  = "gh/baiyangmuzz/SP2Chinese@main/" + $relPath
    return @(
        $raw,
        ("https://fastly.jsdelivr.net/" + $jd),
        ("https://cdn.jsdelivr.net/" + $jd),
        ("https://gcore.jsdelivr.net/" + $jd),
        ("https://testingcf.jsdelivr.net/" + $jd),
        ("https://gh-proxy.com/" + $raw),
        ("https://v6.gh-proxy.org/" + $raw),
        ("https://ghproxy.net/" + $raw),
        ("https://ghproxy.homeboyc.cn/" + $raw),
        ("https://gh.llkk.cc/" + $raw),
        ("https://hub.gitmirror.com/" + $raw),
        ("https://github.akams.cn/" + $raw),
        ("https://mirror.ghproxy.com/" + $raw),
        ("https://github.moeyy.xyz/" + $raw)
    )
}

# 文本下载：manifest 等小型 JSON
function Get-ManifestParallel {
    # 系统代理可用时直接用它取 manifest：代理是单通道，并发没有收益
    $proxy = Get-SystemProxy
    if ($proxy) {
        Show-Bar 30 ("经系统代理 " + $proxy)
        $txt = Get-TextViaCurl ((Get-SourceUrls 'manifest.json')[0])
        Hide-Bar
        if ($txt -and $txt.TrimEnd().EndsWith("}")) { return $txt }
    }

    # 同时向多个源发请求，谁先回就用谁。总耗时约等于最快那个源。
    # 并发上限 5：一次性拉起十几个 curl 会让连接互相拖慢，反而更慢更容易超时。
    $allUrls = Get-SourceUrls 'manifest.json'
    $tmp = Join-Path $env:TEMP ("sp2mf_" + $Stamp)
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    $found = $null
    $done = 0
    for ($base = 0; $base -lt $allUrls.Count -and -not $found; $base += 5) {
        $last = [Math]::Min($base + 4, $allUrls.Count - 1)
        $batch = @($allUrls[$base..$last])

        $procs = @()
        $files = @()
        for ($i = 0; $i -lt $batch.Count; $i++) {
            $f = Join-Path $tmp ("m" + ($base + $i) + ".json")
            $files += $f
            try {
                $procs += Start-Process -FilePath 'curl.exe' -PassThru -WindowStyle Hidden `
                    -ArgumentList @('-sk', '--connect-timeout', '2', '-m', '6', '-o', $f, $batch[$i])
            } catch {}
            $done++
            Show-Bar ([int](100 * $done / $allUrls.Count)) ("探测 " + $done + "/" + $allUrls.Count + "  " + ([uri]$batch[$i]).Host)
        }

        # 等这一批：进程退出且文件有效才算数
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 8) {
            for ($i = 0; $i -lt $files.Count; $i++) {
                $f = $files[$i]
                if (-not (Test-Path $f)) { continue }
                if ((Get-Item $f).Length -lt 20) { continue }
                $txt = $null
                try { $txt = [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8) } catch { continue }
                if ($txt -and $txt.TrimEnd().EndsWith("}")) { $found = $txt; break }
            }
            if ($found) { break }
            Start-Sleep -Milliseconds 60
        }

        # 收掉这一批还没结束的进程
        foreach ($pr in $procs) {
            try { if ($pr -and -not $pr.HasExited) { $pr.Kill() } } catch {}
        }
    }

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Hide-Bar
    return $found
}

function Get-TextViaCurl([string]$url) {
    # 必须先落盘再按 UTF-8 读：直接捕获 curl 的 stdout 会按控制台编码（GBK）
    # 解码，manifest 里的中文会被破坏，JSON 解析就会在中文处失败。
    $tmp = [IO.Path]::GetTempFileName()
    try {
        $proxy = Get-SystemProxy
        $argv = @('-sk', '-o', $tmp, $url)
        if ($proxy) { $argv = @('-sk', '--connect-timeout', '3', '-m', '10', '-x', $proxy, '-o', $tmp, $url) }
        else        { $argv = @('-sk', '--connect-timeout', '2', '-m', '6', '-o', $tmp, $url) }
        & curl.exe @argv 2>$null | Out-Null
        if ((Test-Path $tmp) -and (Get-Item $tmp).Length -gt 20) {
            return [IO.File]::ReadAllText($tmp, [Text.Encoding]::UTF8)
        }
        return $null
    }
    catch { return $null }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

function Save-FileViaCurl([string]$url, [string]$outFile) {
    # 和 Get-TextViaCurl 一样要读系统代理：Watt Toolkit 系统代理模式下，
    # 直连 GitHub 会握手失败，只有走代理才能下载词库。
    $proxy = Get-SystemProxy
    if ($proxy) {
        & curl.exe -sk --connect-timeout 3 -m 120 -x $proxy -o $outFile $url 2>$null
    }
    else {
        & curl.exe -sk --connect-timeout 3 -m 120 -o $outFile $url 2>$null
    }
    return ($LASTEXITCODE -eq 0 -and (Test-Path $outFile) -and (Get-Item $outFile).Length -gt 0)
}

function Invoke-UpdateCheck {
    # 依次尝试全部源；全部失败时静默降级到安装包内版本，绝不阻塞安装
    Write-Host '  正在检查更新……' -ForegroundColor Gray
    $mf = $null
    $overrides = @{}

    if ($TestUpdate) {
        # 诊断模式：逐个串行尝试并打印每个源的结果，便于定位是哪一段网络不通
        $i = 0
        foreach ($u in (Get-SourceUrls 'manifest.json')) {
            $i++
            $h = ([uri]$u).Host
            Write-Host ("  [{0}/14] {1} ……" -f $i, $h) -ForegroundColor DarkGray -NoNewline
            $txt = Get-TextViaCurl $u
            if ($txt) {
                try {
                    $mf = $txt | ConvertFrom-Json
                    Write-Host (" 成功" ) -ForegroundColor Green
                    break
                } catch {
                    $msg = $_.Exception.Message
                    Write-Host (" 解析失败: " + $msg.Substring(0, [Math]::Min(50, $msg.Length))) -ForegroundColor Yellow
                    Write-Host ("      内容开头: " + $txt.Substring(0, [Math]::Min(40, $txt.Length))) -ForegroundColor DarkGray
                }
            }
            else { Write-Host " 不通" -ForegroundColor DarkGray }
        }
    }
    else {
        # 常规安装：按每批 5 个依次探测全部源，避免一次拉起十几个连接
        $json = Get-ManifestParallel
        if ($json) {
            try { $mf = $json | ConvertFrom-Json } catch { $mf = $null }
        }
    }
    if (-not $mf) {
        Write-Host ''
        Write-Warn2 '连不上 GitHub，已跳过更新。'
        Write-Host '  本次使用安装包自带的词库，汉化照常可用。' -ForegroundColor Gray

        # hosts 劫持残留：加速器退出后没清理 hosts，所有 GitHub 域名仍然指向
        # 127.0.0.1，此时连本机一个已关闭的服务，比不加速还糟。
        $hijacked = $false
        try {
            $hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
            if (Test-Path $hostsPath) {
                $hit = Select-String -Path $hostsPath -Pattern '^\s*127\.0\.0\.1\s+.*(githubusercontent|github\.com|jsdelivr)' -ErrorAction SilentlyContinue
                $hijacked = ($null -ne $hit)
            }
        } catch {}
        if ($hijacked) {
            Write-Host '  检测到 hosts 里仍有 GitHub 域名被指向 127.0.0.1。' -ForegroundColor Yellow
            Write-Host '  这是加速器（Watt Toolkit / Steam++）退出后没清理干净：' -ForegroundColor DarkGray
            Write-Host '  重新打开加速器，或在它的设置里执行“恢复 hosts”。' -ForegroundColor DarkGray
        }
        else {
            Write-Host '  想要最新词库：开着 Watt Toolkit / Steam++ 的 GitHub 加速再选一次 [1]。' -ForegroundColor DarkGray
        }
        return $overrides
    }
    Write-Host ''

    # 插件本体更新：只提示，不自动替换 DLL
    $verFile = Join-Path $Payload 'VERSION.txt'
    $localVer = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '' }
    # 只有远程版本确实更高才提示升级；仅仅"版本不同"（例如线上还是旧版）
    # 不代表有新东西，误报会让用户以为自己的包过期了。
    $newer = $false
    if ($mf.modVersion -and $mf.modVersion -ne $localVer) {
        try { $newer = ([version]$mf.modVersion -gt [version]$localVer) } catch { $newer = $false }
    }
    if ($newer) {
        Write-Warn2 ("  GitHub 上有插件新版本 v{0}（安装包内 v{1}）。" -f $mf.modVersion, $localVer)
        Write-Host ("  建议先到 {0} 下载完整包；本次继续安装包内版本。" -f $mf.update.release) -ForegroundColor DarkGray
    }

    # 词库更新：下载并 SHA256 校验后替换
    $dictFile = Join-Path $Payload 'BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv'
    if (-not (Test-Path $dictFile)) { return $overrides }

    # 构建序号：安装包内的词库若已不旧于线上，就不去覆盖它。
    # 只比哈希会误判——本地修过的词库哈希必然和线上不同，一更新就被打回旧版。
    $localBuildFile = Join-Path $Payload 'BepInEx\plugins\SP2Chinese\dict.build'
    $localBuild = 0
    if (Test-Path $localBuildFile) {
        [void][long]::TryParse((Get-Content $localBuildFile -Raw).Trim(), [ref]$localBuild)
    }
    $remoteBuild = 0
    if ($mf.dictBuild) { [void][long]::TryParse([string]$mf.dictBuild, [ref]$remoteBuild) }
    if ($localBuild -gt 0 -and ($remoteBuild -eq 0 -or $remoteBuild -le $localBuild)) {
        Write-Ok ("词库已是较新版本（build {0}），跳过更新" -f $localBuild)
        return $overrides
    }

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
                    # 词库和构建号一起落盘。只换词库不写 dict.build 的话，
                    # 下次比较时本地 build 仍落后于自己的词库，版本保护会失效。
                    if ($remoteBuild -gt 0) {
                        Set-Content -Path $localBuildFile -Value ([string]$remoteBuild) -Encoding ASCII -NoNewline
                        $overrides['BepInEx/plugins/SP2Chinese/dict.build'] = (Get-FileHash $localBuildFile -Algorithm SHA256).Hash.ToLower()
                    }
                    $overrides['BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv'] = $got
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
    return $overrides
}

# ================================================================ 历史版本

function Get-History([string]$dir) {
    $root = Join-Path $dir 'BepInEx\_mod_backups'
    if (-not (Test-Path $root)) { return @() }
    return @(Get-ChildItem $root -Directory -Filter 'SP2Chinese-*' |
        Sort-Object LastWriteTime -Descending)
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
    $cur = Get-InstalledInfo $dir
    if ($cur.HasPlugin) {
        $curVer = if ($cur.InstalledVer) { 'v' + $cur.InstalledVer } else { '已安装' }
        $curBuild = if ($cur.InstalledBuild -gt 0) { [string]$cur.InstalledBuild } else { '未知' }
        Write-Host ("  当前：{0}  词库 build {1}" -f $curVer, $curBuild) -ForegroundColor White
    }
    else {
        Write-Host '  当前：未安装' -ForegroundColor White
    }
    Write-Host '  可回滚的历史版本（最近 3 个）：' -ForegroundColor White
    for ($i = 0; $i -lt $hist.Count; $i++) {
        $folder = $hist[$i].FullName
        $v = Read-LoadedVersion $folder
        $b = Read-BuildFile $folder
        $verText = if ($v) { 'v' + $v } else { 'v?' }
        $buildText = if ($b -gt 0) { [string]$b } else { '未知' }
        $tag = if ($hist[$i].Name -like '*uninstalled*') { '  [卸载备份]' } else { '' }
        Write-Host ("    [{0}] {1}  {2}  词库 build {3}{4}" -f `
            ($i + 1), $hist[$i].LastWriteTime.ToString('yyyy-MM-dd HH:mm'), $verText, $buildText, $tag)
    }
    Write-Host '    [0] 返回主菜单'
    $c = Read-Host '  回滚到哪一个'
    $n = 0
    if (-not [int]::TryParse($c, [ref]$n) -or $n -lt 1 -or $n -gt $hist.Count) {
        Write-Host '  已返回。' -ForegroundColor DarkGray
        return
    }
    # 回滚前先把当前版本挪进备份，别让回滚本身变成不可逆操作
    $pluginDir = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
    if (Test-Path $pluginDir) {
        $backupRoot = Join-Path $dir 'BepInEx\_mod_backups'
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        Move-Item $pluginDir (Join-Path $backupRoot ("SP2Chinese-pre-rollback-" + $Stamp))
    }
    $chosen = $hist[$n - 1]
    Copy-Item $chosen.FullName $pluginDir -Recurse -Force
    Prune-History $dir
    $chosenVer = Read-LoadedVersion $chosen.FullName
    $chosenText = if ($chosenVer) { 'v' + $chosenVer } else { '所选版本' }
    Write-Ok ("已回滚到 {0}（{1}）。重启游戏生效。" -f $chosen.Name, $chosenText)
}

# ================================================================ 安装

function Install-Payload([string]$dir) {
    $info = Get-InstalledInfo $dir
    $pkgBuild = Get-PayloadBuild
    $pluginDir = $info.PluginDir

    # --- 先备份旧插件，再写任何文件 ---
    # 顺序不能反：先复制新文件会把旧版覆盖掉，备份下来的就成了新版本，
    # 菜单 [3] 的回滚也就失去意义。
    # 备份必须放在 plugins 之外：BepInEx 会递归扫描 plugins 下所有目录，
    # 把 plugins\SP2Chinese.bak-xxx\SP2Chinese.dll 也当成插件加载一遍，
    # 同一个 GUID 被加载两次会引发 Harmony 补丁冲突。
    $backupPath = $null
    if (Test-Path $pluginDir) {
        $hasFiles = @(Get-ChildItem $pluginDir -Force -ErrorAction SilentlyContinue)
        if ($hasFiles.Count -gt 0) {
            $backupRoot = Join-Path $dir 'BepInEx\_mod_backups'
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
            $backupPath = Join-Path $backupRoot ("SP2Chinese-" + $Stamp)
            Move-Item $pluginDir $backupPath
            Write-Ok ("旧版已备份到 BepInEx\_mod_backups\" + (Split-Path $backupPath -Leaf))
        }
    }

    # 机器上已装的词库比包内新（比如游戏内热更新过）就保留，不覆盖。
    # 只比较包内和线上不够：安装包本身可能比机器上的版本旧。
    $keepDict = ($info.HasDict -and $backupPath -and $info.InstalledBuild -gt $pkgBuild)
    if ($keepDict) {
        Write-Ok ("已装词库 build {0} 比包内 build {1} 新，保留已装词库" -f $info.InstalledBuild, $pkgBuild)
    }

    # --- 组装复制清单（逐文件复制以便显示进度条）---
    # BepInEx 前置只在缺失时进清单；core/config 显式列出，绝不整目录拷贝。
    $copyList = @()
    $needsBep = -not $info.HasBepInEx
    if ($needsBep) {
        foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
            $src = Join-Path $Payload $f
            if (Test-Path $src) { $copyList += @{ src = $src; dst = (Join-Path $dir $f); rel = $f; kept = $false } }
        }
        $coreSrc = Join-Path $Payload 'BepInEx\core'
        Get-ChildItem $coreSrc -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($Payload.Length + 1)
            $copyList += @{ src = $_.FullName; dst = (Join-Path $dir $rel); rel = $rel.Replace('\', '/'); kept = $false }
        }
        $cfgSrc = Join-Path $Payload 'BepInEx\config'
        if ((Test-Path $cfgSrc) -and -not (Test-Path (Join-Path $dir 'BepInEx\config'))) {
            Get-ChildItem $cfgSrc -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($Payload.Length + 1)
                $copyList += @{ src = $_.FullName; dst = (Join-Path $dir $rel); rel = $rel.Replace('\', '/'); kept = $false }
            }
        }
    }

    # 插件三件套：DLL 用包内的；词库和 dict.build 在“已装更新”时从备份里捞回来。
    $plugSrc = Join-Path $Payload 'BepInEx\plugins\SP2Chinese'
    foreach ($f in @('SP2Chinese.dll', 'dictionary_zh_cn.tsv', 'dict.build')) {
        $rel = 'BepInEx/plugins/SP2Chinese/' + $f
        $kept = $false
        if ($keepDict -and $f -ne 'SP2Chinese.dll') {
            $src = Join-Path $backupPath $f
            $kept = $true
        }
        else {
            $src = Join-Path $plugSrc $f
        }
        if (Test-Path $src) {
            $copyList += @{ src = $src; dst = (Join-Path $pluginDir $f); rel = $rel; kept = $kept }
        }
        elseif ($f -eq 'dict.build') {
            Write-Warn2 '包内缺少 dict.build，游戏内词库版本保护会失效'
        }
        else {
            throw ('payload 缺少 ' + $f)
        }
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

    # 顺带清掉 BepInEx 的旧缓存，避免仍加载旧链
    $cache = Join-Path $dir 'BepInEx\cache'
    if (Test-Path $cache) {
        Get-ChildItem $cache -Filter '*.dat' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    Prune-History $dir

    $backupName = ''
    if ($backupPath) { $backupName = Split-Path $backupPath -Leaf }

    return [pscustomobject]@{
        PluginDir    = $pluginDir
        Copied       = $copyList
        KeptDict     = $keepDict
        PackageBuild = $pkgBuild
        BackupName   = $backupName
    }
}

function Test-Install([string]$pluginDir) {    $dll  = Join-Path $pluginDir 'SP2Chinese.dll'
    $dict = Join-Path $pluginDir 'dictionary_zh_cn.tsv'

    if (-not (Test-Path $dll))  { Write-Err 'SP2Chinese.dll 缺失';  return $false }
    if (-not (Test-Path $dict)) { Write-Err 'dictionary_zh_cn.tsv 缺失'; return $false }

    $build = Join-Path $pluginDir 'dict.build'
    if (-not (Test-Path $build)) {
        Write-Warn2 'dict.build 缺失，游戏内词库版本保护会失效'
    }

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

function Verify-InstalledFiles([string]$dir, $copied, $overrides) {
    <#
      只校验本次安装真正写入的文件。
      旧版按 manifest 全量校验，会把“检测到已有 BepInEx 就没装”的 core/winhttp
      也当成失败，已有 BepInEx 的用户每次升级都会看到一堆红字。
    #>
    $copied = @($copied)
    if ($copied.Count -eq 0) {
        Write-Warn2 '没有可校验的文件'
        return $true
    }

    $mfPath = Join-Path $PackRoot 'manifest.json'
    $data = $null
    if (Test-Path $mfPath) {
        try { $data = Get-Content $mfPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $data = $null }
    }
    $mfBuild = 0
    if ($data -and $data.dictBuild) { [void][long]::TryParse([string]$data.dictBuild, [ref]$mfBuild) }
    $pkgBuild = Get-PayloadBuild
    # 包内词库被在线更新过时，包内 manifest 里的哈希已经过期，按文件自身哈希校验。
    $useManifestDict = ($mfBuild -le 0 -or $pkgBuild -le $mfBuild)

    $checked = 0
    $keptCount = 0
    $bad = New-Object System.Collections.Generic.List[string]

    foreach ($c in $copied) {
        $checked++
        if (-not (Test-Path $c.dst)) {
            $bad.Add($c.rel + ' (缺失)')
            continue
        }
        if ($c.kept) { $keptCount++; continue }

        $expect = $null
        if ($overrides -and $overrides.ContainsKey($c.rel)) {
            $expect = [string]$overrides[$c.rel]
        }
        elseif ($data -and ($useManifestDict -or ($c.rel -notmatch 'dictionary_zh_cn\.tsv$|dict\.build$'))) {
            $prop = $data.files.PSObject.Properties[$c.rel]
            if ($prop) { $expect = [string]$prop.Value.sha256 }
        }
        if (-not $expect) {
            $expect = (Get-FileHash -Path $c.src -Algorithm SHA256).Hash.ToLower()
        }

        Show-Bar ([int](100 * $checked / $copied.Count)) ("SHA256 " + ($c.rel.Split('/')[-1]))
        $actual = (Get-FileHash -Path $c.dst -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $expect.ToLower()) {
            $bad.Add($c.rel + ' (内容不符)')
        }
    }
    Hide-Bar

    if ($keptCount -gt 0) {
        Write-Ok ("保留了 {0} 个本地较新文件（未按包内哈希校验）" -f $keptCount)
    }

    if ($bad.Count -eq 0) {
        Write-Ok ("哈希校验通过（{0} 个文件）" -f $checked)
        # 打印实际装上的词库，不用包内 manifest 的旧值：在线更新过词库时那份已经过期。
        $installedDict = Join-Path $dir 'BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv'
        if (Test-Path $installedDict) {
            $rows = (Get-Content $installedDict -Encoding UTF8 | Measure-Object -Line).Lines
            $dictShort = (Get-FileHash $installedDict -Algorithm SHA256).Hash.ToLower().Substring(0, 12)
            Write-Host ("         词典版本 {0}，{1} 条" -f $dictShort, $rows) -ForegroundColor DarkGray
        }
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
    $backupRoot = Join-Path $dir 'BepInEx\_mod_backups'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $moved = Join-Path $backupRoot ("SP2Chinese-uninstalled-" + $Stamp)
    Move-Item $pluginDir $moved
    Write-Ok ("汉化已卸载（已移到 BepInEx\_mod_backups\{0}，没有删除）" -f (Split-Path $moved -Leaf))
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

$script:PackageVersion = ''
$verFile = Join-Path $Payload 'VERSION.txt'
if (Test-Path $verFile) { $script:PackageVersion = (Get-Content $verFile -Raw).Trim() }
$script:PackageBuild = Get-PayloadBuild

# 非交互模式只打印一次状态；交互模式在每次菜单刷新时打印，装完/卸载后能看到最新状态。
if ($Silent -or $Yes -or $Uninstall -or $TestUpdate) { Show-Status }

# -TestUpdate：只跑更新检查（网络诊断），不安装任何文件
if ($TestUpdate) {
    [void](Invoke-UpdateCheck)
    $d = Join-Path $Payload 'BepInEx\plugins\SP2Chinese\dictionary_zh_cn.tsv'
    Write-Host ''
    Write-Host ("  字典行数  {0}" -f (Get-Content $d -Encoding UTF8 | Measure-Object -Line).Lines)
    Write-Host ("  字典型号  {0}" -f (Get-FileHash $d -Algorithm SHA256).Hash.ToLower().Substring(0, 12))
    exit 0
}

function Ensure-GameDir {
    # 定位游戏目录，整个会话只定位一次
    if ($script:GameDir -and (Test-GameDir $script:GameDir)) { return $script:GameDir }
    $d = Get-DetectedGameDir
    if (-not (Test-GameDir $d)) { $d = Ask-GameDir }
    if (-not $d) {
        Write-Err '无法确定游戏目录。'
        Wait-Key
        exit 1
    }
    $script:GameDir = $d
    $script:DetectedGameDir = $d
    Write-Host ''
    Write-Host ("  游戏目录  {0}" -f $d) -ForegroundColor White
    return $d
}

function Stop-RunningGame([string]$dir) {
    $proc = Get-GameProcess $dir
    if ($proc.Count -eq 0) { return $true }
    if ($Silent -or $Yes) {
        Write-Err '游戏正在运行，请先完全退出游戏再重试。'
        return $false
    }
    Write-Host ''
    Write-Warn2 '游戏正在运行，插件文件会被占用。'
    Write-Host '  请先在游戏里保存进度，然后正常退出游戏。' -ForegroundColor Gray
    Write-Host '  退出后会自动继续，最多等 30 秒。' -ForegroundColor DarkGray

    for ($waited = 1; $waited -le 30; $waited++) {
        Start-Sleep -Seconds 1
        $proc = Get-GameProcess $dir
        if ($proc.Count -eq 0) {
            Write-Ok '游戏已退出，继续安装。'
            return $true
        }
        if ($waited % 5 -eq 0) {
            Write-Host ("  等待游戏退出…… {0}/30 秒" -f $waited) -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    Write-Warn2 '游戏还在运行。强制关闭会丢失未保存的进度。'
    $answer = Read-Host '  强制关闭并继续？(y/N)'
    if ($answer -match '^[Yy]') {
        Write-Host '  正在强制关闭游戏……' -ForegroundColor Gray
        $proc | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
        return $true
    }
    return $false
}

function Show-InstallSummary([string]$dir, $result, [bool]$hashOk) {
    $dict = Join-Path $result.PluginDir 'dictionary_zh_cn.tsv'
    $rows = 0
    if (Test-Path $dict) { $rows = (Get-Content $dict -Encoding UTF8 | Measure-Object -Line).Lines }
    $build = Read-BuildFile $result.PluginDir
    $verText = if ($script:PackageVersion) { 'v' + $script:PackageVersion } else { '未知' }
    $buildText = if ($build -gt 0) { [string]$build } else { '未知' }
    $dictNote = if ($result.KeptDict) { '（保留机器上较新的版本）' } else { '' }
    $checkColor = if ($hashOk) { 'Gray' } else { 'Yellow' }

    Write-Host ''
    Write-Host ('    安装包    {0}' -f $verText) -ForegroundColor Gray
    Write-Host ('    词库      {0} 条{1}' -f $rows, $dictNote) -ForegroundColor Gray
    Write-Host ('    词库构建  {0}' -f $buildText) -ForegroundColor Gray
    Write-Host ('    游戏目录  {0}' -f $dir) -ForegroundColor Gray
    if ($result.BackupName) { Write-Host ('    旧版备份  {0}' -f $result.BackupName) -ForegroundColor Gray }
    Write-Host ('    校验      {0}' -f $(if ($hashOk) { '通过' } else { '有文件不一致' })) -ForegroundColor $checkColor
}

function Invoke-InstallFlow([string]$dir, $overrides) {
    try {
        $result = Install-Payload $dir
        if (-not (Test-Install $result.PluginDir)) {
            Write-Err '安装校验未通过，请把上面的输出发给群主。'
            return
        }
        $hashOk = Verify-InstalledFiles $dir $result.Copied $overrides
        Write-Host ''
        if ($hashOk) { Write-Head '安装完成' }
        else         { Write-Head '安装完成（但有文件校验不一致）' }
        Show-InstallSummary $dir $result $hashOk
        Write-Host ''
        Write-Host '  启动游戏即可，界面应为中文。' -ForegroundColor Green
        if (-not $hashOk) {
            Write-Host '  如果进游戏仍是英文，重新解压安装包再装一次。' -ForegroundColor Yellow
        }
        Write-Host ''
        Write-Host '  首次进游戏如果仍是英文：' -ForegroundColor Gray
        Write-Host '   1. 确认是从 Steam 正常启动（不要用别的快捷方式）' -ForegroundColor DarkGray
        Write-Host '   2. 完全退出游戏后重新打开' -ForegroundColor DarkGray
        Write-Host ('   3. 检查 BepInEx\LogOutput.log 里有没有 "SP2 chinese"') -ForegroundColor DarkGray
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
        Show-Status
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
            $overrides = Invoke-UpdateCheck
            if (-not $overrides) { $overrides = @{} }
            Invoke-InstallFlow $d $overrides
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
if (-not (Stop-RunningGame $GameDir)) { exit 1 }

try {
    if ($Uninstall) {
        Uninstall-Plugin $GameDir
    }
    else {
        $result = Install-Payload $GameDir
        if (-not (Test-Install $result.PluginDir)) {
            Write-Err '安装校验未通过，请把上面的输出发给群主。'
            Wait-Key
            exit 1
        }
        $hashOk = Verify-InstalledFiles $GameDir $result.Copied @{}
        Write-Host ''
        if ($hashOk) { Write-Head '安装完成' }
        else         { Write-Head '安装完成（但有文件校验不一致）' }
        Show-InstallSummary $GameDir $result $hashOk
        Write-Host ''
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
