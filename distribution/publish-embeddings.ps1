<#
.SYNOPSIS
  Builds the base embedding index from scratch and (optionally) uploads it to its GitHub release
  tag, so users can download a prebuilt index instead of spending ~an hour of CPU rebuilding it.

.DESCRIPTION
  The embedding index is derived entirely from the public MangaBaka dump plus the pinned model,
  so it holds nothing user-specific - only MangaBaka ids, content hashes, vectors and tag blobs.
  That is what makes it publishable at all; check embeddings-artifact.cs if you want to see
  exactly which tables ship.

  Everything happens inside a git-ignored .artifacts folder, and every step is incremental:

    1. build-embeddings.cs downloads the *full* MangaBaka dump once, downloads the model, and
       runs the embedding pass into .artifacts\embeddings-base.db. The dump (~4.6 GB), the
       model, and the index all persist between runs, so the first run is slow and every run
       after it only refreshes what changed - a fast "top up and republish".
    2. embeddings-artifact.cs validates the index (integrity, row counts, uniform vector width
       matching the model's dimensions) and compresses it to .zst with a manifest.json.
    3. Only with -Publish, and only after a y/N gate: create/reuse the release tag and upload
       the artifact + manifest.

  Without -Publish the script stops after step 2 and tells you what it would have uploaded.
  Nothing leaves the machine.

  This does not touch your running Maki install: it builds into .artifacts, not your config dir.

.PARAMETER ArtifactsDir
  Where the dump, models, per-model indexes, and packed artifacts live. Defaults to .artifacts in
  the repo root (git-ignored). Keep it around between runs - that is what makes reruns quick.

.PARAMETER MinRows
  Refuse to publish fewer than this many vectors. Guards against shipping a partial index.

.PARAMETER Publish
  Actually upload. Without it the script is a dry run.

.PARAMETER Cuda
  Run the embedding pass on an NVIDIA GPU instead of the CPU. This only changes how long the build
  takes; the artifact it produces is the same shape and is still consumed by CPU-only clients.

  Needs, on this machine: a CUDA 13.x runtime and cuDNN 9.x on PATH. Read that off the package
  rather than guessing at a version - onnxruntime_providers_cuda.dll in
  Microsoft.ML.OnnxRuntime.Gpu.Windows imports cublas64_13, cublasLt64_13 and cudnn64_9, so CUDA
  12.x will not load it whatever the docs elsewhere say. Re-check those imports whenever the ONNX
  Runtime version is bumped; the required CUDA major has moved before.

  Two things happen when it is set. Maki.Metadata is built against Microsoft.ML.OnnxRuntime.Gpu
  rather than the CPU package (a much larger restore, one machine only, never for a release), and
  the pass switches to the fp32 export instead of the int8 one.

  That second part is what makes it worth doing. Measured on an RTX 5080 over 2,000 real passages,
  extrapolated to the 96k catalogue: CPU int8 39 rows/s (41 min), CUDA int8 31 rows/s (51 min),
  CUDA fp32 281 rows/s (5 min 41 s). ONNX Runtime cannot keep a quantized graph on the device, so
  the GPU is *slower* than the CPU unless the precision moves with it.

  fp32 vectors are not identical to the int8 ones users would produce; see
  docs/prebuilt-embeddings.md before publishing an artifact built this way.

  You do not need to set anything up by hand. -Cuda finds the CUDA and cuDNN DLLs itself and puts
  them at the front of this process's PATH, so no environment variable outlives the run and a stray
  copy of cublas64_13.dll shipped by some other tool cannot shadow the toolkit's. A -Cuda run that
  still cannot get the GPU aborts rather than finishing slowly on the CPU.

.PARAMETER CheckGpu
  With -Cuda, resolve and print the GPU libraries and exit without building anything. Use it to
  confirm a CUDA install in seconds rather than by watching how fast a pass runs.

.PARAMETER Precision
  Override what -Cuda picks, which is "fp32". The only other value is "int8", which is what CPU
  clients run: pair it with -Cuda to find out whether the GPU accelerates the quantized graph after
  all. There is no fp16 option; ONNX Runtime 1.27 cannot load the fp16 export at all, failing in
  graph optimization before a provider is even chosen.

.EXAMPLE
  ./distribution/publish-embeddings.ps1
  Build (or refresh) the base index and print what would be uploaded. Uploads nothing.

.EXAMPLE
  ./distribution/publish-embeddings.ps1 -Publish
  Refresh the base index and upload it to embeddings-base-latest after confirmation.

.EXAMPLE
  ./distribution/publish-embeddings.ps1 -Cuda
  Same dry run, with the embedding pass on the GPU.
#>
[CmdletBinding()]
param(
  [string]$ArtifactsDir = "",
  [int]$MinRows = 50000,
  [switch]$Publish,
  [switch]$Cuda,
  [switch]$CheckGpu,
  [ValidateSet("", "int8", "fp32")]
  [string]$Precision = ""
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

# Parse the base model's contract out of the source tree rather than trusting flags: the artifact
# must describe the model this build actually produces, and land on the tag the client polls for it.
function Get-ModelProfile {
  $profilePath = Join-Path $repoRoot "src\Maki.Metadata\Embedding\EmbeddingModelProfile.cs"
  $profileText = Get-Content $profilePath -Raw
  # Grab just the `Base = new(...)` block.
  if ($profileText -notmatch "(?s)EmbeddingModelProfile\s+Base\s*=\s*new\((.*?)\);") {
    throw "Could not find the 'Base' model profile in $profilePath"
  }
  $block = $Matches[1]
  if ($block -notmatch 'Dimensions:\s*(\d+)') { throw "Could not read Dimensions for 'Base'" }
  $dims = [int]$Matches[1]
  if ($block -notmatch 'Version:\s*"([^"]+)"') { throw "Could not read Version for 'Base'" }
  $version = $Matches[1]
  if ($block -notmatch 'PrebuiltTag:\s*"([^"]+)"') { throw "Could not read PrebuiltTag for 'Base'" }
  $tag = $Matches[1]
  return [pscustomobject]@{ Model = "base"; Dimensions = $dims; Version = $version; Tag = $tag }
}

. (Join-Path $PSScriptRoot 'gpu-libraries.ps1')

# Puts Maki.Metadata back on the CPU package after a -Cuda build. The GPU restore is sticky: once
# project.assets.json names Microsoft.ML.OnnxRuntime.Gpu, a later plain `dotnet build` no-ops the
# restore and keeps it, so an ordinary dev build would silently drag in ~250 MB of CUDA natives and
# a release built from that tree would ship the wrong package. --force is what defeats the no-op.
# Failure here is a warning, not an error: the index is already built by this point.
function Restore-CpuOnnxRuntime {
  $csproj = Join-Path $repoRoot "src\Maki.Metadata\Maki.Metadata.csproj"
  $exit = Invoke-Native { & dotnet restore $csproj --force --nologo -v q }
  if ($exit -ne 0) {
    Write-Warning "Could not restore Maki.Metadata back to the CPU ONNX Runtime. Run: dotnet restore `"$csproj`" --force"
  }
}

# Windows PowerShell turns a native command's stderr into ErrorRecords, which $ErrorActionPreference
# = "Stop" would treat as a failure - and dotnet reports progress on stderr. Run with it relaxed and
# judge by the exit code instead.
#
# Out-Host, not a bare invocation: without it the command's stdout becomes this function's output
# and gets collected into the caller's variable alongside the exit code, so a long pass prints
# nothing until it finishes and then dumps every line joined by spaces. Piping to the host consumes
# the stream as each line arrives, which is what makes the embedding progress visible live, and
# leaves the exit code as the only thing returned.
function Invoke-Native {
  param([scriptblock]$Command)
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try { & $Command | Out-Host; return $LASTEXITCODE }
  finally { $ErrorActionPreference = $prev }
}

Require-Command -Name "dotnet" -Hint "Install the .NET SDK (the build/pack tools are file-based C# apps)."
if ($Publish) {
  Require-Command -Name "gh" -Hint "Install the GitHub CLI: https://cli.github.com"
}

New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

# MAKI_EMBED_* are read by EmbeddingRuntime at run time and belong in the environment. The build-time
# half (MakiOnnxGpu) does not: it goes to the build call as -p:, for the reason documented there.
# Everything here is set on this process only, so nothing outside this script sees it.
if ($Cuda) {
  Write-Host "GPU libraries:"
  Initialize-GpuLibraryPath
  $env:MAKI_EMBED_PROVIDER = "cuda"
  if (-not $Precision) { $Precision = "fp32" }
} else {
  # Explicitly cleared rather than left alone: a value inherited from the parent shell would
  # otherwise silently decide what gets published.
  $env:MAKI_EMBED_PROVIDER = $null
}
# Never carried as an environment variable, whatever the parent shell holds: see the -p: comment at
# the build call for why that form does not survive NuGet's restore no-op check.
$env:MakiOnnxGpu = $null
if ($Precision) { $env:MAKI_EMBED_PRECISION = $Precision } else { $env:MAKI_EMBED_PRECISION = $null }

$runtime = if ($Cuda) { "CUDA, $Precision" } else { "CPU, $(if ($Precision) { $Precision } else { 'int8' })" }

# -CheckGpu stops here. Initialize-GpuLibraryPath has already thrown if anything is missing, so
# reaching this point is the whole answer, and it costs seconds instead of a full pass.
if ($CheckGpu) {
  if (-not $Cuda) { throw "-CheckGpu only means anything with -Cuda." }
  Write-Host ""
  Write-Host "GPU libraries resolved. Re-run without -CheckGpu to build." -ForegroundColor Green
  exit 0
}

Write-Host "artifacts dir : $ArtifactsDir"
Write-Host "runtime       : $runtime"
Write-Host ""

if ($Cuda) {
  Write-Warning "Building against Microsoft.ML.OnnxRuntime.Gpu. This is a large restore and needs CUDA 13.x + cuDNN 9.x present. If it is not, the pass finishes on the CPU and only logs a warning."
}

$buildTool = Join-Path $PSScriptRoot "build-embeddings.cs"
$packTool = Join-Path $PSScriptRoot "embeddings-artifact.cs"
$repoSlug = if ($Publish) { (& gh repo view --json nameWithOwner --jq .nameWithOwner) } else { "<owner>/<repo>" }

$m = "base"
$info = Get-ModelProfile
Write-Host "===== $m ($($info.Version), $($info.Dimensions) dims -> tag '$($info.Tag)') =====" -ForegroundColor Cyan

# 1. Build (or incrementally refresh) the index into .artifacts.
# MakiOnnxGpu must be a real MSBuild property, not an environment variable. NuGet's restore no-op
# check does not consider environment-derived properties, so setting it that way leaves a
# previously-restored project.assets.json in place: the CPU package stays referenced, the CPU
# onnxruntime.dll gets copied, and the pass dies with EntryPointNotFoundException on
# OrtSessionOptionsAppendExecutionProvider_CUDA. Passed with -p: it is a global property, which
# does invalidate the restore and does flow to the ProjectReference.
$buildArgs = @($buildTool)
if ($Cuda) { $buildArgs += "-p:MakiOnnxGpu=true" }
$buildArgs += @("--", $m, $ArtifactsDir)
$buildExit = Invoke-Native { & dotnet run @buildArgs }
if ($Cuda) { Restore-CpuOnnxRuntime }
if ($buildExit -ne 0) { throw "Building the $m index failed - nothing packed." }

# 2. Validate and pack it into a compressed artifact + manifest under .artifacts\out\base.
$indexDb = Join-Path $ArtifactsDir "embeddings-$m.db"
$outDir = Join-Path $ArtifactsDir "out\$m"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$packExit = Invoke-Native { & dotnet run $packTool -- $indexDb $outDir $info.Dimensions $MinRows }
if ($packExit -ne 0) { throw "Validating/packing the $m index failed - nothing packed." }

$manifestPath = Join-Path $outDir "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$archivePath = Join-Path $outDir $manifest.fileName

# The client polls the manifest, so it carries the model contract and the URL the assets will
# have once uploaded (a moving tag keeps that URL stable across runs).
$manifest | Add-Member -NotePropertyName modelVersion -NotePropertyValue $info.Version -Force
$manifest | Add-Member -NotePropertyName url -NotePropertyValue `
  "https://github.com/$repoSlug/releases/download/$($info.Tag)/$($manifest.fileName)" -Force

# Which weights produced these vectors. Not to be confused with the "quantized" field above, which
# is about storage: every artifact stores int8 regardless, and modelPrecision says what the floats
# were before that packing. Purely a record - the client has no such property and nothing reads it,
# and it is deliberately not part of modelVersion so it never gates an install (see
# EmbeddingRuntime). It exists because nothing else can tell an fp32-built artifact from an
# int8-built one after the fact: same version, same dimensions, same row hashes, same byte count.
$manifest | Add-Member -NotePropertyName modelPrecision -NotePropertyValue `
  $(if ($Precision) { $Precision } else { "int8" }) -Force

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

$built = @([pscustomobject]@{
  Model = $m; Tag = $info.Tag; ArchivePath = $archivePath; ManifestPath = $manifestPath; FileName = $manifest.fileName
})

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
