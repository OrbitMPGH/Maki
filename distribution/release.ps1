<#
.SYNOPSIS
  Interactive release flow: run the tests and frontend build, merge dev -> main (real merge, no
  squash), tag, generate release notes from git-cliff, and publish a GitHub release.

.DESCRIPTION
  Walks through a release step by step, pausing for confirmation before anything that touches
  origin or GitHub:
    1. Run the full test suite (dotnet test on Maki.sln, Release) and the frontend build
       (npm run build, which is tsc -b then vite build - the frontend has no test runner, so the
       build is the type-check gate). Either one failing aborts the release before a tag is
       picked or anything is merged.
    2. Ask which tag this release is (shows the previous tag on main for reference).
    3. Check dev is clean, in sync with origin/dev, and show what's about to ship.
    4. Merge dev into main with a real merge commit (git-cliff needs the granular history -
       a squash merge collapses everything from dev into one commit and breaks range diffs).
    5. Show the merged log, confirm, then push main and create the tag.
    6. Run git-cliff <previous tag>..<new tag> and save it to a temp release-notes file.
    7. Show the notes, confirm, then `gh release create` with that file.
    8. Delete the temp notes file.
    9. Rebase dev onto main so dev carries the merge commit too (a no-op fast-forward here since
       every dev commit is already an ancestor of main's merge commit - no real rewrite happens).

  Every destructive step (merge, push, tag, release) has its own y/N gate. Answering "N" at any
  gate stops the script without pushing/tagging/releasing further than what already happened.

  Step 1 runs against the current checkout (dev, normally), not the merge result. Since
  the merge is a fast-forward-shaped real merge of dev into main, that is the same tree - but if
  main has commits dev doesn't, re-run after merging to be sure.

.EXAMPLE
  ./distribution/release.ps1
#>
[CmdletBinding()]
param()

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

Require-Command "git" "Install Git."
Require-Command "dotnet" "Install the .NET SDK: https://dotnet.microsoft.com/download"
Require-Command "npm" "Install Node.js: https://nodejs.org"
Require-Command "git-cliff" "Install with: cargo install git-cliff"
Require-Command "gh" "Install the GitHub CLI: https://cli.github.com"

if (git status --porcelain) {
  throw "Working tree has uncommitted changes. Commit or stash first."
}

Write-Host "`n== Step 1: tests and frontend build ==" -ForegroundColor Cyan

Write-Host "dotnet test Maki.sln -c Release (this builds first, give it a minute)"
dotnet test "$repoRoot\Maki.sln" -c Release --nologo
if ($LASTEXITCODE -ne 0) {
  throw "Tests failed (dotnet test exited $LASTEXITCODE). Fix them before releasing - nothing was merged, tagged or pushed."
}
Write-Host "All tests passed." -ForegroundColor Green

# The frontend has no test runner - `npm run build` is `tsc -b && vite build`, so it doubles as
# the type-check gate. `tsc --noEmit` would check nothing here: the root tsconfig is
# "files": [] plus project references.
$frontendDir = Join-Path $repoRoot "frontend"
Push-Location $frontendDir
try {
  if (-not (Test-Path (Join-Path $frontendDir "node_modules"))) {
    Write-Host "node_modules missing - running npm ci"
    npm ci
    if ($LASTEXITCODE -ne 0) {
      throw "npm ci failed (exit $LASTEXITCODE). Nothing was merged, tagged or pushed."
    }
  }
  Write-Host "npm run build (tsc -b + vite build)"
  npm run build
  if ($LASTEXITCODE -ne 0) {
    throw "Frontend build failed (npm run build exited $LASTEXITCODE). Fix it before releasing - nothing was merged, tagged or pushed."
  }
} finally {
  Pop-Location
}
Write-Host "Frontend build passed." -ForegroundColor Green

Write-Host "`n== Step 2: which release ==" -ForegroundColor Cyan
git fetch origin --quiet --tags
git fetch origin main dev --quiet

$prevTag = git describe --tags --abbrev=0 --tags origin/main 2>$null
if ($LASTEXITCODE -ne 0 -or -not $prevTag) {
  $prevTag = $null
  Write-Host "No previous tag found on main - this looks like the first release." -ForegroundColor Yellow
} else {
  Write-Host "Previous tag on main: $prevTag" -ForegroundColor Green
}

$newTag = Read-Host "New release tag (e.g. v0.13.0)"
if ($newTag -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
  throw "Tag must look like vX.Y.Z or vX.Y.Z-<prerelease> (got '$newTag')"
}
if (git rev-parse -q --verify "refs/tags/$newTag" 2>$null) {
  throw "Tag '$newTag' already exists."
}

Write-Host "`n== Step 3: checking dev ==" -ForegroundColor Cyan
git checkout dev --quiet
if (git status --porcelain) {
  throw "dev has uncommitted changes. Commit or stash first."
}

$localDev = (git rev-parse dev).Trim()
$remoteDev = (git rev-parse origin/dev).Trim()
if ($localDev -ne $remoteDev) {
  throw "Local dev ($($localDev.Substring(0,7))) doesn't match origin/dev ($($remoteDev.Substring(0,7))). Pull or push dev first."
}

$pending = git log main..dev --oneline
if (-not $pending) {
  Write-Host "dev has nothing new relative to main." -ForegroundColor Yellow
  if (-not (Confirm-Step "Continue anyway?")) { Write-Host "Aborted."; exit 1 }
} else {
  Write-Host "Commits about to ship (main..dev):" -ForegroundColor Green
  Write-Host $pending
}

if (-not (Confirm-Step "Proceed with merging dev into main?")) { Write-Host "Aborted."; exit 1 }

Write-Host "`n== Step 4: merging dev -> main ==" -ForegroundColor Cyan
git checkout main --quiet
git pull --ff-only origin main
git merge dev --no-ff -m "Merge dev into main: $newTag"
if ($LASTEXITCODE -ne 0) {
  git merge --abort
  throw "Merge conflict. Resolve manually (git checkout main; git merge dev), commit, then re-run this script."
}

Write-Host "`n== Step 5: check before tagging ==" -ForegroundColor Cyan
Write-Host (git log --oneline -8 | Out-String)
if (-not (Confirm-Step "Push main and create tag $newTag on this commit?")) {
  Write-Host "Merge is committed locally on main but not pushed. Push/tag manually when ready, or re-run this script." -ForegroundColor Yellow
  exit 1
}

git push origin main
git tag -a $newTag -m $newTag
git push origin $newTag
Write-Host "Tagged and pushed $newTag." -ForegroundColor Green

Write-Host "`n== Step 6: generating release notes ==" -ForegroundColor Cyan
$notesFile = Join-Path $env:TEMP "maki-release-notes-$newTag.md"
if ($prevTag) {
  git-cliff "$prevTag..$newTag" --tag $newTag --strip header -o $notesFile
} else {
  git-cliff --tag $newTag --strip header -o $notesFile
}
Write-Host "`n--- $notesFile ---" -ForegroundColor Green
Get-Content $notesFile | Write-Host
Write-Host "---`n"

Write-Host "`n== Step 7: publish GitHub release ==" -ForegroundColor Cyan
if (-not (Confirm-Step "Create GitHub release $newTag with the notes above?")) {
  Write-Host "Tag $newTag is pushed but no release was created. Notes kept at: $notesFile" -ForegroundColor Yellow
  Write-Host "Create it later with: gh release create $newTag --notes-file `"$notesFile`"" -ForegroundColor Yellow
  exit 1
}

gh release create $newTag --title $newTag --notes-file $notesFile
if ($LASTEXITCODE -ne 0) {
  throw "gh release create failed. Notes kept at: $notesFile"
}

Write-Host "`n== Step 8: cleanup ==" -ForegroundColor Cyan
Remove-Item $notesFile -Force
Write-Host "Removed $notesFile" -ForegroundColor Green

Write-Host "`n== Step 9: sync dev onto main ==" -ForegroundColor Cyan
git checkout dev --quiet
git fetch origin --quiet
git rebase main
if ($LASTEXITCODE -ne 0) {
  git rebase --abort
  Write-Host "Rebasing dev onto main failed - resolve manually:" -ForegroundColor Yellow
  Write-Host "  git checkout dev; git rebase main" -ForegroundColor Yellow
  Write-Host "then: git push origin dev --force-with-lease" -ForegroundColor Yellow
  exit 1
}
git push origin dev --force-with-lease
Write-Host "dev rebased onto main and pushed." -ForegroundColor Green

Write-Host "`nRelease $newTag done." -ForegroundColor Cyan
