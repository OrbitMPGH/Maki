<#
.SYNOPSIS
  Build and push a single-arch (amd64) Maki image straight from this PC — for fast nightly
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
  Image repository (default: ghcr.io/orbitmpgh/maki). Must be lowercase for ghcr.

.PARAMETER NextVersion
  The release this nightly is heading toward, e.g. 0.10.0. Produces
  <NextVersion>-nightly.<stamp>-<sha>. Omit and it defaults to a patch bump of the highest tag
  reachable from HEAD (0.9.0 -> 0.9.1) — a safe guess that still sorts after the last release, but
  set this once you know the real target so the label isn't wrong (e.g. a 0.10.0 cycle).

.PARAMETER NoPush
  Build only, don't push (image stays local as <Registry>:<Tag>).

.EXAMPLE
  # One-time login (do this yourself — create a PAT with write:packages):
  #   $env:CR_PAT | docker login ghcr.io -u OrbitMPGH --password-stdin
  ./distribution/build-local.ps1                      # -> 0.9.1-nightly.<stamp>-<sha>
  ./distribution/build-local.ps1 -NextVersion 0.10.0  # -> 0.10.0-nightly.<stamp>-<sha>
#>
[CmdletBinding()]
param(
  [string]$Tag = "nightly",
  [string]$Registry = "ghcr.io/orbitmpgh/maki",
  [string]$NextVersion,
  [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

$sha  = (git rev-parse --short HEAD).Trim()
$full = (git rev-parse HEAD).Trim()
$dirty = if (git status --porcelain) { "-dirty" } else { "" }
$stamp = Get-Date -Format "yyyyMMddHHmm"

# Nightly is labelled as a prerelease of the *next* release: <target>-nightly.<stamp>-<sha>. The
# Dockerfile appends "+<sha>" as build metadata, so the target must be a plain X.Y.Z (a "+" in
# VERSION would double up and break SemVer).
#
# Target resolution: -NextVersion wins; otherwise patch-bump the highest tag reachable from HEAD.
# A nightly built after v0.9.0 is 0.9.0 PLUS unreleased commits and must sort *after* 0.9.0 — and
# in SemVer a prerelease sorts before its release (0.9.0-nightly < 0.9.0), so we can't just reuse
# 0.9.0. Patch-bump (0.9.1-nightly) sorts correctly after 0.9.0 no matter what; if the real next
# release is a minor (0.10.0), pass -NextVersion 0.10.0 so the label matches your intent.
# `--merged HEAD` + version sort picks the right tag even when tags share a commit (v0.8.0 and
# v0.9.0 both sit on the same one here).
if ($NextVersion) {
  $target = $NextVersion -replace '^v', ''
  if ($target -notmatch '^\d+\.\d+\.\d+$') {
    throw "NextVersion must be X.Y.Z (got '$NextVersion')"
  }
} else {
  $base = git tag --merged HEAD --sort=-v:refname --list 'v*' | Select-Object -First 1
  if ($base) {
    $p = (($base -replace '^v', '') -replace '-.*$', '').Split('.')
    $target = "$($p[0]).$($p[1]).$([int]$p[2] + 1)"
  } else {
    $target = "0.0.0"
  }
}
$version = "$target-nightly.$stamp-$sha$dirty"

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
