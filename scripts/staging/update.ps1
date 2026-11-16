<#
.SYNOPSIS
    Updates ReelGuo staging to newer code: fetch, back up, build, switch, check. Run for every new version.

.DESCRIPTION
    Run in PowerShell as Administrator, from the repository folder:

        .\scripts\staging\update.ps1

    Steps:
      1. Checks the server and the settings.
      2. Fetches the new code with git (fast-forward only), or deploys -Ref (a branch, tag or commit).
         With -SkipPull it deploys the code that is already in this folder (for example copied over by hand).
      3. Backs up the database.
      4. Builds the new release into its own folder while the current one keeps running.
      5. Switches to it: stops the services, points C:\reelguo-staging\current at the new release, starts them
         (about a minute of downtime). Database migrations run when the API starts.
      6. Waits until the site is ready and checks it; keeps the last 5 releases.

    If the new version does not become ready, nothing is rolled back automatically (the database may already be
    migrated). The script prints the logs and the ways back: rollback.ps1 (the previous release, instantly), and
    rollback.ps1 -RestoreBackup <file> (previous release and the backup taken in step 3).

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
Test-Prerequisites
Add-NewSettings
Test-SettingsReady

# ---------------------------------------------------------------------------------------------------------------
$before = Get-Commit
if (-not $SkipPull) {
    Write-Step 'Fetching the new code'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-WithError 'git was not found. Run setup.ps1 once, or use -SkipPull.' }
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
$previous = Get-CurrentRelease
$backupFile = ''
if (-not $SkipBackup) {
    Write-Step 'Backing up the database'
    $backupFile = Invoke-Backup
    Write-Ok "backup: $backupFile"
}

$release = New-Release
$releaseId = Split-Path $release -Leaf
Install-ReelGuoServices      # picks up changes to the service definitions, if any
Switch-Release $releaseId

if (-not (Wait-Healthy 420)) {
    Show-RecentLogs
    Add-DeployRecord 'update' "FAILED: not ready (backup $backupFile)"
    Write-Host ''
    Write-Host 'The new version is not ready. Ways back:' -ForegroundColor Yellow
    if ($previous) { Write-Host "  .\scripts\staging\rollback.ps1                              the previous release ($previous), database as it is now" }
    if ($backupFile) { Write-Host "  .\scripts\staging\rollback.ps1 -RestoreBackup $backupFile   the previous release AND the database from before this update" }
    Stop-WithError 'Update not completed.'
}
$siteOk = Test-Site
Remove-OldReleases

Add-DeployRecord 'update' ($(if ($siteOk) { "ok (backup $backupFile)" } else { "started, some checks failed (backup $backupFile)" }))
Write-Step "Staging runs $releaseId"
Write-Host "  Site: $(Get-Setting 'REELGUO_APP_URL')    History: $DeployLog"
if (-not $siteOk) { Write-Warn 'Some checks failed (see above).'; exit 2 }
