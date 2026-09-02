<#
.SYNOPSIS
  Exports, validates and (optionally) publishes the crowd graphs Maki's recommendation engine
  reads, so users get each channel in a download instead of a fetch measured in days.

.DESCRIPTION
  Two artifacts, one script, because everything but the validation is identical and two copies of
  this would be two copies to keep in step.

    reco   - reco-edges.db, aggregated "if you liked X, try Y" submissions from AniList and
             MyAnimeList, keyed by MangaBaka id with vote counts. Identifies nobody: the fetcher
             never stores a per-user row at all.

    coread - coread-edges.db, co-occurrence across AniList reading lists. Derived from per-user
             data, and the export is the only part that may leave this machine. graph-artifact.cs
             refuses outright to pack anything still carrying user_entry, user_state or
             pending_user, before it checks anything else and with no override - see the
             PUBLISHING SAFELY note below.

  Three steps per graph, all inside the git-ignored .artifacts folder:

    1. The fetcher's `export` mode folds its resumable working database into the shippable pair
       table, VACUUMed and indexed, with a meta table describing itself.
    2. graph-artifact.cs validates it and compresses it to .zst with a manifest.json.
    3. Only with -Publish, and only after a y/N gate: create or reuse the release tag and upload
       the artifact + manifest.

  Without -Publish the script stops after step 2 and tells you what it would have uploaded.
  Nothing leaves the machine.

  This does not touch your running Maki install: it works in .artifacts, not your config dir.

.NOTES
  PUBLISHING SAFELY
  coread-graph.db and coread-edges.db differ by four characters and live in the same folder. The
  first holds one row per user per series read; the second is the aggregate that ships. Publishing
  the first would be a privacy incident rather than a broken feature, so it is refused in three
  places: here, by graph-artifact.cs before it packs anything, and by CoReadInstaller before it
  installs anything. If any of them says a file holds per-user reading tables, do not work around
  it - re-run the export.

.PARAMETER Graph
  Which graph to work on: reco, coread, or both (the default).

.PARAMETER ArtifactsDir
  Where the working graphs, exports and packed artifacts live. Defaults to .artifacts in the repo
  root (git-ignored).

.PARAMETER MinPairs
  Refuse to publish fewer than this many pairs. Guards against shipping a fetch that is still
  running: a partial graph is not wrong, but it is worse than the one already published. Applied
  per graph, so it defaults per graph unless given explicitly.

.PARAMETER SkipExport
  Pack the existing export as it already stands instead of re-folding it from the working database.
  Use it when the export came from somewhere else.

.PARAMETER Publish
  Actually upload. Without it the script is a dry run.

.EXAMPLE
  ./distribution/publish-graph.ps1
  Export, validate and pack both graphs. Prints what would be uploaded. Uploads nothing.

.EXAMPLE
  ./distribution/publish-graph.ps1 -Graph coread -Publish
  Just the co-read graph, uploaded after confirmation.
#>
[CmdletBinding()]
param(
  [ValidateSet("reco", "coread", "taste", "cohorts", "both", "all")]
  [string]$Graph = "both",
  [string]$ArtifactsDir = "",
  [long]$MinPairs = 0,
  [switch]$SkipExport,
  [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $repoRoot ".artifacts" }

# Everything that differs between the two graphs, in one place. The tags must match the ReleaseTag
# constants on RecoGraphInstaller and CoReadInstaller, which is what clients poll.
$profiles = @{
  reco = [pscustomobject]@{
    Name       = "reco"
    Label      = "Co-recommendation graph"
    Tag        = "reco-graph-latest"
    Fetcher    = "fetch-reco-graph.cs"
    WorkingDb  = "reco-graph.db"
    ExportDb   = "reco-edges.db"
    WorkingArg = "--graph"
    MinPairs   = 20000
    Notes      = "Aggregated 'readers also liked' pairs from AniList and MyAnimeList, keyed by MangaBaka id, for Maki's recommendation engine. Derived from public recommendation submissions; contains no user data. Assets on this tag are replaced in place, so the download URL is stable."
  }
  coread = [pscustomobject]@{
    Name       = "coread"
    Label      = "Co-read graph"
    Tag        = "coread-graph-latest"
    Fetcher    = "fetch-coread-graph.cs"
    WorkingDb  = "coread-graph.db"
    ExportDb   = "coread-edges.db"
    WorkingArg = "--work"
    MinPairs   = 100000
    Notes      = "Co-occurrence across AniList reading lists, keyed by MangaBaka id, for Maki's recommendation engine. An aggregate only: no per-user row is included, and the tooling refuses to publish a file that carries one. Assets on this tag are replaced in place, so the download URL is stable."
  }
  taste = [pscustomobject]@{
    Name       = "taste"
    Label      = "Behavioural vectors"
    Tag        = "taste-vectors-latest"
    Fetcher    = "build-taste-vectors.cs"
    WorkingDb  = "coread-graph.db"
    ExportDb   = "taste-vectors.db"
    WorkingArg = "--work"
    OutArg     = "--out"
    IsBuild    = $true
    MinPairs   = 20000
    Notes      = "Item vectors factorized from AniList reading lists, keyed by MangaBaka id, for Maki's recommendation engine. An aggregate only: no per-user row is included, the tooling refuses to publish a file that carries one, and it also refuses a fold-limited evaluation build. Assets on this tag are replaced in place, so the download URL is stable."
  }
  cohorts = [pscustomobject]@{
    Name       = "cohorts"
    Label      = "Reader cohorts"
    Tag        = "reader-cohorts-latest"
    Fetcher    = "build-reader-cohorts.cs"
    WorkingDb  = "coread-graph.db"
    ExportDb   = "reader-cohorts.db"
    WorkingArg = "--work"
    OutArg     = "--out"
    IsBuild    = $true
    DumpDb     = "mangabaka.full.db"
    TasteDb    = "taste-vectors.db"
    MinPairs   = 20000
    Notes      = "What groups of AniList readers finished and scored, keyed by MangaBaka id, for Maki's 'readers like you' surfaces. An aggregate only, and one with no user axis at all: every row describes a group or a series, cohort membership is never written down, the tooling refuses to publish a file carrying a per-user table, and it also refuses a fold-limited evaluation build. Assets on this tag are replaced in place, so the download URL is stable."
  }
}

$selected = switch ($Graph) {
  "both" { @($profiles.reco, $profiles.coread) }
  "all"  { @($profiles.reco, $profiles.coread, $profiles.taste, $profiles.cohorts) }
  default { @($profiles[$Graph]) }
}

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

$repoSlug = if ($Publish) { (& gh repo view --json nameWithOwner --jq .nameWithOwner) } else { "<owner>/<repo>" }
$packTool = Join-Path $PSScriptRoot "graph-artifact.cs"

Write-Host "artifacts dir : $ArtifactsDir"
Write-Host "graphs        : $(($selected | ForEach-Object { $_.Name }) -join ', ')"
Write-Host ""

$built = @()

foreach ($g in $selected) {
  Write-Host "===== $($g.Name) -> tag '$($g.Tag)' =====" -ForegroundColor Cyan

  $workDb = Join-Path $ArtifactsDir $g.WorkingDb
  $edgesDb = Join-Path $ArtifactsDir $g.ExportDb
  $outDir = Join-Path $ArtifactsDir "out\$($g.Name)"
  $floor = if ($MinPairs -gt 0) { $MinPairs } else { $g.MinPairs }

  # 1. Fold the working graph into the shippable pair table.
  if ($SkipExport) {
    if (-not (Test-Path $edgesDb)) { throw "-SkipExport was given but $edgesDb does not exist." }
    Write-Host "Skipping export; packing $edgesDb as it stands." -ForegroundColor Cyan
  } else {
    if (-not (Test-Path $workDb)) {
      if ($g.IsBuild) {
        throw "No $workDb. Run: dotnet run distribution/fetch-coread-graph.cs -- fetch"
      }
      throw "No $workDb. Run: dotnet run distribution/$($g.Fetcher) -- fetch"
    }

    $fetcher = Join-Path $PSScriptRoot $g.Fetcher
    $exportExit = if ($g.IsBuild) {
      # build-taste-vectors.cs and build-reader-cohorts.cs have no fetch/export split - they are
      # flags-only tools that build directly from the working co-read graph. No `export` verb.
      #
      # The cohort builder reads two more inputs, and both default to a bare `.artifacts/` that is
      # wrong the moment -ArtifactsDir points elsewhere. A missing dump errors loudly; a taste
      # artifact resolved from the wrong folder would silently cluster in the wrong item space.
      $extra = @()
      if ($g.DumpDb) { $extra += "--dump"; $extra += (Join-Path $ArtifactsDir $g.DumpDb) }
      if ($g.TasteDb) { $extra += "--taste"; $extra += (Join-Path $ArtifactsDir $g.TasteDb) }
      Invoke-Native { & dotnet run $fetcher -- $g.WorkingArg $workDb $g.OutArg $edgesDb @extra }
    } else {
      Invoke-Native { & dotnet run $fetcher -- export $g.WorkingArg $workDb --out-db $edgesDb }
    }
    if ($exportExit -ne 0) { throw "Exporting the $($g.Name) graph failed - nothing packed." }
  }

  # 2. Validate and pack.
  if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
  New-Item -ItemType Directory -Path $outDir -Force | Out-Null

  $packExit = Invoke-Native { & dotnet run $packTool -- $g.Name $edgesDb $outDir $floor }
  if ($packExit -ne 0) { throw "Validating/packing the $($g.Name) graph failed - nothing packed." }

  $manifestPath = Join-Path $outDir "manifest.json"
  $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
  $archivePath = Join-Path $outDir $manifest.fileName

  # The client polls the manifest, so it has to carry the URL the asset will have once uploaded.
  $manifest | Add-Member -NotePropertyName url -NotePropertyValue `
    "https://github.com/$repoSlug/releases/download/$($g.Tag)/$($manifest.fileName)" -Force

  # Not Set-Content -Encoding utf8: Windows PowerShell writes a BOM, and .NET's Utf8JsonReader
  # treats a leading BOM as an invalid start of a value - the client would fail to parse this.
  [System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 5),
    (New-Object System.Text.UTF8Encoding $false))

  Write-Host ""
  Write-Host "  artifact : $archivePath ($([math]::Round((Get-Item $archivePath).Length / 1MB, 1)) MB)"
  # Each kind counts a different thing, and printing a graph's field names against a taste or
  # cohort manifest rendered as "pairs : over series" - an empty line that reads like a broken pack
  # rather than like the wrong field.
  $counts = if ($manifest.PSObject.Properties.Name -contains "pairCount") {
    "$($manifest.pairCount) pairs over $($manifest.seriesCount) series"
  } elseif ($manifest.PSObject.Properties.Name -contains "cohortItemCount") {
    "$($manifest.cohortItemCount) cohort rows over $($manifest.itemCount) series, $($manifest.cohortCount) cohorts"
  } else {
    "$($manifest.itemCount) vectors at $($manifest.dimensions) dims"
  }
  Write-Host "  counts   : $counts"
  Write-Host "  sha256   : $($manifest.sha256)"
  Write-Host ""

  $built += [pscustomobject]@{
    Profile = $g; ArchivePath = $archivePath; ManifestPath = $manifestPath; FileName = $manifest.fileName
  }
}

if (-not $Publish) {
  Write-Host "Dry run - nothing uploaded. Re-run with -Publish to upload." -ForegroundColor Cyan
  exit 0
}

Write-Host "About to upload to ${repoSlug}:" -ForegroundColor Yellow
foreach ($b in $built) {
  Write-Host "  $($b.Profile.Name) -> tag '$($b.Profile.Tag)': $($b.FileName) + manifest.json"
}
Write-Host "This is public and replaces whatever is on those tags now." -ForegroundColor Yellow
if (-not (Confirm-Step "Upload?")) {
  Write-Host "Stopped. Artifacts left in $ArtifactsDir."
  exit 1
}

foreach ($b in $built) {
  # A missing release is expected for the first publish of a tag.
  $viewExit = Invoke-Native { & gh release view $b.Profile.Tag --json tagName | Out-Null }
  if ($viewExit -ne 0) {
    Write-Host "Creating release '$($b.Profile.Tag)'…"
    & gh release create $b.Profile.Tag --title $b.Profile.Label --notes $b.Profile.Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed for $($b.Profile.Tag)" }
  }

  & gh release upload $b.Profile.Tag $b.ArchivePath $b.ManifestPath --clobber
  if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $($b.Profile.Tag)" }
  Write-Host "Uploaded $($b.Profile.Name): https://github.com/$repoSlug/releases/download/$($b.Profile.Tag)/manifest.json" -ForegroundColor Green
}
