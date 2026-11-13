<#
.SYNOPSIS
    Updates ReelGuo staging to newer code: fetch, back up, build, restart, check. Run for every new version.

.DESCRIPTION
    Run from the repository folder on the staging server:

        powershell -ExecutionPolicy Bypass -File .\scripts\staging\update.ps1

    Steps:
      1. Checks Docker and the settings.
      2. Fetches the new code with git (fast-forward only), or deploys -Ref (a branch, tag or commit).
         With -SkipPull it deploys the code that is already in this folder (for example copied over by hand).
      3. Keeps the running images as ":rollback", so rollback.ps1 can go back to them.
      4. Backs up the database (same place and format as the nightly backups).
      5. Builds the new images and restarts what changed. Database migrations run when the API starts.
      6. Waits until the site is ready and checks it.
      7. Removes unused image layers to save disk space.

    If the new version does not become ready, nothing is rolled back automatically (the database may already be
    migrated). The script prints the logs and the two ways back: rollback.ps1 (previous images), and
    rollback.ps1 -RestoreBackup <file> (previous images and the backup taken in step 4).

.PARAMETER Ref
    Branch, tag or commit to deploy (fetched from origin). Default: the current branch, fast-forwarded.

.PARAMETER SkipPull
    Deploy the code already in this folder, without git.

.PARAMETER SkipBackup
    Skip the database backup (not advised).

.PARAMETER AllowDirty
    Continue although this folder has uncommitted changes.
#>
param(
    [string]$Ref,
    [switch]$SkipPull,
    [switch]$SkipBackup,
    [switch]$AllowDirty
)

. (Join-Path $PSScriptRoot 'common.ps1')

Write-Host 'ReelGuo staging - update' -ForegroundColor White
Test-Prerequisites -NeedGit:(-not $SkipPull)
Test-SettingsReady

# ---------------------------------------------------------------------------------------------------------------
$before = Get-Commit
if (-not $SkipPull) {
    Write-Step 'Fetching the new code'
    if (-not (Test-Path (Join-Path $RepoRoot '.git'))) { Stop-WithError 'This folder is not a git checkout. Copy the new code here and use -SkipPull.' }
    $dirty = (& git -C $RepoRoot status --porcelain --untracked-files=no) -join "`n"
    if ($dirty -and -not $AllowDirty) { Stop-WithError "There are uncommitted changes in this folder:`n$dirty`nCommit or discard them, or use -AllowDirty." }
    & git -C $RepoRoot fetch --prune --tags origin
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'git fetch failed (network, or access to the repository).' }
    if ($Ref) {
        if (& git -C $RepoRoot rev-parse --verify --quiet "refs/remotes/origin/$Ref") {
            # A branch: follow it, so later updates without -Ref fetch its new commits.
            & git -C $RepoRoot checkout -B $Ref --track "origin/$Ref"
        } else {
            # A tag or commit: a fixed version.
            $target = & git -C $RepoRoot rev-parse --verify --quiet "$Ref^{commit}"
            if (-not $target) { Stop-WithError "Unknown branch, tag or commit: $Ref" }
            & git -C $RepoRoot checkout --detach $target
        }
    } else {
        $branch = & git -C $RepoRoot symbolic-ref --quiet --short HEAD
        if (-not $branch) { Stop-WithError 'This folder is on a fixed version (a tag or commit). To follow a branch again, run: update.ps1 -Ref main' }
        & git -C $RepoRoot pull --ff-only
    }
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Could not update the code (see the git message above).' }
}
$after = Get-Commit
if ($before -eq $after) { Write-Ok "code: $after (unchanged; rebuilding anyway)" } else { Write-Ok "code: $before -> $after" }

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Keeping the running images for a rollback'
$kept = 0
foreach ($img in $AppImages) {
    if (& docker images -q "${img}:latest") {
        & docker tag "${img}:latest" "${img}:rollback"
        if ($LASTEXITCODE -eq 0) { $kept++ }
    }
}
if ($kept -gt 0) { Write-Ok "$kept images tagged :rollback" } else { Write-Warn 'no running images found to keep (first deploy?)' }

$backupFile = ''
if (-not $SkipBackup) {
    Write-Step 'Backing up the database'
    $out = Invoke-ComposeOutput run --rm -T backup 'sh /scripts/backup-now.sh'
    $m = [regex]::Match($out, 'backup ok: (\S+)')
    if ($m.Success) { $backupFile = Split-Path $m.Groups[1].Value -Leaf; Write-Ok "backup: $backupFile" }
    else { Write-Host $out; Stop-WithError 'The backup did not finish; nothing was changed. Fix the backup first, or use -SkipBackup.' }
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Building the new images'
Invoke-Compose build --pull
Write-Ok 'images built'

Write-Step 'Restarting what changed'
Invoke-Compose up -d --remove-orphans
Write-Ok 'containers updated'

if (-not (Wait-Healthy 420)) {
    Show-RecentLogs
    Add-DeployRecord 'update' "FAILED: not ready (backup $backupFile)"
    Write-Host ''
    Write-Host 'The new version is not ready. Ways back:' -ForegroundColor Yellow
    Write-Host '  .\scripts\staging\rollback.ps1                              previous images (database as it is now)'
    if ($backupFile) { Write-Host "  .\scripts\staging\rollback.ps1 -RestoreBackup $backupFile   previous images and the database from before this update" }
    Stop-WithError 'Update not completed.'
}
$siteOk = Test-Site

Write-Step 'Cleaning up unused image layers'
& docker image prune -f | Out-Null
Write-Ok 'done'

Add-DeployRecord 'update' ($(if ($siteOk) { "ok (backup $backupFile)" } else { "started, some checks failed (backup $backupFile)" }))
Write-Step "Staging is on $after"
Write-Host "  Site: $(Get-Setting 'REELGUO_APP_URL')    History: infra\staging.deploy.log"
if (-not $siteOk) { Write-Warn 'Some checks failed (see above).'; exit 2 }
