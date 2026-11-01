<#
.SYNOPSIS
  Builds a portable single-file DaDPlaylistManager.exe in .\dist.

.DESCRIPTION
  1. Gets ffmpeg.exe into .\vendor (downloaded from BtbN's LGPL builds, or copied from -FfmpegPath),
     so it is embedded into the exe and extracted on first use.
  2. Runs the unit tests.
  3. Publishes a self-contained, single-file, compressed win-x64 exe.

.PARAMETER FfmpegPath
  Use this ffmpeg.exe instead of downloading one.

.PARAMETER NoFfmpeg
  Build without embedding ffmpeg (smaller exe; audio import then uses ffmpeg from PATH or asks for it).

.PARAMETER SkipTests
  Don't run the unit tests.
#>
param(
    [string]$FfmpegPath,
    [switch]$NoFfmpeg,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest very slow on Windows PowerShell
$root = $PSScriptRoot
$vendor = Join-Path $root 'vendor'
$vendorFfmpeg = Join-Path $vendor 'ffmpeg.exe'
$dist = Join-Path $root 'dist'

# Static LGPL release builds of ffmpeg (include libvorbis) from https://github.com/BtbN/FFmpeg-Builds
$ffmpegRelease = 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest'
$ffmpegBaseUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'

if ($NoFfmpeg) {
    if (Test-Path $vendorFfmpeg) { Remove-Item $vendorFfmpeg }
    Write-Host 'Building without embedded ffmpeg.'
}
elseif ($FfmpegPath) {
    New-Item -ItemType Directory -Force $vendor | Out-Null
    Copy-Item $FfmpegPath $vendorFfmpeg -Force
    Write-Host "Embedding ffmpeg from $FfmpegPath"
}
elseif (-not (Test-Path $vendorFfmpeg)) {
    New-Item -ItemType Directory -Force $vendor | Out-Null
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("dadpm-ffmpeg-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        # Asset names carry the ffmpeg version (e.g. ffmpeg-n9.0-latest-win64-lgpl-9.0.zip); pick the newest release branch.
        $assets = (Invoke-RestMethod $ffmpegRelease -UseBasicParsing).assets
        $ffmpegZipName = $assets.name |
            Where-Object { $_ -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-lgpl-\d+\.\d+\.zip$' } |
            Sort-Object { [version]($_ -replace '^ffmpeg-n(\d+\.\d+).*', '$1') } -Descending |
            Select-Object -First 1
        if (-not $ffmpegZipName) { throw 'Could not find a win64 LGPL ffmpeg release build.' }
        $zip = Join-Path $tmp $ffmpegZipName
        Write-Host "Downloading $ffmpegZipName ..."
        Invoke-WebRequest "$ffmpegBaseUrl/$ffmpegZipName" -OutFile $zip -UseBasicParsing
        $sums = (Invoke-WebRequest "$ffmpegBaseUrl/checksums.sha256" -UseBasicParsing).Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
        $expected = ($sums -split "`n" | Where-Object { $_ -match [regex]::Escape($ffmpegZipName) } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $ffmpegZipName (expected $expected, got $actual)" }
        Expand-Archive $zip -DestinationPath $tmp
        $exe = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
        if (-not $exe) { throw 'ffmpeg.exe not found in the downloaded archive.' }
        Copy-Item $exe.FullName $vendorFfmpeg -Force
        $license = Get-ChildItem $tmp -Recurse -Filter LICENSE.txt | Select-Object -First 1
        if ($license) { Copy-Item $license.FullName (Join-Path $vendor 'FFMPEG-LICENSE.txt') -Force }
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    Write-Host "Embedding existing $vendorFfmpeg"
}

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\DaDPlaylistManager.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish (Join-Path $root 'src\DaDPlaylistManager') -c Release -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Get-ChildItem $dist -Filter *.pdb | Remove-Item
Copy-Item (Join-Path $root 'README.md') $dist
if (Test-Path (Join-Path $vendor 'FFMPEG-LICENSE.txt')) { Copy-Item (Join-Path $vendor 'FFMPEG-LICENSE.txt') $dist }

$exePath = Join-Path $dist 'DaDPlaylistManager.exe'
$sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $exePath ($sizeMb MB)"
