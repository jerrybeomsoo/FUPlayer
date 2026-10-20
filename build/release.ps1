<# Builds the player, standalone servers and checksums used by the GitHub release. #>
[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$')][string]$Version = '0.7.0')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskRoot "artifacts\release-$Version"
New-Item -ItemType Directory -Path $taskAssets -Force | Out-Null

& (Join-Path $PSScriptRoot 'package.ps1') -Version $Version
Copy-Item -LiteralPath (Join-Path $taskRoot "artifacts\FUPlayer-$Version-win-x64.zip") -Destination $taskAssets
foreach ($taskRuntime in @('win-x64', 'linux-x64')) {
    & (Join-Path $PSScriptRoot 'publish-dsp-server.ps1') -Runtime $taskRuntime -Version $Version
    $taskExtension = if ($taskRuntime.StartsWith('win-')) { '.exe' } else { '' }
    Copy-Item -LiteralPath (Join-Path $taskRoot "artifacts\dsp-server\$taskRuntime\FUPlayer-DspServer$taskExtension") `
        -Destination (Join-Path $taskAssets "FUPlayer-DspServer-$Version-$taskRuntime$taskExtension")
}
# Bash downloads must retain LF endings even when the checkout uses CRLF.
$taskInstaller = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'install-dsp-server.sh')).Replace("`r`n", "`n")
[System.IO.File]::WriteAllText((Join-Path $taskAssets 'install-dsp-server.sh'), $taskInstaller, [System.Text.UTF8Encoding]::new($false))
$taskNames = @("FUPlayer-$Version-win-x64.zip", "FUPlayer-DspServer-$Version-win-x64.exe", "FUPlayer-DspServer-$Version-linux-x64", 'install-dsp-server.sh')
$taskChecksums = foreach ($taskName in $taskNames) {
    $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskAssets $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$taskHash  $taskName"
}
[System.IO.File]::WriteAllText((Join-Path $taskAssets 'SHA256SUMS.txt'), ($taskChecksums -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath $taskAssets | Select-Object Name,Length
