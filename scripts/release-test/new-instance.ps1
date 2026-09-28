<#
.SYNOPSIS
  Starts a throwaway Maki container for docs/release-checklist.md on fresh copies of the fixtures.

.DESCRIPTION
  Copies .release-test/template/config (seeded by seed-config.ps1) and
  .release-test/template/import (made by make-import.mjs) into a new
  .release-test/runs/<timestamp> folder, with an empty library beside them, and runs -Image on
  those copies. The templates themselves are never mounted, so every run starts from the same
  state however the last one ended.

  -Fresh starts on an empty config instead, for the first-run checks. -Reuse starts the image on
  an existing run folder without copying anything, for switching images on the same data.

.EXAMPLE
  ./scripts/release-test/new-instance.ps1

.EXAMPLE
  ./scripts/release-test/new-instance.ps1 -Fresh

.EXAMPLE
  # Upgrade test: use a folder with the previous release, then the candidate on the same folder.
  ./scripts/release-test/new-instance.ps1 -Image ghcr.io/orbitmpgh/maki:latest
  ./scripts/release-test/new-instance.ps1 -Reuse .release-test/runs/20260928-101500
#>
[CmdletBinding()]
param(
  [string]$Image = "ghcr.io/orbitmpgh/maki:rc",
  [int]$Port = 8990,
  [string]$Name = "maki-test",
  [switch]$Fresh,
  [string]$Reuse
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$template = Join-Path $repoRoot ".release-test\template"
if ($Reuse) {
  $run = (Resolve-Path $Reuse).Path
} else {
  $run = Join-Path $repoRoot ".release-test\runs\$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}

function Copy-Tree {
  param([string]$From, [string]$To)
  New-Item -ItemType Directory -Force $To | Out-Null
  & robocopy $From $To /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "Copying $From failed (robocopy exit code $LASTEXITCODE)" }
}

if (-not $Reuse -and -not (Test-Path (Join-Path $template "import"))) {
  throw "No import template. Run: node scripts/release-test/make-import.mjs"
}
if (-not $Reuse -and -not $Fresh -and -not (Test-Path (Join-Path $template "config"))) {
  throw "No config template. Run scripts/release-test/seed-config.ps1, or pass -Fresh."
}

& docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Docker is not running. Start Docker Desktop and try again." }

if (-not $Reuse) {
  Write-Host "Preparing $run"
  Copy-Tree (Join-Path $template "import") (Join-Path $run "import")
  New-Item -ItemType Directory -Force (Join-Path $run "library") | Out-Null
  if ($Fresh) {
    New-Item -ItemType Directory -Force (Join-Path $run "config") | Out-Null
  } else {
    Copy-Tree (Join-Path $template "config") (Join-Path $run "config")
  }
}

# Stop before removing: rm -f kills outright, and with -Reuse the next image opens that database.
if (& docker ps -aq --filter "name=^/$Name$") {
  & docker stop -t 60 $Name | Out-Null
  & docker rm $Name | Out-Null
}
& docker run -d --name $Name -p "${Port}:8990" `
  -v "$(Join-Path $run 'config'):/config" `
  -v "$(Join-Path $run 'library'):/library" `
  -v "$(Join-Path $run 'import'):/import" `
  $Image | Out-Null
if ($LASTEXITCODE -ne 0) { throw "docker run failed" }

$lan = Get-NetIPConfiguration |
  Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq "Up" } |
  ForEach-Object { $_.IPv4Address.IPAddress } |
  Select-Object -First 1

Write-Host ""
Write-Host "Running $Image as $Name"
Write-Host "  Secure:    http://localhost:$Port"
if ($lan) { Write-Host "  Insecure:  http://${lan}:$Port" } else { Write-Host "  Insecure:  no LAN address found" }
Write-Host "  Files:     $run"
if ($Fresh) {
  Write-Host "  Login:     none yet, first-run setup"
} else {
  Write-Host "  Login:     see $(Join-Path $template 'README.txt')"
}
Write-Host "  Logs:      docker logs -f $Name"
Write-Host "  Stop:      docker rm -f $Name"
