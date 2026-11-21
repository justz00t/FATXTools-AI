# Benchmark: original vs. improved FATXTools

Runs the same pipeline (mount → metadata analyzer → file tree → integrity
analyzer → file carver) with the original code (commit `ac5c146`) and the
current code on one drive image, then writes `report.md` comparing:

* speed of every step and crashes
* deleted entries found
* how many recovered deleted XEX/XBE files pass their own SHA-1 checks
  (objective proof that the recovered data is intact)
* carved files and whether they are saved with the exact size
* **chain reconstruction accuracy on real data**: every active file is treated
  as if it were deleted and its cluster chain is predicted from the first
  cluster and size alone; the real FAT chain is the answer key

The image is only read. Requirements: .NET 8 SDK and git.

## Windows

```powershell
git fetch origin
git checkout claude/gifted-ptolemy-m8xhdh
.\bench\run-benchmark.ps1 -Image E:\Xbox\xnaxdk.img
```

Options: `-CarveLimitMB 0` carves whole partitions (the original carver can
take hours on large drives; default 4096 MB of each partition for both
versions), `-NoCarve` skips carving, `-Out <dir>` sets the result folder
(default `bench-results`).

## Linux / macOS

```bash
bench/run-benchmark.sh /path/to/image.img bench-results 4096
```

## Synthetic test image

`dotnet run -c Release --project bench/MakeTestImage -- synth360.img` creates
an Xbox 360 layout with builds, deletions and fragmentation (sparse 7GB file;
on Windows it is not sparse and takes 7GB of disk).
