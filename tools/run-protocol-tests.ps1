param([switch]$SkipTls)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
$output=Join-Path $root '.tmp\protocol-tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item GPUPCMonitor.exe,gpm-remote.exe -Destination $output -Force
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($name in @('security-test','probe-protocol-test','probe-boundary-test')){
    $assembly=if($name -eq 'security-test'){'GPUPCMonitor.exe'}else{'gpm-remote.exe'}
    $exe=Join-Path $output ($name+'.exe')
    & $csc /nologo /platform:x64 ('/r:'+$assembly) /r:System.Web.Extensions.dll /r:System.Security.dll ('/out:'+$exe) (Join-Path $PSScriptRoot ($name+'.cs'))
    if($LASTEXITCODE -ne 0){throw "Compile failed: $name"}
    '<configuration><runtime><loadFromRemoteSources enabled="true" /></runtime></configuration>' | Set-Content -LiteralPath ($exe+'.config') -Encoding UTF8
    & icacls $exe /setintegritylevel M | Out-Null
    & $exe $root
    if($LASTEXITCODE -ne 0){throw "Test failed: $name"}
}
if(!$SkipTls){
    # User store and unique generated certificate; never export a private key or change machine trust.
    $cert=New-SelfSignedCertificate -Subject ('CN=gpm-test-'+[Guid]::NewGuid().ToString('N')) -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddDays(1) -KeyExportPolicy NonExportable
    try{
        & (Join-Path $output 'probe-boundary-test.exe') $root $cert.Thumbprint
        if($LASTEXITCODE -ne 0){throw 'TLS boundary tests failed'}
    }finally{Remove-Item -LiteralPath ('Cert:\CurrentUser\My\'+$cert.Thumbprint)}
    $expired=New-SelfSignedCertificate -Subject ('CN=gpm-expired-test-'+[Guid]::NewGuid().ToString('N')) -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 2048 -NotBefore (Get-Date).AddDays(-2) -NotAfter (Get-Date).AddDays(-1) -KeyExportPolicy NonExportable
    try{
        & (Join-Path $output 'probe-boundary-test.exe') $root $expired.Thumbprint --expired
        if($LASTEXITCODE -ne 0){throw 'Expired TLS certificate test failed'}
    }finally{Remove-Item -LiteralPath ('Cert:\CurrentUser\My\'+$expired.Thumbprint)}
}
Write-Output 'PASS protocol and security checks completed'
