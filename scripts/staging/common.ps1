# Shared helpers for the staging scripts (setup.ps1, update.ps1, rollback.ps1, seed-demo.ps1, ops.ps1).
# Dot-sourced by them; not meant to be run on its own. Works in Windows PowerShell 5.1 and PowerShell 7.
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads non-ASCII characters in files without a BOM.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$EnvFile     = Join-Path $RepoRoot 'infra\staging.env'
$EnvExample  = Join-Path $RepoRoot 'infra\staging.env.example'
$DeployLog   = Join-Path $RepoRoot 'infra\staging.deploy.log'
$ComposeBase = Join-Path $RepoRoot 'infra\docker-compose.yml'
$ComposeStg  = Join-Path $RepoRoot 'infra\docker-compose.staging.yml'
$Project     = 'reelguo-staging'
$Network     = 'reelguo-staging_default'
$AppImages   = @('reelguo-staging-api', 'reelguo-staging-worker', 'reelguo-staging-web')

# ---------------------------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------------------------

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    ok   $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "    ..   $Text" }
function Write-Warn([string]$Text) { Write-Host "    !!   $Text" -ForegroundColor Yellow }

function Stop-WithError([string]$Text) {
    Write-Host ''
    Write-Host "FAILED: $Text" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------------------------------------------
# Settings file (infra/staging.env): KEY=VALUE lines, comments kept as they are
# ---------------------------------------------------------------------------------------------------------------

function Read-Settings {
    $map = @{}
    if (-not (Test-Path $EnvFile)) { return $map }
    foreach ($line in [System.IO.File]::ReadAllLines($EnvFile)) {
        $t = $line.Trim()
        if ($t -eq '' -or $t.StartsWith('#')) { continue }
        $i = $t.IndexOf('=')
        if ($i -lt 1) { continue }
        $map[$t.Substring(0, $i).Trim()] = $t.Substring($i + 1).Trim()
    }
    return $map
}

function Get-Setting([string]$Key, [string]$Default = '') {
    $s = Read-Settings
    if ($s.ContainsKey($Key) -and $s[$Key] -ne '') { return $s[$Key] }
    return $Default
}

# Sets KEY=VALUE in the settings file, replacing the existing line or adding one at the end.
function Set-Setting([string]$Key, [string]$Value) {
    $lines = New-Object System.Collections.Generic.List[string]
    if (Test-Path $EnvFile) { $lines.AddRange([string[]][System.IO.File]::ReadAllLines($EnvFile)) }
    $done = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") { $lines[$i] = "$Key=$Value"; $done = $true; break }
    }
    if (-not $done) { $lines.Add("$Key=$Value") }
    Write-TextFile $EnvFile (($lines -join "`n") + "`n")
}

# UTF-8 without a byte-order mark, LF line ends (docker compose reads a BOM as part of the first key).
function Write-TextFile([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# Limits the settings file to the current user and the Administrators group (it holds secrets).
function Protect-SettingsFile {
    try {
        $who = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        & icacls $EnvFile /inheritance:r /grant:r "${who}:(F)" /grant:r '*S-1-5-32-544:(F)' | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Ok 'infra\staging.env is readable only by you and Administrators' }
    } catch {
        Write-Warn "Could not restrict access to infra\staging.env: $($_.Exception.Message)"
    }
}

function New-Base64Secret([int]$Bytes = 32) {
    $b = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($b)
}

# Letters and digits without look-alikes (0/O, 1/l/I), so it can be read out and typed.
function New-Password([int]$Length = 20) {
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $b = New-Object byte[] ($Length * 2)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    $chars = for ($i = 0; $i -lt $Length; $i++) { $alphabet[([int]$b[2 * $i] * 256 + [int]$b[2 * $i + 1]) % $alphabet.Length] }
    return -join $chars
}

# ---------------------------------------------------------------------------------------------------------------
# Docker
# ---------------------------------------------------------------------------------------------------------------

function Test-Prerequisites([switch]$NeedGit) {
    Write-Step 'Checking this server'
    if ($PSVersionTable.PSVersion -lt [version]'5.1') { Stop-WithError 'PowerShell 5.1 or newer is needed.' }
    Write-Ok "PowerShell $($PSVersionTable.PSVersion)"

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Stop-WithError 'docker was not found. Install Docker (see docs/STAGING.md, "Server requirements").' }
    $os = & docker info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Docker is installed but not running (docker info failed). Start Docker and try again.' }
    if ($os -ne 'linux') { Stop-WithError "Docker runs $os containers. ReelGuo needs Linux containers (Docker Desktop: switch to Linux containers; or Docker Engine in WSL2)." }
    Write-Ok 'Docker is running Linux containers'

    $v = (& docker compose version --short) -replace '^v', ''
    if ($LASTEXITCODE -ne 0 -or -not $v) { Stop-WithError 'docker compose (v2) is not available.' }
    $core = ($v -split '[-+]')[0]
    if ([version]$core -lt [version]'2.24.4') { Stop-WithError "docker compose $v is too old; 2.24.4 or newer is needed (for !reset / !override)." }
    Write-Ok "docker compose $v"

    if ($NeedGit) {
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-WithError 'git was not found (needed to fetch updates). Install Git for Windows, or use -SkipPull.' }
        Write-Ok "git $((& git --version) -replace 'git version ', '')"
    }

    $drive = Get-PSDrive -Name ($RepoRoot.Substring(0, 1)) -ErrorAction SilentlyContinue
    if ($drive -and $drive.Free) {
        $freeGb = [math]::Round($drive.Free / 1GB, 1)
        if ($freeGb -lt 15) { Write-Warn "Only $freeGb GB free on $($drive.Name): (images, media and backups need room; 30 GB or more is advised)" }
        else { Write-Ok "$freeGb GB free on $($drive.Name):" }
    }
}

function Get-ComposeArgs {
    $list = @('compose', '-p', $Project, '--env-file', $EnvFile, '-f', $ComposeBase, '-f', $ComposeStg, '--profile', 'app')
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel') { $list += @('--profile', 'tunnel') }
    return ,$list
}

# docker compose with the staging files, project and settings. Stops the script when the command fails.
function Invoke-Compose {
    $all = (Get-ComposeArgs) + $args
    & docker @all
    if ($LASTEXITCODE -ne 0) { Stop-WithError "docker compose $($args -join ' ') failed (exit code $LASTEXITCODE)." }
}

# The same, returning standard output as text (progress messages still go to the screen).
function Invoke-ComposeOutput {
    $all = (Get-ComposeArgs) + $args
    $out = & docker @all
    if ($LASTEXITCODE -ne 0) { Stop-WithError "docker compose $($args -join ' ') failed (exit code $LASTEXITCODE)." }
    return ($out -join "`n")
}

# ---------------------------------------------------------------------------------------------------------------
# Checks over HTTP, on this server (the web container's local port)
# ---------------------------------------------------------------------------------------------------------------

function Get-LocalBase {
    $bind = Get-Setting 'REELGUO_WEB_BIND' '127.0.0.1:8080'
    $port = $bind.Substring($bind.LastIndexOf(':') + 1)
    return "http://127.0.0.1:$port"
}

function Get-AdminBase {
    $bind = Get-Setting 'REELGUO_ADMIN_BIND' '127.0.0.1:8081'
    $port = $bind.Substring($bind.LastIndexOf(':') + 1)
    return "http://127.0.0.1:$port"
}

function Invoke-Http([string]$Url, [int]$TimeoutSec = 10) {
    try {
        $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = [string]$r.Content }
    } catch {
        $resp = $null
        if ($_.Exception.PSObject.Properties['Response']) { $resp = $_.Exception.Response }
        if ($resp) { return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = '' } }
        return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message }
    }
}

# Waits until the API answers /health/ready through the web container (migrations run on the first start).
function Wait-Healthy([int]$TimeoutSec = 300) {
    Write-Step "Waiting for the site to become ready (up to $([int]($TimeoutSec / 60)) minutes)"
    $url = "$(Get-LocalBase)/health/ready"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $r = [pscustomobject]@{ Status = 0; Body = '(no answer yet)' }
    while ((Get-Date) -lt $deadline) {
        $r = Invoke-Http $url 5
        if ($r.Status -eq 200) { Write-Ok "ready ($url)"; return $true }
        Start-Sleep -Seconds 5
    }
    Write-Warn "Not ready after $TimeoutSec s. Last answer: $($r.Status) $($r.Body)"
    return $false
}

# Quick checks that the site works and stays out of search results. Returns $true when all pass.
function Test-Site {
    Write-Step 'Checking the site'
    $base = Get-LocalBase
    $ok = $true

    $front = Invoke-Http "$base/"
    if ($front.Status -eq 200 -and $front.Body -match '<div id="seo"' -and $front.Body -match '<div id="root">') { Write-Ok 'home page: the app with its server-rendered content' }
    else { Write-Warn "home page: HTTP $($front.Status)"; $ok = $false }

    $robots = Invoke-Http "$base/robots.txt"
    $indexing = (Get-Setting 'REELGUO_ALLOW_INDEXING' 'false') -eq 'true'
    if ($robots.Status -eq 200 -and ($indexing -or $robots.Body -match 'Disallow: /\s*$')) {
        if ($indexing) { Write-Warn 'robots.txt allows search engines (REELGUO_ALLOW_INDEXING=true). Staging should keep it false.' }
        else { Write-Ok 'robots.txt keeps search engines out' }
    } else { Write-Warn "robots.txt: HTTP $($robots.Status)"; $ok = $false }

    $config = Invoke-Http "$base/api/v1/config"
    if ($config.Status -eq 200) { Write-Ok 'API answers (/api/v1/config)' } else { Write-Warn "API /api/v1/config: HTTP $($config.Status)"; $ok = $false }

    $admin = Invoke-Http "$(Get-AdminBase)/"
    if ($admin.Status -eq 200) { Write-Ok "admin console on this server: $((Get-AdminBase).Replace('127.0.0.1', 'localhost'))" } else { Write-Warn "admin console: HTTP $($admin.Status)"; $ok = $false }

    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel') {
        $state = (& docker ps --filter "name=$Project-cloudflared" --format '{{.Status}}') -join ' '
        if ($state -match '^Up') { Write-Ok "Cloudflare tunnel connector is running ($state)" }
        else { Write-Warn "Cloudflare tunnel connector is not running: '$state'. Check: .\scripts\staging\ops.ps1 logs cloudflared"; $ok = $false }
    }
    return $ok
}

function Show-RecentLogs {
    Write-Step 'Recent logs (api, worker, web)'
    $all = (Get-ComposeArgs) + @('logs', '--no-color', '--tail', '60', 'api', 'worker', 'web')
    & docker @all
}

# ---------------------------------------------------------------------------------------------------------------
# Deploy record (infra/staging.deploy.log, git-ignored)
# ---------------------------------------------------------------------------------------------------------------

function Get-Commit {
    if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { return '(no git)' }
    $c = & git -C $RepoRoot rev-parse --short --verify --quiet HEAD
    if ($LASTEXITCODE -ne 0 -or -not $c) { return '(no commit)' }
    return $c
}

function Add-DeployRecord([string]$Action, [string]$Result) {
    $line = '{0:u} | {1,-8} | {2,-12} | {3}' -f (Get-Date).ToUniversalTime(), $Action, (Get-Commit), $Result
    [System.IO.File]::AppendAllText($DeployLog, $line + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Test-SettingsReady {
    if (-not (Test-Path $EnvFile)) { Stop-WithError 'infra\staging.env does not exist yet. Run .\scripts\staging\setup.ps1 first.' }
    foreach ($k in @('REELGUO_APP_URL', 'REELGUO_MEDIA_KEY', 'REELGUO_JWT_KEY', 'REELGUO_ADMIN_PASSWORD')) {
        if ((Get-Setting $k) -eq '') { Stop-WithError "$k is empty in infra\staging.env." }
    }
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel' -and (Get-Setting 'CLOUDFLARE_TUNNEL_TOKEN') -eq '') {
        Stop-WithError 'CLOUDFLARE_TUNNEL_TOKEN is empty in infra\staging.env (tunnel mode).'
    }
}
