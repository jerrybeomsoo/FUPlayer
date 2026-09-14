<#
.SYNOPSIS
    Builds the Windows release package: the player, the command line tool and the
    licence files, as one zip ready to attach to a GitHub release.

.PARAMETER Version
    Version string used in the file name, without a leading "v".

.PARAMETER Runtime
    .NET runtime identifier to publish for. Only win-x64 is tested.

.PARAMETER FrameworkDependent
    Publish against an installed .NET runtime instead of bundling it. About a
    third of the size, but the machine then needs the .NET 9 Desktop Runtime.

.EXAMPLE
    pwsh build/package.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "publish\FUPlayer"
$artifacts = Join-Path $root "artifacts"
$zip = Join-Path $artifacts "FUPlayer-$Version-$Runtime.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

foreach ($project in @("src\FUPlayer.App\FUPlayer.App.csproj", "src\FUPlayer.Cli\FUPlayer.Cli.csproj")) {
    Write-Host "publishing $project"
    dotnet publish (Join-Path $root $project) `
        --configuration Release `
        --runtime $Runtime `
        --self-contained $selfContained `
        -p:DebugType=none `
        --output $stage `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
}

# SkiaSharp and HarfBuzzSharp ship native debug symbols worth about 100 MB, which
# nobody needs in order to run the player.
Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force

foreach ($file in @("LICENSE", "THIRD-PARTY-NOTICES.md", "README.md")) {
    Copy-Item (Join-Path $root $file) $stage
}
Copy-Item (Join-Path $root "licenses") (Join-Path $stage "licenses") -Recurse

Compress-Archive -Path $stage -DestinationPath $zip

$size = (Get-Item $zip).Length / 1MB
Write-Host ("packaged {0} ({1:N1} MB)" -f $zip, $size)
