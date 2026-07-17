<#
.SYNOPSIS
  Build and push a single-arch (amd64) Mangarr image straight from this PC — for fast nightly
  deploys to an amd64 host (Unraid) without waiting on the multi-arch GitHub Actions build.

.DESCRIPTION
  CI builds linux/amd64 + linux/arm64; the arm64 half runs under qemu emulation and is what makes
  it slow. Unraid is amd64, so a plain `docker build` on this (amd64) machine needs no emulation and
  finishes in a couple of minutes. The image is stamped with the same VERSION / SOURCE_COMMIT build
  args the CI Dockerfile expects, with a -nightly version so the app's About/footer shows it is an
  unofficial build.

.PARAMETER Tag
  Primary tag to publish (default: nightly). Also always pushes main-<shortsha>.

.PARAMETER Registry
  Image repository (default: ghcr.io/orbitmpgh/mangarr). Must be lowercase for ghcr.

.PARAMETER NoPush
  Build only, don't push (image stays local as <Registry>:<Tag>).

.EXAMPLE
  # One-time login (do this yourself — create a PAT with write:packages):
  #   $env:CR_PAT | docker login ghcr.io -u OrbitMPGH --password-stdin
  ./distribution/build-local.ps1
#>
[CmdletBinding()]
param(
  [string]$Tag = "nightly",
  [string]$Registry = "ghcr.io/orbitmpgh/mangarr",
  [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

$sha  = (git rev-parse --short HEAD).Trim()
$full = (git rev-parse HEAD).Trim()
$dirty = if (git status --porcelain) { "-dirty" } else { "" }
$stamp = Get-Date -Format "yyyyMMddHHmm"

# Base the nightly on the highest release tag reachable from HEAD, then bump the patch. A nightly
# built after v0.9.0 is 0.9.0 PLUS unreleased commits, so it must sort *after* 0.9.0 — and in
# SemVer a prerelease sorts before its release (0.9.0-nightly < 0.9.0). Bumping to 0.9.1-nightly.X
# gives the correct ordering: after 0.9.0, before any real 0.9.1. `--merged HEAD` + version sort
# picks the right tag even when several tags share a commit (v0.8.0 and v0.9.0 both do).
$base = git tag --merged HEAD --sort=-v:refname --list 'v*' | Select-Object -First 1
if ($base) {
  $p = (($base -replace '^v', '') -replace '-.*$', '').Split('.')
  $next = "$($p[0]).$($p[1]).$([int]$p[2] + 1)"
} else {
  $next = "0.0.0"
}
$version = "$next-nightly.$stamp-$sha$dirty"

Write-Host "Building $Registry`:$Tag  (version $version)" -ForegroundColor Cyan

docker build `
  -f distribution/docker/Dockerfile `
  --build-arg VERSION=$version `
  --build-arg SOURCE_COMMIT=$full `
  -t "${Registry}:$Tag" `
  -t "${Registry}:main-$sha" `
  .
if ($LASTEXITCODE -ne 0) { throw "docker build failed" }

if ($NoPush) {
  Write-Host "Built (not pushed): ${Registry}:$Tag and :main-$sha" -ForegroundColor Green
  return
}

docker push "${Registry}:$Tag"
if ($LASTEXITCODE -ne 0) { throw "docker push failed (logged in to ghcr?)" }
docker push "${Registry}:main-$sha"
if ($LASTEXITCODE -ne 0) { throw "docker push failed" }

Write-Host "Pushed ${Registry}:$Tag and :main-$sha" -ForegroundColor Green
Write-Host "On Unraid: docker pull ${Registry}:$Tag  then restart the container." -ForegroundColor Cyan
