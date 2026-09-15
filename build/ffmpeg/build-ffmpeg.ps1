<#
.SYNOPSIS
    Builds the LGPL FFmpeg libraries for FUPlayer on Windows, installing the compiler first if
    it is missing.

.DESCRIPTION
    build-ffmpeg-lgpl.sh needs a MinGW-w64 compiler. Git Bash does not have one, which is why
    running the script there stops at "C compiler test failed". This wrapper finds or installs
    MSYS2, installs gcc, make and nasm into its UCRT64 environment, and then runs the build
    script in that shell.

    The libraries land in build/ffmpeg/out/win-x64/bin and are copied to native/win-x64, where
    the solution picks them up on the next build.

.PARAMETER Msys2Root
    Where MSYS2 is installed. Found automatically when it is in the usual place.

.PARAMETER SkipToolchainInstall
    Do not install or update anything; fail instead if the compiler is missing.

.PARAMETER Jobs
    Parallel compiler processes. Defaults to the number of logical processors.

.EXAMPLE
    pwsh build/ffmpeg/build-ffmpeg.ps1
#>
[CmdletBinding()]
param(
    [string]$Msys2Root,
    [switch]$SkipToolchainInstall,
    [int]$Jobs = 0
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Find-Msys2Root {
    $candidates = @()
    if ($Msys2Root) { $candidates += $Msys2Root }
    if ($env:MSYS2_ROOT) { $candidates += $env:MSYS2_ROOT }
    $candidates += @("C:\msys64", "C:\msys2", "$env:LOCALAPPDATA\Programs\msys64")

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate "usr\bin\bash.exe"))) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

$msys = Find-Msys2Root

if (-not $msys) {
    if ($SkipToolchainInstall) { throw "MSYS2 was not found and -SkipToolchainInstall was given." }

    Write-Host "installing MSYS2"
    winget install --id MSYS2.MSYS2 --exact --source winget `
        --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -ne 0) { throw "winget could not install MSYS2 (exit code $LASTEXITCODE). Install it from https://www.msys2.org and run this again." }

    $msys = Find-Msys2Root
    if (-not $msys) { throw "MSYS2 reported that it installed but usr\bin\bash.exe is not there. Pass -Msys2Root." }
}

Write-Host "MSYS2: $msys"
$bash = Join-Path $msys "usr\bin\bash.exe"

# Run a command in the UCRT64 environment, which is the one whose gcc produces ordinary Windows
# DLLs. The MSYS environment has a gcc too, but it builds against msys-2.0.dll and the result
# cannot be loaded by a .NET process.
function Invoke-Ucrt64([string]$command) {
    $env:MSYSTEM = "UCRT64"
    $env:CHERE_INVOKING = "1"
    # Leave MSYS2_PATH_TYPE alone so the Windows PATH stays out of the build. FFmpeg's configure
    # calls a lot of small tools by name, and picking up the Windows find.exe or sort.exe instead
    # of the MSYS2 ones fails in ways that are tedious to read.
    # No return value: anything a function writes becomes its output in PowerShell, so returning
    # the exit code here would hand back the whole build log with the code on the end.
    & $bash -lc $command
}

if (-not $SkipToolchainInstall) {
    $packages = "mingw-w64-ucrt-x86_64-gcc make nasm diffutils pkgconf"
    Write-Host "installing the compiler ($packages)"
    Invoke-Ucrt64 "pacman -Sy --noconfirm >/dev/null 2>&1; pacman -S --needed --noconfirm --disable-download-timeout $packages"
    if ($LASTEXITCODE -ne 0) { throw "pacman failed (exit code $LASTEXITCODE)." }
}

if ($Jobs -le 0) { $Jobs = [Environment]::ProcessorCount }

$unixRoot = (& $bash -lc "cygpath -u '$($root -replace "'", "'\''")'").Trim()
if (-not $unixRoot) { throw "Could not translate $root into an MSYS2 path." }

Write-Host "building FFmpeg in $root (jobs: $Jobs)"
Invoke-Ucrt64 "cd '$unixRoot' && JOBS=$Jobs ./build/ffmpeg/build-ffmpeg-lgpl.sh"
if ($LASTEXITCODE -ne 0) { throw "the FFmpeg build failed (exit code $LASTEXITCODE)." }

$staged = Join-Path $root "native\win-x64"
Write-Host ""
Write-Host "staged in $staged"
Get-ChildItem $staged -Filter *.dll | ForEach-Object { "  {0} ({1:N0} bytes)" -f $_.Name, $_.Length }
Write-Host ""
Write-Host "Build the solution and FUPlayer will find them. dotnet run --project src/FUPlayer.Cli -- info shows the loaded version."
