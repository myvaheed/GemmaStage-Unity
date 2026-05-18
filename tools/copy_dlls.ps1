#requires -Version 5.1
<#
.SYNOPSIS
  Phase 6 T1 — populate the Unity GS.Unity plugins tree from the locally-built
  GemmaStage native DLL and the GemmaStage.Session managed module.

.DESCRIPTION
  Drops two sets of artifacts into the Unity project:

    1. Managed plugins   → Assets/GemmaStage/Plugins/
       GemmaStage.Session.dll + every transitive .dll the module needs
       (PdfPig*, System.Text.Json, Microsoft.Bcl.AsyncInterfaces, ...).
       Resolved by running `dotnet publish` on GemmaStage.Session.csproj for
       netstandard2.1 — the publish output is the canonical "all-deps" set.

    2. Native plugins    → Assets/GemmaStage/Plugins/x86_64/
       GemmaStage.dll + llama.cpp + ggml backends + CUDA redistributables,
       copied from GemmaStage/build/output/ (CMake Release build output).

  Re-runs are idempotent: existing .dll files are overwritten; Unity .meta
  files (if present) are NOT touched so import settings stay stable.

.NOTES
  Must be run from the repo root or the script's parent (it auto-resolves).
  Requires: .NET SDK 8.x on PATH, a Release build of GemmaStage native at
  GemmaStage/build/output/. Does NOT invoke CMake — run that manually before
  this script if the native side has changed.
#>

[CmdletBinding()]
param(
    [switch] $SkipManaged,
    [switch] $SkipNative,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

# Resolve repo root: this script lives at <repo>/tools/copy_dlls.ps1.
# In a git worktree layout the current checkout may not have the build output —
# find the main worktree via `git worktree list` and use it as the source for
# native artifacts (GemmaStage.dll + llama.cpp runtime), while targeting the
# Unity project in the current worktree.
$worktreeRoot = Split-Path -Parent $PSScriptRoot   # current checkout (may be a worktree)

# Identify the main worktree (first entry in `git worktree list`)
$mainWorktree = & git -C $worktreeRoot worktree list --porcelain |
    Where-Object { $_ -match '^worktree ' } |
    Select-Object -First 1 |
    ForEach-Object { $_ -replace '^worktree ', '' }
if (-not $mainWorktree) { $mainWorktree = $worktreeRoot }

# Native build output lives in the main worktree; managed sources and Unity
# live in whichever checkout this script is in.
$repoRoot     = $worktreeRoot
$sessionProj  = Join-Path $repoRoot 'GemmaStage.Session\GemmaStage.Session.csproj'
$nativeOutput = Join-Path $mainWorktree 'GemmaStage\build\output'
$unityPlugins = Join-Path $repoRoot 'GS.Unity\GemmaStage\Assets\GemmaStage\Plugins'
$unityNative  = Join-Path $unityPlugins 'x86_64'
$publishDir   = Join-Path $repoRoot 'GemmaStage.Session\bin\Publish\netstandard2.1'

Write-Host "Main worktree  : $mainWorktree" -ForegroundColor DarkGray
Write-Host "Current root   : $repoRoot"     -ForegroundColor DarkGray
Write-Host "Native output  : $nativeOutput" -ForegroundColor DarkGray

function Write-Section($title) {
    Write-Host ''
    Write-Host "── $title ──" -ForegroundColor Cyan
}

function Ensure-Dir($path) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

function Copy-Dll($src, $dstDir) {
    $name = Split-Path -Leaf $src
    $dst  = Join-Path $dstDir $name
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "  $name" -ForegroundColor DarkGray
}

if ($Clean) {
    Write-Section 'Cleaning Unity plugins (DLLs only — .meta preserved)'
    if (Test-Path $unityPlugins) {
        Get-ChildItem -Path $unityPlugins -Filter '*.dll' -Recurse |
            ForEach-Object { Remove-Item -Path $_.FullName -Force }
    }
}

# ── 1. Managed: publish session module and harvest every transitive DLL ────────
if (-not $SkipManaged) {
    Write-Section 'Managed plugins (GemmaStage.Session + deps)'

    if (-not (Test-Path $sessionProj)) {
        throw "Session csproj not found at: $sessionProj"
    }

    Ensure-Dir $publishDir

    Write-Host '  dotnet publish (netstandard2.1, Release)...' -ForegroundColor DarkGray
    & dotnet publish $sessionProj `
        -c Release `
        -f netstandard2.1 `
        -o $publishDir `
        --nologo `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    Ensure-Dir $unityPlugins

    Write-Host "  → $unityPlugins" -ForegroundColor DarkGray
    Get-ChildItem -Path $publishDir -Filter '*.dll' |
        ForEach-Object { Copy-Dll $_.FullName $unityPlugins }
}

# ── 2. Native: GemmaStage.dll + llama.cpp/ggml + CUDA redistributables ─────────
if (-not $SkipNative) {
    Write-Section 'Native plugins (GemmaStage.dll + llama.cpp + ggml + CUDA)'

    if (-not (Test-Path $nativeOutput)) {
        throw "Native build output not found at: $nativeOutput. " +
              "Build it first: cmake --build GemmaStage/build --config Release"
    }

    Ensure-Dir $unityNative

    # Whitelist — explicit so logs / executables from the same folder don't leak.
    $patterns = @(
        'GemmaStage.dll',
        'llama.dll',
        'mtmd.dll',
        'ggml.dll',
        'ggml-base.dll',
        'ggml-cpu*.dll',
        'ggml-cuda.dll',
        'ggml-vulkan.dll',
        'ggml-rpc.dll',
        'cudart64_*.dll',
        'cublas64_*.dll',
        'cublasLt64_*.dll',
        'libomp140.x86_64.dll'
    )

    Write-Host "  → $unityNative" -ForegroundColor DarkGray
    foreach ($pat in $patterns) {
        Get-ChildItem -Path $nativeOutput -Filter $pat -ErrorAction SilentlyContinue |
            ForEach-Object { Copy-Dll $_.FullName $unityNative }
    }
}

Write-Section 'Done'
Write-Host "Reopen the Unity editor (or right-click Assets/GemmaStage/Plugins → Reimport) to pick up the new plugins."
