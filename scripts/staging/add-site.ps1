<#
.SYNOPSIS
    Adds another site to this staging server: its own domain, name and language (web\sites\<key>), database, admin
    console, videos and accounts. Then run update.ps1, which builds it and starts it.

.DESCRIPTION
    Run in PowerShell as Administrator, from the repository folder, once per site:

        .\scripts\staging\add-site.ps1 -Site yelrama -AppUrl https://yelrama.com -AdminHost admin.yelrama.com
        .\scripts\staging\update.ps1

    It writes the site's own settings to C:\reelguo-staging\sites\<key>\site.env (address, admin address, ports,
    database login, generated keys and admin password, video storage) and adds the site to REELGUO_SITES in
    staging.env. update.ps1 then creates its database, builds its web app and admin console in its language, installs
    its services (ReelGuoApi-<key>, ReelGuoWorker-<key>) and serves it on its host names. Settings the site does not
    set itself (staging mode, COS key, monitoring, backups) are the main site's.

    Running it again for a site already added changes only what you pass, and keeps its keys and passwords.
    -Remove takes a site off this server again: its services are removed and update.ps1 stops serving it. Its database
    and files are kept.

.PARAMETER Site
    The site's key: a folder of web\sites (yelrama, ypinedrama, apinedrama).

.PARAMETER AppUrl
    The site's address, for example https://yelrama.com (www. is served as well).

.PARAMETER AdminHost
    The site's admin console address through Cloudflare, for example admin.yelrama.com. Protect it with Cloudflare
    Access. Without it the console is only on this server (http://localhost:<port>).

.PARAMETER VideoProvider
    Hls (videos stored and played from this server; the default) or Cos (the site's own Tencent COS bucket).

.PARAMETER CosBucket
    With Cos: the site's own bucket, full name with the APPID (for example yelrama-1300000000). Never another site's.

.PARAMETER CosFolder
    With Cos: the folder under videos/ that uploads go to. Default: the site's key.

.PARAMETER Remove
    Takes the site off this server (services removed; database and files kept).
#>
param(
    [Parameter(Mandatory = $true)][string]$Site,
    [string]$AppUrl,
    [string]$AdminHost,
    [ValidateSet('Hls', 'Cos')][string]$VideoProvider,
    [string]$CosBucket,
    [string]$CosFolder,
    [switch]$Remove
)

. (Join-Path $PSScriptRoot 'common.ps1')
Test-Administrator
if (-not (Test-Path $SettingsFile)) { Stop-WithError "$SettingsFile does not exist yet. Set up the server first: .\scripts\staging\setup.ps1" }

$key = $Site.Trim().ToLowerInvariant()
$known = @(Get-ChildItem $WebSitesDir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'site.json') } | ForEach-Object { $_.Name })
# -Remove also takes a site this server still lists whose web\sites folder is gone (renamed or deleted in the code).
$listed = @(Get-SiteKeys) -contains $key
if ($key -notmatch '^[a-z0-9-]+$' -or ($known -notcontains $key -and -not ($Remove -and $listed))) {
    if ($Remove) { Write-Ok "Site '$key' is not on this server (REELGUO_SITES): nothing to remove."; exit 0 }
    Stop-WithError "There is no site '$Site' in web\sites. Sites: $($known -join ', ')."
}
if ($key -eq 'huangyue') { Stop-WithError 'huangyue is the main site of this server (staging.env), not one to add.' }

if ($Remove) {
    Write-Step "Removing site $key from this server"
    Set-Setting 'REELGUO_SITES' ((@(Get-SiteKeys) | Where-Object { $_ -ne $key }) -join ',')
    Use-Site $key {
        foreach ($s in @((Get-ApiServiceName), (Get-WorkerServiceName))) {
            if (Get-Service $s -ErrorAction SilentlyContinue) {
                Stop-Service $s -Force -ErrorAction SilentlyContinue
                & sc.exe delete $s | Out-Null
                Write-Ok "service $s removed"
            }
        }
        Write-Ok "$key is off REELGUO_SITES. Its database ($(Get-Setting 'REELGUO_DB_NAME')) and files ($(Join-Path $SitesDir $key)) are kept."
    }
    Write-Host '  Next: .\scripts\staging\ops.ps1 config (or update.ps1), so Caddy stops serving it.'
    exit 0
}

$info = Get-SiteInfo $key
Write-Step "Site ${key}: $($info.name) ($($info.locale))"
New-Item -ItemType Directory -Force -Path (Join-Path $SitesDir $key) | Out-Null
$isNew = -not (Test-Path (Get-SiteSettingsFile $key))

# Ports already taken by the main site and the other sites.
$taken = @(foreach ($k in @('') + @(Get-SiteKeys | Where-Object { $_ -ne $key })) {
    Use-Site $k { [pscustomobject]@{ Api = [int](Get-Setting 'REELGUO_API_PORT' '5080'); Admin = [int](Get-Setting 'REELGUO_ADMIN_PORT' '8081') } }
})
$webPort = [int](Get-MainSetting 'REELGUO_WEB_PORT' '80')
# Read here: inside the Use-Site block, $PSBoundParameters is that block's own (always empty).
$adminHostGiven = $PSBoundParameters.ContainsKey('AdminHost')

Use-Site $key {
    if ($AppUrl) { Set-Setting 'REELGUO_APP_URL' $AppUrl.Trim().TrimEnd('/') }
    if ((Get-Setting 'REELGUO_APP_URL') -eq '') {
        $answer = Read-Host "Address of $($info.name) (for example https://$($info.domain))"
        Set-Setting 'REELGUO_APP_URL' $answer.Trim().TrimEnd('/')
    }
    if ((Get-Setting 'REELGUO_APP_URL') -notmatch '^https://[^/\s]+$') { Stop-WithError "The address must look like https://$($info.domain) (got '$(Get-Setting 'REELGUO_APP_URL')')." }
    if ($adminHostGiven) { Set-Setting 'REELGUO_ADMIN_HOST' $AdminHost.Trim() }

    if ((Get-Setting 'REELGUO_API_PORT') -eq '') {
        $p = 5081
        while (@($taken | ForEach-Object { $_.Api }) -contains $p) { $p++ }
        Set-Setting 'REELGUO_API_PORT' "$p"
    }
    if ((Get-Setting 'REELGUO_ADMIN_PORT') -eq '') {
        $p = 8082
        while ((@($taken | ForEach-Object { $_.Admin }) -contains $p) -or $p -eq $webPort) { $p++ }
        Set-Setting 'REELGUO_ADMIN_PORT' "$p"
    }
    $dbName = 'reelguo_' + ($key -replace '-', '_')
    if ((Get-Setting 'REELGUO_DB_NAME') -eq '') { Set-Setting 'REELGUO_DB_NAME' $dbName }
    if ((Get-Setting 'REELGUO_DB_USER') -eq '') { Set-Setting 'REELGUO_DB_USER' $dbName }

    # Secrets: generated once, never replaced.
    if ((Get-Setting 'REELGUO_DB_PASSWORD') -eq '') { Set-Setting 'REELGUO_DB_PASSWORD' (New-Password 24); Write-Ok 'generated its database password' }
    if ((Get-Setting 'REELGUO_JWT_KEY') -eq '') { Set-Setting 'REELGUO_JWT_KEY' (New-Base64Secret 32); Write-Ok 'generated its sign-in key' }
    if ((Get-Setting 'REELGUO_MEDIA_KEY') -eq '') { Set-Setting 'REELGUO_MEDIA_KEY' (New-Base64Secret 32); Write-Ok 'generated its media signing key' }
    if ((Get-Setting 'REELGUO_ADMIN_PASSWORD') -eq '') { Set-Setting 'REELGUO_ADMIN_PASSWORD' (New-Password 20); Write-Ok 'generated its admin password' }

    if ($VideoProvider) { Set-Setting 'REELGUO_VIDEO_PROVIDER' $VideoProvider }
    if ((Get-Setting 'REELGUO_VIDEO_PROVIDER') -eq '') { Set-Setting 'REELGUO_VIDEO_PROVIDER' 'Hls' }
    if ($CosBucket) { Set-Setting 'REELGUO_COS_BUCKET' $CosBucket.Trim() }
    if ($CosFolder) { Set-Setting 'REELGUO_COS_FOLDER' $CosFolder.Trim() }
    if ((Get-Setting 'REELGUO_COS_FOLDER') -eq '') { Set-Setting 'REELGUO_COS_FOLDER' $key }
    if ((Get-Setting 'REELGUO_ALLOW_INDEXING') -eq '') { Set-Setting 'REELGUO_ALLOW_INDEXING' 'false' }
    Test-SiteSettings
}

Set-Setting 'REELGUO_SITES' ((@(Get-SiteKeys) + @($key) | Select-Object -Unique) -join ',')
Protect-StagingHome
Write-Step "Checking the settings of every site on this server ($key's are saved)"
Test-SettingsReady
Add-DeployRecord 'add-site' "$key $(Use-Site $key { Get-Setting 'REELGUO_APP_URL' })"

Write-Step 'Done'
Use-Site $key {
    $appHost = Get-AppHost
    $console = Get-AdminHost
    Write-Host ''
    Write-Host "  Site:          $(Get-Setting 'REELGUO_APP_URL')   ($($info.name), $($info.locale))"
    Write-Host "  Settings:      $(Get-SiteSettingsFile $key)"
    Write-Host "  Database:      $(Get-Setting 'REELGUO_DB_NAME')    API port $(Get-Setting 'REELGUO_API_PORT')    videos: $(Get-Setting 'REELGUO_VIDEO_PROVIDER')"
    Write-Host "  Admin console: $((Get-AdminBase).Replace('127.0.0.1', 'localhost')) on this server$(if ($console) { ", https://$console through Cloudflare" })   user: admin"
    if ($isNew) {
        Write-Host "  Admin password: $(Get-Setting 'REELGUO_ADMIN_PASSWORD')" -ForegroundColor Yellow
        Write-Host "                  (also in its site.env; store it in your password manager)"
    }
    Write-Host ''
    Write-Host '  Next:'
    Write-Host '    1. .\scripts\staging\update.ps1   (builds the site in its language, creates its database, starts it)'
    Write-Host "    2. Cloudflare: add $($appHost -replace '^www\.', '') as a site; at the registrar, switch to Cloudflare's name servers."
    Write-Host "       DNS: A records (Proxied, orange cloud) to this server's public IP for $appHost and www.$appHost$(if ($console) { ", and for $console" })."
    Write-Host '       SSL/TLS: Flexible, as for the main site.'
    if ($console) { Write-Host "       Zero Trust -> Access -> Applications: protect $console (only your team's emails)." }
    if ((Get-Setting 'REELGUO_STAGING_MODE' 'tunnel') -eq 'tunnel') {
        Write-Host "       Tunnel mode: add public hostnames $appHost, www.$appHost$(if ($console) { " and $console" }) -> HTTP localhost:$(Get-Setting 'REELGUO_WEB_PORT' '8080')."
    }
}
