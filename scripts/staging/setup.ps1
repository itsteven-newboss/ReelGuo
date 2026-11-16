<#
.SYNOPSIS
    First-time setup of ReelGuo staging on this Windows server, natively (no Docker): tools, database, build,
    Windows services, Cloudflare Tunnel, checks, nightly backups, demo content.

.DESCRIPTION
    Run in PowerShell as Administrator, from the repository folder (see docs/STAGING.md):

        .\scripts\staging\setup.ps1 -AppUrl https://staging.example.com -TunnelToken <token>

    Steps:
      1. Checks the server (Administrator, disk space).
      2. Settings: creates C:\reelguo-staging\staging.env with new secrets (an existing file is kept: secrets are never
         replaced, only missing values are filled in). The folder is limited to SYSTEM and Administrators.
      3. Tools: Git, Node.js (pnpm), .NET 10 SDK, cloudflared (winget); Caddy and ffmpeg (downloaded).
      4. PostgreSQL 17 as a Windows service, listening on this server only; the app's database and login.
      5. Builds a release (API, worker, web app, admin console) - 5 to 15 minutes the first time.
      6. Windows services ReelGuoApi, ReelGuoWorker, ReelGuoWeb (Caddy) and cloudflared; all start at boot.
      7. Starts everything, waits until the site is ready, checks it.
      8. Schedules the nightly database backup and loads the demo content (unless -SkipDemo).

    Safe to run again: it keeps the settings and data. For later versions use update.ps1.

.PARAMETER AppUrl
    The staging address visitors use, e.g. https://staging.example.com. Asked for when missing.

.PARAMETER Mode
    tunnel (default; Cloudflare Tunnel, no open ports) or public-port (Cloudflare proxied DNS to port 80).

.PARAMETER TunnelToken
    The Cloudflare Tunnel token (tunnel mode). Asked for when missing.

.PARAMETER SkipDemo
    Do not load the demo content.
#>
param(
    [string]$AppUrl,
    [ValidateSet('tunnel', 'public-port')][string]$Mode,
    [string]$TunnelToken,
    [switch]$SkipDemo,
    [int]$DemoSeries = 4,
    [int]$DemoEpisodes = 5
)

. (Join-Path $PSScriptRoot 'common.ps1')

Write-Host 'ReelGuo staging - first-time setup (native Windows)' -ForegroundColor White
Write-Note "Code:    $RepoRoot"
Write-Note "Install: $StagingHome"
Test-Prerequisites

# ---------------------------------------------------------------------------------------------------------------
Write-Step "Settings ($SettingsFile)"
$created = $false
New-Item -ItemType Directory -Force -Path $StagingHome, $ReleasesDir, $ToolsDir, $StorageDir, $BackupDir, $LogDir | Out-Null
if (-not (Test-Path $SettingsFile)) {
    if (-not (Test-Path $SettingsExample)) { Stop-WithError 'infra\staging\staging.env.example is missing; is this the ReelGuo repository?' }
    Write-TextFile $SettingsFile ([System.IO.File]::ReadAllText($SettingsExample).Replace("`r`n", "`n").Replace("`n", "`r`n"))
    $created = $true
    Write-Ok 'created from infra\staging\staging.env.example'
} else {
    Write-Ok 'found existing settings; keeping their values and secrets'
}

if ($AppUrl) { Set-Setting 'REELGUO_APP_URL' $AppUrl.Trim().TrimEnd('/') }
$siteUrl = Get-Setting 'REELGUO_APP_URL'
if ($siteUrl -eq '' -or $siteUrl -eq 'https://staging.example.com') {
    $answer = Read-Host 'Staging address (for example https://staging.yourdomain.com)'
    Set-Setting 'REELGUO_APP_URL' $answer.Trim().TrimEnd('/')
    $siteUrl = Get-Setting 'REELGUO_APP_URL'
}
if ($siteUrl -notmatch '^https://[^/\s]+$') { Stop-WithError "REELGUO_APP_URL must look like https://staging.yourdomain.com (got '$siteUrl')." }
Write-Ok "address: $siteUrl"

if ($Mode) { Set-Setting 'REELGUO_STAGING_MODE' $Mode }
$stagingMode = Get-Setting 'REELGUO_STAGING_MODE' 'tunnel'
if ($stagingMode -eq 'tunnel') {
    if ((Get-Setting 'REELGUO_WEB_PORT') -eq '80') { Set-Setting 'REELGUO_WEB_PORT' '8080' }
    if ($TunnelToken) { Set-Setting 'CLOUDFLARE_TUNNEL_TOKEN' $TunnelToken.Trim() }
    if ((Get-Setting 'CLOUDFLARE_TUNNEL_TOKEN') -eq '') {
        Write-Note 'Cloudflare Zero Trust -> Networks -> Tunnels -> your tunnel: copy the token from the install command.'
        $tok = Read-Host 'Cloudflare Tunnel token'
        Set-Setting 'CLOUDFLARE_TUNNEL_TOKEN' $tok.Trim()
    }
    Write-Ok 'mode: Cloudflare Tunnel (no open ports on this server)'
} else {
    if ((Get-Setting 'REELGUO_WEB_PORT' '8080') -eq '8080') { Set-Setting 'REELGUO_WEB_PORT' '80' }
    Write-Ok "mode: public port $(Get-Setting 'REELGUO_WEB_PORT') (open to Cloudflare's addresses only)"
}

# Secrets: generated once, never replaced.
$newAdminPassword = $false
if ((Get-Setting 'REELGUO_MEDIA_KEY') -eq '') { Set-Setting 'REELGUO_MEDIA_KEY' (New-Base64Secret 32); Write-Ok 'generated the media signing key' }
if ((Get-Setting 'REELGUO_JWT_KEY') -eq '') { Set-Setting 'REELGUO_JWT_KEY' (New-Base64Secret 32); Write-Ok 'generated the sign-in key' }
if ((Get-Setting 'REELGUO_ADMIN_PASSWORD') -eq '') { Set-Setting 'REELGUO_ADMIN_PASSWORD' (New-Password 20); $newAdminPassword = $true; Write-Ok 'generated the admin password' }
if ((Get-Setting 'REELGUO_PG_SUPERPASSWORD') -eq '') { Set-Setting 'REELGUO_PG_SUPERPASSWORD' (New-Password 24); Write-Ok 'generated the PostgreSQL superuser password' }
if ((Get-Setting 'REELGUO_DB_PASSWORD') -eq '') { Set-Setting 'REELGUO_DB_PASSWORD' (New-Password 24); Write-Ok 'generated the database password' }
if ((Get-Setting 'REELGUO_ALLOW_INDEXING' 'false') -ne 'false') { Write-Warn 'REELGUO_ALLOW_INDEXING is not false: this staging site would be visible to search engines.' }
Protect-StagingHome
Add-NewSettings
Test-SettingsReady

# ---------------------------------------------------------------------------------------------------------------
Install-Tools
Install-PostgreSql
Initialize-Database

$release = New-Release
$releaseId = Split-Path $release -Leaf

Stop-ReelGuo
Set-CurrentRelease $releaseId
Install-ReelGuoServices
Install-TunnelService
Set-CloudflareFirewall

Write-Step 'Starting ReelGuo'
Start-ReelGuo
Write-Ok 'services started'

if (-not (Wait-Healthy 420)) {
    Show-RecentLogs
    Add-DeployRecord 'setup' 'FAILED: not ready'
    Stop-WithError 'The site did not become ready. See the logs above and docs/STAGING.md, "Troubleshooting".'
}
$siteOk = Test-Site

Write-Step 'Backups'
Register-BackupTask

if (-not $SkipDemo) {
    & (Join-Path $PSScriptRoot 'seed-demo.ps1') -Series $DemoSeries -Episodes $DemoEpisodes
    if ($LASTEXITCODE -ne 0) { Write-Warn 'Loading the demo content failed; run .\scripts\staging\seed-demo.ps1 again later.' }
}

Add-DeployRecord 'setup' ($(if ($siteOk) { 'ok' } else { 'started, some checks failed' }))

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Done'
Write-Host ''
Write-Host "  Site (through Cloudflare):    $siteUrl"
Write-Host "  Site on this server:          $(Get-LocalBase)"
Write-Host "  Admin console on this server: $((Get-AdminBase).Replace('127.0.0.1', 'localhost'))   user: admin   (open it as localhost: sign-in cookies need it)"
if ($newAdminPassword) {
    Write-Host "  Admin password:               $(Get-Setting 'REELGUO_ADMIN_PASSWORD')" -ForegroundColor Yellow
    Write-Host "                                (also in $SettingsFile; store it in your password manager)"
} else {
    Write-Host "  Admin password:               in $SettingsFile (REELGUO_ADMIN_PASSWORD), unless it was changed in the console"
}
Write-Host ''
if ($stagingMode -eq 'tunnel') {
    Write-Host '  Next, in Cloudflare Zero Trust -> Networks -> Tunnels -> your tunnel -> Public hostname:'
    Write-Host "    $(([uri]$siteUrl).Host)  ->  HTTP  localhost:$(Get-Setting 'REELGUO_WEB_PORT' '8080')"
    Write-Host "    (optional) an admin hostname  ->  HTTP  localhost:$(Get-Setting 'REELGUO_ADMIN_PORT' '8081'), protected by a Cloudflare Access policy"
}
Write-Host '  Updates later: .\scripts\staging\update.ps1        Guide: docs\STAGING.md'
if (-not $siteOk) { Write-Warn 'Some checks failed (see above).'; exit 2 }
