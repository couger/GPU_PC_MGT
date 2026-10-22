#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]param()
$ErrorActionPreference='Stop'
$base=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'GPUPCMGT'))
$tray=Join-Path $env:LOCALAPPDATA 'GPUPCMGT\gpm-probe-tray.exe'
foreach($name in @('GPUPCMGT-Probe','GPUPCMGT-ProbeTray')){
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if(!$task){continue}
    $expected=if($name -eq 'GPUPCMGT-ProbeTray'){@($tray)}else{@((Join-Path $base 'bin\gpm-remote.exe'),(Join-Path $base 'gpm-remote.exe'))}
    if($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -notin $expected){throw 'Task uses an unexpected executable; refusing to remove it'}
}
if(!$PSCmdlet.ShouldProcess($base,'Stop and remove probe tasks/firewall; retain configuration, certificate and one rollback copy')){return}
foreach($name in @('GPUPCMGT-Probe','GPUPCMGT-ProbeTray')){
    if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Stop-ScheduledTask -TaskName $name;Unregister-ScheduledTask -TaskName $name -Confirm:$false}
}
Get-Process gpm-remote,gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -in @($tray,(Join-Path $base 'bin\gpm-remote.exe'),(Join-Path $base 'gpm-remote.exe'))} | Stop-Process
Get-NetFirewallRule -Name 'GPUPCMGT-Probe-TLS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Output 'Probe disabled and startup/firewall rules removed. Configuration, certificate and rollback files retained. Rotate the token before reusing or sharing this device.'
