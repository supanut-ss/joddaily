<#
    Builds the upload package of the frontend (the LIFF page) into .\publish-web. It is a static site: nothing is built, the files of .\web
    are copied and config.js is written with the address of the API. Nothing is uploaded.

      .\tools\publish-web.ps1                                        # API at https://api.drivetodev.online/linejod/
      .\tools\publish-web.ps1 -ApiUrl https://other.example/linejod/ # another API address

    Upload the contents of .\publish-web to the folder of the frontend site, https://linejod.drivetodev.online (see docs/DEPLOY.md).
#>
[CmdletBinding()]
param(
    # Where the API is, including the path when it runs in a sub-application of another site.
    [ValidatePattern('^https?://')] [string]$ApiUrl = "https://api.drivetodev.online/linejod/"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "web"
$out = Join-Path $root "publish-web"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Copy-Item $src $out -Recurse

$api = $ApiUrl.TrimEnd('/') + '/'
$config = "// Written by tools/publish-web.ps1. Where the LineJod API is.`nwindow.LINEJOD_API = '$api';`n"
[System.IO.File]::WriteAllText((Join-Path $out "config.js"), $config, (New-Object System.Text.UTF8Encoding($false)))   # UTF-8 without BOM

$files = Get-ChildItem $out -Recurse -File
Write-Host "== done: $out ($($files.Count) files). API = $api"
Write-Host "   LIFF endpoint URL = the public address of the folder you upload this to (with the trailing slash)."
