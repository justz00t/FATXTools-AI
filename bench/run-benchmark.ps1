<#
.SYNOPSIS
  Compares the original FATXTools (commit ac5c146) with the current version on a drive image.

.EXAMPLE
  .\bench\run-benchmark.ps1 -Image E:\Xbox\xnaxdk.img

.EXAMPLE
  # Carve the whole partitions (the original carver can take hours on big drives)
  .\bench\run-benchmark.ps1 -Image E:\Xbox\xnaxdk.img -CarveLimitMB 0
#>
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$Out = "bench-results",
    [int]$CarveLimitMB = 4096,
    [switch]$NoCarve
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$baseline = "ac5c146"
$oldSrc = Join-Path ([System.IO.Path]::GetTempPath()) "fatxtools-original-$baseline"

if (-not (Test-Path $Image)) { throw "Image not found: $Image" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not (Test-Path (Join-Path $oldSrc "FATX"))) {
    Write-Host "Checking out the original sources ($baseline) to $oldSrc"
    git -C $repo worktree add --detach $oldSrc $baseline
    if ($LASTEXITCODE -ne 0) { throw "git worktree failed" }
}

Write-Host "Building..."
dotnet build "$repo\bench\OldHarness" -c Release -p:OldSrc="$oldSrc" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build of the original harness failed" }
dotnet build "$repo\bench\NewHarness" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build of the new harness failed" }
dotnet build "$repo\bench\Compare" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build of the compare tool failed" }

$harnessArgs = @()
if ($NoCarve) { $harnessArgs += "--no-carve" } else { $harnessArgs += @("--carve-limit-mb", $CarveLimitMB) }

Write-Host "Running the ORIGINAL version (this can take a while)..."
dotnet "$repo\bench\OldHarness\bin\Release\net8.0\OldHarness.dll" $Image "$Out\old.json" @harnessArgs
Write-Host "Running the NEW version..."
dotnet "$repo\bench\NewHarness\bin\Release\net8.0\NewHarness.dll" $Image "$Out\new.json" @harnessArgs
Write-Host "Comparing..."
dotnet "$repo\bench\Compare\bin\Release\net8.0\Compare.dll" $Image "$Out\old.json" "$Out\new.json" "$Out\report.md"

Write-Host ""
Write-Host "Report: $Out\report.md"
Get-Content "$Out\report.md"
