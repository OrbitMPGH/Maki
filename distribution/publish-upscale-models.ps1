<#
.SYNOPSIS
  Uploads the server upscale models to the upscale-models-v1 GitHub release.

.DESCRIPTION
  Run distribution/upscale-models/prepare.py first. It writes the two .onnx files, manifest.json
  and LICENSES.txt into one folder; this script uploads that folder's *.onnx, manifest.json and
  LICENSES.txt to the release, creating the release when it does not exist yet. Existing assets
  with the same name are replaced.

  Maki downloads the models from this release on first use, so what is uploaded here is what every
  install gets. The SPAN-S model is licensed CC BY-NC 4.0; see LICENSES.txt.

.PARAMETER Folder
  The folder prepare.py wrote.

.PARAMETER Repo
  Defaults to OrbitMPGH/Maki.

.PARAMETER Tag
  Defaults to upscale-models-v1.

.EXAMPLE
  ./distribution/publish-upscale-models.ps1 -Folder specs\page-enhance\models-out
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [string]$Repo = 'OrbitMPGH/Maki',
    [string]$Tag = 'upscale-models-v1'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'The GitHub CLI (gh) is not installed or not on PATH.'
}

$folderPath = (Resolve-Path $Folder).Path
$assets = @(Get-ChildItem -Path $folderPath -File | Where-Object {
        $_.Extension -eq '.onnx' -or $_.Name -in @('manifest.json', 'LICENSES.txt')
    })

foreach ($required in @('manifest.json', 'LICENSES.txt')) {
    if (-not ($assets | Where-Object { $_.Name -eq $required })) {
        throw "$required is missing from $folderPath. Run prepare.py first."
    }
}
if (-not ($assets | Where-Object { $_.Extension -eq '.onnx' })) {
    throw "No .onnx files in $folderPath. Run prepare.py first."
}

$manifest = Get-Content (Join-Path $folderPath 'manifest.json') -Raw | ConvertFrom-Json
foreach ($model in $manifest.models) {
    $file = Join-Path $folderPath $model.file
    if (-not (Test-Path $file)) { throw "manifest.json lists $($model.file) but it is not in the folder." }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $model.sha256) { throw "$($model.file) does not match the sha256 in manifest.json." }
}

# gh reports a missing release on stderr. Under 'Stop' PowerShell 5.1 turns that into a
# terminating error before the exit code is read, so the check runs with errors continuing.
$ErrorActionPreference = 'Continue'
$null = & gh release view $Tag --repo $Repo 2>&1
$releaseExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if (-not $releaseExists) {
    Write-Host "Creating release $Tag on $Repo"
    gh release create $Tag --repo $Repo --title 'Upscale models v1' `
        --notes 'Models for the optional server-side Page Enhance. Downloaded by Maki on first use. See LICENSES.txt: the SPAN-S model is CC BY-NC 4.0.' `
        --latest=false
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
}

foreach ($asset in $assets) {
    Write-Host "Uploading $($asset.Name) ($([math]::Round($asset.Length / 1MB, 1)) MB)"
    gh release upload $Tag $asset.FullName --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "Upload of $($asset.Name) failed." }
}

Write-Host "Done: https://github.com/$Repo/releases/tag/$Tag"
