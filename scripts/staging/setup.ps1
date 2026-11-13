<#
.SYNOPSIS
    First-time setup of ReelGuo staging on this server: settings and secrets, images, start, checks, demo content.

.DESCRIPTION
    Run from the repository folder on the staging server (see docs/STAGING.md):

        powershell -ExecutionPolicy Bypass -File .\scripts\staging\setup.ps1 -AppUrl https://staging.example.com -TunnelToken <token>

    Steps:
      1. Checks Docker (Linux containers), docker compose, free disk space.
      2. Creates infra\staging.env from infra\staging.env.example with new secrets (an existing file is kept:
         secrets are never replaced, only missing values are filled in).
      3. Builds the images (app, admin console, API, worker) - 5 to 15 minutes the first time.
      4. Starts everything (database, API, worker, web, backups, Cloudflare tunnel connector).
      5. Waits until the site is ready and checks it.
      6. Loads the demo content (labelled sample series), unless content already exists or -SkipDemo is given.

    Safe to run again: it keeps the existing settings and data and brings everything up to date with the code here.
    For later updates use update.ps1.

.PARAMETER AppUrl
    The staging address visitors use, e.g. https://staging.example.com. Asked for when missing.

.PARAMETER Mode
    tunnel (default; Cloudflare Tunnel, no open ports) or public-port (Cloudflare proxied DNS to port 80 of this server).

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

Write-Host 'ReelGuo staging - first-time setup' -ForegroundColor White
Write-Note "Repository: $RepoRoot"
Test-Prerequisites

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Settings (infra\staging.env)'
$created = $false
if (-not (Test-Path $EnvFile)) {
    if (-not (Test-Path $EnvExample)) { Stop-WithError 'infra\staging.env.example is missing; is this the ReelGuo repository?' }
    Write-TextFile $EnvFile ([System.IO.File]::ReadAllText($EnvExample).Replace("`r`n", "`n"))
    $created = $true
    Write-Ok 'created from infra\staging.env.example'
} else {
    Write-Ok 'found an existing infra\staging.env; keeping its values and secrets'
}

# Address
if ($AppUrl) { Set-Setting 'REELGUO_APP_URL' $AppUrl.TrimEnd('/') }
$current = Get-Setting 'REELGUO_APP_URL'
if ($current -eq '' -or $current -eq 'https://staging.example.com') {
    $answer = Read-Host 'Staging address (for example https://staging.yourdomain.com)'
    Set-Setting 'REELGUO_APP_URL' $answer.Trim().TrimEnd('/')
}
$siteUrl = Get-Setting 'REELGUO_APP_URL'
if ($siteUrl -notmatch '^https://[^/\s]+$') { Stop-WithError "REELGUO_APP_URL must look like https://staging.yourdomain.com (got '$siteUrl')." }
Write-Ok "address: $siteUrl"

# Mode and Cloudflare tunnel
if ($Mode) { Set-Setting 'REELGUO_STAGING_MODE' $Mode }
$stagingMode = Get-Setting 'REELGUO_STAGING_MODE' 'tunnel'
if ($stagingMode -eq 'tunnel') {
    Set-Setting 'REELGUO_WEB_BIND' '127.0.0.1:8080'
    if ($TunnelToken) { Set-Setting 'CLOUDFLARE_TUNNEL_TOKEN' $TunnelToken.Trim() }
    if ((Get-Setting 'CLOUDFLARE_TUNNEL_TOKEN') -eq '') {
        Write-Note 'Cloudflare Zero Trust -> Networks -> Tunnels -> your tunnel -> Configure: copy the token from the install command.'
        $tok = Read-Host 'Cloudflare Tunnel token'
        Set-Setting 'CLOUDFLARE_TUNNEL_TOKEN' $tok.Trim()
    }
    Write-Ok 'mode: Cloudflare Tunnel (no open ports on this server)'
} else {
    if ((Get-Setting 'REELGUO_WEB_BIND') -eq '127.0.0.1:8080') { Set-Setting 'REELGUO_WEB_BIND' '0.0.0.0:80' }
    Write-Ok "mode: public port ($(Get-Setting 'REELGUO_WEB_BIND')); allow only Cloudflare's IP ranges in the firewall (docs/STAGING.md)"
}

# Secrets: generated once, never replaced
$newAdminPassword = $false
if ((Get-Setting 'REELGUO_MEDIA_KEY') -eq '') { Set-Setting 'REELGUO_MEDIA_KEY' (New-Base64Secret 32); Write-Ok 'generated REELGUO_MEDIA_KEY' }
if ((Get-Setting 'REELGUO_JWT_KEY') -eq '') { Set-Setting 'REELGUO_JWT_KEY' (New-Base64Secret 32); Write-Ok 'generated REELGUO_JWT_KEY' }
if ((Get-Setting 'REELGUO_ADMIN_PASSWORD') -eq '') { Set-Setting 'REELGUO_ADMIN_PASSWORD' (New-Password 20); $newAdminPassword = $true; Write-Ok 'generated the admin password' }
if ((Get-Setting 'REELGUO_ALLOW_INDEXING' 'false') -ne 'false') { Write-Warn 'REELGUO_ALLOW_INDEXING is not false: this staging site would be visible to search engines.' }
if ($created) { Protect-SettingsFile }
Test-SettingsReady

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Building the images (5 to 15 minutes the first time)'
Invoke-Compose build --pull
Write-Ok 'images built'

Write-Step 'Starting ReelGuo'
Invoke-Compose up -d --remove-orphans
Write-Ok 'containers started'

if (-not (Wait-Healthy 420)) {
    Show-RecentLogs
    Add-DeployRecord 'setup' 'FAILED: not ready'
    Stop-WithError 'The site did not become ready. See the logs above and docs/STAGING.md, "Troubleshooting".'
}
$siteOk = Test-Site

# ---------------------------------------------------------------------------------------------------------------
if (-not $SkipDemo) {
    & (Join-Path $PSScriptRoot 'seed-demo.ps1') -Series $DemoSeries -Episodes $DemoEpisodes
    if ($LASTEXITCODE -ne 0) { Write-Warn 'Loading the demo content failed; run .\scripts\staging\seed-demo.ps1 again later.' }
}

Add-DeployRecord 'setup' ($(if ($siteOk) { 'ok' } else { 'started, some checks failed' }))

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Done'
Write-Host ''
Write-Host "  Site (through Cloudflare):   $siteUrl"
Write-Host "  Site on this server:         $(Get-LocalBase)"
Write-Host "  Admin console on this server: $((Get-AdminBase).Replace('127.0.0.1', 'localhost'))   user: admin   (open it as localhost: sign-in cookies need it)"
if ($newAdminPassword) {
    Write-Host "  Admin password:              $(Get-Setting 'REELGUO_ADMIN_PASSWORD')" -ForegroundColor Yellow
    Write-Host '                               (also in infra\staging.env; store it in your password manager)'
} else {
    Write-Host '  Admin password:              in infra\staging.env (REELGUO_ADMIN_PASSWORD), unless it was changed in the console'
}
Write-Host ''
if ($stagingMode -eq 'tunnel') {
    Write-Host '  Next, in Cloudflare Zero Trust -> Networks -> Tunnels -> your tunnel -> Public hostnames:'
    Write-Host "    $(([uri]$siteUrl).Host)  ->  HTTP  web:8080"
    Write-Host '    (optional) an admin hostname  ->  HTTP  web:8081, protected by a Cloudflare Access policy'
}
Write-Host '  Updates later: .\scripts\staging\update.ps1        Guide: docs\STAGING.md'
if (-not $siteOk) { Write-Warn 'Some checks failed (see above).'; exit 2 }
