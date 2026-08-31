<#
.SYNOPSIS
  Scores embedding models against both evals and prints the comparison, streaming progress as it
  goes so a long run can be followed rather than guessed at.

.DESCRIPTION
  Two independent evals, deliberately, because they measure different things and have disagreed:

    held-out  ~59k free labels. The passage is what Maki indexes (title + the MangaUpdates
              description); the query is the MangaBaka description, which that passage has never
              seen - a different person's summary of the same story. Three query shapes are scored:
              `full` (the whole description, which sits at the ceiling and separates nothing),
              `short` (a random 8-16 word span, roughly what somebody types) and `clean` (the same
              span with title words stripped, so a hit cannot be name matching). Judge on `clean`.

    pairs     The twelve hand-written thematic queries the original MRR figures came from. Too few
              to decide anything alone - the interval at n=12 is about +/-0.25 - but it measures
              short thematic search against the whole catalogue, which is the shape Discover
              actually receives. Agreement between the two evals is worth more than either alone.

  Both run against the FULL recommendable catalogue (~95,745), the same set SeriesEmbeddingIndexer
  embeds. An earlier version narrowed the held-out pool to 10k, which is a far easier problem than
  production and is where the two evals parted company.

  Passage vectors are cached under .artifacts/eval, keyed by candidate, pool size and dimension. A
  model that has been scored once needs no GPU and no re-embedding to be scored again with a changed
  metric, so iterating on the analysis is nearly free. Delete .artifacts/eval/vec-*.bin to force one.

.PARAMETER Models
  Which candidates to score. Defaults to the four that matter. See the Candidates table in
  eval-embeddings.cs for the full list.

.PARAMETER Queries
  How many held-out queries. 2000 gives a 95% interval near +/-0.02 on MRR; 500 gives +/-0.04.
  Ignored by the pairs eval, which always has twelve.

.PARAMETER Cuda
  Use the GPU for whatever still needs embedding. Only matters for a candidate with no cached
  vectors; a cached one is pure CPU arithmetic either way.

.PARAMETER Pairs
  Run only the twelve-query eval.

.PARAMETER HeldOut
  Run only the held-out eval.

.PARAMETER Batch
  Texts per forward pass. Leave unset for the provider default (128 on CUDA, 32 on CPU).

  LOWER THIS FOR THE BIG MODELS. Activation memory scales with batch x tokens x width x depth, and a
  1024-dim 24-layer encoder at batch 128 x 512 tokens does not fit in 16 GB next to a desktop's
  browser and chat apps. It does not fail with an out-of-memory error, which is the trap: ONNX
  Runtime thrashes instead, and the pass drops from ~240 rows/s to ~7 while nvidia-smi still reports
  99% GPU utilisation. The tell is that memory-bandwidth utilisation and power draw stay near idle
  (9% and 108 W on a 5080), and `memory.free` is a couple of hundred MiB.

.PARAMETER Log
  Also write everything to this file. Defaults to .artifacts/eval/run-<timestamp>.log.

.EXAMPLE
  ./distribution/run-eval.ps1 -Cuda
  Score the four default candidates on both evals, streaming progress, GPU where needed.

.EXAMPLE
  ./distribution/run-eval.ps1 -Models base,arctic-m -HeldOut -Queries 2000
  Just the two that matter, just the statistical eval.
#>
[CmdletBinding()]
param(
  [string[]]$Models = @("base", "large", "arctic-m", "arctic"),
  [int]$Queries = 2000,
  [switch]$Cuda,
  [switch]$Pairs,
  [switch]$HeldOut,
  [switch]$QuerySet,
  [switch]$Dual,
  [int]$Batch = 0,
  [string]$Log = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

# powershell.exe -File does NOT split a comma-separated argument into an array (only -Command does),
# so "-Models base,gemma" arrives as one string and every model is reported as an unknown candidate.
# Splitting here makes both invocation styles work rather than making the caller remember which.
$Models = @($Models | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$resultsDir = Join-Path $repoRoot ".artifacts\eval"
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
if (-not $Log) {
  $Log = Join-Path $resultsDir "run-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
}

# fp32 always. The cached passage vectors were produced at fp32, and embedding a query at int8
# against fp32 passages puts a precision mismatch inside the very comparison being measured.
$env:MAKI_EMBED_PRECISION = "fp32"
if ($Cuda) {
  . (Join-Path $PSScriptRoot 'gpu-libraries.ps1')
  Write-Host "GPU libraries:" -ForegroundColor Cyan
  Initialize-GpuLibraryPath
  $env:MAKI_EMBED_PROVIDER = "cuda"
} else {
  $env:MAKI_EMBED_PROVIDER = $null
}

if ($Batch -gt 0) { $env:MAKI_EMBED_BATCH = "$Batch" } else { $env:MAKI_EMBED_BATCH = $null }

# Never inherited from the parent shell: as an environment variable it does not invalidate NuGet's
# restore no-op check, so it would silently fail to pull the GPU package. It goes in with -p: below.
$env:MakiOnnxGpu = $null

# Any explicit mode switch selects exactly those modes; with none given, the two default evals run.
$explicit = $Pairs -or $HeldOut -or $QuerySet -or $Dual
$runHeldOut = if ($explicit) { [bool]$HeldOut } else { $true }
$runPairs = if ($explicit) { [bool]$Pairs } else { $true }
$runQuerySet = [bool]$QuerySet
$runDual = [bool]$Dual

# Which per-query file the paired test compares. The hand-written set's `premise` class is the one
# that decides a swap, so it is preferred over the held-out span metric when both were run.
$compareMode = if ($runQuerySet) { "queries-premise" } elseif ($runHeldOut) { "clean" } else { $null }

Write-Host ""
Write-Host "models   : $($Models -join ', ')"
Write-Host "evals    : $(@(
  if ($runHeldOut) { "held-out ($Queries queries)" }
  if ($runPairs) { 'pairs (12 queries)' }
  if ($runQuerySet) { 'hand-written query set' }
  if ($runDual) { "title+description sweep ($Queries queries)" }) -join ' + ')"
Write-Host "runtime  : $(if ($Cuda) {'CUDA'} else {'CPU'}), fp32, batch $(if ($Batch -gt 0) {$Batch} else {'default'})"
Write-Host "log      : $Log"
Write-Host ""

$tool = Join-Path $PSScriptRoot "eval-embeddings.cs"
$started = Get-Date

function Invoke-Eval {
  param([string]$Model, [string[]]$EvalArgs, [string]$Title)
  Write-Host ""
  Write-Host "===== $Title" -ForegroundColor Cyan
  $dotnetArgs = @("run", $tool)
  if ($Cuda) { $dotnetArgs += "-p:MakiOnnxGpu=true" }
  $dotnetArgs += @("--", $Model) + $EvalArgs

  # Windows PowerShell turns a native command's stderr into ErrorRecords, which "Stop" would treat
  # as failure, and dotnet reports progress on stderr. Relax it and judge by the exit code.
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    # Tee-Object streams to the host AND the log; without it a redirect swallows the progress line
    # that makes a long run followable in the first place.
    & dotnet @dotnetArgs 2>&1 | Tee-Object -FilePath $Log -Append
    return $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $prev
    # -p:MakiOnnxGpu=true is a global property and outlives this process by leaving Maki.Metadata
    # restored against the GPU package. publish-embeddings.ps1 has always undone that; this script
    # never did, so a -Cuda bake-off quietly changed which ONNX Runtime every later build linked.
    if ($Cuda) { Restore-CpuOnnxRuntime }
  }
}

foreach ($m in $Models) {
  if ($runHeldOut) {
    Invoke-Eval -Model $m -EvalArgs @("200000", "$Queries") -Title "$m : held-out"
  }
  if ($runPairs) {
    Invoke-Eval -Model $m -EvalArgs @("pairs") -Title "$m : pairs"
  }
  if ($runQuerySet) {
    Invoke-Eval -Model $m -EvalArgs @("queries") -Title "$m : hand-written queries"
  }
  if ($runDual) {
    Invoke-Eval -Model $m -EvalArgs @("dual", "200000", "$Queries") -Title "$m : title+description"
  }
}

Write-Host ""
Write-Host "===== paired comparisons ($compareMode)" -ForegroundColor Cyan
# Unpaired intervals are the conservative view: every candidate answers the identical queries over
# the identical pool, so most of the spread is query difficulty and cancels when the same query is
# compared across two models. This is the test that actually decides a swap.
if ($compareMode -and $Models.Count -gt 1) {
  $python = (Get-Command python -ErrorAction SilentlyContinue)
  if (-not $python) {
    Write-Warning "python not on PATH; skipping the paired tests. Run distribution/eval-compare.py yourself."
  } else {
    $baseline = if ($Models -contains "base") { "base" } else { $Models[0] }
    foreach ($m in $Models | Where-Object { $_ -ne $baseline }) {
      & python (Join-Path $PSScriptRoot "eval-compare.py") $m $baseline $compareMode 2>&1 | Tee-Object -FilePath $Log -Append
      Write-Host ""
    }
  }
}

Write-Host "total elapsed: $(((Get-Date) - $started).ToString('hh\:mm\:ss'))"
Write-Host "log: $Log" -ForegroundColor Green
