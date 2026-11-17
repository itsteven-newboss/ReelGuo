# Shared helpers for the staging scripts (setup.ps1, update.ps1, rollback.ps1, ops.ps1, seed-demo.ps1, backup.ps1).
# Staging runs natively on Windows (no Docker): PostgreSQL, the API, the worker and Caddy are Windows services,
# Cloudflare Tunnel too. Dot-sourced by the scripts; not run on its own. Windows PowerShell 5.1 and PowerShell 7.
# Keep this file ASCII-only: Windows PowerShell 5.1 misreads non-ASCII characters in files without a BOM.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---------------------------------------------------------------------------------------------------------------
# Locations and names
# ---------------------------------------------------------------------------------------------------------------

$RepoRoot        = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$StagingHome     = if ($env:REELGUO_STAGING_HOME) { $env:REELGUO_STAGING_HOME } else { 'C:\reelguo-staging' }
$SettingsFile    = Join-Path $StagingHome 'staging.env'
$SettingsExample = Join-Path $RepoRoot 'infra\staging\staging.env.example'
$CaddyTemplate   = Join-Path $RepoRoot 'infra\staging\Caddyfile.template'
$ReleasesDir     = Join-Path $StagingHome 'releases'
$CurrentLink     = Join-Path $StagingHome 'current'
$ToolsDir        = Join-Path $StagingHome 'tools'
$StorageDir      = Join-Path $StagingHome 'data\storage'
$BackupDir       = Join-Path $StagingHome 'backups'
$LogDir          = Join-Path $StagingHome 'logs'
$DeployLog       = Join-Path $StagingHome 'deploy.log'
$CaddyExe        = Join-Path $ToolsDir 'caddy\caddy.exe'
$FfmpegBin       = Join-Path $ToolsDir 'ffmpeg\bin'
$PgPrefix        = 'C:\Program Files\PostgreSQL\17'
$PgBin           = Join-Path $PgPrefix 'bin'
$PgData          = Join-Path $PgPrefix 'data'
$PgService       = 'postgresql-x64-17'
$ApiService      = 'ReelGuoApi'
$WorkerService   = 'ReelGuoWorker'
$WebService      = 'ReelGuoWeb'
$TunnelService   = 'cloudflared'
$BackupTaskName  = 'ReelGuo staging backup'
$KeepReleases    = 5

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

# Runs a program and stops the script when it fails. Arguments without spaces or quotes are passed as they are.
function Invoke-Checked([string]$What, [string]$Exe, [string[]]$Arguments) {
    # Output goes to the screen, not into the caller's return value.
    & $Exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { Stop-WithError "$What failed (exit code $LASTEXITCODE)." }
}

# ---------------------------------------------------------------------------------------------------------------
# Settings (C:\reelguo-staging\staging.env): KEY=VALUE lines, comments kept as they are
# ---------------------------------------------------------------------------------------------------------------

function Read-Settings {
    $map = @{}
    if (-not (Test-Path $SettingsFile)) { return $map }
    foreach ($line in @(Read-SettingsLines)) {
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

# The admin console's own address (REELGUO_ADMIN_HOST), as a bare host name: "https://Admin.x.com/" -> "admin.x.com".
function Get-AdminHost {
    return (((Get-Setting 'REELGUO_ADMIN_HOST').Trim().ToLowerInvariant()) -replace '^https?://', '' -replace '/.*$', '')
}

function Set-Setting([string]$Key, [string]$Value) {
    $lines = New-Object System.Collections.Generic.List[string]
    if (Test-Path $SettingsFile) {
        $existing = @(Read-SettingsLines)
        if ($existing.Count -gt 0) { $lines.AddRange([string[]]$existing) }
    }
    $done = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") { $lines[$i] = "$Key=$Value"; $done = $true; break }
    }
    if (-not $done) { $lines.Add("$Key=$Value") }
    Write-TextFile $SettingsFile (($lines -join "`r`n") + "`r`n")
}

# UTF-8 without a byte-order mark.
function Write-TextFile([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

function New-Base64Secret([int]$Bytes = 32) {
    $b = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($b)
}

# Letters and digits without look-alikes (0/O, 1/l/I): safe in SQL, connection strings and the command line.
function New-Password([int]$Length = 24) {
    $alphabet = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $b = New-Object byte[] ($Length * 2)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    $chars = for ($i = 0; $i -lt $Length; $i++) { $alphabet[([int]$b[2 * $i] * 256 + [int]$b[2 * $i + 1]) % $alphabet.Length] }
    return -join $chars
}

# The staging folder holds secrets: only SYSTEM and Administrators (by SID, so it works on any Windows language).
# The folder gets those two entries, and everything inside is reset to inherit them. (Giving the entries to every file
# with icacls /T would not work: it strips each file's inherited entries but can't put the folder-style ones on a
# file, which leaves files nobody can open. Earlier versions of this script did that; this also repairs it.)
function Protect-StagingHome {
    & icacls $StagingHome /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /C /Q | Out-Null
    $code = $LASTEXITCODE
    if ($code -eq 0 -and @(Get-ChildItem $StagingHome -Force -ErrorAction SilentlyContinue).Count -gt 0) {
        & icacls (Join-Path $StagingHome '*') /reset /T /C /Q | Out-Null
        $code = $LASTEXITCODE
    }
    if ($code -eq 0) { Write-Ok "$StagingHome is limited to SYSTEM and Administrators" }
    else { Write-Warn "Could not restrict access to $StagingHome (icacls exit code $code)" }
}

# The settings file's lines. If access is denied (files locked by an earlier version of Protect-StagingHome), the
# folder's permissions are repaired once and the file is read again.
function Read-SettingsLines {
    try { return [System.IO.File]::ReadAllLines($SettingsFile) }
    catch {
        $e = $_.Exception
        while ($e.InnerException) { $e = $e.InnerException }
        if ($e -isnot [System.UnauthorizedAccessException]) { throw }
        $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Stop-WithError "Access to $SettingsFile was denied. Run PowerShell as Administrator."
        }
        Write-Note "access to $SettingsFile was denied: repairing the permissions of $StagingHome"
        Protect-StagingHome
        return [System.IO.File]::ReadAllLines($SettingsFile)
    }
}

function Test-SettingsReady {
    if (-not (Test-Path $SettingsFile)) { Stop-WithError "$SettingsFile does not exist yet. Run .\scripts\staging\setup.ps1 first." }
    foreach ($k in @('REELGUO_APP_URL', 'REELGUO_MEDIA_KEY', 'REELGUO_JWT_KEY', 'REELGUO_ADMIN_PASSWORD', 'REELGUO_PG_SUPERPASSWORD', 'REELGUO_DB_PASSWORD')) {
        if ((Get-Setting $k) -eq '') { Stop-WithError "$k is empty in $SettingsFile." }
    }
    $mode = Get-Setting 'REELGUO_STAGING_MODE' 'tunnel'
    if ($mode -notin @('tunnel', 'public-port')) { Stop-WithError "REELGUO_STAGING_MODE must be tunnel or public-port (it is '$mode')." }
    if ($mode -eq 'tunnel' -and (Get-Setting 'CLOUDFLARE_TUNNEL_TOKEN') -eq '') {
        Stop-WithError "CLOUDFLARE_TUNNEL_TOKEN is empty in $SettingsFile (tunnel mode)."
    }
    $adminHost = Get-AdminHost
    if ($adminHost -and $adminHost -notmatch '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$') {
        Stop-WithError "REELGUO_ADMIN_HOST must be a host name such as admin.yourdomain.com (it is '$adminHost')."
    }
    if ($adminHost) {
        $appHost = ''
        try { $appHost = ([uri](Get-Setting 'REELGUO_APP_URL')).Host } catch { $appHost = '' }
        if ($adminHost -eq $appHost) { Stop-WithError 'REELGUO_ADMIN_HOST must differ from the site address (REELGUO_APP_URL).' }
    }
    $otlpEndpoint = Get-Setting 'REELGUO_OTLP_ENDPOINT'
    if ($otlpEndpoint -ne '' -and $otlpEndpoint -notmatch '^https?://[^\s]+$') {
        Stop-WithError "REELGUO_OTLP_ENDPOINT must be an http:// or https:// address (it is '$otlpEndpoint'), or empty."
    }
    if ((Get-Setting 'REELGUO_OTLP_PROTOCOL' 'http/protobuf') -notin @('http/protobuf', 'grpc')) {
        Stop-WithError 'REELGUO_OTLP_PROTOCOL must be http/protobuf or grpc.'
    }
    if ((Get-Setting 'REELGUO_TRACE_DAYS' '7') -notmatch '^[1-9][0-9]?$') { Stop-WithError 'REELGUO_TRACE_DAYS must be a number of days, 1 to 99.' }
    $provider = Get-Setting 'REELGUO_VIDEO_PROVIDER' 'Hls'
    if ($provider -notin @('Hls', 'Cos')) { Stop-WithError "REELGUO_VIDEO_PROVIDER must be Hls or Cos (it is '$provider')." }
    if ($provider -eq 'Cos') {
        foreach ($k in @('REELGUO_COS_SECRET_ID', 'REELGUO_COS_SECRET_KEY', 'REELGUO_COS_BUCKET', 'REELGUO_COS_REGION')) {
            if ((Get-Setting $k) -eq '') { Stop-WithError "$k is empty in $SettingsFile (REELGUO_VIDEO_PROVIDER=Cos)." }
        }
        if ((Get-Setting 'REELGUO_COS_BUCKET') -notmatch '^[a-z0-9][a-z0-9-]*-[0-9]{5,}$') {
            Stop-WithError 'REELGUO_COS_BUCKET must be the full bucket name including the APPID, for example reelguo-1300000000.'
        }
    }
}

# Adds settings that a newer staging.env.example has and this server's staging.env lacks, with their default values,
# so an update never runs with a setting missing. Existing values are never touched.
function Add-NewSettings {
    if (-not (Test-Path $SettingsFile) -or -not (Test-Path $SettingsExample)) { return }
    $have = Read-Settings
    $added = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadAllLines($SettingsExample)) {
        if ($line -match '^\s*([A-Z0-9_]+)\s*=(.*)$' -and -not $have.ContainsKey($Matches[1])) { $added.Add($line.Trim()) }
    }
    if ($added.Count -eq 0) { return }
    $text = ((@(Read-SettingsLines)) -join "`r`n").TrimEnd() + "`r`n`r`n# Added by the update on $(Get-Date -Format 'yyyy-MM-dd'); see infra\staging\staging.env.example for what each one does.`r`n" + ($added -join "`r`n") + "`r`n"
    Write-TextFile $SettingsFile $text
    Write-Note "new settings added to ${SettingsFile}: $(($added | ForEach-Object { ($_ -split '=')[0] }) -join ', ')"
}

# ---------------------------------------------------------------------------------------------------------------
# Server checks and tools
# ---------------------------------------------------------------------------------------------------------------

function Test-Administrator {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Stop-WithError 'Run PowerShell as Administrator (right-click -> Run as administrator): the scripts install Windows services.'
    }
}

function Update-PathFromSystem {
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
    # npm's global folder, where corepack and its pnpm live when corepack was installed with npm (Node.js 25 and later).
    if ($env:APPDATA) {
        $npmGlobal = Join-Path $env:APPDATA 'npm'
        if ((Test-Path $npmGlobal) -and (($env:PATH -split ';') -notcontains $npmGlobal)) { $env:PATH = "$env:PATH;$npmGlobal" }
    }
}

# Start-Process joins its arguments with spaces and adds no quotes, so a value with a space (C:\Program Files\...)
# would arrive as two arguments. This quotes such values.
function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

# cloudflared from PATH, or where its MSI installs it (the installer does not always add it to PATH).
function Get-CloudflaredExe {
    $cmd = Get-Command cloudflared -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    foreach ($dir in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($dir -and (Test-Path (Join-Path $dir 'cloudflared\cloudflared.exe'))) { return (Join-Path $dir 'cloudflared\cloudflared.exe') }
    }
    return $null
}

function Test-Prerequisites {
    Write-Step 'Checking this server'
    if ($PSVersionTable.PSVersion -lt [version]'5.1') { Stop-WithError 'PowerShell 5.1 or newer is needed.' }
    Test-Administrator
    Write-Ok "PowerShell $($PSVersionTable.PSVersion), running as Administrator"
    if ($StagingHome -match '\s') { Stop-WithError "The staging folder must not contain spaces: $StagingHome" }
    $drive = Get-PSDrive -Name ($StagingHome.Substring(0, 1)) -ErrorAction SilentlyContinue
    if ($drive -and $drive.Free) {
        $freeGb = [math]::Round($drive.Free / 1GB, 1)
        if ($freeGb -lt 15) { Write-Warn "Only $freeGb GB free on $($drive.Name): (30 GB or more is advised: tools, releases, media, backups)" }
        else { Write-Ok "$freeGb GB free on $($drive.Name):" }
    }
    Update-PathFromSystem
}

function Install-WingetPackage([string]$Id, [string]$Name, [scriptblock]$IsInstalled) {
    if (& $IsInstalled) { Write-Ok "$Name is installed"; return }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Stop-WithError "winget is not available; install $Name by hand, then run this script again." }
    Write-Note "installing $Name (winget $Id) ..."
    & winget install --id $Id --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    Update-PathFromSystem
    if (-not (& $IsInstalled)) { Stop-WithError "$Name did not install (winget $Id). Install it by hand, then run this script again." }
    Write-Ok "$Name installed"
}

function Test-DotnetSdk10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    return [bool]((& dotnet --list-sdks) -match '^10\.')
}

# Node.js 22.12 or newer (package.json "engines").
function Test-NodeReady {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return $false }
    $v = ((& node --version) -replace '^v', '').Trim()
    try { return ([version]$v -ge [version]'22.12.0') } catch { return $false }
}

function Install-Tools {
    Write-Step 'Tools (installed once)'
    Install-WingetPackage 'Git.Git' 'Git' { [bool](Get-Command git -ErrorAction SilentlyContinue) }
    Install-WingetPackage 'OpenJS.NodeJS.LTS' 'Node.js (22.12 or newer)' { Test-NodeReady }
    Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK' { Test-DotnetSdk10 }
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel') {
        Install-WingetPackage 'Cloudflare.cloudflared' 'cloudflared' { [bool](Get-CloudflaredExe) }
    }

    # pnpm, at the version the repository asks for (package.json "packageManager"), through corepack. Node.js 24 and
    # earlier include corepack; Node.js 25 and later (the LTS since October 2026) don't, so it is installed with npm.
    $env:COREPACK_ENABLE_DOWNLOAD_PROMPT = '0'
    if (-not (Get-Command corepack -ErrorAction SilentlyContinue)) {
        Write-Note 'installing corepack with npm (Node.js 25 and later no longer include it) ...'
        Invoke-Checked 'Installing corepack' 'npm' @('install', '--global', 'corepack@latest')
        Update-PathFromSystem
        if (-not (Get-Command corepack -ErrorAction SilentlyContinue)) { Stop-WithError 'corepack was installed but is not on PATH. Close PowerShell, open it again as Administrator and run setup.ps1 again.' }
    }
    Invoke-Checked 'corepack enable (pnpm)' 'corepack' @('enable')
    Write-Ok "pnpm enabled (corepack), Node.js $(& node --version)"

    New-Item -ItemType Directory -Force -Path (Join-Path $ToolsDir 'caddy'), $FfmpegBin | Out-Null
    if (-not (Test-Path $CaddyExe)) {
        Write-Note 'downloading Caddy (web server) ...'
        Invoke-WebRequest 'https://caddyserver.com/api/download?os=windows&arch=amd64' -OutFile $CaddyExe -UseBasicParsing
    }
    Write-Ok "Caddy: $((& $CaddyExe version) -split ' ' | Select-Object -First 1)"

    if (-not (Test-Path (Join-Path $FfmpegBin 'ffmpeg.exe'))) {
        Write-Note 'downloading ffmpeg (gyan.dev release essentials) ...'
        $zip = Join-Path $env:TEMP 'reelguo-ffmpeg.zip'
        $tmp = Join-Path $env:TEMP 'reelguo-ffmpeg'
        Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip -UseBasicParsing
        if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
        Expand-Archive $zip -DestinationPath $tmp -Force
        $bin = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
        if (-not $bin) { Stop-WithError 'The ffmpeg download did not contain ffmpeg.exe.' }
        Copy-Item (Join-Path $bin.DirectoryName '*') $FfmpegBin -Force
        Remove-Item $zip, $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Ok "ffmpeg: $(((& (Join-Path $FfmpegBin 'ffmpeg.exe') -version) | Select-Object -First 1) -replace 'Copyright.*', '')"
}

# ---------------------------------------------------------------------------------------------------------------
# PostgreSQL
# ---------------------------------------------------------------------------------------------------------------

function Install-PostgreSql {
    Write-Step 'PostgreSQL 17'
    if (Get-Service $PgService -ErrorAction SilentlyContinue) { Write-Ok "service $PgService is installed"; return }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Stop-WithError 'winget is not available to download PostgreSQL.' }
    $dir = Join-Path $env:TEMP 'reelguo-postgresql'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Write-Note 'downloading the PostgreSQL 17 installer (EDB, through winget) ...'
    & winget download --id PostgreSQL.PostgreSQL.17 --exact --source winget --download-directory $dir --accept-package-agreements --accept-source-agreements --disable-interactivity
    $installer = Get-ChildItem $dir -Filter *.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $installer) { Stop-WithError 'Could not download the PostgreSQL installer. Download "PostgreSQL 17 Windows x86-64" from https://www.enterprisedb.com/downloads/postgres-postgresql-downloads, run it once (keep the defaults, note the password), put that password in REELGUO_PG_SUPERPASSWORD and run setup.ps1 again.' }
    Write-Note 'installing PostgreSQL 17 (unattended, a few minutes) ...'
    $arguments = @('--mode', 'unattended', '--unattendedmodeui', 'none',
        '--superpassword', (Get-Setting 'REELGUO_PG_SUPERPASSWORD'), '--serverport', '5432',
        '--servicename', $PgService, '--prefix', $PgPrefix, '--datadir', $PgData,
        '--disable-components', 'pgAdmin,stackbuilder') | ForEach-Object { ConvertTo-ProcessArgument $_ }
    $p = Start-Process -FilePath $installer.FullName -ArgumentList $arguments -Wait -PassThru
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0 -or -not (Get-Service $PgService -ErrorAction SilentlyContinue)) {
        Stop-WithError "The PostgreSQL installer failed (exit code $($p.ExitCode)). Its log is in $env:TEMP (install-postgresql.log)."
    }
    Write-Ok "PostgreSQL 17 installed (service $PgService, data in $PgData)"
}

function Invoke-Psql([string]$Sql, [string]$Database = 'postgres') {
    $file = Join-Path $env:TEMP "reelguo-$([guid]::NewGuid().ToString('N')).sql"
    Write-TextFile $file $Sql
    $env:PGPASSWORD = Get-Setting 'REELGUO_PG_SUPERPASSWORD'
    try {
        & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p 5432 -U postgres -d $Database -v ON_ERROR_STOP=1 -q -X -f $file
        $code = $LASTEXITCODE
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }
    if ($code -ne 0) { Stop-WithError "A database command failed (psql exit code $code)." }
}

# The app's login and database (UTF-8, C collation, as on production), listening on this server only.
function Initialize-Database {
    Write-Step 'Database'
    Start-Service $PgService
    $db = Get-Setting 'REELGUO_DB_NAME' 'reelguo'
    $user = Get-Setting 'REELGUO_DB_USER' 'reelguo'
    $pw = Get-Setting 'REELGUO_DB_PASSWORD'
    Invoke-Psql @"
SELECT format('CREATE ROLE %I LOGIN', '$user') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$user')\gexec
ALTER ROLE "$user" WITH LOGIN PASSWORD '$pw';
SELECT format('CREATE DATABASE %I OWNER %I ENCODING ''UTF8'' LC_COLLATE ''C'' LC_CTYPE ''C'' TEMPLATE template0', '$db', '$user')
  WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$db')\gexec
ALTER SYSTEM SET listen_addresses = 'localhost';
"@
    Write-Ok "database '$db' and login '$user' ready (UTF-8)"
    # listen_addresses takes effect on a restart, which is needed only the first time (the installer's default is every
    # address). -Force: on later runs ReelGuoApi and ReelGuoWorker depend on PostgreSQL; they are started again later.
    if ((Get-PsqlValue 'SHOW listen_addresses') -ne 'localhost') {
        Restart-Service $PgService -Force
        Write-Ok 'PostgreSQL restarted: it listens on this server only'
    } else {
        Write-Ok 'PostgreSQL listens on this server only'
    }
}

# One value from PostgreSQL, as the superuser (for example SHOW listen_addresses).
function Get-PsqlValue([string]$Sql) {
    $env:PGPASSWORD = Get-Setting 'REELGUO_PG_SUPERPASSWORD'
    try {
        $out = & (Join-Path $PgBin 'psql.exe') -h 127.0.0.1 -p 5432 -U postgres -d postgres -X -t -A -c $Sql
        $code = $LASTEXITCODE
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
    if ($code -ne 0) { Stop-WithError "A database query failed (psql exit code $code): $Sql" }
    return ($out | Out-String).Trim()
}

# ---------------------------------------------------------------------------------------------------------------
# Building a release: C:\reelguo-staging\releases\<yyyyMMdd-HHmmss>-<commit>\{api,worker,web\client,web\admin,Caddyfile}
# ---------------------------------------------------------------------------------------------------------------

function Get-Commit {
    if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { return 'nogit' }
    $c = & git -C $RepoRoot rev-parse --short --verify --quiet HEAD
    if ($LASTEXITCODE -ne 0 -or -not $c) { return 'nocommit' }
    return $c
}

function New-Release {
    $id = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), (Get-Commit)
    $dir = Join-Path $ReleasesDir $id
    Write-Step "Building release $id (5 to 15 minutes the first time)"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'

    Push-Location $RepoRoot
    try {
        Write-Note 'API (.NET publish) ...'
        Invoke-Checked 'Building the API' 'dotnet' @('publish', 'backend\src\ReelGuo.Api\ReelGuo.Api.csproj', '-c', 'Release', '-o', (Join-Path $dir 'api'), '--nologo', '-v', 'quiet', '-p:OpenApiGenerateDocuments=false')
        Write-Note 'worker (.NET publish) ...'
        Invoke-Checked 'Building the worker' 'dotnet' @('publish', 'backend\src\ReelGuo.Worker\ReelGuo.Worker.csproj', '-c', 'Release', '-o', (Join-Path $dir 'worker'), '--nologo', '-v', 'quiet')
        Write-Note 'web app and admin console (pnpm) ...'
        $env:COREPACK_ENABLE_DOWNLOAD_PROMPT = '0'
        Invoke-Checked 'Installing web packages' 'pnpm' @('install', '--frozen-lockfile')
        Invoke-Checked 'Building the web apps' 'pnpm' @('--filter', './web/**', 'build')
    } finally {
        Pop-Location
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'web') | Out-Null
    Copy-Item (Join-Path $RepoRoot 'web\apps\client\dist') (Join-Path $dir 'web\client') -Recurse -Force
    Copy-Item (Join-Path $RepoRoot 'web\apps\admin\dist') (Join-Path $dir 'web\admin') -Recurse -Force
    Write-ReleaseConfig $dir
    Write-Ok "release built: $dir"
    return $dir
}

# Settings for the API and the worker (appsettings.Production.json) and the Caddyfile, from staging.env.
function Write-ReleaseConfig([string]$Dir) {
    $apiPort = Get-Setting 'REELGUO_API_PORT' '5080'
    $connection = 'Host=127.0.0.1;Port=5432;Database={0};Username={1};Password={2}' -f (Get-Setting 'REELGUO_DB_NAME' 'reelguo'), (Get-Setting 'REELGUO_DB_USER' 'reelguo'), (Get-Setting 'REELGUO_DB_PASSWORD')
    $appUrl = Get-Setting 'REELGUO_APP_URL'
    $storage = @{ Provider = 'FileSystem'; FileSystemRoot = $StorageDir }
    $logging = @{
        LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning'; 'Microsoft.EntityFrameworkCore' = 'Warning' }
    }

    # Episode videos: Hls keeps them on this server (transcoded); Cos sends them from the admin's browser straight to a
    # private Tencent COS bucket, laid out as videos/<folder>/<title>/<file>, and viewers play them from there.
    $video = [ordered]@{
        Provider = (Get-Setting 'REELGUO_VIDEO_PROVIDER' 'Hls')
        Cos      = [ordered]@{
            SecretId           = (Get-Setting 'REELGUO_COS_SECRET_ID')
            SecretKey          = (Get-Setting 'REELGUO_COS_SECRET_KEY')
            Bucket             = (Get-Setting 'REELGUO_COS_BUCKET')
            Region             = (Get-Setting 'REELGUO_COS_REGION' 'ap-singapore')
            Folder             = (Get-Setting 'REELGUO_COS_FOLDER' 'reelguo')
            CdnHost            = (Get-Setting 'REELGUO_COS_CDN_HOST')
            PlaybackUrlMinutes = [int](Get-Setting 'REELGUO_COS_PLAYBACK_MINUTES' '30')
            UploadUrlMinutes   = [int](Get-Setting 'REELGUO_COS_UPLOAD_MINUTES' '360')
        }
    }

    # Monitoring: failed and slow requests/jobs and warning/error logs are kept in the database for the admin page
    # "tracing" (backend Telemetry/LocalTelemetry.cs). Optionally everything is also sent to an OTLP backend.
    $telemetry = @{ Local = @{ RetentionDays = [int](Get-Setting 'REELGUO_TRACE_DAYS' '7') } }
    $otlp = [ordered]@{}
    if ((Get-Setting 'REELGUO_OTLP_ENDPOINT') -ne '') {
        $otlp['OTEL_EXPORTER_OTLP_ENDPOINT'] = Get-Setting 'REELGUO_OTLP_ENDPOINT'
        $otlp['OTEL_EXPORTER_OTLP_PROTOCOL'] = Get-Setting 'REELGUO_OTLP_PROTOCOL' 'http/protobuf'
        if ((Get-Setting 'REELGUO_OTLP_HEADERS') -ne '') { $otlp['OTEL_EXPORTER_OTLP_HEADERS'] = Get-Setting 'REELGUO_OTLP_HEADERS' }
    }

    $site = @{}
    foreach ($pair in @(@('PermanentDomain', 'REELGUO_PERMANENT_DOMAIN'), @('Telegram', 'REELGUO_TELEGRAM'), @('Email', 'REELGUO_CONTACT_EMAIL'))) {
        $v = Get-Setting $pair[1]
        if ($v -ne '') { $site[$pair[0]] = $v }
    }

    $api = [ordered]@{
        Urls              = "http://127.0.0.1:$apiPort"
        ConnectionStrings = @{ Default = $connection }
        Database          = @{ MigrateOnStartup = $true }
        Admin             = @{ BootstrapUsername = 'admin'; BootstrapPassword = (Get-Setting 'REELGUO_ADMIN_PASSWORD') }
        Media             = @{ SigningKey = (Get-Setting 'REELGUO_MEDIA_KEY'); PublicBaseUrl = '/media' }
        Auth              = @{ JwtKey = (Get-Setting 'REELGUO_JWT_KEY') }
        Storage           = $storage
        Video             = $video
        # Every request comes from Caddy on this server; behind Cloudflare the visitor's IP and country are in its headers.
        Network           = @{ TrustedProxies = @('127.0.0.1/32', '::1/128'); ClientIpHeader = 'CF-Connecting-IP' }
        Geo               = @{ TrustCountryHeader = $true }
        App               = @{ Mirrors = @($appUrl) }
        Seo               = @{
            TemplatePath   = (Join-Path $CurrentLink 'web\client\index.html')
            AllowIndexing  = ((Get-Setting 'REELGUO_ALLOW_INDEXING' 'false') -eq 'true')
            PublicBaseUrl  = $appUrl
        }
        Billing           = @{
            Enabled                         = ((Get-Setting 'REELGUO_BILLING_ENABLED' 'false') -eq 'true')
            Provider                        = (Get-Setting 'REELGUO_PAYMENT_PROVIDER')
            BillingDescriptor               = (Get-Setting 'REELGUO_BILLING_DESCRIPTOR')
            AppBaseUrl                      = $appUrl
            AllowSandboxOutsideDevelopment  = ((Get-Setting 'REELGUO_ALLOW_SANDBOX_PAYMENTS' 'false') -eq 'true')
            SandboxSecret                   = (Get-Setting 'REELGUO_SANDBOX_SECRET')
        }
        Logging           = $logging + @{ EventLog = @{ SourceName = 'ReelGuo.Api'; LogLevel = @{ Default = 'Information'; 'Microsoft.AspNetCore' = 'Warning'; 'Microsoft.EntityFrameworkCore' = 'Warning' } } }
    }
    if ($site.Count -gt 0) { $api['Site'] = $site }

    $worker = [ordered]@{
        ConnectionStrings = @{ Default = $connection }
        Storage           = $storage
        Video             = $video
        Ffmpeg            = @{ BinDirectory = $FfmpegBin }
        Logging           = $logging + @{ EventLog = @{ SourceName = 'ReelGuo.Worker'; LogLevel = @{ Default = 'Information' } } }
    }
    foreach ($settings in @($api, $worker)) {
        $settings['Telemetry'] = $telemetry
        foreach ($key in $otlp.Keys) { $settings[$key] = $otlp[$key] }
    }

    Write-TextFile (Join-Path $Dir 'api\appsettings.Production.json') ($api | ConvertTo-Json -Depth 10)
    Write-TextFile (Join-Path $Dir 'worker\appsettings.Production.json') ($worker | ConvertTo-Json -Depth 10)

    # Caddy: paths with forward slashes; through the "current" link, so a release switch needs no new service.
    $tunnel = (Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel'
    $appPort = Get-Setting 'REELGUO_WEB_PORT' $(if ($tunnel) { '8080' } else { '80' })
    # Public-port mode: the admin console can have its own address through Cloudflare (REELGUO_ADMIN_HOST). It is
    # served on the app's port and told apart by the host name; every other host name gets the app.
    $adminSite = ''
    $adminHost = Get-AdminHost
    if (-not $tunnel -and $adminHost) {
        $adminSite = "`n# The admin console through Cloudflare (public-port mode, REELGUO_ADMIN_HOST): the app's port, chosen by host name.`nhttp://${adminHost}:$appPort {`n`tbind 0.0.0.0`n`timport admin_console`n}`n"
    }
    $caddy = [System.IO.File]::ReadAllText($CaddyTemplate)
    $caddy = $caddy.Replace('{{APP_PORT}}', $appPort)
    $caddy = $caddy.Replace('{{APP_BIND}}', $(if ($tunnel) { '127.0.0.1 [::1]' } else { '0.0.0.0' }))
    $caddy = $caddy.Replace('{{ADMIN_HOST_SITE}}', $adminSite)
    $caddy = $caddy.Replace('{{ADMIN_PORT}}', (Get-Setting 'REELGUO_ADMIN_PORT' '8081'))
    $caddy = $caddy.Replace('{{API}}', "127.0.0.1:$apiPort")
    $caddy = $caddy.Replace('{{WEB_ROOT}}', ((Join-Path $CurrentLink 'web') -replace '\\', '/'))
    $caddy = $caddy.Replace('{{LOG_DIR}}', ($LogDir -replace '\\', '/'))
    Write-TextFile (Join-Path $Dir 'Caddyfile') $caddy
}

function Get-CurrentRelease {
    if (-not (Test-Path $CurrentLink)) { return $null }
    $target = @((Get-Item $CurrentLink -Force).Target) | Select-Object -First 1
    if (-not $target) { return $null }
    return (Split-Path $target -Leaf)
}

function Get-Releases {
    if (-not (Test-Path $ReleasesDir)) { return @() }
    return @(Get-ChildItem $ReleasesDir -Directory | Sort-Object Name -Descending | ForEach-Object { $_.Name })
}

# Points "current" at a release. The services must be stopped (stop/start is done by Switch-Release).
function Set-CurrentRelease([string]$Id) {
    $target = Join-Path $ReleasesDir $Id
    if (-not (Test-Path $target)) { Stop-WithError "No such release: $Id" }
    if (Test-Path $CurrentLink) { [System.IO.Directory]::Delete($CurrentLink) }   # removes the link, not the release
    New-Item -ItemType Junction -Path $CurrentLink -Target $target | Out-Null
}

function Switch-Release([string]$Id) {
    Write-Step "Switching to release $Id"
    Stop-ReelGuo
    Set-CurrentRelease $Id
    Start-ReelGuo
    Write-Ok "running release $Id"
}

# Keeps the newest releases (and the current one), removes the rest.
function Remove-OldReleases {
    $current = Get-CurrentRelease
    $all = Get-Releases
    $keep = @($all | Select-Object -First $KeepReleases) + @($current)
    foreach ($r in $all) {
        if ($keep -notcontains $r) {
            Remove-Item (Join-Path $ReleasesDir $r) -Recurse -Force -ErrorAction SilentlyContinue
            Write-Note "removed old release $r"
        }
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Windows services
# ---------------------------------------------------------------------------------------------------------------

function Set-ServiceDefinition([string]$Name, [string]$DisplayName, [string]$ImagePath, [string[]]$DependsOn, [string[]]$Environment) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Get-Service $Name -ErrorAction SilentlyContinue)) {
        $params = @{ Name = $Name; BinaryPathName = $ImagePath; DisplayName = $DisplayName; StartupType = 'Automatic' }
        if ($DependsOn) { $params['DependsOn'] = $DependsOn }
        New-Service @params | Out-Null
        Write-Ok "service $Name created"
    } else {
        Set-ItemProperty $key -Name ImagePath -Value $ImagePath
        if ($DependsOn) { Set-ItemProperty $key -Name DependOnService -Type MultiString -Value $DependsOn }
        Set-Service $Name -StartupType Automatic
        Write-Ok "service $Name updated"
    }
    if ($Environment) { Set-ItemProperty $key -Name Environment -Type MultiString -Value $Environment }
    # Restart after a crash: 5 s, 15 s, then every minute; the failure count resets after a day.
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
}

function Install-ReelGuoServices {
    Write-Step 'Windows services'
    Set-ServiceDefinition $ApiService 'ReelGuo API' (Join-Path $CurrentLink 'api\ReelGuo.Api.exe') @($PgService) @('ASPNETCORE_ENVIRONMENT=Production')
    Set-ServiceDefinition $WorkerService 'ReelGuo worker' (Join-Path $CurrentLink 'worker\ReelGuo.Worker.exe') @($PgService) @('DOTNET_ENVIRONMENT=Production')
    Set-ServiceDefinition $WebService 'ReelGuo web (Caddy)' ('{0} run --config {1} --adapter caddyfile' -f $CaddyExe, (Join-Path $CurrentLink 'Caddyfile')) @($ApiService) $null
}

# Cloudflare Tunnel connector as a Windows service (re-installed when the token changes).
function Install-TunnelService {
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -ne 'tunnel') { return }
    Write-Step 'Cloudflare Tunnel connector'
    $token = Get-Setting 'CLOUDFLARE_TUNNEL_TOKEN'
    $cloudflared = Get-CloudflaredExe
    if (-not $cloudflared) { Stop-WithError 'cloudflared was not found. Run setup.ps1 (it installs it), or install it by hand: winget install --id Cloudflare.cloudflared' }
    $existing = Get-Service $TunnelService -ErrorAction SilentlyContinue
    if ($existing) {
        $image = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$TunnelService").ImagePath
        if ($image -like "*$token*") { Write-Ok 'cloudflared service is installed with this token'; return }
        Write-Note 'the tunnel token changed: re-installing the connector'
        & $cloudflared service uninstall | Out-Null
        Start-Sleep -Seconds 3
    }
    & $cloudflared service install $token
    if ($LASTEXITCODE -ne 0 -or -not (Get-Service $TunnelService -ErrorAction SilentlyContinue)) { Stop-WithError 'Could not install the cloudflared service (check the token).' }
    Write-Ok 'cloudflared service installed'
}

function Stop-ReelGuo {
    foreach ($s in @($WebService, $WorkerService, $ApiService)) {
        if (Get-Service $s -ErrorAction SilentlyContinue) { Stop-Service $s -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Seconds 2
}

function Start-ReelGuo {
    $names = @($PgService, $ApiService, $WorkerService, $WebService)
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel') { $names += $TunnelService }
    foreach ($s in $names) {
        if (-not (Get-Service $s -ErrorAction SilentlyContinue)) { continue }
        # A service that doesn't start is reported here and by the readiness check that follows, which prints its
        # logs; the script doesn't stop on Windows' bare error message.
        try { Start-Service $s -ErrorAction Stop }
        catch { Write-Warn "service $s did not start: $($_.Exception.Message)" }
    }
}

# Caddy (ReelGuoWeb) needs the app port to itself. Another web server on it, typically IIS, would keep the site from
# starting, so this stops early and says what holds the port. IIS is not needed: Caddy is the web server.
function Test-WebPortFree {
    $port = [int](Get-Setting 'REELGUO_WEB_PORT' '8080')
    $owners = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($id in $owners) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'caddy') { continue }
        if ($id -eq 4) {
            # PID 4 is Windows' own HTTP listener (HTTP.sys), which IIS and some other Windows services use.
            $hint = 'Windows HTTP.sys (usually IIS).'
            if (Get-Service W3SVC -ErrorAction SilentlyContinue) {
                $hint += ' IIS is installed and not needed. Stop it: Stop-Service W3SVC -Force; Set-Service W3SVC -StartupType Disabled  (or remove it: Uninstall-WindowsFeature Web-Server). Then run this script again.'
            }
        } else {
            $hint = "process $id ($(if ($process) { $process.ProcessName } else { 'unknown' })). Stop it or move it to another port, then run this script again."
        }
        Stop-WithError "Port $port is already in use by $hint"
    }
    Write-Ok "port $port is free for the web server"
}

# Public-port mode: Windows Firewall lets only Cloudflare reach the app port.
function Set-CloudflareFirewall {
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -ne 'public-port') { return }
    Write-Step 'Windows Firewall (public-port mode)'
    $port = [int](Get-Setting 'REELGUO_WEB_PORT' '80')
    $ranges = @((Invoke-RestMethod 'https://www.cloudflare.com/ips-v4') -split "`n") + @((Invoke-RestMethod 'https://www.cloudflare.com/ips-v6') -split "`n") |
        ForEach-Object { $_.Trim() } | Where-Object { $_ }
    Get-NetFirewallRule -DisplayName 'ReelGuo staging (Cloudflare only)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'ReelGuo staging (Cloudflare only)' -Direction Inbound -Protocol TCP -LocalPort $port -RemoteAddress $ranges -Action Allow | Out-Null
    Write-Ok "port $port open to Cloudflare's $($ranges.Count) address ranges only"
    # Some cloud images (Tencent's among them) ship with Windows Firewall switched off; the rule above then does nothing.
    $off = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | Where-Object { -not $_.Enabled } | ForEach-Object { $_.Name })
    if ($off.Count -gt 0) {
        Write-Warn "Windows Firewall is OFF ($($off -join ', ')), so port $port is open to everyone, not only Cloudflare."
        Write-Warn "Allow TCP $port only from Cloudflare's addresses in the cloud security group (docs\STAGING.md, public port, step 2),"
        Write-Warn 'or turn Windows Firewall on (check that Remote Desktop stays allowed first).'
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Checks over HTTP, on this server
# ---------------------------------------------------------------------------------------------------------------

function Get-LocalBase { return "http://127.0.0.1:$(Get-Setting 'REELGUO_WEB_PORT' '8080')" }
function Get-AdminBase { return "http://127.0.0.1:$(Get-Setting 'REELGUO_ADMIN_PORT' '8081')" }
function Get-ApiBase   { return "http://127.0.0.1:$(Get-Setting 'REELGUO_API_PORT' '5080')" }

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

function Test-Site {
    Write-Step 'Checking the site'
    $base = Get-LocalBase
    $ok = $true
    foreach ($s in @($PgService, $ApiService, $WorkerService, $WebService)) {
        $svc = Get-Service $s -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') { Write-Ok "service $s running" } else { Write-Warn "service $s is not running"; $ok = $false }
    }
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
    $adminHost = Get-AdminHost
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'public-port' -and $adminHost) {
        # What Cloudflare gets for the admin address: the console (no server-rendered page content), not the app.
        $page = (& curl.exe -s --max-time 10 -H "Host: $adminHost" "$base/") -join "`n"
        if ($page -match 'id="root"' -and $page -notmatch 'id="seo"') { Write-Ok "admin console for $adminHost on port $(Get-Setting 'REELGUO_WEB_PORT' '80')" }
        else { Write-Warn "requests for $adminHost do not get the admin console"; $ok = $false }
    }
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel') {
        $t = Get-Service $TunnelService -ErrorAction SilentlyContinue
        if ($t -and $t.Status -eq 'Running') { Write-Ok 'Cloudflare tunnel connector running (its connection status is on the tunnel page in Cloudflare)' }
        else { Write-Warn 'Cloudflare tunnel connector (service cloudflared) is not running'; $ok = $false }
    }
    return $ok
}

function Show-RecentLogs([int]$Count = 25) {
    foreach ($source in @('ReelGuo.Api', 'ReelGuo.Worker')) {
        Write-Step "Recent log: $source (Windows Event Log, Application)"
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $source } -MaxEvents $Count -ErrorAction SilentlyContinue
        if ($events) { $events | Sort-Object TimeCreated | ForEach-Object { Write-Host ('{0:HH:mm:ss} {1,-11} {2}' -f $_.TimeCreated, $_.LevelDisplayName, (($_.Message -split "`n")[0..2] -join ' ')) } }
        else { Write-Note '(no entries)' }
    }
    $caddyLog = Join-Path $LogDir 'caddy.log'
    if (Test-Path $caddyLog) { Write-Step 'Recent log: web (Caddy)'; Get-Content $caddyLog -Tail 10 }
}

# ---------------------------------------------------------------------------------------------------------------
# Backups (pg_dump, custom format) in C:\reelguo-staging\backups
# ---------------------------------------------------------------------------------------------------------------

function Invoke-Backup {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $db = Get-Setting 'REELGUO_DB_NAME' 'reelguo'
    $file = Join-Path $BackupDir ('{0}-{1:yyyyMMddTHHmmssZ}.dump' -f $db, (Get-Date).ToUniversalTime())
    $env:PGPASSWORD = Get-Setting 'REELGUO_DB_PASSWORD'
    try {
        & (Join-Path $PgBin 'pg_dump.exe') -h 127.0.0.1 -p 5432 -U (Get-Setting 'REELGUO_DB_USER' 'reelguo') -d $db --format=custom --compress=6 --no-owner --no-privileges --file="$file.partial"
        if ($LASTEXITCODE -ne 0) { Stop-WithError "pg_dump failed (exit code $LASTEXITCODE)." }
        Move-Item "$file.partial" $file -Force
        & (Join-Path $PgBin 'pg_restore.exe') --list $file | Out-Null
        if ($LASTEXITCODE -ne 0) { Stop-WithError "The backup $file is not readable." }
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TextFile "$file.sha256" ("$hash  $(Split-Path $file -Leaf)`n")
    $keepDays = [int](Get-Setting 'REELGUO_BACKUP_KEEP_DAYS' '14')
    Get-ChildItem $BackupDir -File | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$keepDays) } | Remove-Item -Force
    return (Split-Path $file -Leaf)
}

# Replaces the database with a backup, in one transaction. The API and worker must be stopped.
function Invoke-Restore([string]$FileName) {
    $file = Join-Path $BackupDir (Split-Path $FileName -Leaf)
    if (-not (Test-Path $file)) { Stop-WithError "No such backup: $file" }
    if (Test-Path "$file.sha256") {
        $expected = ((Get-Content "$file.sha256" -Raw) -split '\s+')[0]
        if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { Stop-WithError "The backup $file does not match its checksum." }
    }
    $env:PGPASSWORD = Get-Setting 'REELGUO_DB_PASSWORD'
    try {
        & (Join-Path $PgBin 'pg_restore.exe') -h 127.0.0.1 -p 5432 -U (Get-Setting 'REELGUO_DB_USER' 'reelguo') -d (Get-Setting 'REELGUO_DB_NAME' 'reelguo') --clean --if-exists --no-owner --no-privileges --single-transaction --exit-on-error $file
        if ($LASTEXITCODE -ne 0) { Stop-WithError "The restore failed (pg_restore exit code $LASTEXITCODE); the database was left as it was." }
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

# Nightly backup at 03:30 server time, as SYSTEM.
function Register-BackupTask {
    $script = Join-Path $RepoRoot 'scripts\staging\backup.ps1'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
    $trigger = New-ScheduledTaskTrigger -Daily -At '03:30'
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $BackupTaskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
    Write-Ok "nightly database backup scheduled (03:30, task '$BackupTaskName')"
}

# ---------------------------------------------------------------------------------------------------------------
# Deploy record (C:\reelguo-staging\deploy.log)
# ---------------------------------------------------------------------------------------------------------------

function Add-DeployRecord([string]$Action, [string]$Result) {
    $line = '{0:u} | {1,-8} | {2,-30} | {3}' -f (Get-Date).ToUniversalTime(), $Action, (Get-CurrentRelease), $Result
    New-Item -ItemType Directory -Force -Path $StagingHome | Out-Null
    [System.IO.File]::AppendAllText($DeployLog, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
}
