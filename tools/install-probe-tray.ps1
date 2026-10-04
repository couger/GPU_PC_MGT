$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$dir = Join-Path $env:LOCALAPPDATA 'GPUPCMGT'
$coreDir = 'C:\ProgramData\GPUPCMGT'
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$core = Join-Path $coreDir 'gpm-remote.exe'
$backup = Join-Path $coreDir ('gpm-remote.pre-tray-' + $stamp + '.exe')
$task = Get-ScheduledTask -TaskName 'GPUPCMGT-Probe'
$arguments = $task.Actions[0].Arguments
if ($arguments -match '--serve-config\s+"?([^"\r\n]+)"?') {
    $shared=Get-Content -LiteralPath $Matches[1].Trim('"',' ') -Raw | ConvertFrom-Json
    Add-Type -AssemblyName System.Security
    $port=[int]$shared.Port
    $token=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($shared.TokenProtected),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
} else {
if ($arguments -notmatch '--serve\s+(\d+)\s+(\S+)') { throw 'Probe task arguments are not recognized.' }
$port = [int]$Matches[1]
$token = $Matches[2].Trim('"')
}
Add-Type -AssemblyName System.Security
$protected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($token), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
$config = Join-Path $dir 'probe-tray.json'
if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination ($config + '.backup-' + $stamp) }
@{ port = $port; tokenProtected = $protected } | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding UTF8
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$account = $identity.Name
& icacls $config /inheritance:r /grant:r ('*' + $identity.User.Value + ':(F)') '*S-1-5-18:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect tray settings.' }
$tray = Join-Path $dir 'gpm-probe-tray.exe'
if (Get-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray' -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray' }
Get-Process gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $tray } | Stop-Process
if (Test-Path -LiteralPath $tray) { Copy-Item -LiteralPath $tray -Destination ($tray + '.backup-' + $stamp) }
if (Test-Path -LiteralPath $tray) {
    $released = $false
    for ($i = 0; $i -lt 60; $i++) {
        try { $file = [IO.File]::Open($tray, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $file.Dispose(); $released = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $released) { Start-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'; throw 'Tray executable was not released.' }
}
Copy-Item -LiteralPath (Join-Path $dir 'gpm-probe-tray.new.exe') -Destination $tray -Force
& icacls $tray /setintegritylevel M | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set tray executable integrity.' }
if ((Get-FileHash -LiteralPath $core).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dir 'gpm-remote.new.exe')).Hash) {
Copy-Item -LiteralPath $core -Destination $backup
Stop-ScheduledTask -TaskName 'GPUPCMGT-Probe'
try {
    $released = $false
    for ($i = 0; $i -lt 60; $i++) {
        try { $file = [IO.File]::Open($core, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $file.Dispose(); $released = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $released) { throw 'Probe executable was not released after stopping its task.' }
    Copy-Item -LiteralPath (Join-Path $dir 'gpm-remote.new.exe') -Destination $core -Force
    Start-ScheduledTask -TaskName 'GPUPCMGT-Probe'
    Start-Sleep -Seconds 3
    if ((Get-ScheduledTask -TaskName 'GPUPCMGT-Probe').State -ne 'Running') { throw 'Updated probe did not start.' }
} catch {
    try { Copy-Item -LiteralPath $backup -Destination $core -Force }
    finally { Start-ScheduledTask -TaskName 'GPUPCMGT-Probe' }
    throw
}
} else { $backup = '(unchanged)' }
$action = New-ScheduledTaskAction -Execute $tray -WorkingDirectory $dir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Visible status icon for the existing GPU PC probe; does not collect data or stop the probe.' -Force | Out-Null
Start-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
Start-Sleep -Seconds 4
Write-Output ('CORE_BACKUP=' + $backup)
Get-ScheduledTask -TaskName 'GPUPCMGT-Probe*' | ForEach-Object { Write-Output ('TASK=' + $_.TaskName + ' STATE=' + $_.State + ' LOGON=' + $_.Principal.LogonType) }
Get-Process gpm-remote,gpm-probe-tray -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,SessionId | ConvertTo-Json -Compress
