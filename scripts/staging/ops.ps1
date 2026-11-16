<#
.SYNOPSIS
    Day-to-day commands for ReelGuo staging (PowerShell as Administrator).

.DESCRIPTION
    .\scripts\staging\ops.ps1 status                 services, readiness, checks, running release
    .\scripts\staging\ops.ps1 logs [api|worker|web|postgres|tunnel]   recent log entries
    .\scripts\staging\ops.ps1 restart [api|worker|web|postgres|tunnel] restart one service, or everything
    .\scripts\staging\ops.ps1 stop                   stop ReelGuo (database stays up)
    .\scripts\staging\ops.ps1 start                  start everything again
    .\scripts\staging\ops.ps1 config                 apply edited settings (staging.env) to the running release
    .\scripts\staging\ops.ps1 backup                 back up the database now
    .\scripts\staging\ops.ps1 backups                list the backups
    .\scripts\staging\ops.ps1 disk                   disk used by releases, media, backups
#>
param(
    [Parameter(Position = 0)][ValidateSet('status', 'logs', 'restart', 'stop', 'start', 'config', 'backup', 'backups', 'disk')][string]$Action = 'status',
    [Parameter(Position = 1)][ValidateSet('', 'api', 'worker', 'web', 'postgres', 'tunnel')][string]$Service = ''
)

. (Join-Path $PSScriptRoot 'common.ps1')
Test-Administrator
Test-SettingsReady

$serviceNames = @{ api = $ApiService; worker = $WorkerService; web = $WebService; postgres = $PgService; tunnel = $TunnelService }

function Get-FolderSize([string]$Path) {
    if (-not (Test-Path $Path)) { return 0 }
    $sum = (Get-ChildItem $Path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($sum) { return $sum } else { return 0 }
}

switch ($Action) {
    'status' {
        Write-Step 'Services'
        foreach ($s in @($PgService, $ApiService, $WorkerService, $WebService, $TunnelService)) {
            $svc = Get-Service $s -ErrorAction SilentlyContinue
            if ($svc) { Write-Note ('{0,-20} {1,-8} {2}' -f $s, $svc.Status, $svc.StartType) } else { Write-Note ('{0,-20} (not installed)' -f $s) }
        }
        [void](Test-Site)
        Write-Step 'Release'
        Write-Note "running: $(Get-CurrentRelease)"
        Write-Note "code in this folder: $(Get-Commit)"
        if (Test-Path $DeployLog) { Get-Content $DeployLog -Tail 5 | ForEach-Object { Write-Note $_ } }
    }
    'logs' {
        switch ($Service) {
            'web' { Get-Content (Join-Path $LogDir 'caddy.log') -Tail 60 -ErrorAction SilentlyContinue }
            'postgres' {
                $log = Get-ChildItem (Join-Path $PgData 'log') -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($log) { Get-Content $log.FullName -Tail 60 } else { Write-Note 'no PostgreSQL log found' }
            }
            'tunnel' {
                Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'cloudflared' } -MaxEvents 40 -ErrorAction SilentlyContinue |
                    Sort-Object TimeCreated | ForEach-Object { Write-Host ('{0:HH:mm:ss} {1}' -f $_.TimeCreated, (($_.Message -split "`n")[0])) }
                Write-Note 'The tunnel''s connection state is also shown on its page in Cloudflare Zero Trust.'
            }
            'api' { Show-RecentLogs 60 }
            'worker' { Show-RecentLogs 60 }
            default { Show-RecentLogs 30 }
        }
    }
    'restart' {
        if ($Service) { Restart-Service $serviceNames[$Service] -Force; Write-Ok "$($serviceNames[$Service]) restarted" }
        else { Stop-ReelGuo; Start-ReelGuo; Write-Ok 'restarted' }
        [void](Wait-Healthy 300)
    }
    'stop' { Stop-ReelGuo; Write-Ok 'ReelGuo stopped (database still running; data kept)' }
    'start' { Start-ReelGuo; [void](Wait-Healthy 300) }
    'config' {
        $current = Get-CurrentRelease
        if (-not $current) { Stop-WithError 'No release is running yet; run setup.ps1.' }
        Write-Step "Applying the settings to release $current"
        Stop-ReelGuo
        Write-ReleaseConfig (Join-Path $ReleasesDir $current)
        Install-TunnelService
        Set-CloudflareFirewall
        Start-ReelGuo
        if (Wait-Healthy 300) { [void](Test-Site) }
    }
    'backup' { $f = Invoke-Backup; Write-Ok "backup: $(Join-Path $BackupDir $f)" }
    'backups' {
        if (Test-Path $BackupDir) { Get-ChildItem $BackupDir -Filter *.dump | Sort-Object Name -Descending | ForEach-Object { Write-Host ('  {0}   {1:N1} MB' -f $_.Name, ($_.Length / 1MB)) } }
    }
    'disk' {
        foreach ($p in @(@('releases', $ReleasesDir), @('media (data\storage)', $StorageDir), @('backups', $BackupDir), @('tools', $ToolsDir), @('database', $PgData))) {
            Write-Note ('{0,-22} {1,8:N0} MB   {2}' -f $p[0], ((Get-FolderSize $p[1]) / 1MB), $p[1])
        }
        $drive = Get-PSDrive -Name ($StagingHome.Substring(0, 1))
        Write-Note ('free on {0}:              {1,8:N0} MB' -f $drive.Name, ($drive.Free / 1MB))
    }
}
