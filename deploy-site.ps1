<#
.SYNOPSIS
  Puts the website (site/index.html plus the logo and screenshots from docs/) on the gh-pages branch.

.DESCRIPTION
  GitHub Pages serves the gh-pages branch, so pushes to main build nothing. Only pushing gh-pages
  starts a Pages build, which is why this script commits but does not push unless you pass -Push.

.EXAMPLE
  ./deploy-site.ps1          # update the branch locally
  ./deploy-site.ps1 -Push    # and publish it (one Pages build)
#>
param([switch]$Push)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$wt = Join-Path $root "artifacts/gh-pages"

if (-not (Test-Path (Join-Path $wt ".git"))) {
    git -C $root fetch -q origin
    $remote = git -C $root ls-remote --heads origin gh-pages
    git -C $root show-ref --verify --quiet refs/heads/gh-pages
    $local = $LASTEXITCODE -eq 0
    if ($local) { git -C $root worktree add $wt gh-pages }
    elseif ($remote) { git -C $root worktree add -B gh-pages $wt origin/gh-pages }
    else { git -C $root worktree add --orphan -b gh-pages $wt }
    if ($LASTEXITCODE -ne 0) { throw "Could not create the gh-pages worktree" }
}

# The branch holds only the site.
Get-ChildItem $wt -Force | Where-Object Name -ne ".git" | Remove-Item -Recurse -Force
Copy-Item (Join-Path $root "site/index.html") $wt
Copy-Item (Join-Path $root "docs/logo.png"), (Join-Path $root "docs/banner.png") $wt
New-Item -ItemType Directory (Join-Path $wt "screenshots") | Out-Null
Copy-Item (Join-Path $root "docs/screenshots/*.png") (Join-Path $wt "screenshots")
New-Item -ItemType File (Join-Path $wt ".nojekyll") | Out-Null

git -C $wt add -A
if (git -C $wt status --porcelain) {
    $from = (git -C $root rev-parse --short HEAD).Trim()
    git -C $wt commit -q -m "Site from main $from" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
    Write-Host "gh-pages updated from main $from"
}
else {
    Write-Host "Site unchanged"
}

if ($Push) {
    git -C $wt push origin gh-pages
}
else {
    Write-Host "Not pushed. ./deploy-site.ps1 -Push publishes it (starts one GitHub Pages build)."
}
