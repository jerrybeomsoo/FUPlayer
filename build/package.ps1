<#
.SYNOPSIS
    Builds the Windows release package: the player, the command line tool, the two
    neural models and the licence files, as one zip ready to attach to a GitHub release.

.PARAMETER Version
    Version string used in the file name, without a leading "v".

.PARAMETER Runtime
    .NET runtime identifier to publish for. Only win-x64 is tested.

.PARAMETER FrameworkDependent
    Publish against an installed .NET runtime instead of bundling it. About a
    third of the size, but the machine then needs the .NET 9 Desktop Runtime.

.PARAMETER IncludeFFmpeg
    Put the locally built FFmpeg libraries in the package, so MP3, AAC, Ogg and
    the rest play without the user building anything. They are left out by
    default: they are LGPL, and shipping them means shipping their license text
    and either the matching source or a written offer for it. The switch adds
    the license text and a note saying where the source is; the offer is yours
    to keep.

.EXAMPLE
    pwsh build/package.ps1 -Version 0.2.1
#>
[CmdletBinding()]
param(
    [string]$Version = "0.2.1",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [switch]$IncludeFFmpeg
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

$skipFFmpeg = if ($IncludeFFmpeg) { "false" } else { "true" }

foreach ($project in @("src\FUPlayer.App\FUPlayer.App.csproj", "src\FUPlayer.Cli\FUPlayer.Cli.csproj")) {
    Write-Host "publishing $project"
    dotnet publish (Join-Path $root $project) `
        --configuration Release `
        --runtime $Runtime `
        --self-contained $selfContained `
        -p:DebugType=none `
        -p:FuPlayerSkipFFmpegCopy=$skipFFmpeg `
        --output $stage `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
}

# SkiaSharp and HarfBuzzSharp ship native debug symbols worth about 100 MB, which
# nobody needs in order to run the player; ONNX Runtime ships import libraries for
# linking against it, which nobody needs either.
Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force
Get-ChildItem $stage -Filter *.lib | Remove-Item -Force

# The neural restorer and upscaler, beside the executables, which is the first
# place the player looks for models.
$models = Get-ChildItem (Join-Path $root "models\*") -Include *.onnx, *.json -File
if (-not ($models | Where-Object Name -like "*restorer.onnx") -or -not ($models | Where-Object { $_.Name -like "*.onnx" -and $_.Name -notlike "*restorer.onnx" })) {
    throw "models/ must hold a restorer (*restorer.onnx) and an upscaler (*.onnx), each with its .json"
}
New-Item -ItemType Directory -Path (Join-Path $stage "models") -Force | Out-Null
$models | Copy-Item -Destination (Join-Path $stage "models")
Write-Host ("including models: {0}" -f ($models.Name -join ", "))

foreach ($file in @("LICENSE", "THIRD-PARTY-NOTICES.md", "README.md")) {
    Copy-Item (Join-Path $root $file) $stage
}
Copy-Item (Join-Path $root "licenses") (Join-Path $stage "licenses") -Recurse

if ($IncludeFFmpeg) {
    $libraries = Get-ChildItem (Join-Path $stage "ffmpeg") -Filter *.dll -ErrorAction SilentlyContinue
    if (-not $libraries) { throw "-IncludeFFmpeg was given but no libraries were published. Build them first: pwsh build/ffmpeg/build-ffmpeg.ps1" }

    $copying = Join-Path $root "ffmpeg-9.0.1\COPYING.LGPLv2.1"
    if (Test-Path $copying) { Copy-Item $copying (Join-Path $stage "licenses\FFmpeg-COPYING.LGPLv2.1") }

    @"
The ffmpeg folder holds FFmpeg 9.0.1's libavformat, libavcodec, libavutil and libswresample,
built from unmodified FFmpeg source with the LGPL v2.1 configuration in
build/ffmpeg/build-ffmpeg-lgpl.sh (no --enable-gpl, no --enable-nonfree, no --enable-version3).

They are covered by the GNU Lesser General Public License version 2.1 or later, whose text is in
licenses/FFmpeg-COPYING.LGPLv2.1. Replace them with your own build of the same major versions
(avformat 63, avcodec 63, avutil 61, swresample 7) at any time; FUPlayer loads whatever is there.

The corresponding source is FFmpeg 9.0.1 as published at https://ffmpeg.org/download.html,
with the configuration named above.
"@ | Set-Content (Join-Path $stage "ffmpeg\README.txt") -Encoding utf8

    Write-Host ("including FFmpeg: {0}" -f ($libraries.Name -join ", "))
}

Compress-Archive -Path $stage -DestinationPath $zip

$size = (Get-Item $zip).Length / 1MB
Write-Host ("packaged {0} ({1:N1} MB)" -f $zip, $size)
