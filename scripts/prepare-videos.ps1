<#
.SYNOPSIS
    Makes a folder of episode videos ready for the admin console's folder upload: MP4 with H.264 video and AAC audio,
    the index at the front ("faststart"), at a bit rate that streams well on phones.

.DESCRIPTION
    Writes a copy of the folder with the same layout: every video is converted, every picture and details file
    (.jpg .jpeg .png .webp .json) is copied. Then pick the new folder on the upload page.

        .\scripts\prepare-videos.ps1 -Source "C:\Downloads\SeriesA"                 # writes C:\Downloads\SeriesA-upload
        .\scripts\prepare-videos.ps1 -Source "C:\Downloads\SeriesA" -Quality high
        .\scripts\prepare-videos.ps1 -Source "C:\Downloads\SeriesA" -Mode faststart # no re-encoding, same size

    compress (default): H.264 High at CRF 23, capped at 4 Mbit/s; AAC 128 kbit/s; at most 1080 px on the short side;
    keyframes every 2 seconds; faststart. Measured on a 6-minute 1080x1920 episode at 16 Mbit/s (694 MB): about 80 MB,
    nearly identical to look at (SSIM 0.99), in about a minute on an 8-core PC. Uploads get about 8 times faster, phones
    play without stalling, and COS traffic costs about 8 times less.

    faststart: copies the video and audio as they are (seconds per file) and only moves the index to the front. The
    size stays the same, and the codecs must already be H.264 and AAC.

    Safe to run again: finished files are skipped, so it can follow a folder that is still downloading (files written
    in the last minute are left for the next run). A file being converted is named "<name>.mp4.part", which the upload
    page treats as not finished yet.

.PARAMETER Source
    The folder to convert: one title, or a folder of titles.

.PARAMETER Destination
    Where to write the copy. Default: next to the source, with "-upload" added to its name.

.PARAMETER Mode
    compress (default) or faststart.

.PARAMETER Quality
    standard (default: CRF 23, preset veryfast, up to 4 Mbit/s) or high (CRF 20, preset fast, up to 6 Mbit/s; larger
    files, about twice the time).

.PARAMETER Ffmpeg
    Folder holding ffmpeg.exe and ffprobe.exe, if not found by itself.
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Destination,
    [ValidateSet('compress', 'faststart')][string]$Mode = 'compress',
    [ValidateSet('standard', 'high')][string]$Quality = 'standard',
    [string]$Ffmpeg
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    ok   $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    ..   $Text" }
function Write-Warn([string]$Text) { Write-Host "    !!   $Text" -ForegroundColor Yellow }
function Stop-WithError([string]$Text) { Write-Host ''; Write-Host "FAILED: $Text" -ForegroundColor Red; exit 1 }
function Format-Size([double]$Bytes) { if ($Bytes -ge 1GB) { '{0:N1} GB' -f ($Bytes / 1GB) } else { '{0:N0} MB' -f ($Bytes / 1MB) } }

# ---------------------------------------------------------------------------------------------------------------
# Folders
# ---------------------------------------------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $Source -PathType Container)) { Stop-WithError "No such folder: $Source" }
$src = (Get-Item -LiteralPath $Source).FullName.TrimEnd('\')
if (-not $Destination) { $Destination = "$src-upload" }
$dst = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\')
if ($dst -eq $src -or $dst.StartsWith("$src\", [StringComparison]::OrdinalIgnoreCase)) {
    Stop-WithError 'The destination must be outside the source folder.'
}

# ---------------------------------------------------------------------------------------------------------------
# ffmpeg: given, on PATH, in this repository's .tools, on a staging server, or downloaded once
# ---------------------------------------------------------------------------------------------------------------

function Find-FfmpegFolder {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Ffmpeg) { $candidates.Add($Ffmpeg) }
    $onPath = Get-Command ffmpeg.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add((Split-Path $onPath.Source)) }
    $candidates.Add((Join-Path $PSScriptRoot '..\.tools\ffmpeg\bin'))
    $candidates.Add('C:\reelguo-staging\tools\ffmpeg\bin')
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'ReelGuo\ffmpeg\bin'))
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'ffmpeg.exe')) -and (Test-Path (Join-Path $c 'ffprobe.exe'))) { return (Get-Item $c).FullName }
    }
    $target = Join-Path $env:LOCALAPPDATA 'ReelGuo\ffmpeg\bin'
    Write-Note "ffmpeg not found: downloading it once to $target (about 100 MB) ..."
    $zip = Join-Path $env:TEMP 'reelguo-ffmpeg.zip'
    $tmp = Join-Path $env:TEMP 'reelguo-ffmpeg'
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip -UseBasicParsing
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive $zip -DestinationPath $tmp -Force
    $bin = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if (-not $bin) { Stop-WithError 'The ffmpeg download did not contain ffmpeg.exe. Pass -Ffmpeg <folder> instead.' }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $bin.DirectoryName '*') $target -Force
    Remove-Item $zip, $tmp -Recurse -Force -ErrorAction SilentlyContinue
    return $target
}

$ffBin = Find-FfmpegFolder
$ffmpegExe = Join-Path $ffBin 'ffmpeg.exe'
$ffprobeExe = Join-Path $ffBin 'ffprobe.exe'

# ---------------------------------------------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------------------------------------------

$videoTypes = @('.mp4', '.mov', '.m4v', '.mkv')
$copyTypes = @('.jpg', '.jpeg', '.png', '.webp', '.json')
$placeholder = '\.(part|crdownload|qkdownloading|tmp|download)$|\.mp4\.[a-z0-9]+$'
$settle = [TimeSpan]::FromMinutes(1)
if ($Quality -eq 'high') { $crf = '20'; $preset = 'fast'; $maxrate = '6M'; $bufsize = '12M' }
else { $crf = '23'; $preset = 'veryfast'; $maxrate = '4M'; $bufsize = '8M' }
# At most 1080 px on the short side, even sizes (H.264 needs them), proportions kept.
$scale = "scale=w='if(lte(iw,ih),min(1080,trunc(iw/2)*2),-2)':h='if(lte(iw,ih),-2,min(1080,trunc(ih/2)*2))'"

# Runs ffmpeg or ffprobe. Their progress and messages go to stderr, which Windows PowerShell 5.1 turns into error
# records when the output is captured (2>&1, a log): those must never stop the script, so errors only "continue" here.
function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @Arguments } finally { $ErrorActionPreference = $previous }
}

function Get-Codecs([string]$Path) {
    $v = (Invoke-Tool $ffprobeExe @('-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=codec_name', '-of', 'csv=p=0', $Path)) -join ''
    $a = (Invoke-Tool $ffprobeExe @('-v', 'error', '-select_streams', 'a:0', '-show_entries', 'stream=codec_name', '-of', 'csv=p=0', $Path)) -join ''
    return @{ Video = "$v".Trim(); Audio = "$a".Trim() }
}

function Convert-Video([string]$In, [string]$Out) {
    $part = "$Out.part"
    if ($Mode -eq 'faststart') {
        $codecs = Get-Codecs $In
        if ($codecs.Video -ne 'h264' -or ($codecs.Audio -and $codecs.Audio -ne 'aac')) {
            Write-Warn "video $($codecs.Video), audio $($codecs.Audio): the site needs H.264 and AAC. Run with -Mode compress for this one."
            return $false
        }
        $arguments = @('-hide_banner', '-v', 'error', '-stats', '-y', '-i', $In, '-map', '0:v:0', '-map', '0:a:0?', '-c', 'copy',
            '-movflags', '+faststart', '-f', 'mp4', $part)
    } else {
        $arguments = @('-hide_banner', '-v', 'error', '-stats', '-y', '-i', $In, '-map', '0:v:0', '-map', '0:a:0?', '-vf', $scale,
            '-c:v', 'libx264', '-preset', $preset, '-crf', $crf, '-maxrate', $maxrate, '-bufsize', $bufsize,
            '-profile:v', 'high', '-pix_fmt', 'yuv420p', '-g', '60',
            '-c:a', 'aac', '-b:a', '128k', '-ac', '2', '-movflags', '+faststart', '-f', 'mp4', $part)
    }
    Invoke-Tool $ffmpegExe $arguments | Out-Host
    $code = $LASTEXITCODE
    Write-Host ''
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $part) -or (Get-Item -LiteralPath $part).Length -eq 0) {
        if (Test-Path -LiteralPath $part) { [System.IO.File]::Delete($part) }
        return $false
    }
    if (Test-Path -LiteralPath $Out) { [System.IO.File]::Delete($Out) }
    [System.IO.File]::Move($part, $Out)
    return $true
}

# ---------------------------------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------------------------------

Write-Host 'Prepare videos for the folder upload' -ForegroundColor White
Write-Note "from: $src"
Write-Note "to:   $dst"
Write-Note "mode: $Mode$(if ($Mode -eq 'compress') { " ($Quality - CRF $crf, preset $preset, up to ${maxrate}bit/s)" })"
Write-Note "ffmpeg: $ffBin"

$files = @(Get-ChildItem -LiteralPath $src -Recurse -File | Sort-Object FullName)
$videos = @($files | Where-Object { $videoTypes -contains $_.Extension.ToLowerInvariant() -and $_.Name -notmatch $placeholder })
$stats = @{ converted = 0; copied = 0; skipped = 0; waiting = 0; failed = 0; inBytes = 0.0; outBytes = 0.0 }
$failedNames = New-Object System.Collections.Generic.List[string]
$i = 0

foreach ($f in $files) {
    $rel = $f.FullName.Substring($src.Length + 1)
    $ext = $f.Extension.ToLowerInvariant()
    if ($f.Name -match $placeholder) { $stats.waiting++; continue }
    $isVideo = $videoTypes -contains $ext
    if (-not $isVideo -and -not ($copyTypes -contains $ext)) { continue }

    $out = Join-Path $dst $(if ($isVideo) { [System.IO.Path]::ChangeExtension($rel, '.mp4') } else { $rel })
    [System.IO.Directory]::CreateDirectory((Split-Path $out)) | Out-Null
    $done = (Test-Path -LiteralPath $out) -and (Get-Item -LiteralPath $out).Length -gt 0 -and (Get-Item -LiteralPath $out).LastWriteTime -ge $f.LastWriteTime

    if (-not $isVideo) {
        if (-not $done) { [System.IO.File]::Copy($f.FullName, $out, $true); $stats.copied++ }
        continue
    }

    $i++
    if ($done) { $stats.skipped++; continue }
    if ((Get-Date) - $f.LastWriteTime -lt $settle) {
        Write-Note "[$i/$($videos.Count)] $rel was written in the last minute (still downloading?): left for the next run"
        $stats.waiting++
        continue
    }
    $drive = Get-PSDrive -Name $dst.Substring(0, 1) -ErrorAction SilentlyContinue
    if ($drive -and $drive.Free -lt [Math]::Max(500MB, $f.Length / 2)) {
        Stop-WithError "Only $(Format-Size $drive.Free) free on $($drive.Name): - free some space, then run this again (finished files are kept)."
    }

    Write-Step "[$i/$($videos.Count)] $rel ($(Format-Size $f.Length))"
    $started = Get-Date
    if (Convert-Video $f.FullName $out) {
        $size = (Get-Item -LiteralPath $out).Length
        $stats.converted++; $stats.inBytes += $f.Length; $stats.outBytes += $size
        Write-Ok ('{0} -> {1} in {2:N0} s' -f (Format-Size $f.Length), (Format-Size $size), ((Get-Date) - $started).TotalSeconds)
    } else {
        $stats.failed++; $failedNames.Add($rel)
        Write-Warn "could not convert $rel (incomplete or damaged? it is tried again next run)"
    }
}

Write-Step 'Done'
Write-Ok "converted $($stats.converted), already done $($stats.skipped), pictures/details copied $($stats.copied)"
if ($stats.converted -gt 0) { Write-Ok ('{0} of video became {1}' -f (Format-Size $stats.inBytes), (Format-Size $stats.outBytes)) }
if ($stats.waiting -gt 0) { Write-Note "$($stats.waiting) file(s) still downloading: run this again when they finish" }
if ($stats.failed -gt 0) {
    Write-Warn "$($stats.failed) file(s) failed:"
    foreach ($n in $failedNames) { Write-Warn "  $n" }
}
Write-Host ''
Write-Host "  Next: admin console -> folder upload, pick $dst"
if ($stats.failed -gt 0) { exit 1 }
