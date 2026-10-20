[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = Join-Path $taskRoot 'src\FUPlayer.App\StreamingAddon'
$taskManifest = Get-Content -LiteralPath (Join-Path $taskSource 'manifest.json') -Raw | ConvertFrom-Json
$taskOutput = Join-Path $taskRoot 'artifacts\firefox-addon'
$taskStage = Join-Path $taskOutput ("fuplayer-streaming-" + $taskManifest.version)
$taskZip = Join-Path $taskOutput ("fuplayer-streaming-" + $taskManifest.version + '-unsigned.zip')
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
$taskFiles = @('manifest.json', 'bootstrap.js', 'background.js', 'content.js', 'page-controls.js', 'worklet.js', 'host.html', 'PRIVACY.md')
$taskFileStream = [IO.File]::Open($taskZip, [IO.FileMode]::Create)
$taskArchive = [IO.Compression.ZipArchive]::new($taskFileStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskFile in $taskFiles) {
        $taskBytes = [IO.File]::ReadAllBytes((Join-Path $taskSource $taskFile))
        [IO.File]::WriteAllBytes((Join-Path $taskStage $taskFile), $taskBytes)
        $taskEntry = $taskArchive.CreateEntry($taskFile)
        $taskEntry.LastWriteTime = [DateTimeOffset]::new(2026, 10, 20, 0, 0, 0, [TimeSpan]::Zero)
        $taskEntryStream = $taskEntry.Open()
        try { $taskEntryStream.Write($taskBytes) } finally { $taskEntryStream.Dispose() }
    }
} finally { $taskArchive.Dispose(); $taskFileStream.Dispose() }
Get-FileHash -LiteralPath $taskZip -Algorithm SHA256 | Format-List
Write-Output $taskZip
