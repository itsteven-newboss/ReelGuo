<#
.SYNOPSIS
    Goes back to the images that ran before the last update, and optionally restores a database backup.

.DESCRIPTION
        .\scripts\staging\rollback.ps1                                  previous images, database unchanged
        .\scripts\staging\rollback.ps1 -ListBackups                     show the backups on this server
        .\scripts\staging\rollback.ps1 -RestoreBackup <file>            previous images AND the database from that backup

    Use -RestoreBackup when the failed update had already changed the database (migrations), with the backup that
    update.ps1 took just before. Everything written to the database after that backup is lost; media files are not
    touched. The code folder is not changed: to stay on the old version, check out the old commit
    (see infra\staging.deploy.log) before the next update.

.PARAMETER Yes
    Do not ask for confirmation before restoring the database.
#>
param(
    [string]$RestoreBackup,
    [switch]$ListBackups,
    [switch]$Yes
)

. (Join-Path $PSScriptRoot 'common.ps1')
Test-SettingsReady

if ($ListBackups) {
    Write-Step 'Backups on this server (newest first)'
    Write-Host (Invoke-ComposeOutput run --rm -T --no-deps backup 'ls -1t /backups')
    exit 0
}

Write-Host 'ReelGuo staging - rollback' -ForegroundColor White

if ($RestoreBackup) {
    $file = Split-Path $RestoreBackup -Leaf
    Write-Warn "The database will be replaced by the backup $file. Everything saved after it is lost."
    if (-not $Yes) {
        $answer = Read-Host "Type the backup file name to confirm"
        if ($answer.Trim() -ne $file) { Stop-WithError 'Not confirmed; nothing was changed.' }
    }
}

Write-Step 'Switching back to the previous images'
$restored = 0
foreach ($img in $AppImages) {
    if (& docker images -q "${img}:rollback") {
        & docker tag "${img}:rollback" "${img}:latest"
        if ($LASTEXITCODE -eq 0) { $restored++ }
    }
}
if ($restored -eq 0) { Write-Warn 'no :rollback images found; keeping the current ones' } else { Write-Ok "$restored images back to :rollback" }

if ($RestoreBackup) {
    Write-Step 'Restoring the database'
    Invoke-Compose stop api worker web
    $out = Invoke-ComposeOutput run --rm -T backup "sh /scripts/restore.sh $file"
    Write-Host $out
    if ($out -notmatch 'restore ok') { Stop-WithError 'The restore did not finish. The site is stopped; see the message above.' }
    Write-Ok "database restored from $file"
}

Write-Step 'Starting'
Invoke-Compose up -d --no-build --remove-orphans
if (-not (Wait-Healthy 300)) {
    Show-RecentLogs
    Add-DeployRecord 'rollback' 'FAILED: not ready'
    Stop-WithError 'The site did not become ready after the rollback.'
}
$siteOk = Test-Site
Add-DeployRecord 'rollback' ($(if ($RestoreBackup) { "images + database from $file" } else { 'images' }))
Write-Step 'Rolled back'
Write-Host '  The code folder was not changed. Before the next update, check out the version you want (see infra\staging.deploy.log).'
if (-not $siteOk) { exit 2 }
