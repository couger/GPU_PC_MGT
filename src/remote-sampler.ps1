# GPU_PC_MGT remote sampler payload.
# Deployed once to C:\ProgramData\GPUPCMGT\sampler.ps1 and then invoked per cycle as:
#   powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <that path> -WantInfo 0|1
# NOTE: keep this file ASCII-only. It travels through an ssh command line; non-ASCII bytes
#       can be mangled by a legacy code page on the remote side.
param([int]$WantInfo = 0)
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$WantInfo = ($WantInfo -eq 1)
function ToNum([string]$v) { if ($v -match '^-?[0-9]+(\.[0-9]+)?$') { return [double]$v } return $null }

# ---------------- 1) GPU temperature / fan / power: D3DKMT PERFDATA (same source as Task Manager) --------
$gpuTemp = $null; $gpuFan = $null; $gpuPower = $null
$diagGpu = 'none'
try {
    if (-not ('GpmGpu' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class GpmGpu
{
    [StructLayout(LayoutKind.Sequential)] public struct QAI { public uint hAdapter; public int Type; public IntPtr pData; public uint Size; }
    [StructLayout(LayoutKind.Sequential)] public struct AI { public uint hAdapter; public long Luid; public uint NumSources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] public struct Enum2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)] public struct CloseA { public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)] public struct Perf
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency; public ulong MaxMemoryFrequency; public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth; public ulong PCIEBandwidth;
        public uint FanRPM; public uint Power; public uint Temperature; public byte PowerStateOverride;
    }
    [DllImport("gdi32.dll")] public static extern int D3DKMTEnumAdapters2(ref Enum2 e);
    [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref QAI q);
    [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref CloseA c);

    public static double[] Read()
    {
        double temp = Double.NaN, fan = Double.NaN, power = Double.NaN;
        int cap = 16;
        int isz = Marshal.SizeOf(typeof(AI));
        int psz = Marshal.SizeOf(typeof(Perf));
        IntPtr buf = Marshal.AllocHGlobal(isz * cap);
        try
        {
            Enum2 e = new Enum2(); e.NumAdapters = (uint)cap; e.pAdapters = buf;
            if (D3DKMTEnumAdapters2(ref e) != 0) return new double[] { temp, fan, power };
            uint n = e.NumAdapters; if (n > (uint)cap) n = (uint)cap;
            for (uint i = 0; i < n; i++)
            {
                AI ai = (AI)Marshal.PtrToStructure((IntPtr)((long)buf + (long)i * isz), typeof(AI));
                IntPtr pb = Marshal.AllocHGlobal(psz);
                try
                {
                    for (int k = 0; k < psz; k++) Marshal.WriteByte(pb, k, 0);
                    QAI q = new QAI(); q.hAdapter = ai.hAdapter; q.Type = 62; q.pData = pb; q.Size = (uint)psz;
                    if (D3DKMTQueryAdapterInfo(ref q) == 0)
                    {
                        Perf pf = (Perf)Marshal.PtrToStructure(pb, typeof(Perf));
                        double t = pf.Temperature / 10.0;
                        if (t > 0 && t < 130 && (Double.IsNaN(temp) || t > temp)) temp = t;
                        if (pf.FanRPM > 0 && (Double.IsNaN(fan) || pf.FanRPM > fan)) fan = pf.FanRPM;
                        double w = pf.Power / 10.0;
                        if (w > 0 && (Double.IsNaN(power) || w > power)) power = w;
                    }
                }
                finally { Marshal.FreeHGlobal(pb); }
                CloseA cl = new CloseA(); cl.hAdapter = ai.hAdapter; D3DKMTCloseAdapter(ref cl);
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return new double[] { temp, fan, power };
    }
}
"@
    }
    $g = [GpmGpu]::Read()
    if ($g -ne $null -and $g.Length -ge 3) {
        if (-not [double]::IsNaN($g[0])) { $gpuTemp = $g[0] }
        if (-not [double]::IsNaN($g[1])) { $gpuFan = $g[1] }
        if (-not [double]::IsNaN($g[2])) { $gpuPower = $g[2] }
        $diagGpu = 'ok'
    }
} catch { $diagGpu = 'err:' + $_.Exception.Message }

# ---------------- 2) Performance counters (one batched call; PDH has a ~1s sampling floor) --------------
$corePaths = @(
    '\Processor Information(*)\% Processor Utility'
    '\Processor Information(*)\% Processor Time'
    '\Processor Information(_Total)\Processor Frequency'
    '\Memory\Available MBytes'
    '\Memory\Committed Bytes'
    '\PhysicalDisk(_Total)\Disk Read Bytes/sec'
    '\PhysicalDisk(_Total)\Disk Write Bytes/sec'
    '\PhysicalDisk(_Total)\% Idle Time'
    '\PhysicalDisk(_Total)\Avg. Disk Queue Length'
    '\LogicalDisk(*)\Free Megabytes'
    '\Network Interface(*)\Bytes Received/sec'
    '\Network Interface(*)\Bytes Sent/sec'
)
$gpuPaths = @(
    '\GPU Engine(*)\Utilization Percentage'
    '\GPU Adapter Memory(*)\Dedicated Usage'
    '\GPU Adapter Memory(*)\Shared Usage'
)
$tempPaths = @('\Thermal Zone Information(*)\Temperature')

$samples = @()
$ctrLevel = -1
$ctrErr = ''
$level = 0
foreach ($set in @(($corePaths + $gpuPaths + $tempPaths), ($corePaths + $tempPaths), $corePaths)) {
    try {
        $c = Get-Counter -Counter $set -MaxSamples 1 -ErrorAction Stop
        $samples = @($c.CounterSamples)
        $ctrLevel = $level
        break
    } catch {
        $ctrErr = $_.Exception.Message
    }
    $level++
}

# Build a lookup table. IMPORTANT: PowerShell variable names are case-insensitive,
# so this table must NOT be named $S while a loop uses $s.
$CT = @{}
foreach ($sample in $samples) {
    $path = [string]$sample.Path
    $i = $path.IndexOf('\', 2)
    if ($i -lt 0) { continue }
    $rest = $path.Substring($i + 1)
    $j = $rest.LastIndexOf('\')
    if ($j -lt 0) { continue }
    $head = $rest.Substring(0, $j)
    $cnt = $rest.Substring($j + 1).ToLower()
    $obj = $head
    $inst = ''
    $k = $head.IndexOf('(')
    if ($k -ge 0 -and $head.EndsWith(')')) {
        $obj = $head.Substring(0, $k)
        $inst = $head.Substring($k + 1, $head.Length - $k - 2)
    }
    $CT[($obj.ToLower() + '|' + $inst.ToLower() + '|' + $cnt)] = [double]$sample.CookedValue
}
function G($o, $i, $c) {
    $key = $o + '|' + $i + '|' + $c
    if ($CT.ContainsKey($key)) { return $CT[$key] }
    return $null
}
function Insts($o, $c) {
    $suffix = '|' + $c
    $r = @{}
    foreach ($key in $CT.Keys) {
        if ($key.StartsWith($o + '|') -and $key.EndsWith($suffix)) {
            $inst = $key.Substring($o.Length + 1, $key.Length - $o.Length - 1 - $suffix.Length)
            $r[$inst] = $CT[$key]
        }
    }
    return $r
}

# CPU: both "_total" and "0,_total" may exist; prefer the plain one
$cpuPct = G 'processor information' '_total' '% processor utility'
if ($cpuPct -eq $null) { $cpuPct = G 'processor information' '_total' '% processor time' }
if ($cpuPct -eq $null) { $cpuPct = G 'processor information' '0,_total' '% processor utility' }
$cpuMhz = G 'processor information' '_total' 'processor frequency'

$cores = @()
$utilInsts = Insts 'processor information' '% processor utility'
if ($utilInsts.Count -eq 0) { $utilInsts = Insts 'processor information' '% processor time' }
foreach ($inst in ($utilInsts.Keys | Sort-Object { [int](($_ -split ',')[-1]) })) {
    if ($inst -match '_total$') { continue }
    $cores += [double]$utilInsts[$inst]
}

# GPU engines aggregated by engtype (Windows reports one instance per process/engine)
$engines = @{}
$engineInsts = Insts 'gpu engine' 'utilization percentage'
foreach ($inst in $engineInsts.Keys) {
    if ($inst -notmatch 'engtype_([^)]+)') { continue }
    $key = $Matches[1]
    $key = $key -replace '#[0-9]+$', ''
    $key = $key -replace '[ 0-9]+$', ''
    $key = $key.Trim().ToLower()
    if ($key.Length -eq 0) { continue }
    $cur = 0.0
    if ($engines.ContainsKey($key)) { $cur = [double]$engines[$key] }
    $sum = $cur + [double]$engineInsts[$inst]
    if ($sum -gt 100) { $sum = 100 }
    $engines[$key] = $sum
}
$gpuPct = $null
foreach ($v in $engines.Values) { if ($gpuPct -eq $null -or [double]$v -gt $gpuPct) { $gpuPct = [double]$v } }

$vramUsed = $null
$vramShared = $null
foreach ($v in (Insts 'gpu adapter memory' 'dedicated usage').Values) { if ($vramUsed -eq $null -or [double]$v -gt $vramUsed) { $vramUsed = [double]$v } }
foreach ($v in (Insts 'gpu adapter memory' 'shared usage').Values) { if ($vramShared -eq $null -or [double]$v -gt $vramShared) { $vramShared = [double]$v } }

# Disk / network
$diskIdle = G 'physicaldisk' '_total' '% idle time'
$diskActive = $null
if ($diskIdle -ne $null) { $diskActive = [Math]::Max(0, [Math]::Min(100, 100 - $diskIdle)) }

$rx = 0.0; $tx = 0.0
$bestNic = ''
$bestVal = -1.0
$rxInsts = Insts 'network interface' 'bytes received/sec'
$txInsts = Insts 'network interface' 'bytes sent/sec'
foreach ($inst in $rxInsts.Keys) {
    if ($inst -match 'loopback|isatap|teredo|pseudo|bluetooth|_total') { continue }
    $r = [double]$rxInsts[$inst]
    $t = 0.0
    if ($txInsts.ContainsKey($inst)) { $t = [double]$txInsts[$inst] }
    $rx += $r
    $tx += $t
    if (($r + $t) -gt $bestVal) { $bestVal = $r + $t; $bestNic = $inst }
}
# PDH appends _2/_3 to duplicate instance names
$bestNic = $bestNic -replace '_[0-9]+$', ''

# Thermal zones report Kelvin
$sysTemp = $null
foreach ($v in (Insts 'thermal zone information' 'temperature').Values) {
    if ($v -gt 250 -and $v -lt 400) {
        $cel = [double]$v - 273.15
        if ($sysTemp -eq $null -or $cel -gt $sysTemp) { $sysTemp = $cel }
    }
}

$diskFree = @{}
$freeInsts = Insts 'logicaldisk' 'free megabytes'
foreach ($inst in $freeInsts.Keys) {
    if ($inst -eq '_total') { continue }
    $diskFree[$inst] = [double]$freeInsts[$inst]
}

# ---------------- 3) Machine info (only when requested; CIM with registry fallback) --------------------
$hostName = $env:COMPUTERNAME
$cpuName = ''
$gpuName = ''
$osCaption = ''
$uptime = ''
$ramTotal = 0.0
$volumes = @()
if ($WantInfo) {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cp = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
    if ($cs -and $cs.TotalPhysicalMemory) { $ramTotal = [double]$cs.TotalPhysicalMemory }
    if ($cs -and $cs.Name) { $hostName = [string]$cs.Name }
    if ($os) {
        $osCaption = ([string]$os.Caption).Trim()
        if ($os.LastBootUpTime) {
            $up = (Get-Date) - $os.LastBootUpTime
            $uptime = "$($up.Days)d $($up.Hours.ToString('00')):$($up.Minutes.ToString('00'))"
        }
    }
    if ($cp) { $cpuName = ([string]$cp.Name).Trim() }
    $gn = @()
    foreach ($vc in @(Get-CimInstance -ClassName Win32_VideoController)) {
        if ($vc.Name -notmatch 'Remote Display|Basic Display|Basic Render') { $gn += ([string]$vc.Name).Trim() }
    }
    $gpuName = ($gn -join ' / ')
    foreach ($ld in @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3')) {
        $volumes += [pscustomobject]@{ d = [string]$ld.DeviceID; size = [double]$ld.Size; free = [double]$ld.FreeSpace }
    }
    if (-not $osCaption) {
        $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        if ($cv) { $osCaption = (([string]$cv.ProductName) + ' ' + ([string]$cv.DisplayVersion)).Trim() }
    }
    if (-not $cpuName) {
        $cp0 = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0'
        if ($cp0) { $cpuName = ([string]$cp0.ProcessorNameString).Trim() }
    }
    if (-not $gpuName) {
        $cls = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
        $list = @()
        foreach ($sub in @(Get-ChildItem $cls)) {
            if ($sub.PSChildName -notmatch '^[0-9]{4}$') { continue }
            $pr = Get-ItemProperty $sub.PSPath
            if ($pr -and $pr.DriverDesc -and $pr.DriverDesc -notmatch 'Remote Display|Basic Display|Basic Render') { $list += [string]$pr.DriverDesc }
        }
        $gpuName = ($list -join ' / ')
    }
}

# Volume capacities: total from machine info, free space from the live counter
$volOut = @()
foreach ($kv in $diskFree.GetEnumerator()) {
    $drive = $kv.Key.ToUpper()
    $size = 0.0
    $free = [double]$kv.Value * 1048576.0
    foreach ($v in $volumes) {
        if (([string]$v.d).ToLower() -eq $kv.Key) {
            $size = [double]$v.size
            if ($v.free -gt 0 -and $free -le 0) { $free = [double]$v.free }
        }
    }
    if ($size -le 0 -and $drive -notmatch '^[A-Z]:$') { continue }
    $volOut += [pscustomobject]@{ d = $drive; size = $size; free = $free }
}

$memAvailB = $null
$memAvail = G 'memory' '' 'available mbytes'
if ($memAvail -ne $null) { $memAvailB = [double]$memAvail * 1048576.0 }

$result = [ordered]@{
    host         = $hostName
    os           = $osCaption
    cpu          = $cpuName
    gpu          = $gpuName
    uptime       = $uptime
    ramTotal     = $ramTotal
    cpuPct       = $cpuPct
    cpuMhz       = $cpuMhz
    cores        = $cores
    memTotal     = $ramTotal
    memAvail     = $memAvailB
    memCommitted = (G 'memory' '' 'committed bytes')
    diskActive   = $diskActive
    diskRead     = (G 'physicaldisk' '_total' 'disk read bytes/sec')
    diskWrite    = (G 'physicaldisk' '_total' 'disk write bytes/sec')
    diskQueue    = (G 'physicaldisk' '_total' 'avg. disk queue length')
    netRx        = $rx
    netTx        = $tx
    netAdapter   = $bestNic
    gpuPct       = $gpuPct
    engines      = $engines
    vramUsed     = $vramUsed
    vramShared   = $vramShared
    gpuTemp      = $gpuTemp
    gpuFan       = $gpuFan
    gpuPower     = $gpuPower
    sysTemp      = $sysTemp
    volumes      = $volOut
    diag         = "ctr=$ctrLevel n=$(@($samples).Count) gpu=$diagGpu err=$ctrErr"
}
# Emit ASCII-only JSON: non-ASCII (e.g. localized OS names) becomes \uXXXX so that no
# code-page mangling can happen on the wire in either direction.
$json = $result | ConvertTo-Json -Depth 6 -Compress
$sb = New-Object System.Text.StringBuilder
foreach ($ch in $json.ToCharArray()) {
    $code = [int]$ch
    if ($code -lt 128) { [void]$sb.Append($ch) }
    elseif ($code -le 65535) { [void]$sb.AppendFormat('\u{0:x4}', $code) }
    else { [void]$sb.Append('?') }
}
# Write via Console.Out so PowerShell's output formatter cannot wrap the long line.
[Console]::Out.WriteLine($sb.ToString())
