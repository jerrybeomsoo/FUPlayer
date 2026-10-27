<# Publishes the Forms controller, listener, workers and models as one portable Windows executable. #>
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [string]$ArtifactsDirectory,
    [string]$Version = '0.7.2'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'artifacts\dsp-server-windows\win-x64' }
if (-not $ArtifactsDirectory) { $ArtifactsDirectory = Join-Path $taskRoot 'artifacts\dsp-server-windows-build' }
& dotnet publish (Join-Path $taskRoot 'src\FUPlayer.DspServer.Windows\FUPlayer.DspServer.Windows.csproj') `
    -c Release -r $Runtime --self-contained true --artifacts-path $ArtifactsDirectory `
    -o $OutputDirectory --nologo -p:Version=$Version -p:DebugType=embedded -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Windows DSP controller publish failed.' }
$taskBinary = Join-Path $OutputDirectory 'FUPlayer-DspServer-Windows.exe'
if (-not (Test-Path -LiteralPath $taskBinary -PathType Leaf)) { throw 'The portable controller was not produced.' }
$taskExtras = @(Get-ChildItem -LiteralPath $OutputDirectory | Where-Object Name -ne 'FUPlayer-DspServer-Windows.exe')
if ($taskExtras.Count) { throw 'Unexpected sidecar files in the controller output. Inspect before distributing.' }
Get-Item -LiteralPath $taskBinary | Select-Object FullName,Length
Get-FileHash -LiteralPath $taskBinary -Algorithm SHA256
