<#
.SYNOPSIS
    Day-to-day commands for ReelGuo staging.

.DESCRIPTION
    .\scripts\staging\ops.ps1 status                 containers, health, checks, deployed version
    .\scripts\staging\ops.ps1 logs [service]         follow the logs (api, worker, web, postgres, backup, cloudflared)
    .\scripts\staging\ops.ps1 restart [service]      restart one service, or everything
    .\scripts\staging\ops.ps1 stop                   stop everything (data is kept)
    .\scripts\staging\ops.ps1 start                  start everything again
    .\scripts\staging\ops.ps1 backup                 back up the database now
    .\scripts\staging\ops.ps1 backups                list the backups on this server
    .\scripts\staging\ops.ps1 disk                   disk used by images, containers and volumes
#>
param(
    [Parameter(Position = 0)][ValidateSet('status', 'logs', 'restart', 'stop', 'start', 'backup', 'backups', 'disk')][string]$Action = 'status',
    [Parameter(Position = 1)][string]$Service
)

. (Join-Path $PSScriptRoot 'common.ps1')
Test-SettingsReady

switch ($Action) {
    'status' {
        Write-Step 'Containers'
        Invoke-Compose ps
        $ready = (Invoke-Http "$(Get-LocalBase)/health/ready").Status -eq 200
        if ($ready) { Write-Ok 'site ready' } else { Write-Warn 'site not ready' }
        [void](Test-Site)
        Write-Step 'Deployed version'
        Write-Note "code in this folder: $(Get-Commit)"
        if (Test-Path $DeployLog) { Get-Content $DeployLog -Tail 5 | ForEach-Object { Write-Note $_ } }
    }
    'logs' {
        if ($Service) { Invoke-Compose logs -f --tail 200 $Service } else { Invoke-Compose logs -f --tail 100 }
    }
    'restart' {
        if ($Service) { Invoke-Compose restart $Service } else { Invoke-Compose restart }
        [void](Wait-Healthy 300)
    }
    'stop' { Invoke-Compose stop; Write-Ok 'stopped (data kept)' }
    'start' { Invoke-Compose up -d --no-build; [void](Wait-Healthy 300) }
    'backup' {
        $out = Invoke-ComposeOutput run --rm -T backup 'sh /scripts/backup-now.sh'
        Write-Host $out
    }
    'backups' { Write-Host (Invoke-ComposeOutput run --rm -T --no-deps backup 'ls -1t /backups') }
    'disk' {
        & docker system df
        Write-Note "Volumes of this project: $Project`_pgdata (database), $Project`_media (videos, images), $Project`_backups (backups)"
    }
}
