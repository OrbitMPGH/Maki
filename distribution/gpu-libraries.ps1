<#
.SYNOPSIS
  Shared GPU library discovery, dot-sourced by publish-embeddings.ps1 and run-eval.ps1.

  Kept in one file because both scripts need the identical resolution order and the identical
  list of required DLLs. Two copies would drift, and the failure mode of a drifted copy is a
  silent fall back to CPU that only shows up as a run taking six times too long.
#>

# Puts the CUDA and cuDNN DLLs this process needs at the FRONT of its own PATH, and fails loudly if
# either is missing. Prepending is the point, not a convenience: other software ships its own copies
# of these (an ffmpeg build on the PATH ahead of the toolkit was the real case that prompted this),
# and whichever directory comes first wins, so relying on the ambient PATH means the pass may load a
# cuBLAS that has nothing to do with the installed toolkit.
#
# The required names come from what onnxruntime_providers_cuda.dll actually imports. RE-CHECK THEM
# WHENEVER THE ONNX RUNTIME VERSION IS BUMPED - the required CUDA major has moved between releases:
#   grep -aoiE "(cublas64|cudnn64)_[0-9]+" ~/.nuget/packages/microsoft.ml.onnxruntime.gpu.windows/*/runtimes/win-x64/native/onnxruntime_providers_cuda.dll | sort -u
function Initialize-GpuLibraryPath {
  $required = @("cublas64_13.dll", "cublasLt64_13.dll", "cudnn64_9.dll")

  # CUDA_PATH is set by the toolkit installer at machine scope, so a shell opened before the install
  # will not have it; fall back to the newest versioned directory on disk.
  $cudaRoot = $env:CUDA_PATH
  if (-not $cudaRoot) { $cudaRoot = [Environment]::GetEnvironmentVariable('CUDA_PATH', 'Machine') }
  if (-not $cudaRoot) {
    $cudaRoot = Get-ChildItem "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v*" -Directory -ErrorAction SilentlyContinue |
      Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
  }

  # CUDA 13 puts the redistributables in bin\x64; older layouts used bin directly. cuDNN lands in the
  # toolkit dir when installed by NVIDIA's exe, or under site-packages when installed as a wheel.
  $candidates = @()
  if ($cudaRoot) { $candidates += (Join-Path $cudaRoot "bin\x64"), (Join-Path $cudaRoot "bin") }
  $userSite = & python -m site --user-site 2>$null
  if ($userSite) { $candidates += (Join-Path $userSite "nvidia\cudnn\bin") }
  $candidates += (Get-ChildItem "C:\Program Files\Python*\Lib\site-packages\nvidia\cudnn\bin" -Directory -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty FullName)

  $resolved = [ordered]@{}
  foreach ($dll in $required) {
    foreach ($dir in ($candidates | Where-Object { $_ -and (Test-Path $_) })) {
      if (Test-Path (Join-Path $dir $dll)) { $resolved[$dll] = $dir; break }
    }
  }

  $missing = $required | Where-Object { -not $resolved.Contains($_) }
  if ($missing) {
    throw @"
Cannot find $($missing -join ', ') for the CUDA build.
Searched:
$($candidates | Where-Object { $_ } | ForEach-Object { "  $_" } | Out-String)
Install the CUDA 13.x toolkit (winget install --id Nvidia.CUDA) and cuDNN 9.x (pip install
nvidia-cudnn-cu13, or NVIDIA's installer). Re-run without -Cuda to build on the CPU instead.
"@
  }

  # Deduplicate while keeping order: several of the required DLLs usually share one directory.
  $prepend = @($resolved.Values | Select-Object -Unique)
  $env:PATH = ($prepend -join ';') + ';' + $env:PATH
  foreach ($dll in $required) { Write-Host "  $dll -> $($resolved[$dll])" }
}

# Puts Maki.Metadata back on the CPU ONNX Runtime after a -Cuda run.
#
# MUST be called by anything that builds with -p:MakiOnnxGpu=true. That is a GLOBAL MSBuild
# property, so it flows to the ProjectReference and invalidates the restore - which is exactly why
# it is passed that way rather than as an environment variable, and exactly why the effect outlives
# the run. Left alone, the next ordinary `dotnet build` of the API links the GPU package: on this
# machine that silently changes which runtime loads, and on a machine with no CUDA it fails to start
# with an EntryPointNotFoundException that says nothing about a bake-off run days earlier.
function Restore-CpuOnnxRuntime {
  $csproj = Join-Path (Split-Path $PSScriptRoot -Parent) "src\Maki.Metadata\Maki.Metadata.csproj"
  $prev = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try { & dotnet restore $csproj --force --nologo -v q | Out-Host } finally { $ErrorActionPreference = $prev }
  if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not restore Maki.Metadata back to the CPU ONNX Runtime. Run: dotnet restore `"$csproj`" --force"
  }
}
