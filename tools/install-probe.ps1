#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$MonitorAddress,
    [string]$HostAddress=$env:COMPUTERNAME,
    [ValidateRange(1,65535)][int]$Port=9187,
    [string]$SourceDirectory=(Split-Path $PSScriptRoot -Parent),
    [switch]$Upgrade
)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$ip=$null
if(![Net.IPAddress]::TryParse($MonitorAddress,[ref]$ip)){throw 'MonitorAddress must be one exact monitor IP address'}
$source=[IO.Path]::GetFullPath($SourceDirectory)
foreach($name in @('gpm-remote.exe','gpm-probe-tray.exe')){if(!(Test-Path -LiteralPath (Join-Path $source $name))){throw "Build first: missing $name"}}
$base=Join-Path $env:ProgramData 'GPUPCMGT'
$config=Join-Path $base 'config\probe-config.json'
$core=Join-Path $base 'bin\gpm-remote.exe'
$trayDir=Join-Path $env:LOCALAPPDATA 'GPUPCMGT'
$tray=Join-Path $trayDir 'gpm-probe-tray.exe'
$oldConfig=Join-Path $base 'probe-config.json'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$taskNames=@('GPUPCMGT-Probe','GPUPCMGT-ProbeTray')
$oldTasks=@{}
$oldFirewall=Get-NetFirewallRule -Name 'GPUPCMGT-Probe-TLS' -ErrorAction SilentlyContinue
$oldFirewallSpec=$null
if($oldFirewall){$oldFirewallSpec=@{Profile=$oldFirewall.Profile;LocalPort=($oldFirewall|Get-NetFirewallPortFilter).LocalPort;RemoteAddress=($oldFirewall|Get-NetFirewallAddressFilter).RemoteAddress;Program=($oldFirewall|Get-NetFirewallApplicationFilter).Program}}
foreach($name in $taskNames){
    $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if($task){
        if(!$Upgrade){throw 'Existing probe detected; use -Upgrade to replace this installation'}
        $expected=if($name -eq 'GPUPCMGT-ProbeTray'){@($tray)}else{@($core,(Join-Path $base 'gpm-remote.exe'))}
        if($task.Actions.Count -ne 1 -or $task.Actions[0].Execute -notin $expected){throw 'Existing task points outside the known probe installation; no changes made'}
        $oldTasks[$name]=Export-ScheduledTask -TaskName $name
    }
}
if(!$PSCmdlet.ShouldProcess($base,'Install or upgrade TLS probe, exact-IP firewall rule and startup tasks')){return}
# Load the built probe, not an assembly from an untrusted installed directory.
[void][Reflection.Assembly]::LoadFrom((Join-Path $source 'gpm-remote.exe'))
[Gpm.SafeInput]::Host($HostAddress)
function Set-DirectoryAcl([string]$Path,[switch]$UserWritable){
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    if($UserWritable){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity.User,'Modify','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Set-DirectoryAcl $base
Set-DirectoryAcl (Join-Path $base 'bin')
Set-DirectoryAcl (Join-Path $base 'config') -UserWritable
New-Item -ItemType Directory -Path $trayDir -Force | Out-Null
$backup=Join-Path $base 'previous-install'
Set-DirectoryAcl $backup
$files=@($core,$tray,$config)
$saved=@{}
for($i=0;$i -lt $files.Count;$i++){if(Test-Path -LiteralPath $files[$i]){$dest=Join-Path $backup ("file-$i.previous");Copy-Item -LiteralPath $files[$i] -Destination $dest -Force;$saved[$files[$i]]=$dest}}
foreach($name in $oldTasks.Keys){[Gpm.PrivateFile]::Write((Join-Path $backup ($name+'.xml')),$oldTasks[$name])}
$cfg=New-Object Gpm.ProbeConfig
$existing=if(Test-Path -LiteralPath $config){$config}elseif(Test-Path -LiteralPath $oldConfig){$oldConfig}else{$null}
if($existing){
    # Deserialize old schemas without enabling the old insecure listener.
    $json=Get-Content -LiteralPath $existing -Raw | ConvertFrom-Json
    foreach($name in @('TokenProtected','Interval','Idle')){if($null -ne $json.$name){$cfg.$name=$json.$name}}
}else{$cfg.SetToken([Gpm.ProbeConfig]::NewToken())}
$cert=$null
function Wait-Unlocked([string]$Path){
    if(!(Test-Path -LiteralPath $Path)){return}
    for($i=0;$i -lt 80;$i++){
        try{$file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$file.Dispose();return}catch{Start-Sleep -Milliseconds 250}
    }
    throw 'Probe executable remains locked; stopped installation before overwrite'
}
try{
    $cert=New-SelfSignedCertificate -Subject 'CN=GPUPCMGT-Probe' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
    $cfg.Host=$HostAddress;$cfg.Bind='0.0.0.0';$cfg.Port=$Port;$cfg.Allowed=$MonitorAddress;$cfg.CertificateThumbprint=$cert.Thumbprint;$cfg.AllowInsecureLan=$false
    $cfg.Validate()
    foreach($name in $oldTasks.Keys){Stop-ScheduledTask -TaskName $name}
    # Only stop processes from the verified installation paths.
    Get-Process gpm-remote,gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -in @($core,$tray,(Join-Path $base 'gpm-remote.exe'))} | Stop-Process
    Wait-Unlocked $core
    Wait-Unlocked $tray
    Copy-Item -LiteralPath (Join-Path $source 'gpm-remote.exe') -Destination $core -Force
    $coreAcl=New-Object Security.AccessControl.FileSecurity
    $coreAcl.SetAccessRuleProtection($true,$false)
    $coreAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$coreAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))}
    $coreAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','Allow'))
    Set-Acl -LiteralPath $core -AclObject $coreAcl
    Copy-Item -LiteralPath (Join-Path $source 'gpm-probe-tray.exe') -Destination $tray -Force
    & icacls $tray /setintegritylevel M | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Cannot set tray integrity'}
    $cfg.Save($config)
    $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    $action=New-ScheduledTaskAction -Execute $core -Argument ('--serve-config '+[Gpm.SafeInput]::Arg($config)) -WorkingDirectory (Split-Path $core)
    Register-ScheduledTask -TaskName 'GPUPCMGT-Probe' -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount) -Settings $settings -Force | Out-Null
    $action=New-ScheduledTaskAction -Execute $tray -WorkingDirectory $trayDir
    Register-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray' -Action $action -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $identity.Name) -Principal (New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited) -Settings $settings -Force | Out-Null
    if($oldFirewall){$oldFirewall|Remove-NetFirewallRule}
    New-NetFirewallRule -Name 'GPUPCMGT-Probe-TLS' -DisplayName 'GPU PC probe TLS (exact monitor IP)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -RemoteAddress $MonitorAddress -Program $core -Profile Private -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName 'GPUPCMGT-Probe'
    Start-ScheduledTask -TaskName 'GPUPCMGT-ProbeTray'
    Start-Sleep -Seconds 3
    $client=[Net.Sockets.TcpClient]::new('127.0.0.1',$Port);$client.ReceiveTimeout=3000;$client.SendTimeout=3000
    try{
        $stream=[Gpm.ProbeWire]::Client($client.GetStream(),'localhost',[Gpm.ProbeWire]::Fingerprint($cert))
        $bytes=[Text.Encoding]::ASCII.GetBytes($cfg.Token+"`n");$stream.Write($bytes,0,$bytes.Length)
        $reply=[Gpm.ProbeWire]::ReadLine($stream,65536,3000) | ConvertFrom-Json
        if(!$reply.ok -or $reply.error){throw 'Probe did not produce a valid TLS sample'}
    }finally{$client.Dispose()}
    [Gpm.PrivateFile]::Write((Join-Path $base 'installation.json'),(@{Core=$core;Tray=$tray;Config=$config;Certificate=$cert.Thumbprint;Version=3}|ConvertTo-Json))
    Write-Output 'TLS probe installed. Copy fresh credentials from probe tray settings to the monitor. No token was printed.'
}catch{
    foreach($name in $taskNames){Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue}
    Get-Process gpm-remote,gpm-probe-tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -in @($core,$tray)} | Stop-Process
    foreach($file in $saved.Keys){Wait-Unlocked $file;Copy-Item -LiteralPath $saved[$file] -Destination $file -Force}
    if(!$saved.ContainsKey($config) -and (Test-Path -LiteralPath $config)){Remove-Item -LiteralPath $config}
    Get-NetFirewallRule -Name 'GPUPCMGT-Probe-TLS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if($oldFirewallSpec){New-NetFirewallRule -Name 'GPUPCMGT-Probe-TLS' -DisplayName 'GPU PC probe TLS (restored)' -Direction Inbound -Action Allow -Protocol TCP @oldFirewallSpec | Out-Null}
    foreach($name in $taskNames){if($oldTasks.ContainsKey($name)){Register-ScheduledTask -TaskName $name -Xml $oldTasks[$name] -Force | Out-Null;Start-ScheduledTask -TaskName $name}else{Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue}}
    if($cert){Remove-Item -LiteralPath ('Cert:\LocalMachine\My\'+$cert.Thumbprint)}
    throw
}
