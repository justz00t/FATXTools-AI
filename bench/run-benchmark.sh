#!/usr/bin/env bash
# Compares the original FATXTools (commit ac5c146) with the current version on a drive image.
# usage: bench/run-benchmark.sh <image> [out-dir] [carve-limit-mb (0 = all)]
set -euo pipefail
image="$1"; out="${2:-bench-results}"; limit="${3:-4096}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
baseline=ac5c146
old_src="${TMPDIR:-/tmp}/fatxtools-original-$baseline"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
[ -d "$old_src/FATX" ] || git -C "$repo" worktree add --detach "$old_src" "$baseline"
dotnet build "$repo/bench/OldHarness" -c Release -p:OldSrc="$old_src" -nologo -v q
dotnet build "$repo/bench/NewHarness" -c Release -nologo -v q
dotnet build "$repo/bench/Compare" -c Release -nologo -v q
dotnet "$repo/bench/OldHarness/bin/Release/net8.0/OldHarness.dll" "$image" "$out/old.json" --carve-limit-mb "$limit"
dotnet "$repo/bench/NewHarness/bin/Release/net8.0/NewHarness.dll" "$image" "$out/new.json" --carve-limit-mb "$limit"
dotnet "$repo/bench/Compare/bin/Release/net8.0/Compare.dll" "$image" "$out/old.json" "$out/new.json" "$out/report.md"
echo "Report: $out/report.md"
