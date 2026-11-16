<#
.SYNOPSIS
    Goes back to the previous release (instantly), and optionally restores a database backup.

.DESCRIPTION
        .\scripts\staging\rollback.ps1                              the release before the current one; database unchanged
        .\scripts\staging\rollback.ps1 -To <release>                a specific release (see -List)
        .\scripts\staging\rollback.ps1 -List                        releases and backups on this server
        .\scripts\staging\rollback.ps1 -RestoreBackup <file>        the previous release AND the database from that backup

    Use -RestoreBackup when the failed update had already changed the database (migrations), with the backup that
    update.ps1 took just before. Everything written to the database after that backup is lost; media files are not
    touched. The code folder is not changed: to stay on the old version, check out the old commit before the next
    update (the commit is the last part of the release name).

.PARAMETER Yes
    Do not ask for confirmation before restoring the database.
#>
param(
    [string]$To,
    [string]$RestoreBackup,
    [switch]$List,
    [switch]$Yes
)

. (Join-Path $PSScriptRoot 'common.ps1')
Test-Administrator
Test-SettingsReady

$current = Get-CurrentRelease
$releases = Get-Releases

if ($List) {
    Write-Step 'Releases (newest first)'
    foreach ($r in $releases) { if ($r -eq $current) { Write-Host "  $r   <- running" -ForegroundColor Green } else { Write-Host "  $r" } }
    Write-Step 'Backups (newest first)'
    if (Test-Path $BackupDir) { Get-ChildItem $BackupDir -Filter *.dump | Sort-Object Name -Descending | ForEach-Object { Write-Host ('  {0}   {1:N1} MB' -f $_.Name, ($_.Length / 1MB)) } }
    exit 0
}

Write-Host 'ReelGuo staging - rollback' -ForegroundColor White

if ($To) {
    $target = $To
    if ($releases -notcontains $target) { Stop-WithError "No such release: $target (see rollback.ps1 -List)" }
} else {
    $index = [array]::IndexOf($releases, $current)
    if ($index -lt 0 -or $index + 1 -ge $releases.Count) { Stop-WithError "There is no release before $current (see rollback.ps1 -List)." }
    $target = $releases[$index + 1]
}
Write-Note "running: $current"
Write-Note "going back to: $target"

if ($RestoreBackup) {
    $file = Split-Path $RestoreBackup -Leaf
    Write-Warn "The database will be replaced by the backup $file. Everything saved after it is lost."
    if (-not $Yes) {
        $answer = Read-Host 'Type the backup file name to confirm'
        if ($answer.Trim() -ne $file) { Stop-WithError 'Not confirmed; nothing was changed.' }
    }
}

Write-Step 'Stopping ReelGuo'
Stop-ReelGuo
if ($RestoreBackup) {
    Write-Step 'Restoring the database'
    Invoke-Restore $file
    Write-Ok "database restored from $file"
}
Set-CurrentRelease $target
Write-Step 'Starting'
Start-ReelGuo

if (-not (Wait-Healthy 300)) {
    Show-RecentLogs
    Add-DeployRecord 'rollback' 'FAILED: not ready'
    Stop-WithError 'The site did not become ready after the rollback.'
}
$siteOk = Test-Site
Add-DeployRecord 'rollback' ($(if ($RestoreBackup) { "from $current, database from $file" } else { "from $current" }))
Write-Step "Rolled back to $target"
Write-Host '  The code folder was not changed. Before the next update, check out the version you want.'
if (-not $siteOk) { exit 2 }
