<#
.SYNOPSIS
  Builds Poppy release artifacts.

.DESCRIPTION
  1. Publishes a self-contained win-x64 build.
  2. Bundles an LGPL build of FFmpeg (engines\ffmpeg) so video/audio work out of the box.
  3. Creates the GitHub/winget installer with Velopack (Poppy-win-Setup.exe + update feed).
  4. Creates the Microsoft Store package (Poppy-<version>-x64.msix).
  5. Writes winget manifests with the installer hash filled in.

.EXAMPLE
  ./build.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipFfmpeg,
    [switch]$SkipSetup,
    [switch]$SkipMsix,
    [switch]$NoDelta
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "artifacts"
$publish = Join-Path $out "publish"
$release = Join-Path $out "release"
$repo = "KaanBahaSever/Poppy"

Remove-Item $publish, $release -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish, $release | Out-Null

Write-Host "==> Publishing Poppy $Version" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/Poppy/Poppy.csproj") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not $SkipFfmpeg) {
    Write-Host "==> Bundling FFmpeg (LGPL build)" -ForegroundColor Cyan
    $cache = Join-Path $out "cache"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = Join-Path $cache "ffmpeg-lgpl.zip"
    if (-not (Test-Path $zip)) {
        $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip"
        Invoke-WebRequest $url -OutFile $zip
    }
    $tmp = Join-Path $cache "ffmpeg-extract"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $zip $tmp
    $bin = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    $dest = Join-Path $publish "engines/ffmpeg"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $bin.DirectoryName "ffmpeg.exe"), (Join-Path $bin.DirectoryName "ffprobe.exe") $dest
    $license = Get-ChildItem $tmp -Recurse -Filter LICENSE.txt | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $dest "LICENSE.txt") }
    Set-Content (Join-Path $dest "README.txt") "FFmpeg (https://ffmpeg.org), LGPL v2.1+ build from https://github.com/BtbN/FFmpeg-Builds. Source: https://github.com/FFmpeg/FFmpeg"
}

if (-not $SkipSetup) {
    Write-Host "==> Creating installer (Velopack)" -ForegroundColor Cyan
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { dotnet tool install -g vpk | Out-Null }
    if (-not $NoDelta) {
        # With the previous release next to it, vpk pack also makes a delta package, so updates
        # download only what changed instead of the whole app.
        $token = if ($env:GH_TOKEN) { @("--token", $env:GH_TOKEN) } else { @() }
        vpk download github --repoUrl "https://github.com/$repo" -o $release @token
        if ($LASTEXITCODE -ne 0) { Write-Warning "No previous release found: full package only." }
    }
    vpk pack --packId Poppy --packVersion $Version --packDir $publish --mainExe Poppy.exe `
        --packTitle Poppy --packAuthors "Kaan Baha Sever" --icon (Join-Path $root "src/Poppy/Assets/Poppy.ico") `
        --outputDir $release
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

    $setup = Join-Path $release "Poppy-win-Setup.exe"
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    $wingetOut = Join-Path $out "winget/manifests/k/KaanBahaSever/Poppy/$Version"
    New-Item -ItemType Directory -Force $wingetOut | Out-Null
    Get-ChildItem (Join-Path $root "packaging/winget") -Filter *.yaml | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName)
        $text = $text.Replace('$Version$', $Version).Replace('$Sha256$', $hash).Replace('$Repo$', $repo)
        [IO.File]::WriteAllText((Join-Path $wingetOut $_.Name), $text, (New-Object Text.UTF8Encoding $false))
    }
}

if (-not $SkipMsix) {
    Write-Host "==> Creating Store package (MSIX)" -ForegroundColor Cyan
    $makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $makeappx) { throw "makeappx.exe not found (install the Windows SDK)" }
    $layout = Join-Path $out "msix-layout"
    Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $publish $layout -Recurse
    $assets = Join-Path $layout "Assets"
    New-Item -ItemType Directory -Force $assets | Out-Null
    # Unqualified names point at the 200% artwork (no resources.pri needed).
    Get-ChildItem (Join-Path $root "packaging/msix/Assets") -Filter "*.scale-200.png" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $assets ($_.Name -replace '\.scale-200', ''))
    }
    Copy-Item (Join-Path $root "packaging/msix/Assets/*.png") $assets
    $store = Get-Content (Join-Path $root "packaging/store.json") -Raw | ConvertFrom-Json
    $parts = $Version.Split('.')
    while ($parts.Count -lt 3) { $parts += "0" }
    $msixVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
    $manifest = [IO.File]::ReadAllText((Join-Path $root "packaging/msix/AppxManifest.xml"))
    $manifest = $manifest.Replace('$IdentityName$', $store.IdentityName).Replace('$Publisher$', $store.Publisher).
        Replace('$PublisherDisplayName$', $store.PublisherDisplayName).Replace('$Version$', $msixVersion)
    [IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))
    $msix = Join-Path $release "Poppy-$Version-x64.msix"
    & $makeappx.FullName pack /d $layout /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
}

Write-Host "==> Done: $release" -ForegroundColor Green
Get-ChildItem $release | Format-Table Name, Length
