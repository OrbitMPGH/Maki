<#
.SYNOPSIS
  Builds the config template for docs/release-checklist.md: a /config folder with the admin
  account already created, /library and /import registered as root folders, the setup guide
  finished, and the MangaBaka catalogue and recommendation index downloaded.

.DESCRIPTION
  Starts -Image on an empty config, drives first-run setup over the API, waits for the catalogue
  and recommendation artifacts to arrive, stops the container cleanly and keeps the result in
  .release-test/template/config. new-instance.ps1 copies it for each test run, so the multi-minute
  catalogue download happens once instead of every time.

  Seed with the last released image (the default). A release candidate started on this template
  then migrates it forward, which is the upgrade path most self-hosters take. Re-seed now and then
  so the template does not drift too many releases behind.

  The account below is a local test account for throwaway instances. Never reuse it anywhere real.

.PARAMETER CatalogueFrom
  Path to an existing mangabaka.db to copy in instead of downloading it (about 3.5 GB either way).

.EXAMPLE
  ./scripts/release-test/seed-config.ps1

.EXAMPLE
  ./scripts/release-test/seed-config.ps1 -CatalogueFrom .e2econfig/mangabaka.db
#>
[CmdletBinding()]
param(
  [string]$Image = "ghcr.io/orbitmpgh/maki:latest",
  [int]$Port = 8995,
  [string]$Username = "admin",
  [string]$Password = "maki-release-test",
  [string]$CatalogueFrom,
  [int]$TimeoutMinutes = 90
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$work = Join-Path $repoRoot ".release-test"
$template = Join-Path $work "template"
$staging = Join-Path $work "seeding"
$container = "maki-seed"
$base = "http://localhost:$Port"

function Invoke-Docker {
  & docker @args
  if ($LASTEXITCODE -ne 0) { throw "docker $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Invoke-Maki {
  param([string]$Method, [string]$Path, $Body)
  $headers = @{}
  $xsrf = $session.Cookies.GetCookies($base) | Where-Object Name -eq "XSRF-TOKEN" | Select-Object -First 1
  if ($xsrf) { $headers["X-XSRF-TOKEN"] = [Uri]::UnescapeDataString($xsrf.Value) }
  $params = @{ Method = $Method; Uri = "$base/api/v1/$Path"; WebSession = $session; Headers = $headers; UseBasicParsing = $true }
  if ($null -ne $Body) {
    $params.ContentType = "application/json"
    $params.Body = ($Body | ConvertTo-Json -Compress)
  }
  $response = Invoke-WebRequest @params
  if ($response.Content) { return $response.Content | ConvertFrom-Json }
}

& docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Docker is not running. Start Docker Desktop and try again." }

if (& docker ps -aq --filter "name=^/$container$") { & docker rm -f $container | Out-Null }
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
foreach ($dir in "config", "library", "import") { New-Item -ItemType Directory -Force (Join-Path $staging $dir) | Out-Null }

if ($CatalogueFrom) {
  Write-Host "Copying catalogue from $CatalogueFrom"
  Copy-Item (Resolve-Path $CatalogueFrom) (Join-Path $staging "config\mangabaka.db")
}

Write-Host "Pulling $Image"
& docker pull $Image | Out-Null
$labels = (& docker image inspect $Image --format '{{json .Config.Labels}}') | ConvertFrom-Json
$version = $labels.'org.opencontainers.image.version'

Write-Host "Starting $container on $base"
Invoke-Docker run -d --name $container -p "${Port}:8990" `
  -v "$(Join-Path $staging 'config'):/config" `
  -v "$(Join-Path $staging 'library'):/library" `
  -v "$(Join-Path $staging 'import'):/import" `
  $Image | Out-Null

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$deadline = (Get-Date).AddMinutes(5)
while ($true) {
  try {
    Invoke-WebRequest "$base/api/v1/auth/me" -WebSession $session -UseBasicParsing | Out-Null
    break
  } catch {
    if ($_.Exception.Response) { break }
    if ((Get-Date) -gt $deadline) { throw "Maki did not answer on $base within five minutes. See: docker logs $container" }
    Start-Sleep -Seconds 2
  }
}

Write-Host "Creating admin account '$Username'"
Invoke-Maki POST "auth/setup" @{ username = $Username; password = $Password; displayName = "Release Test" } | Out-Null
Invoke-Maki GET "auth/me" | Out-Null
foreach ($path in "/library", "/import") { Invoke-Maki POST "rootfolder" @{ path = $path } | Out-Null }
Invoke-Maki PUT "settings/setup" @{ completed = $true } | Out-Null

Write-Host "Waiting for the catalogue, model and recommendation index (up to $TimeoutMinutes minutes)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$ready = $false
$last = ""
while ((Get-Date) -lt $deadline) {
  $index = Invoke-Maki GET "settings/recommendations"
  $ready = $index.dumpPresent -and $index.modelPresent -and $index.vectorCount -gt 0 -and -not $index.running -and -not $index.modelSwitching
  if ($ready) { break }
  if ($index.dumpPresent -and $index.vectorCount -gt 0 -and -not $index.modelPresent -and -not $index.modelSwitching) {
    # The ONNX model is only fetched the first time something embeds a query, so ask for one.
    Invoke-Maki POST "recommendations/discover/search" @{ query = "a wandering swordsman"; limit = 1 } | Out-Null
    continue
  }
  $missing = @()
  if (-not $index.dumpPresent) { $missing += "catalogue" }
  if (-not $index.modelPresent -or $index.modelSwitching) { $missing += "model" }
  if ($index.vectorCount -eq 0 -or $index.running) { $missing += "index ($($index.phase))" }
  if ($index.modelSwitchError) { $missing += "model error: $($index.modelSwitchError)" }
  $line = $missing -join ", "
  if ($line -ne $last) { Write-Host ("  {0:HH:mm:ss} waiting on: {1}" -f (Get-Date), $line); $last = $line }
  Start-Sleep -Seconds 15
}
if (-not $ready) { Write-Warning "Timed out; the template is saved anyway and the missing parts will download on first start." }

# The artifact jobs fire a few minutes after startup, possibly before the model was there, so
# fetch each one now rather than waiting out their schedules.
foreach ($artifact in "co-graph", "co-read", "reader-cohorts", "taste-vectors") {
  try {
    $result = Invoke-Maki POST "settings/recommendations/$artifact/download"
    if ($result.installed) { Write-Host "  $artifact installed" } else { Write-Warning "$artifact not installed: $($result.reason)" }
  } catch {
    Write-Warning "$artifact download failed: $($_.Exception.Message)"
  }
}

Write-Host "Stopping $container"
Invoke-Docker stop -t 120 $container | Out-Null
Invoke-Docker rm $container | Out-Null

# Logs and backups from the seeding run would read as the test run's own.
foreach ($dir in "logs", "backups") {
  $path = Join-Path $staging "config\$dir"
  if (Test-Path $path) { Remove-Item -Recurse -Force $path }
}

$target = Join-Path $template "config"
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
New-Item -ItemType Directory -Force $template | Out-Null
Move-Item (Join-Path $staging "config") $target
Remove-Item -Recurse -Force $staging

@"
Config template for docs/release-checklist.md, made by scripts/release-test/seed-config.ps1.

Seeded with: $Image ($version) on $(Get-Date -Format 'yyyy-MM-dd')
Admin login: $Username / $Password (local throwaway test account)
Root folders: /library and /import, so mount both when running it.
Changed from defaults: setup guide finished. Everything else is as a new install leaves it.
"@ | Set-Content -Encoding utf8 (Join-Path $template "README.txt")

Write-Host "Config template saved to $target"
