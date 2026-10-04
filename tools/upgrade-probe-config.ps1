$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$dir=Join-Path $env:LOCALAPPDATA 'GPUPCMGT'
$coreDir=Join-Path $env:ProgramData 'GPUPCMGT'
$config=Join-Path $coreDir 'probe-config.json'
$task=Get-ScheduledTask -TaskName 'GPUPCMGT-Probe'
$oldArguments=$task.Actions[0].Arguments
$stamp=Get-Date -Format yyyyMMdd-HHmmss
Add-Type -AssemblyName System.Security
if (-not (Test-Path -LiteralPath $config)) {
    if ($oldArguments -notmatch '--serve\s+(\d+)\s+(\S+)(?:\s+(\d+))?(?:\s+(\d+))?(?:\s+(\S+))?') {throw 'Unsupported probe arguments'}
    $port=[int]$Matches[1]; $token=$Matches[2].Trim('"'); $interval=2; $idle=10; $bind='0.0.0.0'
    if ($Matches[3]) {$interval=[int]$Matches[3]}; if ($Matches[4]) {$idle=[int]$Matches[4]}; if ($Matches[5]) {$bind=$Matches[5]}
    $connection=$env:SSH_CONNECTION -split ' '
    if ($connection.Count -ne 4) {throw 'Cannot identify existing monitor address; configuration migration stopped'}
    $protected=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($token),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
    @{Port=$port;Bind=$bind;Interval=$interval;Idle=$idle;Allowed=$connection[0];TokenProtected=$protected;Host=$connection[2]} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding UTF8
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
& icacls $config /inheritance:r /grant:r ('*'+$identity.User.Value+':(F)') '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) {throw 'Cannot protect configuration'}
$core=Join-Path $coreDir 'gpm-remote.exe'
$tray=Join-Path $dir 'gpm-probe-tray.exe'
$coreBackup=$core+'.config-backup-'+$stamp
$trayBackup=$tray+'.config-backup-'+$stamp
Copy-Item -LiteralPath $core -Destination $coreBackup
Copy-Item -LiteralPath $tray -Destination $trayBackup
function Replace-StoppedFile($path,$source) {
    for($i=0;$i -lt 80;$i++) {
        try {$f=[IO.File]::Open($path,'Open','ReadWrite','None');$f.Dispose();Copy-Item -LiteralPath $source -Destination $path -Force;return}
        catch {Start-Sleep -Milliseconds 250}
    }
    throw 'Executable did not release'
}
Stop-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
Stop-ScheduledTask -TaskName 'GPUPCMGT-Probe'
try {
    Replace-StoppedFile $core (Join-Path $dir 'gpm-remote.new.exe')
    Replace-StoppedFile $tray (Join-Path $dir 'gpm-probe-tray.new.exe')
    & icacls $tray /setintegritylevel M | Out-Null
    if ($LASTEXITCODE -ne 0) {throw 'Cannot set tray integrity'}
    $action=New-ScheduledTaskAction -Execute $core -Argument ('--serve-config "'+$config+'"') -WorkingDirectory $coreDir
    Set-ScheduledTask -TaskName 'GPUPCMGT-Probe' -Action $action | Out-Null
    Start-ScheduledTask -TaskName 'GPUPCMGT-Probe'
    Start-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
    Start-Sleep -Seconds 5
    $cfg=Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $token=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($cfg.TokenProtected),$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
    $c=[Net.Sockets.TcpClient]::new();$c.Connect('127.0.0.1',$cfg.Port);$c.ReceiveTimeout=3000
    $ns=$c.GetStream();$bytes=[Text.Encoding]::ASCII.GetBytes($token+"`n");$ns.Write($bytes,0,$bytes.Length)
    $reader=[IO.StreamReader]::new($ns);$sample=$reader.ReadLine() | ConvertFrom-Json;$c.Dispose()
    if($sample.error) {throw 'Probe health check failed'}
    @{upgraded=$true;allowed=$cfg.Allowed;port=$cfg.Port;coreState=(Get-ScheduledTask 'GPUPCMGT-Probe').State.ToString();trayState=(Get-ScheduledTask 'GPUPCMGT-ProbeTray').State.ToString();backup=$coreBackup} | ConvertTo-Json -Compress
} catch {
    Stop-ScheduledTask -TaskName 'GPUPCMGT-Probe';Stop-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
    Replace-StoppedFile $core $coreBackup;Replace-StoppedFile $tray $trayBackup
    Set-ScheduledTask -TaskName 'GPUPCMGT-Probe' -Action (New-ScheduledTaskAction -Execute $core -Argument $oldArguments -WorkingDirectory $coreDir) | Out-Null
    Start-ScheduledTask -TaskName 'GPUPCMGT-Probe';Start-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
    throw
}
