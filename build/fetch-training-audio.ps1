<#
.SYNOPSIS
    Downloads freely licensed lossless music to train a lossy-repair model on.

.DESCRIPTION
    Queries the Internet Archive for FLAC releases whose licence is a public domain dedication
    (CC0) or another licence that permits any use, and downloads them into a folder along with a
    manifest recording where each file came from and under what licence.

    Only lossless material is any use here. The model is trained by coding a file with a real
    encoder and learning the difference from the original, so the original has to be the original.

    Nothing downloaded here is redistributed by this project. The manifest exists so that anyone can
    see what a model was fitted to, and repeat it.

.PARAMETER Destination
    Folder to download into. Created if it does not exist.

.PARAMETER Count
    How many files to fetch. One file per release, so this is also how many releases.

.PARAMETER MaxMegabytes
    Stop once this much has been downloaded, whatever Count says.

.PARAMETER Licence
    Which licences to accept. "cc0" is the default and the safest: CC0 and the Public Domain Mark,
    neither of which asks for anything in return. "permissive" also takes CC-BY-SA, which wants
    credit and carries a share-alike term, so prefer the default unless more material is needed.

.EXAMPLE
    pwsh build/fetch-training-audio.ps1 -Destination D:\training -Count 40
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,
    [int]$Count = 40,
    [int]$MaxMegabytes = 4096,
    [ValidateSet("cc0", "permissive")]
    [string]$Licence = "cc0",
    [int]$MinSeconds = 240,
    [int]$MaxFileMegabytes = 250
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# The search does not take slashes in a wildcard, so the licence is matched on a distinctive
# fragment of its URL rather than on the whole path.
$query = if ($Licence -eq "cc0") {
    'collection:netlabels AND format:(FLAC) AND licenseurl:*publicdomain*'
}
else {
    'collection:netlabels AND format:(FLAC) AND (licenseurl:*publicdomain* OR licenseurl:*by-sa*)'
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$manifestPath = Join-Path $Destination "manifest.csv"
$existing = @{}
if (Test-Path $manifestPath) {
    Import-Csv $manifestPath | ForEach-Object { $existing[$_.File] = $true }
}
else {
    "File,Identifier,Licence,Source,Seconds" | Out-File -FilePath $manifestPath -Encoding utf8
}

Write-Host "Searching the Internet Archive ($Licence)..."
$search = "https://archive.org/advancedsearch.php?q=$([uri]::EscapeDataString($query))" +
    "&fl%5B%5D=identifier&fl%5B%5D=licenseurl&rows=600&output=json"
$found = (Invoke-RestMethod -Uri $search -TimeoutSec 120).response.docs
Write-Host "$($found.Count) releases match."

# A stable shuffle, so a second run with a larger count keeps what the first one chose and adds to it.
$found = $found | Sort-Object { [System.BitConverter]::ToUInt32(
    [System.Security.Cryptography.MD5]::HashData([System.Text.Encoding]::UTF8.GetBytes($_.identifier)), 0) }

$downloaded = 0
$megabytes = 0.0

foreach ($release in $found) {
    if ($downloaded -ge $Count -or $megabytes -ge $MaxMegabytes) { break }

    try {
        $meta = Invoke-RestMethod -Uri "https://archive.org/metadata/$($release.identifier)" -TimeoutSec 120
    }
    catch {
        continue
    }

    # One file per release, the longest that is not enormous: variety across releases beats depth
    # inside one, and a four-minute track is the least worth the round trip.
    $flac = $meta.files |
        Where-Object {
            $_.format -eq "Flac" -and
            [double]($_.size ?? 0) -lt ($MaxFileMegabytes * 1MB) -and
            [double]($_.length ?? 0) -ge $MinSeconds
        } |
        Sort-Object { [double]($_.length ?? 0) } -Descending |
        Select-Object -First 1

    if (-not $flac) { continue }

    $safe = ($release.identifier -replace '[^A-Za-z0-9._-]', '_') + ".flac"
    $target = Join-Path $Destination $safe
    if ($existing.ContainsKey($safe) -or (Test-Path $target)) { continue }

    $url = "https://archive.org/download/$($release.identifier)/$([uri]::EscapeDataString($flac.name))"
    $size = [double]($flac.size ?? 0) / 1MB
    Write-Host ("[{0,3}/{1}] {2,-38} {3,7:N1} MB" -f ($downloaded + 1), $Count, $release.identifier.Substring(0, [Math]::Min(38, $release.identifier.Length)), $size)

    try {
        Invoke-WebRequest -Uri $url -OutFile $target -TimeoutSec 900
    }
    catch {
        Write-Warning "  failed: $($_.Exception.Message)"
        if (Test-Path $target) { Remove-Item $target -Force }
        continue
    }

    # The manifest is the record of where a model's training data came from.
    $row = [PSCustomObject]@{
        File       = $safe
        Identifier = $release.identifier
        Licence    = $release.licenseurl
        Source     = $url
        Seconds    = [math]::Round([double]($flac.length ?? 0), 1)
    }
    $row | Export-Csv -Path $manifestPath -Append -NoTypeInformation -Encoding utf8

    $downloaded++
    $megabytes += $size
}

Write-Host ""
Write-Host ("Downloaded {0} files, {1:N0} MB, into {2}" -f $downloaded, $megabytes, $Destination)
Write-Host "Licences are recorded in manifest.csv."
Write-Host "Next: fuplayer-cli dataset `"$Destination`" --out <file> to build the coded/original pairs."
