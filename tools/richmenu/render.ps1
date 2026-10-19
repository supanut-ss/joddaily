# Renders tools/richmenu/menu.html to src/JodDaily.Api/richmenu/richmenu.png (2500 x 1686) with headless Edge or Chrome.
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$html = Join-Path $PSScriptRoot 'menu.html'
$out = Join-Path $root 'src/JodDaily.Api/richmenu/richmenu.png'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$browser = @(
  "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Edge or Chrome not found' }

# own profile dir: a running Edge/Chrome would otherwise swallow the command and exit without writing the file
$profile = Join-Path $env:TEMP 'joddaily-richmenu-profile'
$cliArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--user-data-dir=$profile",
  '--window-size=2500,1686', "--screenshot=$out", ('file:///' + $html.Replace('\', '/')))
Start-Process -FilePath $browser -ArgumentList $cliArgs -Wait -NoNewWindow
Get-Item $out | Select-Object FullName, Length
