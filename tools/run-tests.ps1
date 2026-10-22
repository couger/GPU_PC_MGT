param([switch]$SkipDesktop)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
if(!(Test-Path -LiteralPath (Join-Path $root 'GPUPCMonitor.exe'))){throw 'Run build.cmd first'}
$output=Join-Path $root '.tmp\tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'GPUPCMonitor.exe') -Destination $output -Force
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$env:TEMP=Join-Path $root '.tmp';$env:TMP=$env:TEMP
foreach($name in @('fault-history-test','fault-sampler-test','runtime-log-test','probe-config-test','widget-test','alerts-test')){
    if($SkipDesktop -and $name -eq 'widget-test'){continue}
    $exe=Join-Path $output ($name+'.exe')
    & $csc /nologo /platform:x64 /r:GPUPCMonitor.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Windows.Forms.DataVisualization.dll ('/out:'+$exe) (Join-Path $PSScriptRoot ($name+'.cs'))
    if($LASTEXITCODE -ne 0){throw ('Compile failed: '+$name)}
    '<configuration><runtime><loadFromRemoteSources enabled="true" /></runtime></configuration>' | Set-Content -LiteralPath ($exe+'.config') -Encoding UTF8
    & icacls $exe /setintegritylevel M | Out-Null
    if($LASTEXITCODE -ne 0){throw ('Integrity label failed: '+$name)}
    # The sampler/log/widget tests take a root path; screenshots use their output folder.
    $arg=if($name -in @('fault-sampler-test','runtime-log-test','widget-test','alerts-test')){$root}else{$output}
    if($SkipDesktop -and $name -eq 'fault-history-test'){& $exe $arg --headless}else{& $exe $arg}
    if($LASTEXITCODE -ne 0){throw ('Test failed: '+$name)}
}
if(!$SkipDesktop){& (Join-Path $PSScriptRoot 'run-probe-power-test.ps1')}
& (Join-Path $PSScriptRoot 'run-protocol-tests.ps1')
& (Join-Path $PSScriptRoot 'install-script-test.ps1')
Write-Output 'PASS: all regression checks completed'
