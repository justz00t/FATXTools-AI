<img align="left" width="80" height="80" src="FATXTools/appicon.ico">

# FATXTools [![Build status](https://ci.appveyor.com/api/projects/status/ua22jsn54pr1398c/branch/master?svg=true)](https://ci.appveyor.com/project/aerosoul94/fatxtools/build/artifacts)
FATXTools is a data recovery tool for FATX file systems. The goal of this project is to automate searching and recovering deleted files as well as provide tools for manual data recovery.

This is a C# port of the Python version: [fatx-tools](https://github.com/aerosoul94/fatx-tools)

Download: [Github Releases](https://github.com/aerosoul94/FATXTools/releases)

# Contributors
* Tdijital - Thanks for the bug reports and the amazing app logo <3
* The many many testers who provided bug reports <3

# Features
* Supports loading from hdd images or physical devices.
* Use the Cluster Viewer to visually see what data occupies any cluster.
* Metadata Analyzer scans for abandoned file system metadata.
* File Carver scans for files based on their file formats.
* Save results to an editable and reloadable json database.

# What's new in this fork

**Recovery improvements**
* Deleted files written into fragmented free space are rebuilt correctly: chain prediction skips clusters that belonged to files which already existed when the deleted file was written (switchable back to the old contiguous assumption).
* Recovered executables are verified with their own checksums: XEX compressed-block SHA-1 chains (including dev-kit encrypted XEX) and XBE section digests, plus a size check against the directory entry that catches files whose clusters were reused by another file.
* The metadata analyzer finds entries it used to miss (combined attribute flags such as Directory|Archive, the last cluster of a partition, single character names, 0x00 padded Xbox 360 names) and rejects more false positives.
* The file carver is several times faster (chunked reads, one in-memory magic lookup per offset), computes exact sizes for XEX (block chain), XBE (sections), STFS CON/LIVE/PIRS packages, PDB 2.00/7.00, XDBF/GPD, RIFF, XPR0 and PE files, and can scan free space only.
* Original Xbox F:/G: partitions (XBPartitioner table or standard layouts) and the Xbox 360 SystemExt partition are detected.

**Drive imaging and compressed images**
* *File → Dump Drive to Image...* and `fatxtools dump <drive> <target>` copy a drive to a raw `.img` or a compressed `.imgc` image (the HDD Raw Copy Tool v1 format). Reading, compressing and writing run on separate threads with large sequential requests, so the source drive never waits for the target.
* Unreadable sectors never stop a dump. For raw targets they are skipped first and retried sector by sector after everything readable is safe; what stays unreadable is zero filled and listed in `<target>.dump.json`. Interrupted dumps resume (`--resume`), and MD5 / SHA-1 / SHA-256 / SHA-512 are computed while copying.
* `.imgc` images open directly everywhere (GUI, command line, MCP, benchmark): no unpacking to a raw image first. Truncated or damaged `.imgc` files still open; the missing parts read as zeroes and are reported.
* `fatxtools convert` converts between raw and compressed images, `fatxtools hash` hashes the drive contents of either, `fatxtools devices` lists drives. On Linux use `/dev/sdX` paths.
* Not supported: the newer `.imgcv2` format of HDD Raw Copy Tool 2.x, and writing images back to a drive.

**Reference sets: find deleted copies and older versions of known files**
* *Reference → Build Reference Set from Folder...* / `fatxtools fingerprint <folder> <set.json>` fingerprints every file below a folder (e.g. one build of a game) block by block and saves the result as reusable JSON.
* *Reference → Find Reference Files on This Partition...* / `fatxtools match <image> <partition> --ref <set.json> --out <dir>` fingerprints every free cluster, joins the hits into files (also across fragments) and confirms complete files with the SHA-1 of the whole file. Exact copies are saved under their original names and paths; files that only partly match (usually other versions of the file) are saved under `_partial` with the share that is identical.
* The file types of a reference set (same extension, same four starting bytes) are added to the file carver, which then also finds files of those types that are not in the set (`carve --ref`, or automatically in the GUI once a set is loaded).

**More complete carving**
* Files whose signature matches but whose header is damaged are no longer dropped: they are listed as `..._damaged` and saved up to the next file or used cluster (`KeepDamaged`, on by default).
* With verification on, a deleted XEX/XBE in free space that fails its checksums when read contiguously is re-read from the free clusters only (skipping clusters in use) and kept if it then verifies, which recovers executables that were written around existing files.
* Bink video (`.bik`, `.bk2`) and PNG files are carved with exact sizes.

**Bug fixes** (selection): crash on volumes whose cluster size is not 16KB, Xbox 360 directory entries corrupting the shared cluster buffer, out-of-range FAT links and FAT loops, directory cycles that hung the tree view and recovery, chains built from cluster 0 or past the end of the partition (crash in the integrity analyzer), gigabytes of memory used by the integrity analyzer on large drives, raw device reads straddling a sector boundary, recovered files keeping stale bytes when overwriting a larger file, deleted and re-created files with the same name overwriting each other, invalid Windows file names, the log window freezing the UI, carver reading past the end of the partition.

**Claude assistant (MCP, no API key needed)**
* `fatxtools mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server. Add it to Claude Desktop or Claude Code and just ask, e.g. *"Open E:\Xbox\xnaxdk.img, find deleted game builds and recover the ones whose executables verify."* Claude runs on your normal Claude subscription; the tools run locally and only read the image. See [MCP setup](#mcp-setup).
* Optional, with an Anthropic API key: *Claude → Recovery Assistant...* in the GUI, or `fatxtools ai <image> "<request>"` on the command line, lets Claude run the analysis: find deleted game builds, verify XEX/XBE files, carve lost executables and recover them into organized folders. Requires an Anthropic API key (`ANTHROPIC_API_KEY` or entered in the dialog). Tools run locally; only small JSON summaries (names, sizes, header fields) are sent to the API.

**Command line**: `fatxtools info|ls|analyze|recover|carve|identify|fingerprint|match|devices|dump|convert|hash|ai` (run `dotnet run --project FATXTools.Cli -- --help`).

**Benchmark**: `bench/run-benchmark.ps1 -Image <image>` compares the original version with this one on your own image (see `bench/README.md`).

# MCP setup

Build a standalone executable (or download `fatxtools-win-x64` from the CI artifacts):

```powershell
dotnet publish FATXTools.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

**Claude Desktop**: Settings → Developer → Edit Config, add the server to `claude_desktop_config.json` and restart Claude:

```json
{
  "mcpServers": {
    "fatxtools": {
      "command": "C:\\Tools\\FATXTools\\fatxtools.exe",
      "args": ["mcp", "--out", "D:\\Recovered"]
    }
  }
}
```

**Claude Code**:

```powershell
claude mcp add fatxtools -- C:\Tools\FATXTools\fatxtools.exe mcp --out D:\Recovered
```

Optional arguments: `--image <path>` opens an image at start-up (otherwise Claude calls `open_image` with the path you give it), `--out <dir>` sets where recovered files go (default `Documents\FATXTools Recovered`).

Tools: `open_image`, `list_partitions`, `run_metadata_analysis`, `set_chain_strategy`, `list_files`, `find_executables`, `find_game_builds`, `inspect_file`, `run_file_carver`, `list_carved`, `hex_dump`, `recover_files`. Images are opened read-only and files are only written below the output folder. Scanning a large drive can take a few minutes per call.

# Screenshots
![](Screenshots/FATXTools_2020-10-26_11-03-21.png)
![](Screenshots/FATXTools_2020-10-26_11-04-26.png)
![](Screenshots/FATXTools_2020-10-26_11-07-02.png)
![](Screenshots/FATXTools_2020-10-26_11-09-07.png)
