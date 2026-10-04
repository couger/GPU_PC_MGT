<#
  test-remote-wmi.ps1 -- 检查"正统免 agent"的远程取数路线在这台机器上到底能不能用。
  在 *Windows PowerShell 5.1* 里运行（-Credential 参数只有它那条线才完整）:

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-remote-wmi.ps1 `
        -TargetHost 192.0.2.10 -User monitoruser

  它会依次测：
    [1] TCP 可达性 135(DCOM/RPC) / 5985,5986(WinRM) / 445(SMB) / 22(SSH)
    [2] 远程 PDH 性能计数器   Get-Counter -ComputerName ... -Credential ...
    [3] 远程 WMI/DCOM         Get-CimInstance -ComputerName ... -Credential ...
    [4] 远程 WMI 的 GPU 性能类（看有没有温度属性）
    [5] WinRM/CIM 会话        New-CimSession -Credential ...
  失败时会打印需要在哪里开什么。
#>
param(
    [string]$TargetHost = '192.0.2.10',
    [string]$User = 'monitoruser',
    [string]$Password
)

function Tcp-Test([string]$h, [int]$port, [int]$timeoutMs = 2500) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $c.ConnectAsync($h, $port)
        if ($t.Wait($timeoutMs) -and $c.Connected) { return $true }
        return $false
    } catch { return $false }
    finally { $c.Close() }
}

Write-Host ("=== 目标 {0}（用户 {1}）===" -f $TargetHost, $User) -ForegroundColor Cyan

Write-Host "`n[1] TCP 可达性" -ForegroundColor Yellow
foreach ($p in 135, 5985, 5986, 445, 22) {
    $ok = Tcp-Test $TargetHost $p
    $name = switch ($p) { 135 { 'DCOM/RPC' } 5985 { 'WinRM http' } 5986 { 'WinRM https' } 445 { 'SMB' } 22 { 'SSH' } }
    Write-Host ("    {0,-6} {1,-12} {2}" -f $p, $name, $(if ($ok) { 'OK' } else { 'closed' }))
}

if (-not $Password) {
    $sec = Read-Host -Prompt "  输入 $User 的密码（不会显示）" -AsSecureString
} else {
    $sec = ConvertTo-SecureString $Password -AsPlainText -Force
}
$cred = New-Object System.Management.Automation.PSCredential($User, $sec)

$hasCred = (Get-Command Get-Counter).Parameters.ContainsKey('Credential')
Write-Host ("`n    本机 PowerShell {0}：Get-Counter 支持 -Credential = {1}" -f $PSVersionTable.PSVersion, $hasCred) -ForegroundColor DarkGray
if (-not $hasCred) {
    Write-Host "    （被安全策略限制或版本不支持；请在普通 5.1 会话里重跑本脚本）" -ForegroundColor DarkYellow
}

Write-Host "`n[2] 远程 PDH 性能计数器（这条才是性能数据的「正统」远程读法，Perfmon 同源）" -ForegroundColor Yellow
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    $v = Get-Counter -ComputerName $TargetHost -Credential $cred -MaxSamples 1 -ErrorAction Stop `
         -Counter '\Processor Information(_Total)\% Processor Utility'
    Write-Host ("    OK   CPU={0}%   用时 {1} ms" -f [math]::Round($v.CounterSamples[0].CookedValue, 1), $sw.ElapsedMilliseconds) -ForegroundColor Green
} catch {
    Write-Host ("    FAIL {0}   用时 {1} ms" -f $_.Exception.Message, $sw.ElapsedMilliseconds) -ForegroundColor Red
    Write-Host "    需要在目标机放行：Enable-NetFirewallRule -DisplayGroup '性能日志和警报';" -ForegroundColor DarkYellow
    Write-Host "                     并把账号加入本机组 'Performance Monitor Users'" -ForegroundColor DarkYellow
}

Write-Host "`n[3] 远程 WMI / DCOM" -ForegroundColor Yellow
$sw.Restart()
try {
    $c = Get-CimInstance -ComputerName $TargetHost -Credential $cred -ClassName Win32_OperatingSystem -ErrorAction Stop
    Write-Host ("    OK   {0}   用时 {1} ms" -f $c.Caption, $sw.ElapsedMilliseconds) -ForegroundColor Green
} catch {
    Write-Host ("    FAIL {0}   用时 {1} ms" -f $_.Exception.Message, $sw.ElapsedMilliseconds) -ForegroundColor Red
    Write-Host "    需要在目标机放行：Enable-NetFirewallRule -DisplayGroup 'Windows Management Instrumentation (WMI)';" -ForegroundColor DarkYellow
    Write-Host "    工作组 + 本地账号还要：LocalAccountTokenFilterPolicy=1（否则远程 UAC 会剥掉管理员令牌）" -ForegroundColor DarkYellow
}

Write-Host "`n[4] 远程 WMI 的 GPU 性能类（重点：有没有温度）" -ForegroundColor Yellow
$sw.Restart()
try {
    $g = Get-CimInstance -ComputerName $TargetHost -Credential $cred -ErrorAction Stop `
         -ClassName Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine
    Write-Host ("    OK   实例数={0}   用时 {1} ms" -f @($g).Count, $sw.ElapsedMilliseconds) -ForegroundColor Green
    $cls = @(Get-CimClass -Namespace root\cimv2 -ClassName 'Win32_PerfFormattedData_GPUPerformanceCounters_*')
    $temp = @()
    foreach ($x in $cls) { foreach ($pr in $x.CimClassProperties) { if ($pr.Name -match 'Temp') { $temp += "$($x.CimClassName).$($pr.Name)" } } }
    Write-Host ("    GPU 性能类 {0} 个；含温度属性 {1} 个 -> {2}" -f $cls.Count, $temp.Count, $(if ($temp.Count) { $temp -join ', ' } else { '（没有，WMI 拿不到 GPU 温度）' }))
} catch {
    Write-Host ("    FAIL {0}   用时 {1} ms" -f $_.Exception.Message, $sw.ElapsedMilliseconds) -ForegroundColor Red
}

Write-Host "`n[5] WinRM / CIM 会话（PowerShell 7 的 CIM 走 WinRM）" -ForegroundColor Yellow
$sw.Restart()
try {
    $sess = New-CimSession -ComputerName $TargetHost -Credential $cred -ErrorAction Stop
    $o = Get-CimInstance -CimSession $sess -ClassName Win32_OperatingSystem -ErrorAction Stop
    Write-Host ("    OK   {0}   用时 {1} ms" -f $o.Caption, $sw.ElapsedMilliseconds) -ForegroundColor Green
    Remove-CimSession $sess
} catch {
    Write-Host ("    FAIL {0}   用时 {1} ms" -f $_.Exception.Message, $sw.ElapsedMilliseconds) -ForegroundColor Red
    Write-Host "    需要在目标机：Enable-PSRemoting -Force （放行 5985/5986）" -ForegroundColor DarkYellow
    Write-Host "    工作组下本机还要（管理员）：Set-Item WSMan:\localhost\Client\TrustedHosts -Value $TargetHost -Concatenate -Force" -ForegroundColor DarkYellow
}

Write-Host "`n=== 结论怎么看 ===" -ForegroundColor Cyan
Write-Host "  · [2]/[3] 通了 = 可以不装任何东西远程读性能/清单；但每次调用都要重新认证（DCOM/RPC），"
Write-Host "    且 WMI 的 GPU 性能类没有温度，GPU 温度只能靠 D3DKMT（任务管理器同源）。"
Write-Host "  · [5] 通了 = 可以用 WinRM 持久会话（一次认证、多轮复用），这是微软自家的远程管理协议。"
Write-Host "  · 想每轮成本接近 0：让采集器常驻（Windows 服务/计划任务）+ 本地读端点，"
Write-Host "    这也是 SCOM / Azure Monitor Agent 的形状。"
