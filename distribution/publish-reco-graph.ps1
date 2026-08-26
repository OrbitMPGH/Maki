<#
.SYNOPSIS
  Exports, validates and (optionally) publishes the co-recommendation graph that Maki's
  recommendation engine reads, so users get the channel in a 1 MB download instead of a fetch
  measured in days.

.DESCRIPTION
  The graph is an aggregate of public "if you liked X, try Y" submissions from AniList and MAL,
  keyed by MangaBaka id, with vote counts. It identifies nobody: the fetcher never stores a
  per-user row, and reco-graph-artifact.cs refuses anything holding the fetcher's own tables.

  Three steps, all inside the git-ignored .artifacts folder:

    1. fetch-reco-graph.cs export folds .artifacts\reco-graph.db (the resumable working state a
       long fetch writes) into .artifacts\reco-edges.db - the shippable pair table, VACUUMed and
       indexed in both directions, with a meta table describing itself.
    2. reco-graph-artifact.cs validates it (integrity, pair floor, no self-pairs, no negative
       votes, meta present and consistent with the votes actually in the file) and compresses it
       to .zst with a manifest.json.
    3. Only with -Publish, and only after a y/N gate: create or reuse the release tag and upload
       the artifact + manifest.

  Without -Publish the script stops after step 2 and tells you what it would have uploaded.
  Nothing leaves the machine.

  This does not touch your running Maki install: it works in .artifacts, not your config dir.

.PARAMETER ArtifactsDir
  Where the working graph, the export and the packed artifact live. Defaults to .artifacts in the
  repo root (git-ignored).

.PARAMETER MinPairs
  Refuse to publish fewer than this many pairs. Guards against shipping a fetch that is still
  running: a partial graph is not wrong, but it is worse than the one already published.

.PARAMETER Tag
  Release tag to publish to. A moving tag keeps the download URL stable across runs, which is what
  lets the client poll one manifest forever.

.PARAMETER SkipExport
  Pack .artifacts\reco-edges.db as it already stands instead of re-folding it from reco-graph.db.
  Use it when the export came from somewhere else.

.PARAMETER Publish
  Actually upload. Without it the script is a dry run.

.EXAMPLE
  ./distribution/publish-reco-graph.ps1
  Export, validate and pack. Prints what would be uploaded. Uploads nothing.

.EXAMPLE
  ./distribution/publish-reco-graph.ps1 -Publish
  Same, then upload to reco-graph-latest after confirmation.
#>
[CmdletBinding()]
param(
  [string]$ArtifactsDir = "",
  [long]$MinPairs = 20000,
  [string]$Tag = "reco-graph-latest",
  [switch]$SkipExport,
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

# Windows PowerShell turns a native command's stderr into ErrorRecords, which
# $ErrorActionPreference = "Stop" would treat as a failure - and dotnet reports progress on stderr.
# Run with it relaxed and judge by the exit code instead. Out-Host consumes stdout as each line
# arrives, so a long step prints live rather than dumping at the end.
function Invoke-Native {
  param([scriptblock]$Command)
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try { & $Command | Out-Host; return $LASTEXITCODE }
  finally { $ErrorActionPreference = $prev }
}

Require-Command -Name "dotnet" -Hint "Install the .NET SDK (the export/pack tools are file-based C# apps)."
if ($Publish) {
  Require-Command -Name "gh" -Hint "Install the GitHub CLI: https://cli.github.com"
}

New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

$workDb = Join-Path $ArtifactsDir "reco-graph.db"
$edgesDb = Join-Path $ArtifactsDir "reco-edges.db"
$outDir = Join-Path $ArtifactsDir "out\reco-graph"
$repoSlug = if ($Publish) { (& gh repo view --json nameWithOwner --jq .nameWithOwner) } else { "<owner>/<repo>" }

Write-Host "artifacts dir : $ArtifactsDir"
Write-Host "tag           : $Tag"
Write-Host ""

# 1. Fold the working graph into the shippable pair table.
if ($SkipExport) {
  if (-not (Test-Path $edgesDb)) { throw "-SkipExport was given but $edgesDb does not exist." }
  Write-Host "Skipping export; packing $edgesDb as it stands." -ForegroundColor Cyan
} else {
  if (-not (Test-Path $workDb)) {
    throw "No $workDb. Run: dotnet run distribution/fetch-reco-graph.cs -- fetch"
  }

  $exportExit = Invoke-Native {
    & dotnet run (Join-Path $PSScriptRoot "fetch-reco-graph.cs") -- export --graph $workDb --out-db $edgesDb
  }
  if ($exportExit -ne 0) { throw "Export failed - nothing packed." }
}

# 2. Validate and pack.
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$packExit = Invoke-Native {
  & dotnet run (Join-Path $PSScriptRoot "reco-graph-artifact.cs") -- $edgesDb $outDir $MinPairs
}
if ($packExit -ne 0) { throw "Validating/packing the graph failed - nothing packed." }

$manifestPath = Join-Path $outDir "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$archivePath = Join-Path $outDir $manifest.fileName

# The client polls the manifest, so it has to carry the URL the asset will have once uploaded.
$manifest | Add-Member -NotePropertyName url -NotePropertyValue `
  "https://github.com/$repoSlug/releases/download/$Tag/$($manifest.fileName)" -Force

# Not Set-Content -Encoding utf8: Windows PowerShell writes a BOM, and .NET's Utf8JsonReader treats
# a leading BOM as an invalid start of a value - the client would fail to parse this.
[System.IO.File]::WriteAllText(
  $manifestPath,
  ($manifest | ConvertTo-Json -Depth 5),
  (New-Object System.Text.UTF8Encoding $false))

Write-Host ""
Write-Host "  artifact : $archivePath ($([math]::Round((Get-Item $archivePath).Length / 1KB)) KB)"
Write-Host "  pairs    : $($manifest.pairCount) over $($manifest.seriesCount) series"
Write-Host "  providers: $($manifest.providers)"
Write-Host "  sha256   : $($manifest.sha256)"
Write-Host ""

if (-not $Publish) {
  Write-Host "Dry run - nothing uploaded. Re-run with -Publish to upload." -ForegroundColor Cyan
  exit 0
}

Write-Host "About to upload to ${repoSlug}, tag '${Tag}':" -ForegroundColor Yellow
Write-Host "  $($manifest.fileName) + manifest.json"
Write-Host "This is public and replaces whatever is on that tag now." -ForegroundColor Yellow
if (-not (Confirm-Step "Upload?")) {
  Write-Host "Stopped. Artifacts left in $outDir."
  exit 1
}

# A missing release is expected for the first publish of a tag.
$viewExit = Invoke-Native { & gh release view $Tag --json tagName | Out-Null }
if ($viewExit -ne 0) {
  Write-Host "Creating release '$Tag'…"
  & gh release create $Tag --title "Co-recommendation graph" --notes `
    "Aggregated 'readers also liked' pairs from AniList and MAL, keyed by MangaBaka id, for Maki's recommendation engine. Derived from public recommendation submissions; contains no user data. Assets on this tag are replaced in place, so the download URL is stable."
  if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $Tag" }
}

& gh release upload $Tag $archivePath $manifestPath --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $Tag" }
Write-Host "Uploaded: https://github.com/$repoSlug/releases/download/$Tag/manifest.json" -ForegroundColor Green
