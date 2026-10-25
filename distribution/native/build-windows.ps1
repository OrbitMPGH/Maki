<#
.SYNOPSIS
  Builds the Windows desktop package: Maki.Api and the Maki.Desktop tray app, self-contained for
  win-x64, checked with --selftest and a real start, then packed by Velopack into Setup.exe, a
  portable zip and the update feed.

.DESCRIPTION
  Layout of the package (what Velopack installs under %LOCALAPPDATA%\Maki.Desktop\current):
    Maki.exe            the tray app, Velopack's main executable
    server\Maki.Api.exe Maki itself, with wwwroot\ beside it
    LICENSE
  The two are published into separate folders on purpose: both carry a self-contained runtime, and
  sharing one folder would let either project's copy of a shared assembly overwrite the other's.

  Velopack builds a delta update only when the previous release's full package is in the output
  folder, so -PreviousReleases downloads it from GitHub first. Without it every update is a full
  download, which still works.

  Signing is not set up: the Windows build ships unsigned for now (see the native builds proposal).
  When a certificate exists, pass it through vpk pack's --signParams or --signTemplate below.

.PARAMETER Version
  X.Y.Z or X.Y.Z-prerelease, without the leading v. Defaults to 0.0.1-local for a test build.

.PARAMETER FrontendDist
  An already built frontend/dist. Omit it to run npm ci and npm run build here.

.PARAMETER Pack
  Run vpk pack after the checks. Needs the vpk tool (dotnet tool install -g vpk --version 1.2.161).

.PARAMETER PreviousReleases
  Download the previous release from GitHub before packing, so Velopack can build a delta.

.EXAMPLE
  ./distribution/native/build-windows.ps1 -Pack
  # -> out/native/win-x64/releases/Maki.Desktop-win-Setup.exe and the update feed

.EXAMPLE
  ./distribution/native/build-windows.ps1 -Version 0.35.0 -FrontendDist frontend/dist -Pack -PreviousReleases
#>
[CmdletBinding()]
param(
  [string]$Version = "0.0.1-local",
  [string]$SourceCommit,
  [string]$FrontendDist,
  [string]$OutDir = "out/native/win-x64",
  [switch]$Pack,
  [switch]$PreviousReleases
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repoRoot

$version = $Version -replace '^v', ''
if (-not $SourceCommit) { $SourceCommit = (git rev-parse HEAD).Trim() }
$out = Join-Path $repoRoot $OutDir
$app = Join-Path $out "app"
$server = Join-Path $app "server"
$releases = Join-Path $out "releases"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $server, $releases | Out-Null

if (-not $FrontendDist) {
  Write-Host "== Building the frontend ==" -ForegroundColor Cyan
  Push-Location frontend
  try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
  } finally { Pop-Location }
  $FrontendDist = "frontend/dist"
}

$versionArgs = @("-p:Version=$version", "-p:InformationalVersion=$version+$SourceCommit")

Write-Host "== Publishing Maki.Api (win-x64, self-contained) ==" -ForegroundColor Cyan
dotnet publish src/Maki.Api/Maki.Api.csproj -c Release -r win-x64 --self-contained true `
  -p:SkipFrontendBuild=true -o $server @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Publishing Maki.Api failed" }
Copy-Item -Recurse (Resolve-Path $FrontendDist) (Join-Path $server "wwwroot")

Write-Host "== Publishing Maki.Desktop (win-x64, self-contained) ==" -ForegroundColor Cyan
dotnet publish src/Maki.Desktop/Maki.Desktop.csproj -c Release -r win-x64 --self-contained true `
  -o $app @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Publishing Maki.Desktop failed" }
Copy-Item LICENSE $app

Write-Host "== Self-test ==" -ForegroundColor Cyan
& (Join-Path $server "Maki.Api.exe") --selftest
if ($LASTEXITCODE -ne 0) { throw "Maki.Api --selftest failed" }

Write-Host "== Start against an empty config folder ==" -ForegroundColor Cyan
$config = Join-Path ([IO.Path]::GetTempPath()) "maki-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $config | Out-Null
'{ "Port": 8997 }' | Set-Content (Join-Path $config "config.json")
$env:MAKI_CONFIG_DIR = $config
$env:MAKI_RUNTIME = "desktop"
$process = Start-Process (Join-Path $server "Maki.Api.exe") -PassThru -WindowStyle Hidden
try {
  $ok = $false
  foreach ($attempt in 1..120) {
    Start-Sleep -Seconds 1
    if ($process.HasExited) { throw "Maki.Api exited during startup with code $($process.ExitCode)" }
    try {
      $response = Invoke-WebRequest "http://localhost:8997/initialize.json" -UseBasicParsing -TimeoutSec 5
      if ($response.StatusCode -eq 200) { $ok = $true; break }
    } catch { }
  }
  if (-not $ok) { throw "Maki.Api did not answer /initialize.json within two minutes" }
  $index = Invoke-WebRequest "http://localhost:8997/" -UseBasicParsing -TimeoutSec 5
  if ($index.Content -notmatch '<div id="root">') { throw "The SPA shell was not served" }
  Write-Host "Started and served the SPA" -ForegroundColor Green
} finally {
  if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
  Remove-Item Env:MAKI_CONFIG_DIR, Env:MAKI_RUNTIME
  Start-Sleep -Seconds 1
  Remove-Item $config -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $Pack) {
  Write-Host "Built $app (not packed; pass -Pack for Setup.exe and the update feed)" -ForegroundColor Green
  return
}

if ($PreviousReleases) {
  Write-Host "== Fetching the previous release for a delta ==" -ForegroundColor Cyan
  # Fails harmlessly on the first desktop release, when there is nothing to download yet.
  vpk download github --repoUrl https://github.com/OrbitMPGH/Maki --channel win -o $releases
  if ($LASTEXITCODE -ne 0) { Write-Host "No previous release to build a delta from" -ForegroundColor Yellow }
}

Write-Host "== Packing with Velopack ==" -ForegroundColor Cyan
vpk pack --packId Maki.Desktop --packVersion $version --packDir $app --mainExe Maki.exe `
  --packTitle Maki --packAuthors Maki --icon src/Maki.Desktop/maki.ico --runtime win-x64 --channel win -o $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Write-Host "Packed into $releases" -ForegroundColor Green
Get-ChildItem $releases | Format-Table Name, Length
