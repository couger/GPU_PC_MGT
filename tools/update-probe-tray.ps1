param([string]$NewExecutablePath)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$dir=Join-Path $env:LOCALAPPDATA 'GPUPCMGT'
$tray=Join-Path $dir 'gpm-probe-tray.exe'
if(!$NewExecutablePath){$NewExecutablePath=Join-Path $dir 'gpm-probe-tray.new.exe'}
$task=Get-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
if($task.Actions[0].Execute -ne $tray){throw 'Existing tray task does not use the expected per-user executable'}
if(!(Test-Path -LiteralPath $NewExecutablePath)){throw 'New tray executable missing'}
$expected=(Get-FileHash -LiteralPath $NewExecutablePath).Hash
$backup=$tray+'.previous.exe'
Copy-Item -LiteralPath $tray -Destination $backup -Force
Stop-ScheduledTask -TaskName $task.TaskName
Get-Process gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $tray} | Stop-Process
try {
    $released=$false
    for($i=0;$i -lt 40;$i++){
        try{$file=[IO.File]::Open($tray,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$file.Dispose();$released=$true;break}catch{Start-Sleep -Milliseconds 250}
    }
    if(!$released){throw 'Tray executable still in use'}
    Copy-Item -LiteralPath $NewExecutablePath -Destination $tray -Force
    & icacls $tray /setintegritylevel M | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Cannot set tray integrity'}
    Start-ScheduledTask -TaskName $task.TaskName
    Start-Sleep -Seconds 5
    $process=Get-Process gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $tray -and $_.SessionId -gt 0}
    if(!$process -or (Get-FileHash -LiteralPath $tray).Hash -ne $expected){throw 'Updated tray did not start in an interactive session'}
    [pscustomobject]@{Updated=$true;Hash=$expected;TrayPid=$process.Id;Session=$process.SessionId;Window=$process.MainWindowTitle;ProbeState=[string](Get-ScheduledTask -TaskName 'GPUPCMGT-Probe').State}|ConvertTo-Json -Compress
}catch{
    Stop-ScheduledTask -TaskName $task.TaskName
    Get-Process gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $tray} | Stop-Process
    Copy-Item -LiteralPath $backup -Destination $tray -Force
    Start-ScheduledTask -TaskName $task.TaskName
    throw
}
