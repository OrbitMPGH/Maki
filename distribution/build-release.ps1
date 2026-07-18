<#
.SYNOPSIS
  Build and push an official multi-arch Mangarr release image straight from this PC — an escape
  hatch for when the GitHub Actions Docker build is broken or stuck (e.g. a hung `npm ci`).

.DESCRIPTION
  Mirrors what .github/workflows/docker.yml does for a real release tag: builds linux/amd64 +
  linux/arm64 via buildx, stamps VERSION/SOURCE_COMMIT into the binary, and pushes the same tag
  set CI would — <version>, <major>.<minor>, <major>, and `latest` (only for a stable, non-
  prerelease version, matching CI's rule).

  Two safety checks a nightly build doesn't need, because this publishes as a real version number:
    - the working tree must be clean (an official image must be reproducible from its tag)
    - a git tag matching -Version must already exist and point at HEAD
  Both are overridable for a dry run, but leave them on for anything you intend to ship.

.PARAMETER Version
  Release version, e.g. 0.11.0 or v0.11.0 (leading v is optional). Must be plain X.Y.Z, optionally
  with a prerelease suffix (0.11.0-rc.1) — a prerelease only pushes the full version, no
  major/major.minor/latest tags, same as CI.

.PARAMETER Registry
  Image repository (default: ghcr.io/orbitmpgh/mangarr). Must be lowercase for ghcr.

.PARAMETER Amd64Only
  Build linux/amd64 only, skipping the slower arm64-under-qemu leg. Use for a quick smoke test —
  NOT for an image you intend users to actually pull, since arm64 hosts (Raspberry Pi, ARM NAS)
  would silently get no image for this tag.

.PARAMETER NoPush
  Build only, don't push. Only valid together with -Amd64Only: buildx can't load a multi-platform
  build into the local Docker engine, only push it straight to a registry.

.PARAMETER AllowDirty
  Skip the clean-working-tree check.

.PARAMETER SkipTagCheck
  Skip requiring a git tag matching -Version to exist and point at HEAD.

.EXAMPLE
  # One-time login (do this yourself — create a PAT with write:packages):
  #   $env:CR_PAT | docker login ghcr.io -u OrbitMPGH --password-stdin
  git tag v0.11.0 && git push origin v0.11.0
  ./distribution/build-release.ps1 -Version 0.11.0
  # -> pushes :0.11.0, :0.11, :0, :latest

.EXAMPLE
  # Quick amd64-only sanity build, nothing pushed:
  ./distribution/build-release.ps1 -Version 0.11.0 -Amd64Only -NoPush -AllowDirty -SkipTagCheck
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Version,
  [string]$Registry = "ghcr.io/orbitmpgh/mangarr",
  [switch]$Amd64Only,
  [switch]$NoPush,
  [switch]$AllowDirty,
  [switch]$SkipTagCheck
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

if ($Registry -cne $Registry.ToLowerInvariant()) {
  throw "Registry must be lowercase for ghcr: $Registry"
}

$version = $Version -replace '^v', ''
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
  throw "Version must be X.Y.Z or X.Y.Z-<prerelease> (got '$Version')"
}
$stable = $version -notmatch '-'

if ($NoPush -and -not $Amd64Only) {
  throw "-NoPush needs -Amd64Only - buildx can't --load a multi-platform build, only --push it"
}

if (-not $AllowDirty -and (git status --porcelain)) {
  throw "Working tree is dirty. An official release image should build from a clean, tagged " +
        "commit - commit/stash first, or pass -AllowDirty for a throwaway test build."
}

$tagRef = "v$version"
if (-not $SkipTagCheck) {
  $tagCommit = git rev-parse --verify --quiet "$tagRef^{commit}" 2>$null
  $headCommit = git rev-parse HEAD
  if (-not $tagCommit) {
    throw "Git tag '$tagRef' doesn't exist. Create and push it first:`n" +
          "  git tag $tagRef && git push origin $tagRef`n" +
          "or pass -SkipTagCheck for a build that isn't a real release."
  }
  if ($tagCommit.Trim() -ne $headCommit.Trim()) {
    throw "Git tag '$tagRef' doesn't point at HEAD ($($tagCommit.Substring(0,7)) vs " +
          "$($headCommit.Substring(0,7))). Check out the tagged commit, or pass -SkipTagCheck."
  }
}

$full = (git rev-parse HEAD).Trim()
$platforms = if ($Amd64Only) { "linux/amd64" } else { "linux/amd64,linux/arm64" }

$tags = @("${Registry}:$version")
if ($stable) {
  $parts = $version.Split('.')
  $tags += "${Registry}:$($parts[0]).$($parts[1])"
  $tags += "${Registry}:$($parts[0])"
  $tags += "${Registry}:latest"
}

Write-Host "Building $($tags -join ', ')" -ForegroundColor Cyan
Write-Host "Platforms: $platforms" -ForegroundColor Cyan

# A dedicated builder is needed for multi-platform: the default "docker" driver on most installs
# can't produce a multi-arch manifest. Idempotent — reused across runs.
#
# Checked via `buildx ls` text output, not `buildx inspect`'s exit code: a native command's stderr
# under $ErrorActionPreference = "Stop" gets wrapped into a terminating NativeCommandError even
# when redirected (*> / 2>&1 both trigger it), so a nonexistent builder would abort the script here
# instead of falling through to create one.
$builderName = "mangarr-release"
$existing = docker buildx ls | Select-String -Pattern "^$builderName\b"
if (-not $existing) {
  docker buildx create --name $builderName --driver docker-container --use | Out-Null
} else {
  docker buildx use $builderName | Out-Null
}

$tagArgs = $tags | ForEach-Object { "-t", $_ }
$pushOrLoad = if ($NoPush) { "--load" } else { "--push" }

docker buildx build `
  --platform $platforms `
  -f distribution/docker/Dockerfile `
  --build-arg VERSION=$version `
  --build-arg SOURCE_COMMIT=$full `
  @tagArgs `
  $pushOrLoad `
  .
if ($LASTEXITCODE -ne 0) {
  throw "docker buildx build failed. If it's an arm64 'exec format error', binfmt/qemu isn't " +
        "registered - run: docker run --privileged --rm tonistiigi/binfmt --install all"
}

if ($NoPush) {
  Write-Host "Built (not pushed): $($tags -join ', ')" -ForegroundColor Green
} else {
  Write-Host "Pushed: $($tags -join ', ')" -ForegroundColor Green
}
