<#
================================================================
 SimplePlanes 2 简体中文汉化 · 安装器

 作者    白杨木 (baiyangmuzz)
 QQ 群   1128992783
 项目    https://github.com/baiyangmuzz/SP2Chinese

 功能：
   [1] 检查更新并安装  —— 官方 raw / jsDelivr 多源校验拉取 manifest 与词库
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
    [switch]$TestUpdate,
    [switch]$NoGui
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackRoot = $ScriptDir
$Payload = Join-Path $PackRoot 'payload'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# 图形界面：WinForms 窗体（仅依赖系统自带 PowerShell 5.1 + .NET Framework）。
# 无人值守开关（-Silent / -Yes / -Uninstall / -TestUpdate）一律走纯控制台路径，
# 保证 CI、脚本调用与老用户的行为完全不变。-NoGui 可强制回退到旧版数字菜单。
$UseGui = (-not $Silent -and -not $Yes -and -not $Uninstall -and
           -not $TestUpdate -and -not $NoGui -and
           [Environment]::UserInteractive)
if ($UseGui -and -not (Test-Path (Join-Path $ScriptDir 'Gui.ps1'))) { $UseGui = $false }

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
    if ($script:Silent -or $script:GuiMode) { return }
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
    if ($script:Silent -or $script:GuiMode) { return }
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

# 只信任项目自己的分发域名：GitHub raw 与 jsDelivr 官方节点。
# 第三方 gh 前缀代理站（gh-proxy.com / gitmirror / moeyy / akams / llkk 等）已从清单移除：
# manifest 没有签名，从不受控主机取「期望哈希」等于自证，任一被投毒即可下发任意词库。
# 网络不通时静默降级到随包版本，这比信任来路不明的镜像安全。
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
        ("https://testingcf.jsdelivr.net/" + $jd)
    )
}

# 统一 curl 调用：始终校验 TLS 证书（-s，绝不使用 -sk）。
# 历史实现会在「检测到加速器痕迹」时降级为不校验证书重试；但 manifest 未签名，
# 一旦放弃证书校验，hosts 劫持 / 恶意代理就能下发任意 dictSha256 与下载地址。
# 下载失败只降级到随包版本，绝不降级 TLS 信任。
function Invoke-Curl([string]$url, [string]$outFile, [int]$connectTimeout = 3, [int]$maxTime = 20, [string]$proxy = $null) {
    $base = @('-s', '--connect-timeout', [string]$connectTimeout, '-m', [string]$maxTime)
    if ($proxy) { $base += @('-x', $proxy) }
    $argv = $base + @('-o', $outFile, $url)
    & curl.exe @argv 2>$null
    return ($LASTEXITCODE -eq 0 -and (Test-Path $outFile) -and (Get-Item $outFile).Length -gt 0)
}

# 文本下载：manifest 等小型 JSON（顺序探测，复用统一的 curl 调用）
function Get-ManifestParallel {
    # 系统代理可用时先试第一个（官方 raw）源：代理是单通道，快速返回即可。
    $proxy = Get-SystemProxy
    if ($proxy) {
        $txt = Get-TextViaCurl ((Get-SourceUrls 'manifest.json')[0])
        if (Test-ManifestText $txt) { return $txt }
    }

    # 顺序向全部可信源请求（始终校验 TLS），要求至少两个源返回完全一致的文本才采纳。
    # 「谁先回用谁」会让单个被劫持的源直接决定期望哈希；多源一致显著提高投毒成本。
    # 5 个源 × 6s 上限，最坏 30s；命中两个一致即提前退出，正常网络下 1-2 个源就够。
    # 旧版用 Start-Process 拉起 5 个 curl + 轮询临时文件，代码复杂还会残留进程，
    # 这里改为顺序调用，去掉子进程管理与临时目录。
    $allUrls = Get-SourceUrls 'manifest.json'
    $counts = @{}
    $i = 0
    foreach ($u in $allUrls) {
        $i++
        Show-Bar ([int](100 * $i / $allUrls.Count)) ("探测 " + $i + "/" + $allUrls.Count + "  " + ([uri]$u).Host)
        $txt = Get-TextViaCurl $u
        if (-not (Test-ManifestText $txt)) { continue }
        $norm = $txt.Trim()
        if (-not $counts.ContainsKey($norm)) { $counts[$norm] = 0 }
        $counts[$norm]++
        if ($counts[$norm] -ge 2) { Hide-Bar; return $txt }
    }
    Hide-Bar

    if ($proxy -and $counts.Count -eq 1) {
        # 只有一个源可达时（常见于只走了系统代理），仍接受官方 raw 的结果：
        # 它经过 TLS 校验且 host 属于允许列表，风险可控。
        return @($counts.Keys)[0]
    }
    return $null
}

function Test-ManifestText([string]$txt) {
    if (-not $txt) { return $false }
    $t = $txt.Trim()
    if (-not $t.EndsWith("}") -or $t.Length -lt 40) { return $false }
    # 只做一个轻量结构检查：必须含项目自己的版本/哈希字段，挡住错误页与伪造 JSON。
    return ($t -match '"dictSha256"\s*:' -and $t -match '"modVersion"\s*:')
}

function Get-TextViaCurl([string]$url) {
    # 必须先落盘再按 UTF-8 读：直接捕获 curl 的 stdout 会按控制台编码（GBK）
    # 解码，manifest 里的中文会被破坏，JSON 解析就会在中文处失败。
    $tmp = [IO.Path]::GetTempFileName()
    try {
        $proxy = Get-SystemProxy
        if (-not (Invoke-Curl -url $url -outFile $tmp -connectTimeout 2 -maxTime 6 -proxy $proxy)) { return $null }
        if ((Test-Path $tmp) -and (Get-Item $tmp).Length -gt 20) {
            return [IO.File]::ReadAllText($tmp, [Text.Encoding]::UTF8)
        }
        return $null
    }
    catch { return $null }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

# 下载体的硬上限：词库当前约 795 KB，留足余量同时挡住超大文件 DoS。
$script:MaxDownloadBytes = 8 * 1024 * 1024

function Save-FileViaCurl([string]$url, [string]$outFile, [long]$expectedSize = 0) {
    # 和 Get-TextViaCurl 一样要读系统代理：Watt Toolkit 系统代理模式下，
    # 直连 GitHub 会握手失败，只有走代理才能下载词库。
    $proxy = Get-SystemProxy
    if (-not (Invoke-Curl -url $url -outFile $outFile -connectTimeout 3 -maxTime 120 -proxy $proxy)) {
        return $false
    }
    if (-not (Test-Path $outFile)) { return $false }
    $len = (Get-Item $outFile).Length
    if ($len -le 0) { return $false }
    if ($len -gt $script:MaxDownloadBytes) {
        Write-Warn2 ("下载体超过大小上限（{0} > {1}），已丢弃" -f $len, $script:MaxDownloadBytes)
        Remove-Item $outFile -Force -ErrorAction SilentlyContinue
        return $false
    }
    if ($expectedSize -gt 0 -and $len -ne $expectedSize) {
        Write-Warn2 ("下载体大小与 manifest 不符（{0} != {1}），已丢弃" -f $len, $expectedSize)
        Remove-Item $outFile -Force -ErrorAction SilentlyContinue
        return $false
    }
    return $true
}

function Invoke-UpdateCheck {
    # 依次尝试全部源；全部失败时静默降级到安装包内版本，绝不阻塞安装
    Write-Host '  正在检查更新……' -ForegroundColor Gray
    $mf = $null
    $overrides = @{}

    if ($TestUpdate) {
        # 诊断模式：逐个串行尝试并打印每个源的结果，便于定位是哪一段网络不通
        $urls = Get-SourceUrls 'manifest.json'
        $i = 0
        foreach ($u in $urls) {
            $i++
            $h = ([uri]$u).Host
            Write-Host ("  [{0}/{1}] {2} ……" -f $i, $urls.Count, $h) -ForegroundColor DarkGray -NoNewline
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

        # 期望哈希必须锚定随包分发的 manifest.json（不经网络、不可被中间人改写），
        # 且必须与线上 manifest 声明的 dictSha256 一致。线上只用来判断「有没有新版本」，
        # 绝不单独决定装什么内容；两者不一致时拒绝下载（fail-closed）。
        $expectedSha = ''
        $expectedSize = 0
        $packMf = Get-ManifestData
        if ($packMf) {
            $expectedSha = [string]$packMf.dictSha256
            $pe = $packMf.files.PSObject.Properties['BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv']
            if ($pe -and $pe.Value.size) {
                [void][long]::TryParse([string]$pe.Value.size, [ref]$expectedSize)
            }
        }
        if (-not $expectedSha) {
            Write-Warn2 '安装包缺少 manifest.json 哈希锚点，跳过在线词库更新（拒绝安装未校验文件）'
            Move-Item $bak $dictFile -Force
            return $overrides
        }
        $remoteSha = [string]$mf.dictSha256
        if ($remoteSha -and $remoteSha.ToLower() -ne $expectedSha.ToLower()) {
            Write-Warn2 '线上声明的词库哈希与安装包锚点不一致，跳过在线词库更新'
            Move-Item $bak $dictFile -Force
            return $overrides
        }

        foreach ($u in $dictUrls) {
            $i++
            Show-Bar ([int](100 * $i / $dictUrls.Count)) ("[{0}/{1}] {2}" -f $i, $dictUrls.Count, ([uri]$u).Host)
            if (Save-FileViaCurl $u $dictFile $expectedSize) {
                $got = (Get-FileHash $dictFile -Algorithm SHA256).Hash.ToLower()
                if ($got -eq $expectedSha.ToLower()) {
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

# 只有「安装备份」参与 3 份上限的清理。卸载备份与回滚前备份是用户数据安全网，
# 绝不能被后续安装静默删掉（README 承诺「不删除文件」）。
function Get-InstallBackups([string]$dir) {
    return @(Get-History $dir | Where-Object {
        $_.Name -notlike '*-uninstalled-*' -and $_.Name -notlike '*-pre-rollback-*'
    })
}

function Prune-History([string]$dir) {
    $old = Get-InstallBackups $dir | Select-Object -Skip 3
    if ($old) { $old | Remove-Item -Recurse -Force }
}

# 安装中途失败时把状态恢复到「旧插件还在、新文件不在」。$backupPath 是本次安装
# 开始时移走的旧插件目录；$written 是已经写入的目标路径列表。
function Restore-FailedInstall([string]$pluginDir, $backupPath, $written) {
    try {
        foreach ($p in @($written)) {
            if ($p -and (Test-Path $p)) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
        }
        if ($backupPath -and (Test-Path $backupPath)) {
            if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force -ErrorAction SilentlyContinue }
            Move-Item $backupPath $pluginDir -Force
            Write-Ok '已恢复安装前的版本'
        }
    }
    catch {
        Write-Err ('回滚失败：' + $_.Exception.Message +
                   '。请到 BepInEx\_mod_backups 手动取回最近一次备份。')
    }
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
    $n = 0
    if ($script:GuiMode -and (Get-Command Gui-SelectRollback -ErrorAction SilentlyContinue)) {
        # GUI 模式：用下拉窗体选择，避免 Read-Host 阻塞 UI 线程。
        $labels = @()
        for ($i = 0; $i -lt $hist.Count; $i++) {
            $folder = $hist[$i].FullName
            $v = Read-LoadedVersion $folder
            $b = Read-BuildFile $folder
            $verText = if ($v) { 'v' + $v } else { 'v?' }
            $buildText = if ($b -gt 0) { [string]$b } else { '未知' }
            $tag = if ($hist[$i].Name -like '*uninstalled*') { '  [卸载备份]' } else { '' }
            $labels += ("{0}  {1}  词库 build {2}{3}" -f `
                $hist[$i].LastWriteTime.ToString('yyyy-MM-dd HH:mm'), $verText, $buildText, $tag)
        }
        $n = Gui-SelectRollback $labels
    }
    else {
        $c = Read-Host '  回滚到哪一个'
        if (-not [int]::TryParse($c, [ref]$n)) { $n = 0 }
    }
    if ($n -lt 1 -or $n -gt $hist.Count) {
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

function Install-Payload([string]$dir, $overrides) {
    $info = Get-InstalledInfo $dir
    $pkgBuild = Get-PayloadBuild
    $pluginDir = $info.PluginDir
    if ($null -eq $overrides) { $overrides = @{} }

    # 先验证整个 payload 完整性，再动游戏目录里的任何东西。
    Assert-PayloadIntegrity $overrides

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
            $script:LastBackupPath = $backupPath
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
        # PowerShell 的 -Force 不覆盖 ReadOnly 属性，游戏目录常见只读文件需先清属性。
        if (Test-Path $c.dst) {
            try { Set-ItemProperty -Path $c.dst -Name IsReadOnly -Value $false -ErrorAction Stop } catch {}
        }
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
        BackupPath   = $backupPath
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

# 可信锚点：manifest.files 里的哈希（随包分发、不经网络），在线更新过的文件用
# $overrides 覆盖。只有 build_dist.py 明确排除、且只在目标缺 config 时才复制的
# BepInEx.cfg 允许「按源文件自身哈希」自证；其它任何文件缺少锚点都必须 fail-closed，
# 绝不能退化成「拿被复制的源文件自己算哈希给自己验」的自证式校验。
$script:ManifestData = $null
function Get-ManifestData {
    if ($null -ne $script:ManifestData) { return $script:ManifestData }
    $mfPath = Join-Path $PackRoot 'manifest.json'
    if (Test-Path $mfPath) {
        try { $script:ManifestData = Get-Content $mfPath -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { $script:ManifestData = $null }
    }
    return $script:ManifestData
}

function Get-ExpectedHash([string]$rel, $overrides) {
    if ($overrides -and $overrides.ContainsKey($rel)) { return [string]$overrides[$rel] }
    $data = Get-ManifestData
    if ($data) {
        $prop = $data.files.PSObject.Properties[$rel]
        if ($prop -and $prop.Value.sha256) { return [string]$prop.Value.sha256 }
    }
    if ($rel -like 'BepInEx/config/*') { return $null }   # 明确豁免：按源文件自身校验
    return '__MISSING__'                                   # fail-closed 标记
}

# 覆盖写入之前先验证整个 payload 自身完整。坏包（被篡改 / 下载不全 / 打包半途失败）
# 在移动旧插件之前就被拦下，游戏目录完全不动。manifest.files 是唯一可信锚点。
function Assert-PayloadIntegrity($overrides) {
    $data = Get-ManifestData
    if (-not $data -or -not $data.files) {
        throw ('安装包缺少 manifest.json（或缺少 files 清单），无法校验完整性，已中止安装：' + $PackRoot)
    }
    $bad = New-Object System.Collections.Generic.List[string]
    $checked = 0
    foreach ($prop in $data.files.PSObject.Properties) {
        $rel = $prop.Name
        $p = Join-Path $Payload ($rel.Replace('/', '\'))
        if (-not (Test-Path $p)) { $bad.Add($rel + ' (payload 缺失)'); continue }
        $expect = Get-ExpectedHash $rel $overrides
        $actual = (Get-FileHash -Path $p -Algorithm SHA256).Hash.ToLower()
        $checked++
        if ($expect -eq '__MISSING__') {
            $bad.Add($rel + ' (manifest 缺少可信哈希锚点)')
        }
        elseif ($expect -and $actual -ne $expect.ToLower()) {
            $bad.Add($rel + ' (payload 内容不符)')
        }
    }
    if ($bad.Count -gt 0) {
        throw ('安装包完整性校验失败，已中止安装：' + [string]::Join('；', $bad))
    }
    Write-Ok ("安装包完整性预校验通过（{0} 个文件）" -f $checked)
}

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

    $checked = 0
    $keptCount = 0
    $bad = New-Object System.Collections.Generic.List[string]

    foreach ($c in $copied) {
        $checked++
        if (-not (Test-Path $c.dst)) {
            $bad.Add($c.rel + ' (缺失)')
            continue
        }
        $expect = Get-ExpectedHash $c.rel $overrides
        $actual = (Get-FileHash -Path $c.dst -Algorithm SHA256).Hash.ToLower()
        if ($c.kept) {
            # 保留的本地较新文件：只按它自己被保留下来的那份哈希核对，确保复制没损坏。
            $keptCount++
            $srcHash = (Get-FileHash -Path $c.src -Algorithm SHA256).Hash.ToLower()
            if ($actual -ne $srcHash) { $bad.Add($c.rel + ' (保留文件复制损坏)') }
            continue
        }
        Show-Bar ([int](100 * $checked / $copied.Count)) ("SHA256 " + ($c.rel.Split('/')[-1]))
        if ($expect -eq '__MISSING__') {
            $bad.Add($c.rel + ' (manifest 缺少可信哈希锚点)')
        }
        elseif ($expect -and $actual -ne $expect.ToLower()) {
            $bad.Add($c.rel + ' (内容不符)')
        }
        elseif (-not $expect) {
            # 明确的 config 豁免：按源文件自身哈希校验复制是否无损。
            $srcHash = (Get-FileHash -Path $c.src -Algorithm SHA256).Hash.ToLower()
            if ($actual -ne $srcHash) { $bad.Add($c.rel + ' (复制损坏)') }
        }
    }
    Hide-Bar

    if ($keptCount -gt 0) {
        Write-Ok ("保留了 {0} 个本地较新文件（已核对复制无误）" -f $keptCount)
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

# 控制台头部横幅与项目信息只在纯控制台模式打印。GUI 模式下窗体标题栏与副标题
# 已经显示版本 / 作者 / QQ 群，若在这里先打到 cmd 会留下一个「黑窗口 + GUI」双窗口，
# 且这些输出发生在 Gui.ps1 加载之前，无法被日志重定向捕获。
if (-not $UseGui) {
    Write-Head 'SimplePlanes 2 简体中文汉化'
}

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
if (-not $UseGui) {
    Write-Host ''
    Write-Host ('    >  作者      {0}' -f $mfAuthor) -ForegroundColor Gray
    Write-Host ('    >  QQ 群     {0}' -f $mfGroup) -ForegroundColor Gray
    Write-Host ('    >  项目      {0}' -f $mfRepo) -ForegroundColor Gray
}

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

function Invoke-InstallFlow([string]$dir) {
    # 交互与静默两条路径共用：都要先检查在线词库更新，再把 overrides 传进安装与校验，
    # 并且任一校验失败都必须回滚、返回非零，而不是「装完再告诉你有红字」。
    $overrides = Invoke-UpdateCheck
    if ($null -eq $overrides) { $overrides = @{} }
    $result = $null
    $script:LastBackupPath = $null
    try {
        $result = Install-Payload $dir $overrides
        if (-not (Test-Install $result.PluginDir)) {
            throw '安装后基础校验未通过（DLL / 词库缺失）。'
        }
        $hashOk = Verify-InstalledFiles $dir $result.Copied $overrides
        if (-not $hashOk) {
            throw '写入文件与 manifest 记录不一致，安装未通过校验。'
        }
        Write-Host ''
        Write-Head '安装完成'
        Show-InstallSummary $dir $result $true
        Write-Host ''
        Write-Host '  启动游戏即可，界面应为中文。' -ForegroundColor Green
        Write-Host ''
        Write-Host '  首次进游戏如果仍是英文：' -ForegroundColor Gray
        Write-Host '   1. 确认是从 Steam 正常启动（不要用别的快捷方式）' -ForegroundColor DarkGray
        Write-Host '   2. 完全退出游戏后重新打开' -ForegroundColor DarkGray
        Write-Host ('   3. 检查 BepInEx\LogOutput.log 里有没有 "SP2 chinese"') -ForegroundColor DarkGray
        return $true
    }
    catch {
        Write-Err $_.Exception.Message
        Write-Host '  已中止安装并尝试回滚，游戏目录保持安装前状态。' -ForegroundColor Yellow
        if ($result) {
            Restore-FailedInstall $result.PluginDir $result.BackupPath @($result.Copied | ForEach-Object { $_.dst })
        }
        elseif ($script:LastBackupPath) {
            # Install-Payload 在返回前就抛异常：从备份目录把旧插件放回原处。
            $pd = Join-Path $dir 'BepInEx\plugins\SP2Chinese'
            Restore-FailedInstall $pd $script:LastBackupPath @()
        }
        Write-Host '  如果提示文件被占用，请彻底关闭游戏（含任务栏图标）后重试。' -ForegroundColor Yellow
        return $false
    }
}

if ($UseGui) {
    # ---------------- 图形界面（默认）----------------
    . (Join-Path $ScriptDir 'Gui.ps1')
    Show-GuiInstaller
    exit 0
}
elseif (-not $Silent -and -not $Yes -and -not $Uninstall) {
    # ---------------- 回退：数字 + 回车菜单（-NoGui）----------------
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
            [void](Invoke-InstallFlow $d)
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
        if (-not (Invoke-InstallFlow $GameDir)) { Wait-Key; exit 1 }
    }
}
catch {
    Write-Err $_.Exception.Message
    Wait-Key
    exit 1
}

if (-not $Silent) { Wait-Key '按回车关闭窗口' }
