param([string]$LocalConfigPath)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
if(!$LocalConfigPath){$LocalConfigPath=Join-Path $root 'config.json'}
$known=@()
if(Test-Path -LiteralPath $LocalConfigPath){
    $config=Get-Content -LiteralPath $LocalConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($target in $config.targets){
        foreach($field in @('password','passwordProtected','token','host','user')){
            $value=[string]$target.$field
            if($value.Length -ge 5){$known+=,$value}
        }
        if($target.kind -ne 'local' -and $target.name){$known+=,[string]$target.name}
    }
}
$files=@(& git ls-files --cached)
if($LASTEXITCODE -ne 0){throw 'Cannot read Git index'}
if($files.Count -eq 0){throw 'Git index is empty'}
$failed=@()
foreach($file in $files){
    if($file -match '(^|/)(\.tmp|data|ssh)/|(^|/)(config|widget|probe-config|probe-tray)\.json$|\.(exe|dll|pdb|log|pem|key|pfx|p12|bak)$|^web/data\.json$'){
        $failed+=,$file;continue
    }
    if($file -match '\.(ico|png)$'){continue}
    # Read the staged blob, not the working copy.
    $blob=(& git show (':'+$file)) -join "`n"
    if($LASTEXITCODE -ne 0){throw ('Cannot inspect staged file: '+$file)}
    $patterns=@(
        '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
        '\bgh[pousr]_[A-Za-z0-9]{20,}\b|github_pat_[A-Za-z0-9_]{20,}',
        '\bAKIA[A-Z0-9]{16}\b',
        'AQAAANCMnd8BFdER[A-Za-z0-9+/=]{30,}',
        '\b192\.168\.\d{1,3}\.\d{1,3}\b',
        '\b10\.\d{1,3}\.\d{1,3}\.\d{1,3}\b',
        '\b172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}\b',
        '[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s"'']+'
    )
    $bad=$false
    foreach($pattern in $patterns){if($blob -match $pattern){$bad=$true;break}}
    foreach($value in $known){if($blob.Contains($value)){$bad=$true;break}}
    if($bad){$failed+=,$file}
}
if($failed.Count){$failed | Sort-Object -Unique | ForEach-Object {Write-Output ('REVIEW: '+$_)};throw 'Sensitive-data check failed; matched values are not printed'}
Write-Output ('PASS: inspected '+$files.Count+' staged files; no known local credentials or blocked data found')
