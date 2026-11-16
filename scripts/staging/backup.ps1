<#
.SYNOPSIS
    One database backup into C:\reelguo-staging\backups (kept REELGUO_BACKUP_KEEP_DAYS days).

.DESCRIPTION
    Run every night at 03:30 by the scheduled task "ReelGuo staging backup" (created by setup.ps1, runs as SYSTEM).
    By hand: .\scripts\staging\ops.ps1 backup. Restore with rollback.ps1 -RestoreBackup <file>.
    Each backup is a pg_dump archive (custom format) with a .sha256 checksum next to it. Copy the backups folder
    off the server regularly (for example to object storage): a backup on the same disk does not survive losing it.
#>
. (Join-Path $PSScriptRoot 'common.ps1')
Test-SettingsReady
$file = Invoke-Backup
Write-Host "$(Get-Date -Format u) backup ok: $(Join-Path $BackupDir $file)"
