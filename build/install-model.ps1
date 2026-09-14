<#
.SYNOPSIS
    Installs a high-band model into the FUPlayer models folder.

.DESCRIPTION
    Copies a local file, or downloads one over https, into %APPDATA%\FUPlayer\models. The file is
    checked before it is installed: it has to parse as JSON, carry the version this build reads, and
    hold exactly the number of weights its own band layout implies. Anything else is refused and
    nothing is written.

    No model ships with FUPlayer. Train one from your own lossless music with
    "fuplayer-cli train", or use this to install one somebody else fitted.

.PARAMETER Source
    A path to a .fumodel.json file, or an https URL to download one from.

.PARAMETER Name
    File name to install as, without the extension. Defaults to the source's own name.

.PARAMETER Force
    Overwrite a model that is already installed under that name.

.EXAMPLE
    pwsh build/install-model.ps1 -Source D:\downloads\aac-20k.fumodel.json

.EXAMPLE
    pwsh build/install-model.ps1 -Source https://example.com/aac-20k.fumodel.json -Name aac-20k
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [string]$Name,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$extension = ".fumodel.json"
$folder = Join-Path $env:APPDATA "FUPlayer\models"
$readerVersion = 1

if (-not $Name) {
    $leaf = Split-Path -Leaf ($Source -split '\?')[0]
    $Name = $leaf -replace [regex]::Escape($extension), '' -replace '\.json$', ''
}

if ($Name -match '[\\/:*?"<>|]') {
    throw "The name '$Name' cannot be used as a file name."
}

# Fetch into a temporary file first, so a bad download never lands in the folder.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("fuplayer-model-" + [guid]::NewGuid().ToString("N") + ".json")

try {
    if ($Source -match '^https://') {
        Write-Host "Downloading $Source"
        Invoke-WebRequest -Uri $Source -OutFile $staging -MaximumRedirection 3
    }
    elseif ($Source -match '^http://') {
        throw "Refusing to download over plain http. Use https, or download it yourself and pass the file."
    }
    else {
        if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
            throw "No file at '$Source'."
        }

        Copy-Item -LiteralPath $Source -Destination $staging
    }

    $size = (Get-Item $staging).Length
    if ($size -gt 8MB) {
        throw "That file is $([math]::Round($size / 1MB, 1)) MB. A model is a few hundred numbers, so this is not one."
    }

    try {
        $model = Get-Content -LiteralPath $staging -Raw | ConvertFrom-Json
    }
    catch {
        throw "That file is not JSON, so it is not a model."
    }

    foreach ($field in @("Version", "SampleRate", "CutoffHz", "LowHz", "TopHz", "InputBands", "OutputBands", "Weights")) {
        if ($null -eq $model.$field) {
            throw "The file has no '$field', so it is not a model this build can read."
        }
    }

    if ($model.Version -ne $readerVersion) {
        throw "The model is version $($model.Version); this build reads version $readerVersion."
    }

    $expected = ($model.InputBands + 1) * $model.OutputBands
    if ($model.Weights.Count -ne $expected) {
        throw "The model should carry $expected weights for $($model.InputBands) by $($model.OutputBands) bands, but carries $($model.Weights.Count)."
    }

    $destination = Join-Path $folder ($Name + $extension)
    if ((Test-Path -LiteralPath $destination) -and -not $Force) {
        throw "$destination already exists. Pass -Force to replace it."
    }

    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Move-Item -LiteralPath $staging -Destination $destination -Force

    Write-Host "Installed $destination"
    Write-Host ("  cutoff {0:0.#} kHz, predicts to {1:0.#} kHz, {2} bands in and {3} out, fitted at {4} Hz" -f `
        ($model.CutoffHz / 1000), ($model.TopHz / 1000), $model.InputBands, $model.OutputBands, $model.SampleRate)
    Write-Host "  Turn on Generative prediction in the DSP studio to use it."
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
    }
}
