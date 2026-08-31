<#
.SYNOPSIS
  Runs the whole recommendation measurement suite and writes one comparable log.

.DESCRIPTION
  The v3 baseline, and the thing every later change is read against. A single knob swept by hand
  against one label set is how a channel gets tuned into looking good on the population that
  produced its own training data; this runs every mode against every label set in one go so that
  cannot happen quietly.

  Modes:
    single    one seed, graded on that seed's neighbours in the label graph. The "More like this"
              rail's shape.
    small     three seeds. Where the centroid starts competing with the seed queries, so anything
              touching attribution moves here and not in `single`.
    library   real reading lists with a slice held out. n is a population rather than one install,
              and the only mode that measures the whole-library Recommendations tab.

  Label sets, and why there are four:
    reco      AniList + MAL submitted pairs.       Vote channel forced off.
    coread    AniList reading-list co-occurrence.  Co-read channel forced off.
    mu        MangaUpdates' own category derivation. INDEPENDENT of both, 96.5% novel pairs, but
              partly tag-derived - not the primary grader for a tag change.
    mu-human  MangaUpdates human submissions.      Independent and clean, only 7,036 series wide.

  The first two share a population with everything the recommender learns from. The MangaUpdates
  pair does not, which is the only reason a behaviourally-trained channel can be graded honestly.

.PARAMETER Variants
  Which variants to score. Defaults to the shipped configuration against the no-crowd baseline.
  Later phases pass their own, e.g. -Variants default,"anc:tagancestordecay=0.5".

.PARAMETER Config
  MAKI_CONFIG_DIR for the run. Defaults to .simulated, which holds a copy of a production config
  directory. THE GRAPH ARTIFACTS HAVE TO BE IN IT: their absence is a silent no-op that reads as
  "the channel did nothing" rather than as a missing file.

.PARAMETER Quick
  Smaller request counts for a shape check. Not a result.

.EXAMPLE
  ./distribution/run-reco-suite.ps1
  The full v3 baseline, roughly an hour.

.EXAMPLE
  ./distribution/run-reco-suite.ps1 -Variants default,v4 -Log .artifacts/eval/phase1.log
#>
[CmdletBinding()]
param(
  [string[]]$Variants = @("nocrowd", "default"),
  [string]$Config = "",
  [int]$Requests = 500,
  [int]$Libraries = 400,
  [switch]$Quick,
  [switch]$NoFeel,
  [string]$LibraryFold = "",
  [string]$Log = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

# powershell.exe -File does not split a comma-separated argument into an array (only -Command
# does), so "-Variants a,b" arrives as one string. Split here so both invocation styles work.
$Variants = @($Variants | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if (-not $Config) { $Config = Join-Path $repoRoot ".simulated" }
$env:MAKI_CONFIG_DIR = $Config

$resultsDir = Join-Path $repoRoot ".artifacts\eval"
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
if (-not $Log) { $Log = Join-Path $resultsDir "suite-$(Get-Date -Format 'yyyyMMdd-HHmmss').log" }

if ($Quick) { $Requests = 40; $Libraries = 30 }

foreach ($f in @("mangabaka.db", "embeddings.db", "reco-edges.db", "coread-edges.db")) {
  if (-not (Test-Path (Join-Path $Config $f))) {
    Write-Warning "$f is missing from $Config. A missing graph reads as 'the channel did nothing'."
  }
}
if (-not (Test-Path (Join-Path $repoRoot ".artifacts\mu-edges.db"))) {
  throw "No .artifacts/mu-edges.db. Build it: dotnet run distribution/build-mu-graph.cs"
}

# A file-based app caches its build under %TEMP%\dotnet\runfile, so a suite run started right after
# editing a default in src/ would score the PREVIOUS build, silently and for every row in the table.
$runfileCache = Join-Path $env:TEMP "dotnet\runfile"
if (Test-Path $runfileCache) { Remove-Item -Recurse -Force $runfileCache }

Write-Host ""
Write-Host "config   : $Config"
Write-Host "variants : $($Variants -join ', ')"
Write-Host "requests : $Requests pair-mode, $Libraries libraries"
Write-Host "log      : $Log"
Write-Host ""

$tool = Join-Path $PSScriptRoot "eval-reco-labels.cs"
$started = Get-Date

function Invoke-Suite {
  param([string]$Title, [string[]]$EvalArgs)
  Write-Host ""
  Write-Host "===== $Title" -ForegroundColor Cyan
  "`n===== $Title" | Add-Content -Path $Log

  $dotnetArgs = @("run", $tool, "--") + $EvalArgs + $Variants
  # Windows PowerShell turns a native command's stderr into ErrorRecords, which "Stop" would treat
  # as failure, and dotnet reports progress on stderr. Relax it and judge by the exit code.
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    & dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $Log -Append
    if ($LASTEXITCODE -ne 0) { Write-Warning "$Title exited $LASTEXITCODE" }
  } finally { $ErrorActionPreference = $prev }
}

$feel = if ($NoFeel) { @() } else { @("--feel") }

foreach ($labels in @("reco", "coread", "mu", "mu-human")) {
  Invoke-Suite -Title "single / $labels" -EvalArgs (@(
    "single", "--labels", $labels, "--requests", "$Requests", "--strata") + $feel)
  Invoke-Suite -Title "small / $labels" -EvalArgs (@(
    "small", "--labels", $labels, "--requests", "$Requests", "--per-request", "3") + $feel)
}

# `library` ignores --labels entirely: it holds out a slice of a real reading list and asks for it
# back, and forces the co-read channel off because those lists ARE its training data.
#
# THE BEHAVIOURAL ARTIFACT NEEDS THE SAME TREATMENT AND CANNOT GET IT BY BEING SWITCHED OFF, because
# holding it out is not a flag, it is a different artifact. Pass -LibraryFold k/n together with a
# taste-vectors.db built by `build-taste-vectors.cs --fold-out k/n`, and the eval will refuse the
# run outright if the installed artifact was trained on the fold being graded. Without it, library
# mode grades a model against readers it learned from and the number is meaningless.
$libraryArgs = @("library", "--requests", "$Libraries", "--holdout", "0.2", "--strata") + $feel
if ($LibraryFold) {
  $libraryArgs += @("--fold-users", $LibraryFold)
} elseif (Test-Path (Join-Path $Config "taste-vectors.db")) {
  Write-Warning ("A behavioural artifact is installed but -LibraryFold was not given. " +
    "Library-mode numbers will be contaminated unless that artifact held those readers out.")
}

Invoke-Suite -Title "library / held-out lists" -EvalArgs $libraryArgs

Write-Host ""
Write-Host "total elapsed: $(((Get-Date) - $started).ToString('hh\:mm\:ss'))"
Write-Host "log: $Log" -ForegroundColor Green
Write-Host ""
Write-Host "Per-request metrics are in .artifacts/eval/rr-<variant>-<labels>.csv." -ForegroundColor Yellow
Write-Host "A difference in the tables above is not a result until eval-compare.py gives it an interval:"
Write-Host "  python distribution/eval-compare.py $($Variants[-1]) $($Variants[0]) mu-human ndcg"
