<#
 SimplePlanes 2 简体中文汉化 · 图形界面层
 ------------------------------------------------------------
 只依赖 Windows 自带的 Windows PowerShell 5.1 + System.Windows.Forms，
 不引入 .NET Runtime / WebView2 / 第三方库，安装包体积不变。

 设计：install.ps1 的全部安装逻辑保持不变，本模块做三件事：
   1. 覆盖 Write-Host，把文字按颜色送进窗体的日志框（含 \r 进度条处理）
   2. 覆盖 Read-Host / Wait-Key，用 InputBox / MessageBox 代替控制台输入
   3. 提供 3 个按钮（安装 / 卸载 / 回滚），点击后在后台线程跑原有函数
 无人值守开关（-Silent / -Yes / -Uninstall / -TestUpdate）不加载本模块，
 行为与旧版完全一致，CI 与脚本调用不受影响。
#>

$script:GuiForm = $null
$script:GuiLog = $null
$script:GuiBar = $null
$script:GuiButtons = @{}
$script:GuiBusy = $false
$script:GuiPendingLine = ''
$script:GuiMode = $false

function Gui-Log([string]$text, [object]$color) {
    if (-not $script:GuiLog -or $script:GuiLog.IsDisposed) { return }
    # \r 是旧版控制台的「原地刷新进度条」；GUI 里直接丢弃控制字符。
    $text = $text -replace "`r", ''
    if ($text -eq '') { return }
    $c = [System.Drawing.Color]::FromArgb(210, 213, 219)   # 默认灰白
    switch ([string]$color) {
        'Green'      { $c = [System.Drawing.Color]::FromArgb(126, 211, 148) }
        'Yellow'     { $c = [System.Drawing.Color]::FromArgb(230, 192, 104) }
        'Red'        { $c = [System.Drawing.Color]::FromArgb(235, 111, 111) }
        'Cyan'       { $c = [System.Drawing.Color]::FromArgb(111, 194, 214) }
        'DarkCyan'   { $c = [System.Drawing.Color]::FromArgb(104, 165, 184) }
        'White'      { $c = [System.Drawing.Color]::FromArgb(236, 238, 242) }
        'DarkGray'   { $c = [System.Drawing.Color]::FromArgb(140, 145, 155) }
        'Gray'       { $c = [System.Drawing.Color]::FromArgb(170, 175, 185) }
    }
    # 按钮事件在 UI 线程同步执行，直接写入即可；若将来改为后台线程，
    # 这里用 Invoke 同步回 UI 线程（而不是易错的 BeginInvoke + GetType()）。
    if ($script:GuiLog.InvokeRequired) {
        $action = [System.Action[string, System.Drawing.Color]]{
            param($t, $col)
            $script:GuiLog.SelectionStart = $script:GuiLog.TextLength
            $script:GuiLog.SelectionColor = $col
            [void]$script:GuiLog.AppendText($t + [Environment]::NewLine)
            $script:GuiLog.ScrollToCaret()
        }
        [void]$script:GuiLog.Invoke($action, @($text, $c))
        return
    }
    $script:GuiLog.SelectionStart = $script:GuiLog.TextLength
    $script:GuiLog.SelectionColor = $c
    [void]$script:GuiLog.AppendText($text + [Environment]::NewLine)
    $script:GuiLog.ScrollToCaret()
}

# 覆盖 Write-Host：GUI 模式下写入日志框，否则原样转发给宿主 cmdlet。
function global:Write-Host {
    param(
        [Parameter(Position = 0, ValueFromRemainingArguments = $true)][object[]]$Object,
        [switch]$NoNewline,
        [object]$Separator,
        [System.ConsoleColor]$ForegroundColor,
        [System.ConsoleColor]$BackgroundColor
    )
    if (-not $script:GuiMode) {
        Microsoft.PowerShell.Utility\Write-Host @PSBoundParameters
        return
    }
    $s = ($Object | ForEach-Object { [string]$_ }) -join ' '
    $col = if ($PSBoundParameters.ContainsKey('ForegroundColor')) { [string]$ForegroundColor } else { 'Gray' }
    if ($NoNewline) {
        $script:GuiPendingLine += $s
    }
    else {
        Gui-Log ($script:GuiPendingLine + $s) $col
        $script:GuiPendingLine = ''
    }
}

# 覆盖 Read-Host：GUI 模式下不弹 InputBox（在按钮事件里会阻塞 UI 线程）。
# 现有调用点全是「是否继续 / 选哪个 / 手输路径」这类可取消的问题，返回空字符串
# 等价于旧版「用户直接回车 / EOF」，即取消或采用默认值，是安全且无阻塞的。
# 真正需要用户选目录时走 Select-GameFolderViaDialog（FolderBrowserDialog，已是模态窗体）。
function global:Read-Host {
    param(
        [Parameter(Position = 0)][object]$Prompt,
        [switch]$AsSecureString
    )
    if (-not $script:GuiMode) {
        if ($PSBoundParameters.ContainsKey('AsSecureString')) {
            return Microsoft.PowerShell.Utility\Read-Host -Prompt $Prompt -AsSecureString
        }
        return Microsoft.PowerShell.Utility\Read-Host -Prompt $Prompt
    }
    return ''
}

# 覆盖 Wait-Key：GUI 模式下不阻塞（用户已在看日志，按钮可继续点）。
function global:Wait-Key([string]$msg = '按回车关闭窗口') {
    if (-not $script:GuiMode) {
        Write-Host ''
        Microsoft.PowerShell.Utility\Read-Host ("  " + $msg)
    }
    # GUI 模式：不做任何等待
}

function Gui-SetBusy([bool]$busy) {
    $script:GuiBusy = $busy
    foreach ($b in $script:GuiButtons.Values) {
        if ($b -and -not $b.IsDisposed) { $b.Enabled = -not $busy }
    }
}

function Gui-RefreshStatus {
    if ($script:GuiForm -and -not $script:GuiForm.IsDisposed) {
        Gui-Log '────────────────────────────────────────────' 'DarkGray'
        Show-Status
    }
}

# 回滚用的下拉选择窗体。返回 1-based 序号，取消返回 0。
function Gui-SelectRollback([string[]]$labels) {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    $f = New-Object System.Windows.Forms.Form
    $f.Text = '回滚到历史版本'
    $f.Size = New-Object System.Drawing.Size(520, 170)
    $f.StartPosition = 'CenterParent'
    $f.FormBorderStyle = 'FixedDialog'
    $f.MaximizeBox = $false
    $f.MinimizeBox = $false
    $f.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

    $lab = New-Object System.Windows.Forms.Label
    $lab.Text = '选择要恢复的版本：'
    $lab.Location = New-Object System.Drawing.Point(16, 16)
    $lab.Size = New-Object System.Drawing.Size(470, 22)
    [void]$f.Controls.Add($lab)

    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.Location = New-Object System.Drawing.Point(16, 42)
    $combo.Size = New-Object System.Drawing.Size(470, 26)
    $combo.DropDownStyle = 'DropDownList'
    foreach ($l in $labels) { [void]$combo.Items.Add($l) }
    if ($combo.Items.Count -gt 0) { $combo.SelectedIndex = 0 }
    [void]$f.Controls.Add($combo)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = '回滚'
    $ok.Location = New-Object System.Drawing.Point(300, 88)
    $ok.Size = New-Object System.Drawing.Size(88, 30)
    $ok.DialogResult = 'OK'
    [void]$f.Controls.Add($ok)
    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = '取消'
    $cancel.Location = New-Object System.Drawing.Point(398, 88)
    $cancel.Size = New-Object System.Drawing.Size(88, 30)
    $cancel.DialogResult = 'Cancel'
    [void]$f.Controls.Add($cancel)
    $f.AcceptButton = $ok
    $f.CancelButton = $cancel

    if ($f.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return 0 }
    return ($combo.SelectedIndex + 1)
}

function Hide-ConsoleWindow {
    # 隐藏由 bat 拉起的 cmd 控制台，避免「黑窗口 + GUI 窗体」双窗口。
    # 用 Win32 隐藏当前进程附加的控制台；失败时静默忽略（例如从已有终端启动）。
    try {
        if (-not ('SP2Chinese.Win32' -as [type])) {
            Add-Type -Namespace SP2Chinese -Name Win32 -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@
        }
        $h = [SP2Chinese.Win32]::GetConsoleWindow()
        if ($h -ne [IntPtr]::Zero) {
            [void][SP2Chinese.Win32]::ShowWindow($h, 0)   # SW_HIDE
            return $true
        }
    } catch {}
    return $false
}

function Show-ConsoleWindow {
    try {
        if ('SP2Chinese.Win32' -as [type]) {
            $h = [SP2Chinese.Win32]::GetConsoleWindow()
            if ($h -ne [IntPtr]::Zero) { [void][SP2Chinese.Win32]::ShowWindow($h, 5) }  # SW_SHOW
        }
    } catch {}
}

function Set-DpiAware {
    # 关键：进程默认非 DPI 感知，Windows 在高分屏（125%/150% 缩放）会把整个窗体
    # 先按 96dpi 渲染再位图拉伸，文字就会发虚。必须在任何窗体/控件创建之前声明。
    # 优先 Win10 1703+ 的 Per-Monitor V2；失败时退回老 API；都失败则静默继续。
    try {
        if (-not ('SP2Chinese.Dpi' -as [type])) {
            Add-Type -Namespace SP2Chinese -Name Dpi -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);
'@
        }
        # PER_MONITOR_AWARE_V2 = (DPI_AWARENESS_CONTEXT)-4
        if ([SP2Chinese.Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4))) { return }
        [void][SP2Chinese.Dpi]::SetProcessDPIAware()
    } catch {
        try { [void][SP2Chinese.Dpi]::SetProcessDPIAware() } catch {}
    }
}

function Show-GuiInstaller {
    Set-DpiAware
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    [void][System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'SimplePlanes 2 简体中文汉化 · 安装器'
    $form.Size = New-Object System.Drawing.Size(820, 600)
    $form.StartPosition = 'CenterScreen'
    $form.MinimizeBox = $true
    $form.MaximizeBox = $true
    $form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $form.BackColor = [System.Drawing.Color]::FromArgb(30, 32, 38)

    # 顶部标题（字号加大、ClearType 友好）
    $title = New-Object System.Windows.Forms.Label
    $title.Text = 'SimplePlanes 2 简体中文汉化'
    $title.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 16, [System.Drawing.FontStyle]::Bold)
    $title.ForeColor = [System.Drawing.Color]::FromArgb(240, 242, 246)
    $title.Location = New-Object System.Drawing.Point(20, 14)
    $title.Size = New-Object System.Drawing.Size(760, 36)
    [void]$form.Controls.Add($title)

    # 信息行做成「只读但可选中、可复制」的 TextBox（Label 无法选中文字）。
    # 用 BorderStyle=None + 与背景同色，视觉上仍像一行说明文字。
    $info = New-Object System.Windows.Forms.TextBox
    $info.Text = "v$script:PackageVersion    词库 build $script:PackageBuild    白杨木 (baiyangmuzz)    QQ 群 1128992783"
    $info.ReadOnly = $true
    $info.BorderStyle = 'None'
    $info.BackColor = [System.Drawing.Color]::FromArgb(30, 32, 38)
    $info.ForeColor = [System.Drawing.Color]::FromArgb(178, 184, 194)
    $info.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $info.Location = New-Object System.Drawing.Point(22, 54)
    $info.Size = New-Object System.Drawing.Size(690, 24)
    $info.Cursor = 'IBeam'
    $info.TabStop = $false
    # TextBox 获得焦点时会默认全选并滚动到中间，看起来像「前半截被截掉了」。
    # 这里取消全选、把光标归零，让它看起来仍是一行普通说明文字。
    $info.Add_GotFocus({ $info.SelectionStart = 0; $info.SelectionLength = 0; $info.ScrollToCaret() })
    [void]$form.Controls.Add($info)

    # 一键复制群号 / 仓库地址（避免从长串里手抠数字）
    $copyQq = New-Object System.Windows.Forms.Button
    $copyQq.Text = '复制群号'
    $copyQq.Size = New-Object System.Drawing.Size(88, 26)
    $copyQq.Location = New-Object System.Drawing.Point(716, 51)
    $copyQq.FlatStyle = 'Flat'
    $copyQq.FlatAppearance.BorderSize = 0
    $copyQq.BackColor = [System.Drawing.Color]::FromArgb(58, 62, 70)
    $copyQq.ForeColor = [System.Drawing.Color]::FromArgb(220, 224, 232)
    $copyQq.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
    $copyQq.Cursor = 'Hand'
    $copyQq.Add_Click({
        try {
            [System.Windows.Forms.Clipboard]::SetText('1128992783')
            Gui-Log '已复制 QQ 群号：1128992783' 'Green'
        } catch { Gui-Log ('复制失败：' + $_.Exception.Message) 'Red' }
    })
    [void]$form.Controls.Add($copyQq)

    # 按钮区（字号跟随窗体 10pt，中文更清楚）
    $y = 92
    $x = 20
    foreach ($spec in @(
        @{ Name = 'Install'; Text = '检查更新并安装'; W = 158 },
        @{ Name = 'Uninstall'; Text = '卸载汉化'; W = 116 },
        @{ Name = 'Rollback'; Text = '回滚到历史版本'; W = 148 }
    )) {
        $b = New-Object System.Windows.Forms.Button
        $b.Text = $spec.Text
        $b.Size = New-Object System.Drawing.Size($spec.W, 36)
        $b.Location = New-Object System.Drawing.Point($x, $y)
        $b.FlatStyle = 'Flat'
        $b.FlatAppearance.BorderSize = 0
        $b.BackColor = [System.Drawing.Color]::FromArgb(52, 120, 168)
        $b.ForeColor = [System.Drawing.Color]::White
        $b.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
        $b.Cursor = 'Hand'
        [void]$form.Controls.Add($b)
        $script:GuiButtons[$spec.Name] = $b
        $x += $spec.W + 10
    }
    $close = New-Object System.Windows.Forms.Button
    $close.Text = '退出'
    $close.Size = New-Object System.Drawing.Size(84, 36)
    $close.Location = New-Object System.Drawing.Point(716, $y)
    $close.FlatStyle = 'Flat'
    $close.FlatAppearance.BorderSize = 0
    $close.BackColor = [System.Drawing.Color]::FromArgb(70, 73, 80)
    $close.ForeColor = [System.Drawing.Color]::FromArgb(210, 213, 219)
    $close.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $close.Add_Click({ $form.Close() })
    [void]$form.Controls.Add($close)

    # 日志框：等宽字体加大到 11pt，避免高分屏下笔画挤在一起发糊。
    $log = New-Object System.Windows.Forms.RichTextBox
    $log.Location = New-Object System.Drawing.Point(20, 142)
    $log.Size = New-Object System.Drawing.Size(780, 408)
    $log.ReadOnly = $true
    $log.BackColor = [System.Drawing.Color]::FromArgb(22, 24, 29)
    $log.ForeColor = [System.Drawing.Color]::FromArgb(210, 213, 219)
    $log.Font = New-Object System.Drawing.Font('Consolas', 11)
    $log.BorderStyle = 'FixedSingle'
    $log.WordWrap = $false
    $log.ScrollBars = 'Both'
    [void]$form.Controls.Add($log)
    $script:GuiLog = $log
    $script:GuiForm = $form

    # 按钮事件：直接在 UI 线程同步执行（安装过程几秒内完成，且 WinForms
    # 的 FolderBrowserDialog 需要 STA 线程，UI 线程正好是 STA）。
    $script:GuiButtons['Install'].Add_Click({
        if ($script:GuiBusy) { return }
        Gui-SetBusy $true
        try {
            $d = Ensure-GameDir
            if (-not $d) { Gui-Log '无法确定游戏目录，操作已取消。' 'Red'; return }
            if (-not (Stop-RunningGame $d)) { Gui-Log '操作已取消。' 'Yellow'; return }
            [void](Invoke-InstallFlow $d)
        }
        catch { Gui-Log ('错误：' + $_.Exception.Message) 'Red' }
        finally { Gui-SetBusy $false; Gui-RefreshStatus }
    })
    $script:GuiButtons['Uninstall'].Add_Click({
        if ($script:GuiBusy) { return }
        Gui-SetBusy $true
        try {
            $d = Ensure-GameDir
            if (-not $d) { Gui-Log '无法确定游戏目录，操作已取消。' 'Red'; return }
            if (-not (Stop-RunningGame $d)) { Gui-Log '操作已取消。' 'Yellow'; return }
            Uninstall-Plugin $d
        }
        catch { Gui-Log ('错误：' + $_.Exception.Message) 'Red' }
        finally { Gui-SetBusy $false; Gui-RefreshStatus }
    })
    $script:GuiButtons['Rollback'].Add_Click({
        if ($script:GuiBusy) { return }
        Gui-SetBusy $true
        try {
            $d = Ensure-GameDir
            if (-not $d) { Gui-Log '无法确定游戏目录，操作已取消。' 'Red'; return }
            if (-not (Stop-RunningGame $d)) { Gui-Log '操作已取消。' 'Yellow'; return }
            Show-Rollback $d
        }
        catch { Gui-Log ('错误：' + $_.Exception.Message) 'Red' }
        finally { Gui-SetBusy $false; Gui-RefreshStatus }
    })

    $script:GuiMode = $true
    $consoleHidden = Hide-ConsoleWindow
    # 兜底：任何未捕获异常都写进日志，而不是弹 .NET Framework 的「未处理异常」原生框
    # （那个框对玩家毫无信息量，还会让人以为系统中毒）。UI 线程异常走 ThreadException。
    $onThreadException = [System.Threading.ThreadExceptionEventHandler]{
        param($sender, $e)
        try { Gui-Log ('发生未预期的错误：' + $e.Exception.Message) 'Red' } catch {}
    }
    [System.Windows.Forms.Application]::add_ThreadException($onThreadException)
    try {
        Gui-Log '欢迎使用 SimplePlanes 2 简体中文汉化安装器。' 'Cyan'
        Gui-Log '安装前请完全退出游戏；操作日志会显示在这里。' 'DarkGray'
        Gui-Log '项目地址 github.com/baiyangmuzz/SP2Chinese（可选中复制）' 'DarkGray'
        Gui-RefreshStatus
        [void]$form.ShowDialog()
    }
    finally {
        # 无论异常还是正常关闭都恢复控制台，避免留下一个看不见的 cmd 进程。
        try { [System.Windows.Forms.Application]::remove_ThreadException($onThreadException) } catch {}
        if ($consoleHidden) { Show-ConsoleWindow }
        $script:GuiMode = $false
        [void]$form.Dispose()
    }
    return
}
