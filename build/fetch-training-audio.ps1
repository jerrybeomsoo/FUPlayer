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

.PARAMETER PerCreator
    At most this many files from any one artist. A model fitted to two hundred tracks by the same
    ambient netlabel has learned that netlabel, not music, and it shows on anything with a cymbal in
    it. Spreading the same number of files over more artists costs nothing and is the cheapest thing
    that can be done for the result.

.PARAMETER Collection
    Restrict to one Internet Archive collection. Empty searches everything with an audio media type,
    which is what variety needs; "netlabels" reproduces the narrower search this script used to do.

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
    [int]$MaxFileMegabytes = 250,
    [int]$PerCreator = 2,
    [string]$Collection = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# The search does not take slashes in a wildcard, so the licence is matched on a distinctive
# fragment of its URL rather than on the whole path.
$licenceTerm = if ($Licence -eq "cc0") {
    'licenseurl:*publicdomain*'
}
else {
    '(licenseurl:*publicdomain* OR licenseurl:*by-sa*)'
}

# Everything with an audio media type, not one collection. The netlabel collections are mostly
# electronic and ambient, and a high band learned from those alone is wrong about anything played on
# a stretched skin or a bowed string. Searching the whole archive turns 784 candidate releases into
# about 46,000, across every kind of music people have put there.
$scope = if ($Collection) { "collection:$Collection" } else { "mediatype:audio" }
$query = "$scope AND format:(FLAC) AND $licenceTerm"


New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$manifestPath = Join-Path $Destination "manifest.csv"
$existing = @{}
if (Test-Path $manifestPath) {
    Import-Csv $manifestPath | ForEach-Object { $existing[$_.File] = $true }
}
else {
    "File,Identifier,Creator,Licence,Source,Seconds" | Out-File -FilePath $manifestPath -Encoding utf8
}

Write-Host "Searching the Internet Archive ($Licence)..."

# Several pages, because one page of a few hundred from a search sorted by relevance comes back
# dominated by whichever collection happens to rank highest, and the whole point is to get away from
# that.
# Pages spread through the result set rather than the first few, because a search sorted any one way
# hands back its first pages full of whatever sorts first, which here is a run of releases whose
# names begin with a digit.
$found = @()
# The archive stops paging a search at ten thousand rows, so these are as deep as it will go.
foreach ($page in 1, 4, 7, 10, 13, 16, 19) {
    $search = "https://archive.org/advancedsearch.php?q=$([uri]::EscapeDataString($query))" +
        "&fl%5B%5D=identifier&fl%5B%5D=licenseurl&fl%5B%5D=creator&fl%5B%5D=collection" +
        "&sort%5B%5D=identifier+asc&rows=500&page=$page&output=json"
    try {
        $docs = (Invoke-RestMethod -Uri $search -TimeoutSec 180).response.docs
    }
    catch {
        continue
    }

    if ($docs) { $found += $docs }
}

Write-Host "$($found.Count) releases to choose from."

# A stable shuffle, so a second run with a larger count keeps what the first one chose and adds to it.
$found = $found | Sort-Object { [System.BitConverter]::ToUInt32(
    [System.Security.Cryptography.MD5]::HashData([System.Text.Encoding]::UTF8.GetBytes($_.identifier)), 0) }

$downloaded = 0
$megabytes = 0.0
$creatorCounts = @{}

foreach ($release in $found) {
    if ($downloaded -ge $Count -or $megabytes -ge $MaxMegabytes) { break }

    # One artist's idea of a high band is not music's idea of a high band. The creator field is
    # sometimes a list, so it is flattened to one string before being used as a key.
    $creator = "$($release.creator)".Trim()
    if (-not $creator) { $creator = "$($release.identifier)" }

    $taken = 0
    if ($creatorCounts.ContainsKey($creator)) { $taken = [int]$creatorCounts[$creator] }
    if ($taken -ge $PerCreator) { continue }

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
        Creator    = $creator
        Licence    = $release.licenseurl
        Source     = $url
        Seconds    = [math]::Round([double]($flac.length ?? 0), 1)
    }
    $row | Export-Csv -Path $manifestPath -Append -NoTypeInformation -Encoding utf8

    $downloaded++
    $megabytes += $size
    $creatorCounts[$creator] = $taken + 1
}

Write-Host ""
Write-Host ("Downloaded {0} files from {1} artists, {2:N0} MB, into {3}" -f $downloaded, $creatorCounts.Count, $megabytes, $Destination)
Write-Host "Licences are recorded in manifest.csv."
Write-Host "Next: fuplayer-cli dataset `"$Destination`" --out <file> to build the coded/original pairs."
