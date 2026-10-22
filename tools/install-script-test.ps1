$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-probe.ps1') -Raw
# Exercise planning in an isolated scope with read-only mocks, never bypass elevation for a real install.
$plan=[scriptblock]::Create(($source -replace '(?m)^#Requires -RunAsAdministrator\s*',''))
& {
    param($Plan,$Root)
    function Get-ScheduledTask {param($TaskName,$ErrorAction) return $null}
    function Get-NetFirewallRule {param($Name,$ErrorAction) return $null}
    function New-Item {throw 'Installation planning attempted a mutation'}
    & $Plan -MonitorAddress 192.0.2.20 -HostAddress 192.0.2.10 -SourceDirectory $Root -WhatIf
    Write-Output 'PASS installer WhatIf performs no mutation'
    $rejected=$false
    try{& $Plan -MonitorAddress 'host -oProxyCommand=evil' -SourceDirectory $Root -WhatIf}catch{$rejected=$true}
    if(!$rejected){throw 'Invalid monitor address accepted'}
    Write-Output 'PASS installer rejects invalid monitor before changes'
    function Get-ScheduledTask {param($TaskName,$ErrorAction) return [pscustomobject]@{Actions=@([pscustomobject]@{Execute='C:\unexpected\other.exe'})}}
    $rejected=$false
    try{& $Plan -MonitorAddress 192.0.2.20 -SourceDirectory $Root -Upgrade -WhatIf}catch{$rejected=$true}
    if(!$rejected){throw 'Unexpected task accepted'}
    Write-Output 'PASS installer refuses to take over unrelated task'
} $plan $root
