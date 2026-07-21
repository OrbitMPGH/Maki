<#
.SYNOPSIS
  Packs the local embedding index into a release artifact and (optionally) uploads it to a
  GitHub release, so users can download it instead of spending ~an hour of CPU rebuilding it.

.DESCRIPTION
  The embedding index is derived entirely from the public MangaBaka dump plus the pinned model,
  so it holds nothing user-specific - only MangaBaka ids, content hashes, vectors and tag blobs.
  That is what makes it publishable at all; check embeddings-artifact.cs if you want to see
  exactly which tables ship.

  Steps:
    1. Read the expected model version and dimensions out of EmbeddingOptions.cs, so the artifact
       can never claim a model the source tree doesn't build.
    2. Validate the database (embeddings-artifact.cs): integrity check, row counts, and uniform
       vector width matching those dimensions. A pass that is still running, or a half-finished
       re-embed after a model change, is refused here rather than shipped.
    3. Compress to .zst (the client already depends on ZstdSharp for the MangaBaka dump).
    4. Write manifest.json next to it - what the client polls to decide whether a download is
       compatible and newer than what it has.
    5. Only with -Publish, and only after a y/N gate: create/reuse the release tag and upload.

  Without -Publish the script stops after step 4 and tells you what it would have uploaded.
  Nothing leaves the machine.

  Stop Maki (or at least let the indexing pass finish) before running this: publishing a file
  that is being written to is how you ship a corrupt index.

.PARAMETER ConfigDir
  Maki's config dir. Defaults to MAKI_CONFIG_DIR, else %APPDATA%\Maki.

.PARAMETER Tag
  Release tag to attach the artifact to. A moving tag is intended - re-running replaces the
  assets in place, so the download URL never changes.

.PARAMETER MinRows
  Refuse to publish fewer than this many vectors. Guards against shipping a partial index.

.PARAMETER Publish
  Actually upload. Without it the script is a dry run.

.EXAMPLE
  ./distribution/publish-embeddings.ps1
  Validate and pack, print what would be uploaded, upload nothing.

.EXAMPLE
  ./distribution/publish-embeddings.ps1 -Publish
  The same, then upload after confirmation.
#>
[CmdletBinding()]
param(
  [string]$ConfigDir = $(if ($env:MAKI_CONFIG_DIR) { $env:MAKI_CONFIG_DIR } else { Join-Path $env:APPDATA "Maki" }),
  [string]$Tag = "embeddings-latest",
  [int]$MinRows = 50000,
  [string]$StagingDir = $(Join-Path $env:TEMP "maki-embeddings"),
  [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

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

Require-Command -Name "dotnet" -Hint "Install the .NET SDK (the artifact tool is a file-based C# app)."
if ($Publish) {
  Require-Command -Name "gh" -Hint "Install the GitHub CLI: https://cli.github.com"
}

$dbPath = Join-Path $ConfigDir "embeddings.db"
if (-not (Test-Path $dbPath)) {
  throw "No embedding index at $dbPath. Build it first (Settings -> Recommendation index -> Build index)."
}

# A non-empty -wal means there are committed pages the main file doesn't have yet, so Maki is
# probably running and this is not a consistent snapshot. The sidecar itself survives a clean
# shutdown at zero bytes, so its mere existence proves nothing - only its size does.
$walPath = "$dbPath-wal"
if ((Test-Path $walPath) -and ((Get-Item $walPath).Length -gt 0)) {
  $walSize = [math]::Round((Get-Item $walPath).Length / 1MB, 1)
  Write-Host "warning: $walPath holds $walSize MB of un-checkpointed writes - Maki looks like it is running." -ForegroundColor Yellow
  Write-Host "         Stop it first; publishing a database mid-write ships a corrupt index." -ForegroundColor Yellow
  if (-not (Confirm-Step "Continue anyway?")) { exit 1 }
}

# Parse the model contract out of the source tree rather than trusting a flag: the artifact must
# describe the model this build actually produces.
$optionsPath = Join-Path $repoRoot "src\Maki.Metadata\Embedding\EmbeddingOptions.cs"
$optionsText = Get-Content $optionsPath -Raw
if ($optionsText -notmatch 'ModelVersion\s*=\s*"([^"]+)"') {
  throw "Could not read ModelVersion from $optionsPath"
}
$modelVersion = $Matches[1]
if ($optionsText -notmatch 'Dimensions\s*\{\s*get;\s*init;\s*\}\s*=\s*(\d+)') {
  throw "Could not read Dimensions from $optionsPath"
}
$dimensions = [int]$Matches[1]

Write-Host "config dir    : $ConfigDir"
Write-Host "model version : $modelVersion ($dimensions dims)"
Write-Host "index         : $dbPath ($([math]::Round((Get-Item $dbPath).Length / 1MB)) MB)"
Write-Host ""

if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

$tool = Join-Path $PSScriptRoot "embeddings-artifact.cs"
# Windows PowerShell turns a native command's stderr into ErrorRecords, which $ErrorActionPreference
# = "Stop" would treat as a failure - and the tool reports progress on stderr. Let it write freely
# and judge it by its exit code instead.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
  & dotnet run $tool -- $dbPath $StagingDir $dimensions $MinRows
  $toolExit = $LASTEXITCODE
} finally {
  $ErrorActionPreference = $prevEap
}

if ($toolExit -ne 0) {
  throw "Validation failed - nothing was packed."
}

$manifestPath = Join-Path $StagingDir "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$archivePath = Join-Path $StagingDir $manifest.fileName

# The client polls the manifest, so it carries the model contract and the URL the assets will
# have once uploaded (a moving tag keeps that URL stable across runs).
$repoSlug = if ($Publish) { (& gh repo view --json nameWithOwner --jq .nameWithOwner) } else { "<owner>/<repo>" }
$manifest | Add-Member -NotePropertyName modelVersion -NotePropertyValue $modelVersion -Force
$manifest | Add-Member -NotePropertyName url -NotePropertyValue `
  "https://github.com/$repoSlug/releases/download/$Tag/$($manifest.fileName)" -Force

# Not Set-Content -Encoding utf8: Windows PowerShell writes a BOM, and .NET's Utf8JsonReader
# treats a leading BOM as an invalid start of a value - the client would fail to parse this.
[System.IO.File]::WriteAllText(
  $manifestPath,
  ($manifest | ConvertTo-Json -Depth 5),
  (New-Object System.Text.UTF8Encoding $false))

Write-Host ""
Write-Host "artifact : $archivePath ($([math]::Round((Get-Item $archivePath).Length / 1MB)) MB)"
Write-Host "manifest : $manifestPath"
Write-Host "vectors  : $($manifest.rowCount) rows, $($manifest.vocabRowCount) tag vocabulary entries"
Write-Host "sha256   : $($manifest.sha256)"

if (-not $Publish) {
  Write-Host ""
  Write-Host "Dry run - nothing uploaded. Re-run with -Publish to upload to tag '$Tag'." -ForegroundColor Cyan
  exit 0
}

Write-Host ""
Write-Host "About to upload to $repoSlug, release tag '$Tag':" -ForegroundColor Yellow
Write-Host "  $($manifest.fileName)  ($([math]::Round((Get-Item $archivePath).Length / 1MB)) MB)"
Write-Host "  manifest.json"
Write-Host "This is public and replaces whatever is on that tag now." -ForegroundColor Yellow
if (-not (Confirm-Step "Upload?")) {
  Write-Host "Stopped. Artifact left in $StagingDir."
  exit 1
}

$existing = & gh release view $Tag --json tagName 2>$null
if ($LASTEXITCODE -ne 0) {
  Write-Host "Creating release '$Tag'…"
  & gh release create $Tag --title "Prebuilt embedding index" --notes `
    "Prebuilt embedding index for Maki's Discover search and recommendations. Generated from the public MangaBaka dump; contains no user data. Assets on this tag are replaced in place, so the download URL is stable."
  if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
}

& gh release upload $Tag $archivePath $manifestPath --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }

Write-Host ""
Write-Host "Uploaded. Manifest URL:" -ForegroundColor Green
Write-Host "  https://github.com/$repoSlug/releases/download/$Tag/manifest.json"
