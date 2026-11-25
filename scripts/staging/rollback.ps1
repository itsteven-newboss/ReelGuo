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
    [string[]]$RestoreBackup,
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

# Each backup restores the database of the site it was taken from (its file name starts with the database name).
$restores = @()
foreach ($item in @($RestoreBackup | Where-Object { $_ } | ForEach-Object { $_ -split ',' } | Where-Object { $_.Trim() })) {
    $file = Split-Path $item.Trim() -Leaf
    $restores += [pscustomobject]@{ File = $file; Site = (Get-BackupSite $file) }
}
foreach ($r in $restores) {
    $label = Use-Site $r.Site { Get-SiteLabel }
    Write-Warn "The $label's database will be replaced by the backup $($r.File). Everything saved after it is lost."
    if (-not $Yes) {
        $answer = Read-Host 'Type the backup file name to confirm'
        if ($answer.Trim() -ne $r.File) { Stop-WithError 'Not confirmed; nothing was changed.' }
    }
}

Write-Step 'Stopping ReelGuo'
Stop-ReelGuo
foreach ($r in $restores) {
    Write-Step "Restoring the database ($(Use-Site $r.Site { Get-SiteLabel }))"
    Use-Site $r.Site { Invoke-Restore $r.File }
    Write-Ok "database restored from $($r.File)"
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
Add-DeployRecord 'rollback' ($(if ($restores.Count) { "from $current, databases from $(($restores | ForEach-Object { $_.File }) -join ', ')" } else { "from $current" }))
Write-Step "Rolled back to $target"
Write-Host '  The code folder was not changed. Before the next update, check out the version you want.'
if (-not $siteOk) { exit 2 }
