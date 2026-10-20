param(
    [ValidateSet('win-x64','linux-x64','linux-arm64')][string]$Runtime = 'linux-x64',
    [string]$OutputDirectory,
    [string]$Version = '0.7.0'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot "artifacts\dsp-server\$Runtime" }
$taskBuild = Join-Path $taskRoot "artifacts\dsp-server-build\$Runtime"
$taskDirectMl = if ($Runtime.StartsWith('win-')) { 'true' } else { 'false' }
& dotnet publish (Join-Path $taskRoot 'src\FUPlayer.DspServer\FUPlayer.DspServer.csproj') -c Release -r $Runtime --self-contained true --artifacts-path $taskBuild -o $OutputDirectory --nologo `
    -p:Version=$Version -p:FuPlayerDirectML=$taskDirectMl -p:FuPlayerSkipFFmpegCopy=true -p:FuPlayerSkipModelCopy=true -p:DebugType=embedded -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'DSP server publish failed.' }
$taskName = if ($Runtime.StartsWith('win-')) { 'FUPlayer-DspServer.exe' } else { 'FUPlayer-DspServer' }
$taskBinary = Join-Path $OutputDirectory $taskName
if (-not (Test-Path -LiteralPath $taskBinary -PathType Leaf)) { throw 'Portable executable was not produced.' }
$taskExtras = @(Get-ChildItem -LiteralPath $OutputDirectory | Where-Object Name -ne $taskName)
if ($taskExtras.Count -gt 0) { throw "Unexpected sidecar files in $OutputDirectory; inspect the publish before distributing." }
Get-Item -LiteralPath $taskBinary | Select-Object FullName,Length
Get-FileHash -LiteralPath $taskBinary -Algorithm SHA256
