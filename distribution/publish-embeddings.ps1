<#
.SYNOPSIS
  Builds the base and large embedding indexes from scratch and (optionally) uploads each to its
  GitHub release tag, so users can download a prebuilt index instead of spending ~an hour of CPU
  rebuilding it.

.DESCRIPTION
  The embedding index is derived entirely from the public MangaBaka dump plus the pinned model,
  so it holds nothing user-specific - only MangaBaka ids, content hashes, vectors and tag blobs.
  That is what makes it publishable at all; check embeddings-artifact.cs if you want to see
  exactly which tables ship.

  Everything happens inside a git-ignored .artifacts folder, and every step is incremental:

    1. build-embeddings.cs downloads the *full* MangaBaka dump once, downloads each model, and
       runs the embedding pass into .artifacts\embeddings-<model>.db. The dump (~4.6 GB), the
       models, and the per-model index all persist between runs, so the first run is slow and
       every run after it only refreshes what changed - a fast "top up and republish".
    2. embeddings-artifact.cs validates each index (integrity, row counts, uniform vector width
       matching the model's dimensions) and compresses it to .zst with a manifest.json.
    3. Only with -Publish, and only after a y/N gate: create/reuse each model's release tag and
       upload its artifact + manifest.

  Without -Publish the script stops after step 2 and tells you what it would have uploaded.
  Nothing leaves the machine.

  This does not touch your running Maki install: it builds into .artifacts, not your config dir.

.PARAMETER Model
  Which model(s) to build and publish: "base", "large", or "both" (default). Both share the one
  dump download.

.PARAMETER ArtifactsDir
  Where the dump, models, per-model indexes, and packed artifacts live. Defaults to .artifacts in
  the repo root (git-ignored). Keep it around between runs - that is what makes reruns quick.

.PARAMETER MinRows
  Refuse to publish fewer than this many vectors. Guards against shipping a partial index.

.PARAMETER Publish
  Actually upload. Without it the script is a dry run.

.EXAMPLE
  ./distribution/publish-embeddings.ps1
  Build (or refresh) both indexes and print what would be uploaded. Uploads nothing.

.EXAMPLE
  ./distribution/publish-embeddings.ps1 -Model large -Publish
  Refresh only the large index and upload it to embeddings-large-latest after confirmation.
#>
[CmdletBinding()]
param(
  [ValidateSet("base", "large", "both")]
  [string]$Model = "both",
  [string]$ArtifactsDir = "",
  [int]$MinRows = 50000,
  [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $repoRoot ".artifacts" }

function Confirm-Step {
  param([string]$Message)
  $resp = Read-Host "$Message [y/N]"
  return $resp -match '^[Yy]'
}

function Require-Command {
  param([string]$Name, [string]$Hint)
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "$Name not found on PATH. $Hint"
  }
}

# Parse a model's contract out of the source tree rather than trusting flags: the artifact must
# describe the model this build actually produces, and land on the tag the client polls for it.
function Get-ModelProfile {
  param([string]$ModelName)
  $profilePath = Join-Path $repoRoot "src\Maki.Metadata\Embedding\EmbeddingModelProfile.cs"
  $profileText = Get-Content $profilePath -Raw
  $profileName = if ($ModelName -eq "large") { "Large" } else { "Base" }
  # Grab just this model's `Base = new(...)` / `Large = new(...)` block so the two can't be confused.
  if ($profileText -notmatch "(?s)EmbeddingModelProfile\s+$profileName\s*=\s*new\((.*?)\);") {
    throw "Could not find the '$profileName' model profile in $profilePath"
  }
  $block = $Matches[1]
  if ($block -notmatch 'Dimensions:\s*(\d+)') { throw "Could not read Dimensions for '$profileName'" }
  $dims = [int]$Matches[1]
  if ($block -notmatch 'Version:\s*"([^"]+)"') { throw "Could not read Version for '$profileName'" }
  $version = $Matches[1]
  if ($block -notmatch 'PrebuiltTag:\s*"([^"]+)"') { throw "Could not read PrebuiltTag for '$profileName'" }
  $tag = $Matches[1]
  return [pscustomobject]@{ Model = $ModelName; Dimensions = $dims; Version = $version; Tag = $tag }
}

# Windows PowerShell turns a native command's stderr into ErrorRecords, which $ErrorActionPreference
# = "Stop" would treat as a failure - and dotnet reports progress on stderr. Run with it relaxed and
# judge by the exit code instead.
function Invoke-Native {
  param([scriptblock]$Command)
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try { & $Command; return $LASTEXITCODE }
  finally { $ErrorActionPreference = $prev }
}

Require-Command -Name "dotnet" -Hint "Install the .NET SDK (the build/pack tools are file-based C# apps)."
if ($Publish) {
  Require-Command -Name "gh" -Hint "Install the GitHub CLI: https://cli.github.com"
}

$models = if ($Model -eq "both") { @("base", "large") } else { @($Model) }
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

Write-Host "artifacts dir : $ArtifactsDir"
Write-Host "models        : $($models -join ', ')"
Write-Host ""

$buildTool = Join-Path $PSScriptRoot "build-embeddings.cs"
$packTool = Join-Path $PSScriptRoot "embeddings-artifact.cs"
$repoSlug = if ($Publish) { (& gh repo view --json nameWithOwner --jq .nameWithOwner) } else { "<owner>/<repo>" }

$built = @()
foreach ($m in $models) {
  $info = Get-ModelProfile -ModelName $m
  Write-Host "===== $m ($($info.Version), $($info.Dimensions) dims -> tag '$($info.Tag)') =====" -ForegroundColor Cyan

  # 1. Build (or incrementally refresh) the index for this model into .artifacts.
  $buildExit = Invoke-Native { & dotnet run $buildTool -- $m $ArtifactsDir }
  Write-Host "$buildExit"
  if ($buildExit[-1] -ne "0") { throw "Building the $m index failed - nothing packed." }

  # 2. Validate and pack it into a compressed artifact + manifest under .artifacts\out\<model>.
  $indexDb = Join-Path $ArtifactsDir "embeddings-$m.db"
  $outDir = Join-Path $ArtifactsDir "out\$m"
  if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
  New-Item -ItemType Directory -Path $outDir -Force | Out-Null

  $packExit = Invoke-Native { & dotnet run $packTool -- $indexDb $outDir $info.Dimensions $MinRows }
  Write-Host "$packExit"
  if ($packExit[-1] -ne "0") { throw "Validating/packing the $m index failed - nothing packed." }

  $manifestPath = Join-Path $outDir "manifest.json"
  $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
  $archivePath = Join-Path $outDir $manifest.fileName

  # The client polls the manifest, so it carries the model contract and the URL the assets will
  # have once uploaded (a moving tag keeps that URL stable across runs).
  $manifest | Add-Member -NotePropertyName modelVersion -NotePropertyValue $info.Version -Force
  $manifest | Add-Member -NotePropertyName url -NotePropertyValue `
    "https://github.com/$repoSlug/releases/download/$($info.Tag)/$($manifest.fileName)" -Force

  # Not Set-Content -Encoding utf8: Windows PowerShell writes a BOM, and .NET's Utf8JsonReader
  # treats a leading BOM as an invalid start of a value - the client would fail to parse this.
  [System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 5),
    (New-Object System.Text.UTF8Encoding $false))

  Write-Host ""
  Write-Host "  artifact : $archivePath ($([math]::Round((Get-Item $archivePath).Length / 1MB)) MB)"
  Write-Host "  vectors  : $($manifest.rowCount) rows, $($manifest.vocabRowCount) tag vocabulary entries"
  Write-Host "  sha256   : $($manifest.sha256)"
  Write-Host ""

  $built += [pscustomobject]@{
    Model = $m; Tag = $info.Tag; ArchivePath = $archivePath; ManifestPath = $manifestPath; FileName = $manifest.fileName
  }
}

if (-not $Publish) {
  Write-Host "Dry run - nothing uploaded. Re-run with -Publish to upload." -ForegroundColor Cyan
  exit 0
}

Write-Host "About to upload to ${repoSlug}:" -ForegroundColor Yellow
foreach ($b in $built) {
  Write-Host "  $($b.Model) -> tag '$($b.Tag)': $($b.FileName) + manifest.json"
}
Write-Host "This is public and replaces whatever is on those tags now." -ForegroundColor Yellow
if (-not (Confirm-Step "Upload?")) {
  Write-Host "Stopped. Artifacts left in $ArtifactsDir."
  exit 1
}

foreach ($b in $built) {
  # A missing release is expected for the first publish of a tag.
  $viewExit = Invoke-Native { & gh release view $b.Tag --json tagName 2>$null | Out-Null }
  if ($viewExit -ne 0) {
    Write-Host "Creating release '$($b.Tag)'…"
    & gh release create $b.Tag --title "Prebuilt embedding index ($($b.Model))" --notes `
      "Prebuilt $($b.Model) embedding index for Maki's Discover search and recommendations. Generated from the public MangaBaka dump; contains no user data. Assets on this tag are replaced in place, so the download URL is stable."
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $($b.Tag)" }
  }

  & gh release upload $b.Tag $b.ArchivePath $b.ManifestPath --clobber
  if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $($b.Tag)" }
  Write-Host "Uploaded $($b.Model): https://github.com/$repoSlug/releases/download/$($b.Tag)/manifest.json" -ForegroundColor Green
}
