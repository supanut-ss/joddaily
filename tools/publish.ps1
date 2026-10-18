<#
    Builds the upload package for the Plesk host into .\publish (nothing is uploaded, nothing on the server is touched).

      .\tools\publish.ps1                # tests, then win-x86 self-contained publish
      .\tools\publish.ps1 -SkipTests

    win-x86 self-contained: the shared app pool of the host is 32-bit and has no x86 runtime installed, so the package carries its own.
    Upload the contents of .\publish to the site's folder (see docs/DEPLOY.md), then put appsettings.Production.json next to it.
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x86",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish"

if (-not $SkipTests) {
    Write-Host "== tests"
    dotnet test (Join-Path $root "LineJod.slnx") --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: not publishing." }
}

Write-Host "== publish ($Runtime, self-contained)"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root "src/LineJod.Api/LineJod.Api.csproj") -c Release -r $Runtime --self-contained true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# secrets never travel inside the package
Get-ChildItem $out -Filter "appsettings.Production*.json" | Remove-Item -Force

# the ASP.NET Core Module writes its stdout log here (turn it on in web.config for the first deploy)
New-Item -ItemType Directory -Force (Join-Path $out "logs") | Out-Null

$webConfig = Join-Path $out "web.config"
if (-not (Test-Path $webConfig)) { throw "web.config was not generated." }
if ((Get-Content $webConfig -Raw) -notmatch 'hostingModel="outofprocess"') { Write-Warning "web.config is not out-of-process; check <AspNetCoreHostingModel> in LineJod.Api.csproj." }

$size = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "== done: $out ($size MB). Next: docs/DEPLOY.md"
