$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
$output=Join-Path $root '.tmp\probe-power-tests';New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath gpm-probe-tray.exe -Destination $output -Force
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$env:TEMP=Join-Path $root '.tmp';$env:TMP=$env:TEMP
& $csc /nologo /platform:x64 /r:gpm-probe-tray.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ('/out:'+(Join-Path $output 'probe-power-test.exe')) tools\probe-power-test.cs
if($LASTEXITCODE -ne 0){throw 'Probe power test compile failed'}
$exe=Join-Path $output 'probe-power-test.exe'
'<configuration><runtime><loadFromRemoteSources enabled="true" /></runtime></configuration>' | Set-Content -LiteralPath ($exe+'.config') -Encoding UTF8
& icacls $exe /setintegritylevel M | Out-Null
& $exe $output
if($LASTEXITCODE -ne 0){throw 'Probe power tests failed'}
