<#
.SYNOPSIS
  Publishes a Poppy release from this PC. Nothing is built on GitHub.

.DESCRIPTION
  1. Checks that src/Poppy/Poppy.csproj has this version and that main is committed and pushed.
  2. Runs the tests locally.
  3. Runs build.ps1: installer, delta update package, Store MSIX, winget manifests.
  4. Publishes the GitHub release with vpk (installer, packages, update feed), then adds the MSIX
     and the notes from docs/RELEASE_NOTES.md.

  Creating the release also creates the tag on GitHub; no workflow listens to tags or releases,
  so this starts no GitHub Actions run.

.EXAMPLE
  ./publish.ps1 -Version 1.0.5
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$repo = "KaanBahaSever/Poppy"
$tag = "v$Version"

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

Step "Checking $tag"
$csproj = [IO.File]::ReadAllText((Join-Path $root "src/Poppy/Poppy.csproj"))
if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "src/Poppy/Poppy.csproj does not say <Version>$Version</Version>. Bump it and commit first."
}
if (git -C $root status --porcelain) { throw "There are uncommitted changes. Commit them first." }
$head = (git -C $root rev-parse HEAD).Trim()
git -C $root fetch -q origin
$remote = (git -C $root rev-parse origin/main).Trim()
if ($head -ne $remote) { throw "main is not pushed (local $head, GitHub $remote). Push it first." }
gh release view $tag --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $root "tests/Poppy.Core.Tests") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was published." }
}

Step "Building"
$env:GH_TOKEN = (gh auth token).Trim()  # lets build.ps1 fetch the previous release for the delta
& (Join-Path $root "build.ps1") -Version $Version

Step "Publishing to GitHub"
$release = Join-Path $root "artifacts/release"
vpk upload github --repoUrl "https://github.com/$repo" --token $env:GH_TOKEN -o $release `
    --tag $tag --releaseName "Poppy $Version" --targetCommitish $head --publish
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed" }
$msix = Get-ChildItem $release -Filter "*.msix" | Select-Object -First 1
if ($msix) { gh release upload $tag $msix.FullName --repo $repo --clobber }
gh release edit $tag --repo $repo --notes-file (Join-Path $root "docs/RELEASE_NOTES.md")
git -C $root fetch -q --tags

Write-Host ""
Write-Host "Published https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
Write-Host "winget manifests for a winget-pkgs update: $(Join-Path $root 'artifacts/winget')"
