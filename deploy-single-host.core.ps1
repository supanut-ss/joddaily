<#
    Single-host deployment for JodDaily on the shared Plesk host (the same host as Pay_Yet, nothing shared with it).

      API       ASP.NET Core app, a sub-application of the gateway site: https://gateway.drivetodev.online/joddaily   (FTP folder gateway.drivetodev.online/joddaily)
      Frontend  static LIFF page, a folder of the main site:          https://drivetodev.online/joddaily      (FTP folder drivetodev.online/joddaily)

    API flow:  0. validate target + production config   1. unit tests   2. dotnet publish (win-x86 self-contained)
               3. stage config into the publish folder  4. app_offline.htm  5. upload files  6. bring online
               7. health checks (and restore maintenance mode if they fail)
    Web flow:  stage web\ with config.js pointing at the API, upload, check the page and that the API allows its origin.

    This file is tracked and contains no credentials. Credentials are passed in by deploy-api|web|all.ps1 (from the git-ignored
    deploy.credentials.ps1). The secrets of the app live in deploy\appsettings.Production.json (git-ignored; template next to it).

    Why win-x86 self-contained: the app pool of this subscription runs 32-bit and has no x86 runtime installed; a self-contained
    publish needs nothing from the host. Use -FrameworkDependent only after the app pool is switched to 64-bit.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Server,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Username,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$Password,

    # FTP paths relative to the FTP login root (the subscription root). Pinned below on purpose.
    [string]$RemotePath = "gateway.drivetodev.online/joddaily",
    [string]$PublicBaseUrl = "https://gateway.drivetodev.online/joddaily",

    # The static frontend lives in a sub-folder of the main site (drivetodev.online).
    [string]$WebRemotePath = "drivetodev.online/joddaily",
    [string]$WebPublicUrl = "https://drivetodev.online/joddaily",
    [ValidateSet("all", "api", "web")] [string]$Component = "all",   # api = backend only, web = frontend only
    [string]$ConfigPath = "",

    [string]$Runtime = "win-x86",
    [switch]$FrameworkDependent,
    [switch]$StdoutLog,            # keep ANCM stdout logs on (use for the first deploy / debugging)
    [switch]$SkipTests,

    [ValidateRange(0, 600)] [int]$OfflineDrainSeconds = 20,
    [ValidateRange(1, 100)] [int]$HealthCheckAttempts = 12,
    [ValidateRange(1, 300)] [int]$HealthCheckDelaySeconds = 5
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
if (-not $ConfigPath) { $ConfigPath = Join-Path $repoRoot "deploy\appsettings.Production.json" }
$dryRun = $env:JODDAILY_DEPLOY_DRY_RUN -eq "1"

# ---- 0. Guard rails -------------------------------------------------------------------------------------
# The FTP login root is the whole subscription (Pay_Yet's folders, other domains and subdomains). Refuse anything but our own
# folders, because -PruneRemoteRootFiles below deletes files in the target folder that are not part of this deployment.
# Never allow 'api.drivetodev.online' or 'gateway.drivetodev.online' itself: those are other applications' roots.
$allowedApiFolders = @("gateway.drivetodev.online/joddaily", "api.drivetodev.online/joddaily", "joddaily-api.drivetodev.online")   # the second one: fallback if the sub-application is not possible
$target = $RemotePath.Trim('/').ToLowerInvariant()
if ($allowedApiFolders -notcontains $target) {
    throw "Refusing to deploy: RemotePath must be one of '$($allowedApiFolders -join "', '")' (got '$RemotePath')."
}
$webTarget = $WebRemotePath.Trim('/').ToLowerInvariant()
if (@("drivetodev.online/joddaily", "joddaily.drivetodev.online") -notcontains $webTarget) {
    throw "Refusing to deploy: WebRemotePath must be 'drivetodev.online/joddaily' or 'joddaily.drivetodev.online' (got '$WebRemotePath')."
}
$PublicBaseUrl = $PublicBaseUrl.TrimEnd('/')
$WebPublicUrl = $WebPublicUrl.TrimEnd('/')
$webOrigin = ([Uri]$WebPublicUrl).GetLeftPart([UriPartial]::Authority)
$expectedPathBase = ([Uri]$PublicBaseUrl).AbsolutePath.TrimEnd('/')    # "/joddaily" or "" - must equal App:PathBase in the app config

$pwsh = if (Get-Command powershell -ErrorAction SilentlyContinue) { "powershell" } else { "pwsh" }
$uploader = Join-Path $repoRoot "upload-ftp.ps1"
$dryArgs = @(); if ($dryRun) { $dryArgs = @("-DryRun") }

# The folders must already exist: the API folder has to be made an IIS application in Plesk first (a plain folder would be served by
# Pay_Yet's app, and the uploader would happily create it), the frontend folder comes with the subdomain.
function Assert-RemoteFolder([string]$Path) {
    $parts = $Path.Trim('/').Split('/')
    $leaf = $parts[$parts.Count - 1]
    $parent = if ($parts.Count -gt 1) { ($parts[0..($parts.Count - 2)] -join '/') } else { "" }
    $uri = if ($parent) { "ftp://$Server/$parent/" } else { "ftp://$Server/" }
    try {
        $req = [System.Net.FtpWebRequest]::Create($uri)
        $req.Method = [System.Net.WebRequestMethods+Ftp]::ListDirectory
        $req.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
        $req.UsePassive = $true
        $resp = $req.GetResponse()
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $names = $reader.ReadToEnd() -split "`r?`n" | ForEach-Object { ($_.Trim().Replace('\', '/').TrimEnd('/') -split '/')[-1] }
        $reader.Close(); $resp.Close()
    }
    catch { throw "Cannot list ftp://$Server/$parent : $($_.Exception.Message)" }
    if ($names -notcontains $leaf) {
        throw "Remote folder '$Path' does not exist. Create it first in Plesk (see docs/DEPLOY.md); for the API it must be made an IIS application."
    }
}

# ---- Static frontend (separate Plesk site; independent of the API deploy) ---------------------------------
function Deploy-Web {
    $webDir = Join-Path $repoRoot "web"
    if (-not (Test-Path -LiteralPath (Join-Path $webDir "index.html"))) { throw "web\index.html not found." }
    Assert-RemoteFolder $webTarget

    # Stage a copy with config.js pointing at the API (the only per-environment setting of the frontend).
    $webStage = Join-Path $repoRoot "deploy\web"
    if (Test-Path -LiteralPath $webStage) { Remove-Item -LiteralPath $webStage -Recurse -Force }
    Copy-Item -LiteralPath $webDir -Destination $webStage -Recurse
    $configJs = "// Written by the deploy script. Where the JodDaily API is.`nwindow.JODDAILY_API = '$PublicBaseUrl/';`n"
    [IO.File]::WriteAllText((Join-Path $webStage "config.js"), $configJs, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Uploading frontend to ftp://$Server/$webTarget ..." -ForegroundColor Yellow
    $webManifest = Join-Path $repoRoot ".deploy-cache\joddaily-web-manifest.json"
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $webStage -RemotePath $webTarget -ManifestPath $webManifest -PruneRemoteRootFiles @dryArgs
    if ($LASTEXITCODE -ne 0) { throw "Uploading the frontend failed (exit code $LASTEXITCODE). The API is unaffected; fix and rerun." }
    if ($dryRun) { Write-Host "Dry run complete. No remote files were changed." -ForegroundColor Cyan; return }

    $webProblems = @()
    try {
        $page = Invoke-WebRequest -Uri "$WebPublicUrl/" -UseBasicParsing -TimeoutSec 30
        if ($page.Content -notmatch "JodDaily") { $webProblems += "$WebPublicUrl/ does not look like the JodDaily page" }
        $appPage = Invoke-WebRequest -Uri "$WebPublicUrl/app/" -UseBasicParsing -TimeoutSec 30
        if ($appPage.Content -notmatch "config\.js") { $webProblems += "$WebPublicUrl/app/ does not look like the JodDaily app" }
    } catch { $webProblems += "$WebPublicUrl/ -> $($_.Exception.Message)" }
    try {
        $cfgJs = Invoke-WebRequest -Uri "$WebPublicUrl/config.js" -UseBasicParsing -TimeoutSec 30
        if ($cfgJs.Content -notmatch [regex]::Escape("$PublicBaseUrl/")) { $webProblems += "config.js does not point at $PublicBaseUrl/" }
    } catch { $webProblems += "$WebPublicUrl/config.js -> $($_.Exception.Message)" }
    try {   # the page calls the API from the browser: the API must allow this origin
        $cors = Invoke-WebRequest -Uri "$PublicBaseUrl/api/config" -UseBasicParsing -TimeoutSec 30 -Headers @{ Origin = $webOrigin }
        if ($cors.Headers["Access-Control-Allow-Origin"] -ne $webOrigin) { $webProblems += "API does not allow origin $webOrigin (check App:WebBaseUrl)" }
    } catch { $webProblems += "CORS check: $($_.Exception.Message)" }
    if ($webProblems.Count -gt 0) {
        Write-Warning ("Frontend checks reported problems:`n - " + ($webProblems -join "`n - "))
        throw "Frontend uploaded but not healthy. Is the Plesk subdomain site created with a valid certificate, and is the API deployed?"
    }
    Write-Host "Frontend verified: $WebPublicUrl" -ForegroundColor Green
}

if ($Component -eq "web") {
    Deploy-Web
    Write-Host "--- Frontend deployment complete ---" -ForegroundColor Cyan
    return
}

# ---- API: production config -------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Production config not found: $ConfigPath (copy deploy\appsettings.Production.example.json and fill it in)" }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

function Get-ConfigValue([string]$Path) {
    $node = $config
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $node -or -not ($node.PSObject.Properties.Name -contains $part)) { return $null }
        $node = $node.$part
    }
    return [string]$node
}
$required = @("Line.LoginChannelId", "Liff.Id", "Jobs.Key", "App.PublicBaseUrl", "App.WebBaseUrl")
if ([string]::IsNullOrWhiteSpace((Get-ConfigValue "Gateway.ApiKey"))) {
    $required += @("ConnectionStrings.Default", "Line.ChannelSecret", "Line.ChannelAccessToken")   # no gateway: the keys must be in this file
} else {
    $required += @("Gateway.BaseUrl", "Gateway.AppId")             # the DB string and LINE keys come from the gateway
}
$problems = @()
foreach ($key in $required) {
    $value = Get-ConfigValue $key
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match "PASTE_|CHANGE_ME") { $problems += $key }
}
if ($problems.Count -gt 0) { throw "Fill these values in $ConfigPath before deploying: $($problems -join ', ')" }

# the app and the deployment must agree about where the app is, or the links, the path base and the CORS check go wrong
$mismatch = @()
if ((Get-ConfigValue "App.PublicBaseUrl").TrimEnd('/') -ne $PublicBaseUrl) { $mismatch += "App:PublicBaseUrl must be $PublicBaseUrl" }
$webBaseEntries = @((Get-ConfigValue "App.WebBaseUrl") -split "," | ForEach-Object { $_.Trim().TrimEnd("/") } | Where-Object { $_ })   # several comma-separated frontend origins are allowed (CORS)
if ($webBaseEntries -notcontains $WebPublicUrl -and $webBaseEntries -notcontains $webOrigin -and $webBaseEntries -notcontains "https://drivetodev.online") { $mismatch += "App:WebBaseUrl must include $WebPublicUrl or https://drivetodev.online" }
$configPathBase = (Get-ConfigValue "App.PathBase"); if ($null -eq $configPathBase) { $configPathBase = "" }
if ($configPathBase.Trim().TrimEnd('/') -ne $expectedPathBase) { $mismatch += "App:PathBase must be '$expectedPathBase'" }
if ($mismatch.Count -gt 0) { throw "$ConfigPath does not match this deployment: $($mismatch -join '; ')" }

# ---- 1. Tests ---------------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "1. Running tests..." -ForegroundColor Yellow
    & dotnet test "$repoRoot\JodDaily.slnx" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed - not deploying." }
}

# the remote folder must exist before anything is built or taken offline
Assert-RemoteFolder $target

# ---- 2. Publish -------------------------------------------------------------------------------------------
$stage = Join-Path $repoRoot "deploy"
$appDir = Join-Path $stage "app"
$offlineDir = Join-Path $stage "offline"
if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
if (Test-Path -LiteralPath $offlineDir) { Remove-Item -LiteralPath $offlineDir -Recurse -Force }
New-Item -ItemType Directory -Path $appDir | Out-Null
New-Item -ItemType Directory -Path $offlineDir | Out-Null

$mode = if ($FrameworkDependent) { "framework-dependent" } else { "$Runtime self-contained" }
Write-Host "2. Publishing ($mode)..." -ForegroundColor Yellow
$publishArgs = @("publish", "$repoRoot\src\JodDaily.Api", "-c", "Release", "-o", $appDir, "--nologo", "-v", "q")
if (-not $FrameworkDependent) { $publishArgs += @("-r", $Runtime, "--self-contained", "true") }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# web.config: out-of-process (the pool is shared with other sites), stdout logging only when asked.
$webConfigPath = Join-Path $appDir "web.config"
if (-not (Test-Path -LiteralPath $webConfigPath)) { throw "web.config missing from the publish output." }
[xml]$webConfig = Get-Content -LiteralPath $webConfigPath
$aspNetCore = $webConfig.configuration.location.'system.webServer'.aspNetCore
if (-not $aspNetCore) { throw "aspNetCore element not found in web.config." }
$aspNetCore.SetAttribute("hostingModel", "outofprocess")
$aspNetCore.SetAttribute("stdoutLogEnabled", $(if ($StdoutLog) { "true" } else { "false" }))

# Plesk replaces 4xx/5xx responses with its own HTML error pages. The page reads the JSON error bodies of the API
# (e.g. a validation message), so let the app's own responses through unchanged. The post-deploy check below verifies
# this; if the host locks the httpErrors section, drop this block.
$webServer = $webConfig.configuration.location.'system.webServer'
$httpErrors = $webServer.httpErrors
if (-not $httpErrors) {
    $httpErrors = $webConfig.CreateElement("httpErrors")
    [void]$webServer.AppendChild($httpErrors)
}
$httpErrors.SetAttribute("existingResponse", "PassThrough")
$webConfig.Save($webConfigPath)

# logs folder must exist for ANCM stdout logging; the uploader only sends files, so ship a placeholder file.
New-Item -ItemType Directory -Path (Join-Path $appDir "logs") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDir "logs\README.txt") -Value "ANCM stdout logs are written here when stdoutLogEnabled=true."

# ---- 3. Stage production config (removed again in 'finally') ---------------------------------------------
Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $appDir "appsettings.Production.json") -Force

# ---- 4. Maintenance page ----------------------------------------------------------------------------------
# The Thai text lives in a UTF-8 template file (not inside this script): Windows PowerShell 5.1 reads a BOM-less script as ANSI,
# which turns Thai literals into mojibake. The page is self-contained (no external files) because the app is down while it is shown.
$offlinePath = Join-Path $offlineDir "app_offline.htm"
$templatePath = Join-Path $repoRoot "deploy\templates\app_offline.template.htm"
if (-not (Test-Path -LiteralPath $templatePath)) { throw "Missing maintenance page asset: $templatePath" }
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($offlinePath, [IO.File]::ReadAllText($templatePath, [System.Text.Encoding]::UTF8), $utf8NoBom)
# read the bytes back and make sure the Thai text survived (guards against another encoding mix-up)
$check = [System.Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($offlinePath))
if ($check.Contains([string][char]0xFFFD) -or $check -notmatch '\p{IsThai}') {
    throw "Maintenance page was not written as valid UTF-8 Thai text."
}

$manifestPath = Join-Path $repoRoot ".deploy-cache\joddaily-manifest.json"

function Assert-UploadSucceeded([string]$Stage, [int]$ExitCode) {
    if ($ExitCode -ne 0) {
        throw "$Stage failed (upload-ftp.ps1 exit code $ExitCode). The site stays in maintenance mode; fix the error and rerun."
    }
}
function Send-Offline {
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $offlineDir -RemotePath $target @dryArgs
}

$deployed = $false
try {
    Write-Host "4. Taking the app offline..." -ForegroundColor Yellow
    Send-Offline
    Assert-UploadSucceeded "Taking the application offline" $LASTEXITCODE
    if (-not $dryRun) {
        Write-Host "Waiting $OfflineDrainSeconds s for IIS to release files..."
        Start-Sleep -Seconds $OfflineDrainSeconds
    }

    Write-Host "5. Uploading application files to ftp://$Server/$target ..." -ForegroundColor Yellow
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $uploader -Server $Server -Username $Username -Password $Password `
        -LocalPath $appDir -RemotePath $target -ManifestPath $manifestPath -PruneRemoteRootFiles @dryArgs
    Assert-UploadSucceeded "Uploading application files" $LASTEXITCODE

    if ($dryRun) {
        Write-Host "Dry run complete. No remote files were changed." -ForegroundColor Cyan
        return
    }

    # ---- 6. Bring online --------------------------------------------------------------------------------
    Write-Host "6. Bringing the app back online..." -ForegroundColor Yellow
    $del = [System.Net.FtpWebRequest]::Create("ftp://$Server/$target/app_offline.htm")
    $del.Method = [System.Net.WebRequestMethods+Ftp]::DeleteFile
    $del.Credentials = New-Object System.Net.NetworkCredential($Username, $Password)
    $del.UsePassive = $true
    $del.GetResponse().Close()

    # ---- 7. Health checks ---------------------------------------------------------------------------------
    $healthUrl = "$PublicBaseUrl/health"
    $ok = $false; $last = "no response"
    for ($i = 1; $i -le $HealthCheckAttempts; $i++) {
        try {
            $r = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 30
            $last = "HTTP $([int]$r.StatusCode)"
            if ([int]$r.StatusCode -eq 200) { $ok = $true; break }
        }
        catch {
            $last = if ($_.Exception.Response) { "HTTP $([int]$_.Exception.Response.StatusCode)" } else { $_.Exception.Message }
        }
        if ($i -lt $HealthCheckAttempts) {
            Write-Host "Health check not ready ($last), attempt $i/$HealthCheckAttempts..." -ForegroundColor Gray
            Start-Sleep -Seconds $HealthCheckDelaySeconds
        }
    }
    if (-not $ok) {
        Write-Warning "Health check failed ($last). Restoring maintenance mode."
        Send-Offline
        throw "$healthUrl did not return HTTP 200 after $HealthCheckAttempts attempts (last: $last). With -StdoutLog the reason is in $target/logs/ on the server."
    }

    # Extra checks: status page served by JodDaily (not Pay_Yet), config reached the app, webhook and API reject anonymous calls,
    # the job is key-guarded, and the frontend's origin is allowed.
    $problems = @()
    try {
        $root = Invoke-RestMethod -Uri "$PublicBaseUrl/" -TimeoutSec 30
        if ($root.app -ne "JodDaily API") { $problems += "$PublicBaseUrl/ is not answered by JodDaily (is the folder an IIS application?)" }
    } catch { $problems += "API status at /: $($_.Exception.Message)" }

    try {
        $cfg = Invoke-RestMethod -Uri "$PublicBaseUrl/api/config" -TimeoutSec 30
        if ($cfg.liffId -ne (Get-ConfigValue "Liff.Id")) { $problems += "Liff:Id on the server does not match the local config" }
    } catch { $problems += "api/config: $($_.Exception.Message)" }

    try {
        Invoke-WebRequest -Uri "$PublicBaseUrl/api/line/webhook" -Method Post -Body "{}" -ContentType "application/json" -UseBasicParsing -TimeoutSec 30 | Out-Null
        $problems += "webhook accepted an unsigned request"
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($code -ne 401) { $problems += "webhook (unsigned) returned HTTP $code instead of 401" }
    }

    try {
        Invoke-WebRequest -Uri "$PublicBaseUrl/jobs/daily" -UseBasicParsing -TimeoutSec 30 | Out-Null
        $problems += "the job endpoint ran without a key"
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($code -ne 401) { $problems += "jobs/daily (no key) returned HTTP $code instead of 401" }
    }

    try {
        Invoke-WebRequest -Uri "$PublicBaseUrl/api/ledger/home" -UseBasicParsing -TimeoutSec 30 | Out-Null
        $problems += "the ledger API accepted a request without a LINE token"
    } catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $body = ""
        if ($_.Exception.Response) { $body = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() }
        if ($code -ne 401) { $problems += "ledger API (no token) returned HTTP $code instead of 401" }
        elseif ($body -notmatch '"error"') { $problems += "ledger API error body was replaced by a host error page (httpErrors PassThrough not applied)" }
    }

    try {
        $cors = Invoke-WebRequest -Uri "$PublicBaseUrl/api/config" -UseBasicParsing -TimeoutSec 30 -Headers @{ Origin = $webOrigin }
        if ($cors.Headers["Access-Control-Allow-Origin"] -ne $webOrigin) { $problems += "API does not allow origin $webOrigin (check App:WebBaseUrl)" }
    } catch { $problems += "CORS check: $($_.Exception.Message)" }

    if ($problems.Count -gt 0) {
        Write-Warning ("Post-deploy checks reported problems:`n - " + ($problems -join "`n - "))
        throw "Deployment is up but not healthy. Fix the items above."
    }

    $deployed = $true
    Write-Host "Deployment verified: $PublicBaseUrl (health, status, config, webhook and job guards, JSON errors, CORS)." -ForegroundColor Green
}
catch {
    Write-Host "Deployment stopped: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
finally {
    # never leave secrets or the offline page lying around in the local staging folder
    Remove-Item -LiteralPath (Join-Path $appDir "appsettings.Production.json") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $offlinePath -Force -ErrorAction SilentlyContinue
    Set-Location $repoRoot
}

if ($deployed -and $Component -eq "all") { Deploy-Web }

if ($deployed) {
    Write-Host "--- Deployment complete ---" -ForegroundColor Cyan
    Write-Host "Next: LINE Developers -> Webhook URL $PublicBaseUrl/api/line/webhook -> Verify; LIFF endpoint URL $WebPublicUrl/ ;"
    Write-Host "      open $PublicBaseUrl/jobs/richmenu?key=<Jobs:Key> once; Plesk scheduled task EVERY HOUR: $PublicBaseUrl/jobs/daily?key=<Jobs:Key>"
}
