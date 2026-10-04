#Requires -Version 5.1
<#
  GPU_PC_MGT · 指标采集端
  ==================================================================
  只用 Windows 自带能力：
    * 性能计数器 (PDH)        —— 与任务管理器"性能"页同一数据源，单次批量采样
    * CIM/WMI + 注册表 + Win32 API —— 机器信息（WMI 不可用时自动降级）
    * OpenSSH / WinRM         —— 远程通道（系统自带组件）
  可选增强（装了就有，没有就自动跳过）：
    * nvidia-smi                 —— NVIDIA 温度/功耗/风扇/频率/显存
    * LibreHardwareMonitor (WMI) —— 任意显卡 + CPU 的温度/功耗/风扇

  用法：
    pwsh -File tools\collect.ps1              # 按 config.json 循环采集 -> web\data.json
    pwsh -File tools\collect.ps1 -Once        # 采一次就退出（排错）
    pwsh -File tools\collect.ps1 -Only 本机   # 只采指定目标
    pwsh -File tools\collect.ps1 -RefreshInfo # 强制立刻重采机器信息
#>
[CmdletBinding()]
param(
  [string]   $ConfigPath = (Join-Path $PSScriptRoot '..\config.json'),
  [string]   $OutPath    = (Join-Path $PSScriptRoot '..\web\data.json'),
  [double]   $Interval   = 0,
  [switch]   $Once,
  [string[]] $Only,
  [switch]   $RefreshInfo
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'
try { $OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

# --------------------------------------------------------------------
# 计数器路径分级：一次 Get-Counter 调用有 ~1 秒采样下限，
# 所以所有路径必须合并成"一次调用"，并根据目标机能力逐级降级。
# --------------------------------------------------------------------
$PathsBase = @(
  '\Processor Information(*)\% Processor Utility'
  '\Processor Information(_Total)\Processor Frequency'
  '\Processor Information(_Total)\% Processor Performance'
  '\Memory\Available MBytes'
  '\Memory\Committed Bytes'
  '\Memory\Commit Limit'
  '\Memory\Cache Bytes'
  '\PhysicalDisk(_Total)\Disk Read Bytes/sec'
  '\PhysicalDisk(_Total)\Disk Write Bytes/sec'
  '\PhysicalDisk(_Total)\% Idle Time'
  '\PhysicalDisk(_Total)\Avg. Disk Queue Length'
  '\LogicalDisk(*)\Free Megabytes'
  '\Network Interface(*)\Bytes Received/sec'
  '\Network Interface(*)\Bytes Sent/sec'
)
$PathsGpu = @(
  '\GPU Engine(*)\Utilization Percentage'
  '\GPU Adapter Memory(*)\Dedicated Usage'
  '\GPU Adapter Memory(*)\Shared Usage'
)
# 0 = 全部, 1 = 无 GPU, 2 = 最小集（老系统/受限目标）
$PathLevels = @(
  ($PathsBase + $PathsGpu),
  $PathsBase,
  @(
    '\Processor Information(*)\% Processor Time'
    '\Memory\Available MBytes'
    '\Memory\Committed Bytes'
    '\PhysicalDisk(_Total)\Disk Read Bytes/sec'
    '\PhysicalDisk(_Total)\Disk Write Bytes/sec'
    '\PhysicalDisk(_Total)\% Idle Time'
    '\Network Interface(*)\Bytes Received/sec'
    '\Network Interface(*)\Bytes Sent/sec'
  )
)

$DynTemplate = @'
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
function N([string]$v) { $v = ([string]$v).Trim(); if ($v -match '^-?[0-9]+(\.[0-9]+)?$') { return [double]$v } return $null }
$c = Get-Counter -Counter @PATHS@ -MaxSamples 1 -ErrorAction Stop
$samples = New-Object System.Collections.ArrayList
$engines = @{}
$hasUtility = $false
foreach ($s in $c.CounterSamples) {
  $p = [string]$s.Path
  $v = [double]$s.CookedValue
  if ($p -match 'gpu engine\(') {
    if ($p -match 'engtype_([^)]+)') {
      $k = $Matches[1]
      $k = $k -replace '#[0-9]+$', ''
      $k = $k -replace '[ 0-9]+$', ''
      $k = $k.Trim().ToLower()
      $cur = 0.0
      if ($engines.ContainsKey($k)) { $cur = [double]$engines[$k] }
      $sum = $cur + $v
      if ($sum -gt 100) { $sum = 100 }
      $engines[$k] = $sum
    }
    continue
  }
  if ($p -match '% processor utility') { $hasUtility = $true }
  [void]$samples.Add([pscustomobject]@{ path = $p; value = $v })
}
if (-not $hasUtility) {
  $c2 = Get-Counter -Counter '\Processor Information(*)\% Processor Time' -MaxSamples 1 -ErrorAction SilentlyContinue
  foreach ($s in @($c2.CounterSamples)) {
    [void]$samples.Add([pscustomobject]@{ path = [string]$s.Path; value = [double]$s.CookedValue })
  }
}
$smi = @()
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
  $csv = & nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm --format=csv,noheader,nounits 2>$null
  foreach ($line in @($csv)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $f = @(($line -split ',') | ForEach-Object { $_.Trim() })
    if ($f.Count -lt 10) { continue }
    $smi += [pscustomobject]@{
      index = N $f[0]; name = $f[1]; utilPct = N $f[2]; memUsedMB = N $f[3]; memTotalMB = N $f[4]
      tempC = N $f[5]; powerW = N $f[6]; powerLimitW = N $f[7]; fanPct = N $f[8]; clockMhz = N $f[9]
    }
  }
}
$lhm = @()
foreach ($s in @(Get-CimInstance -Namespace 'root/LibreHardwareMonitor' -ClassName Sensor -ErrorAction SilentlyContinue)) {
  $lhm += [pscustomobject]@{ name = [string]$s.Name; sensorType = [string]$s.SensorType; value = [double]$s.Value; identifier = [string]$s.Identifier }
}
$result = [pscustomobject]@{
  ts = (Get-Date).ToString('o'); host = $env:COMPUTERNAME
  counters = $samples; gpuEngines = $engines; nvidia = $smi; lhm = $lhm
}
$result | ConvertTo-Json -Depth 8 -Compress
'@

$InfoTemplate = @'
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
function S($v) { if ($null -eq $v) { return '' } return [string]$v }
function L($v) { if ($null -eq $v) { return 0 } if ([string]$v -match '^-?[0-9]+$') { return [long]$v } return 0 }
function D($v) { if ($null -eq $v) { return 0.0 } if ([string]$v -match '^-?[0-9]+(\.[0-9]+)?$') { return [double]$v } return 0.0 }

$manufacturer = ''; $model = ''
$osCaption = ''; $osBuild = ''; $lastBoot = $null
$ramTotal = 0.0; $ramAvail = 0.0
$cpuName = ''; $cpuCores = 0; $cpuLogical = 0; $cpuMaxMhz = 0
$gpus = @(); $volumes = @()
$sources = @()

# ---------- 1) CIM / WMI ----------
$cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
if ($cs) { $manufacturer = S $cs.Manufacturer; $model = S $cs.Model; $ramTotal = D $cs.TotalPhysicalMemory; $sources += 'wmi' }
$os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
if ($os) {
  $osCaption = S $os.Caption; $osBuild = S $os.BuildNumber
  if ($os.LastBootUpTime) { $lastBoot = ([datetime]$os.LastBootUpTime).ToString('o') }
  $ramAvail = (D $os.FreePhysicalMemory) * 1024
}
$cp = Get-CimInstance -ClassName Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cp) { $cpuName = S $cp.Name; $cpuCores = L $cp.NumberOfCores; $cpuLogical = L $cp.NumberOfLogicalProcessors; $cpuMaxMhz = L $cp.MaxClockSpeed }
foreach ($vc in @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue)) {
  $gpus += [pscustomobject]@{ name = S $vc.Name; driver = S $vc.DriverVersion; vramBytes = D $vc.AdapterRAM }
}
foreach ($ld in @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue)) {
  $volumes += [pscustomobject]@{ drive = S $ld.DeviceID; sizeBytes = D $ld.Size; freeBytes = D $ld.FreeSpace }
}

# ---------- 2) 注册表兜底（WMI 被禁用/损坏时） ----------
if (-not $cpuName -or -not $osCaption) {
  $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
  if ($cv) {
    if (-not $osCaption) { $osCaption = ((S $cv.ProductName) + ' ' + (S $cv.DisplayVersion)).Trim() }
    if (-not $osBuild)   { $osBuild = S $cv.CurrentBuild }
    $sources += 'registry'
  }
  $cp0 = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -ErrorAction SilentlyContinue
  if ($cp0 -and -not $cpuName) { $cpuName = S $cp0.ProcessorNameString }
}
if ($gpus.Count -eq 0) {
  $cls = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
  foreach ($k in @(Get-ChildItem $cls -ErrorAction SilentlyContinue)) {
    if ($k.PSChildName -notmatch '^[0-9]{4}$') { continue }
    $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
    if ($p -and $p.DriverDesc) {
      $gpus += [pscustomobject]@{ name = S $p.DriverDesc; driver = S $p.DriverVersion; vramBytes = D $p.'HardwareInformation.qwMemorySize' }
    }
  }
}

# ---------- 3) Win32 API 兜底（内存容量） ----------
if ($ramTotal -le 0) {
  try {
    Add-Type -ErrorAction Stop -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class GpmMemEx {
  [StructLayout(LayoutKind.Sequential)] public struct M { public uint len; public uint load; public ulong total; public ulong avail; public ulong tp; public ulong ap; public ulong tv; public ulong av; public ulong ave; }
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GlobalMemoryStatusEx(ref M m);
  public static ulong[] Get() { M m = new M(); m.len = (uint)Marshal.SizeOf(typeof(M)); if (!GlobalMemoryStatusEx(ref m)) { return null; } return new ulong[] { m.total, m.avail }; }
}
"@
    $r = [GpmMemEx]::Get()
    if ($r) { $ramTotal = [double]$r[0]; if ($ramAvail -le 0) { $ramAvail = [double]$r[1] }; $sources += 'win32' }
  } catch { }
}

# ---------- 4) 卷容量兜底 ----------
if ($volumes.Count -eq 0) {
  foreach ($d in @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
    if ($null -eq $d.Used) { continue }
    $volumes += [pscustomobject]@{ drive = ($d.Name + ':'); sizeBytes = [double]($d.Used + $d.Free); freeBytes = [double]$d.Free }
  }
}

$info = [pscustomobject]@{
  hostname = $env:COMPUTERNAME; user = $env:USERNAME
  manufacturer = $manufacturer; model = $model
  osCaption = $osCaption; osBuild = $osBuild; lastBoot = $lastBoot
  ramTotalBytes = $ramTotal; ramAvailBytes = $ramAvail
  cpuName = $cpuName; cpuCores = $cpuCores; cpuLogical = $cpuLogical; cpuMaxMhz = $cpuMaxMhz
  gpuAdapters = $gpus; volumes = $volumes; sources = $sources
}
$info | ConvertTo-Json -Depth 8 -Compress
'@

function New-Payload([string]$template, [string[]]$paths) {
  # 注意：这里要生成 PowerShell 数组字面量，不能直接用 JSON 数组
  $list = ($paths | ForEach-Object { "'" + ([string]$_).Replace("'", "''") + "'" }) -join ','
  return $template.Replace('@PATHS@', "@($list)")
}

$DynPayloads = @(
  (New-Payload $DynTemplate $PathLevels[0]),
  (New-Payload $DynTemplate $PathLevels[1]),
  (New-Payload $DynTemplate $PathLevels[2])
)
$InfoPayload = $InfoTemplate

# --------------------------------------------------------------------
# 远程通道
# --------------------------------------------------------------------
function Get-CredFromConfig($target) {
  $path = [string]$target.credential
  if ([string]::IsNullOrWhiteSpace($path)) { return $null }
  if (-not [System.IO.Path]::IsPathRooted($path)) {
    $path = Join-Path (Split-Path -Parent $ConfigPath) $path
  }
  if (-not (Test-Path -LiteralPath $path)) { throw "找不到凭据文件: $path" }
  return (Import-Clixml -LiteralPath $path)
}

function Invoke-LocalPayload([string]$text) {
  $out = & ([scriptblock]::Create($text))
  return (Select-JsonLine $out)
}

function Invoke-SshPayload($target, [string]$text) {
  if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw '本机没有 ssh.exe（安装"OpenSSH 客户端"可选功能）'
  }
  $dest = if ($target.user) { "$($target.user)@$($target.host)" } else { [string]$target.host }
  $args = @('-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=accept-new', '-o', 'ConnectTimeout=8')
  if ($target.port) { $args += @('-p', [string]$target.port) }
  if ($target.key)  { $args += @('-i', [string]$target.key) }
  $args += @($dest, 'powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -')
  $out = $text | & ssh @args 2>&1
  return (Select-JsonLine $out)
}

function Invoke-WinRmPayload($target, [string]$text) {
  $p = @{
    ComputerName = [string]$target.host
    ScriptBlock  = [scriptblock]::Create($text)
    ErrorAction  = 'Stop'
  }
  $cred = Get-CredFromConfig $target
  if ($cred) { $p.Credential = $cred }
  $out = Invoke-Command @p
  return (Select-JsonLine $out)
}

function Select-JsonLine($lines) {
  $arr = @($lines | ForEach-Object { [string]$_ })
  for ($i = $arr.Count - 1; $i -ge 0; $i--) {
    $t = $arr[$i].Trim()
    if ($t.StartsWith('{') -and $t.EndsWith('}')) { return $t }
  }
  $tail = ($arr | Where-Object { $_.Trim() } | Select-Object -Last 3) -join ' | '
  throw "目标机没有返回可解析的 JSON。最后输出: $tail"
}

function Invoke-TargetPayload($target, [string]$text) {
  $channel = Get-Channel $target
  switch ($channel) {
    'local' { return Invoke-LocalPayload $text }
    'ssh'   { return Invoke-SshPayload   $target $text }
    'winrm' { return Invoke-WinRmPayload $target $text }
    default { throw "通道 $channel 不支持脚本载荷" }
  }
}

function Get-Channel($target) {
  if ($target.channel) { return [string]$target.channel }
  if ($target.host)    { return 'winrm' }
  return 'local'
}

function Get-PdhDynamic($target) {
  $p = @{
    ComputerName = [string]$target.host
    Counter      = $PathLevels[1]
    MaxSamples   = 1
    ErrorAction  = 'Stop'
  }
  $cred = Get-CredFromConfig $target
  if ($cred) { $p.Credential = $cred }
  $c = Get-Counter @p
  $samples = @()
  foreach ($s in $c.CounterSamples) {
    if ($null -eq $s.CookedValue) { continue }
    $samples += [pscustomobject]@{ path = [string]$s.Path; value = [double]$s.CookedValue }
  }
  return [pscustomobject]@{
    ts = (Get-Date).ToString('o'); host = [string]$target.host
    counters = $samples; gpuEngines = @{}; nvidia = @(); lhm = @()
  }
}

# --------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "找不到配置文件: $ConfigPath" }
$cfg = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json

if ($Interval -le 0) {
  if ($cfg.intervalSec) { $Interval = [double]$cfg.intervalSec } else { $Interval = 2 }
}
if ($Interval -lt 0.5) { $Interval = 0.5 }
$infoRefreshSec = 600
if ($cfg.infoRefreshSec) { $infoRefreshSec = [double]$cfg.infoRefreshSec }

$targets = @($cfg.targets | Where-Object { $_.enabled -ne $false })
if ($Only) { $targets = @($targets | Where-Object { ($Only -contains $_.name) -or ($Only -contains $_.host) }) }
if ($targets.Count -eq 0) { throw 'config.json 里没有启用任何目标' }

$state = @{}
foreach ($t in $targets) {
  $key = if ($t.name) { [string]$t.name } else { [string]$t.host }
  $state[$key] = @{ level = 0; info = $null; infoAt = [datetime]::MinValue; nextInfoTry = [datetime]::MinValue; infoFails = 0 }
}

$outDir = Split-Path -Parent $OutPath
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$targetNames = ($targets | ForEach-Object { if ($_.name) { $_.name } else { $_.host } }) -join ', '
Write-Host "[collect] 目标: $targetNames" -ForegroundColor Cyan
Write-Host "[collect] 标称间隔 ${Interval}s -> $OutPath" -ForegroundColor DarkGray
Write-Host "[collect] 说明: Get-Counter 有约 1 秒采样下限，实际刷新率取决于采集耗时" -ForegroundColor DarkGray

function Update-Info($target, $key, [switch]$Force) {
  $st = $state[$key]
  if ((Get-Channel $target) -eq 'pdh') { return }
  $now = Get-Date
  $due = ((($now - $st.infoAt).TotalSeconds -ge $infoRefreshSec))
  if ($Force) { $due = $true }
  if (-not $st.info) { $due = $true }
  if (-not $due) { return }
  if (-not $Force -and $now -lt $st.nextInfoTry) { return }
  try {
    $json = Invoke-TargetPayload $target $InfoPayload
    $st.info = ($json | ConvertFrom-Json)
    $st.infoAt = Get-Date
    $st.infoFails = 0
    $st.nextInfoTry = [datetime]::MinValue
    $src = ''
    if ($st.info.sources) { $src = ' 源: ' + ($st.info.sources -join '+') }
    Write-Host ("  [{0}] {1} 机器信息已更新{2}" -f (Get-Date -Format 'HH:mm:ss'), $key, $src) -ForegroundColor DarkCyan
  } catch {
    $st.infoFails++
    $backoff = [Math]::Min(1800, 60 * $st.infoFails)
    $st.nextInfoTry = (Get-Date).AddSeconds($backoff)
    Write-Host ("  [{0}] {1} 机器信息采集失败（{2} 秒后重试）: {3}" -f (Get-Date -Format 'HH:mm:ss'), $key, $backoff, $_.Exception.Message) -ForegroundColor DarkYellow
  }
}

function Get-Dynamic($target, $key) {
  $st = $state[$key]
  if ((Get-Channel $target) -eq 'pdh') { return (Get-PdhDynamic $target) }
  $lastErr = $null
  for ($lv = $st.level; $lv -le 2; $lv++) {
    try {
      $json = Invoke-TargetPayload $target $DynPayloads[$lv]
      if ($lv -ne $st.level) {
        Write-Host ("  [{0}] {1} 计数器降级到第 {2} 级（GPU/部分计数器不可用）" -f (Get-Date -Format 'HH:mm:ss'), $key, $lv) -ForegroundColor DarkYellow
      }
      $st.level = $lv
      return ($json | ConvertFrom-Json)
    } catch {
      $lastErr = $_
    }
  }
  throw $lastErr
}

while ($true) {
  $cycleStart = Get-Date
  $results = @()

  foreach ($t in $targets) {
    $key = if ($t.name) { [string]$t.name } else { [string]$t.host }
    Update-Info $t $key -Force:$RefreshInfo
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $data = $null; $errText = $null
    try { $data = Get-Dynamic $t $key } catch { $errText = $_.Exception.Message }
    $sw.Stop()
    $results += [pscustomobject]@{
      name      = $key
      channel   = Get-Channel $t
      host      = [string]$t.host
      ok        = ($null -ne $data)
      error     = $errText
      collectMs = [int]$sw.ElapsedMilliseconds
      info      = $state[$key].info
      data      = $data
    }
    if ($data) {
      Write-Host ("  [{0}] {1} OK {2}ms 计数器={3}" -f (Get-Date -Format 'HH:mm:ss'), $key, $sw.ElapsedMilliseconds, @($data.counters).Count) -ForegroundColor Green
    } else {
      Write-Host ("  [{0}] {1} 失败: {2}" -f (Get-Date -Format 'HH:mm:ss'), $key, $errText) -ForegroundColor Red
    }
  }

  $payload = [ordered]@{
    generated   = (Get-Date).ToString('o')
    intervalSec = $Interval
    collectMs   = [int]((Get-Date) - $cycleStart).TotalMilliseconds
    targets     = $results
  }
  $jsonText = $payload | ConvertTo-Json -Depth 14 -Compress
  $tmp = "$OutPath.tmp"
  [System.IO.File]::WriteAllText($tmp, $jsonText, [System.Text.UTF8Encoding]::new($false))
  Move-Item -Force -LiteralPath $tmp -Destination $OutPath

  if ($Once) { break }

  $wait = $Interval - ((Get-Date) - $cycleStart).TotalSeconds
  if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
}
