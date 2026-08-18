# embed-render-pipelines-core.ps1
#
# Moves com.unity.render-pipelines.core and com.unity.render-pipelines.universal from
# Library/PackageCache into Packages/ so they become embedded packages that can be
# locally modified.
#
# Run from anywhere; paths are resolved relative to this script's location.
# Re-running is safe: packages already embedded are skipped.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve unity root regardless of whether this script lives under Packages/<sgsr>/Tools/
# (3 levels up) or Library/PackageCache/<sgsr@hash>/Tools/ (4 levels up).
$twoUp = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ((Split-Path $twoUp -Leaf) -eq 'PackageCache')
{
    $unityRoot = Resolve-Path (Join-Path $PSScriptRoot '../../../..')
}
else
{
    $unityRoot = Resolve-Path (Join-Path $PSScriptRoot '../../..')
}

$manifestPath = Join-Path $unityRoot 'Packages\manifest.json'
$packageCache = Join-Path $unityRoot 'Library\PackageCache'
$packagesDir  = Join-Path $unityRoot 'Packages'

# --- 1. Confirm project uses URP ---
Write-Host "Reading manifest: $manifestPath"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$urpVersion = $manifest.dependencies.'com.unity.render-pipelines.universal'
if (-not $urpVersion)
{
    Write-Error "com.unity.render-pipelines.universal not found in manifest.json. Aborting."
    exit 1
}
Write-Host "Found URP $urpVersion in manifest."

# Helper: move a single package from PackageCache to Packages/.
# Skips silently if the destination already exists (already embedded) or if the
# source is gone from the cache (e.g. Unity already cleaned it up after a previous run).
function Move-PackageToEmbedded
{
    param([string]$Filter)

    $cacheDir = Get-ChildItem -Path $packageCache -Directory -Filter $Filter |
                Select-Object -First 1

    if (-not $cacheDir)
    {
        Write-Host "  $Filter not found in PackageCache — may already be embedded. Skipping."
        return
    }

    $destination = Join-Path $packagesDir $cacheDir.Name

    if (Test-Path $destination)
    {
        Write-Host "  Destination already exists: $destination — already embedded, skipping."
        return
    }

    Write-Host "  Moving $($cacheDir.Name) -> $destination"
    Move-Item -Path $cacheDir.FullName -Destination $destination
    Write-Host "  Done."
}

# --- 2. Embed com.unity.render-pipelines.core ---
Write-Host ""
Write-Host "Embedding com.unity.render-pipelines.core ..."
Move-PackageToEmbedded -Filter 'com.unity.render-pipelines.core@*'

# --- 3. Embed com.unity.render-pipelines.universal ---
Write-Host ""
Write-Host "Embedding com.unity.render-pipelines.universal ..."
Move-PackageToEmbedded -Filter 'com.unity.render-pipelines.universal@*'

# --- 4. Apply SGSR render-pipeline patch ---
Write-Host ""
Write-Host "Applying render-pipeline patch ..."

$patchFile = Join-Path $PSScriptRoot 'patches\render-pipelines.patch'

# Locate patch.exe — bundled with Git for Windows, no git-repo context required.
$patchExe = $null
$patchCmd = Get-Command 'patch' -ErrorAction SilentlyContinue
if ($patchCmd)
{
    $patchExe = $patchCmd.Source
}
else
{
    $gitCmd = Get-Command 'git' -ErrorAction SilentlyContinue
    if ($gitCmd)
    {
        $candidate = Join-Path (Split-Path (Split-Path $gitCmd.Source -Parent) -Parent) 'usr\bin\patch.exe'
        if (Test-Path $candidate) { $patchExe = $candidate }
    }
}
if (-not $patchExe)
{
    Write-Error "patch.exe not found. Install Git for Windows and ensure its usr\bin is accessible."
    exit 1
}

# Idempotency: check for the modification marker in a patched file.
# Also guards against running when the packages were never embedded.
$checkFile = Resolve-Path (Join-Path $packagesDir 'com.unity.render-pipelines.core@*\Runtime\Upscaling\IUpscaler.cs') -ErrorAction SilentlyContinue |
             Select-Object -First 1
if (-not $checkFile)
{
    Write-Error "  com.unity.render-pipelines.core is not present in $packagesDir. Embed the packages first (steps 2-3 above must succeed)."
    exit 1
}
if ((Get-Content $checkFile -Raw) -match 'BEGIN SGSR MODIFICATION')
{
    Write-Host "  Patch already applied, skipping."
}
else
{
    # Resolve the actual hashed folder names so the patch applies regardless of which
    # hash the user has (hash varies by URP version but is identical across machines
    # on the same version).
    $coreName = (Get-ChildItem $packagesDir -Directory -Filter 'com.unity.render-pipelines.core@*' | Select-Object -First 1).Name
    $urpName  = (Get-ChildItem $packagesDir -Directory -Filter 'com.unity.render-pipelines.universal@*' | Select-Object -First 1).Name
    $patchText = (Get-Content $patchFile -Raw) `
        -replace 'com\.unity\.render-pipelines\.core@[0-9a-f]+',      $coreName `
        -replace 'com\.unity\.render-pipelines\.universal@[0-9a-f]+', $urpName
    $tempPatch = Join-Path $env:TEMP 'render-pipelines.resolved.patch'
    Set-Content -Path $tempPatch -Value $patchText -NoNewline

    Push-Location $packagesDir
    & $patchExe -p1 --forward --fuzz 3 --input $tempPatch
    $exitCode = $LASTEXITCODE
    Pop-Location
    if ($exitCode -ne 0)
    {
        Write-Error "  Failed to apply render-pipelines.patch."
        exit 1
    }
    Write-Host "  Done."
}

Write-Host ""
Write-Host "All steps complete. Re-open the Unity editor — it will pick up the embedded packages automatically."
Write-Host ""
Write-Host "IMPORTANT: Add 'SGSR_APPLIED_PATCH' to Project Settings > Player > Scripting Define Symbols"
Write-Host "           to activate the SGSR spatial-upscaler optimisation (skips CopyDepth + MotionVector passes)."
