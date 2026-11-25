using FATX.AI;
using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.Formats;
using FATX.Imaging;
using FATX.Reference;
using FATX.Recovery;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FATXTools.Cli
{
    public static class Program
    {
        private const string Usage = @"FATXTools command line

Usage:
  fatxtools info <image>
      List partitions and their status.
  fatxtools ls <image> <partition> [--deleted] [--analyze] [--filter <glob>]
      List files. --analyze runs the metadata analyzer first so deleted files are included.
  fatxtools analyze <image> [<partition>] [--carve] [--free-only] [--verify] [--strategy contiguous|smart]
      Run the metadata analyzer (and optionally the file carver) and print a summary.
  fatxtools recover <image> <partition> --out <dir> [--deleted-only] [--filter <glob>] [--carved] [--verify]
                    [--strategy contiguous|smart]
      Recover files. Without --deleted-only active files are recovered too.
  fatxtools carve <image> <partition> [--interval byte|align|sector|page|cluster] [--free-only] [--verify] [--out <dir>]
      Carve files by signature; --out saves them.
  fatxtools identify <file> [--verify]
      Identify an Xbox file (XEX/XBE/STFS/...) and verify its checksums.
  fatxtools fingerprint <folder> <out.json> [--block <bytes>] [--name <name>]
      Fingerprint every file below a folder (e.g. one build of a game) into a reusable reference set.
  fatxtools match <image> <partition> --ref <set.json>[,<set2.json>...] [--out <dir>] [--all-clusters]
                  [--min-percent <n>] [--report <file.json>]
      Find deleted copies and older versions of the reference files on the partition by content.
      Exact copies are confirmed by SHA-1 and saved under their original paths; partial matches
      (usually other versions) go to _partial. --all-clusters also searches clusters that are in use.
      'carve --ref <set.json>' additionally makes the carver look for the file types of a reference set.
  fatxtools devices
      List the physical drives of this computer.
  fatxtools dump <device|image> <target.img|target.imgc> [--resume] [--overwrite] [--hash md5,sha1,sha256]
                 [--retries <n>] [--chunk <KB>] [--length <bytes>]
      Copy a drive (e.g. \\.\PhysicalDrive2 or /dev/sdb, run as Administrator / root) to an image file.
      A .imgc target is compressed (HDD Raw Copy Tool format) and can be opened directly by every command.
      Unreadable sectors never stop the dump: they are retried, then zero filled and listed in
      <target>.dump.json. --resume continues an interrupted dump.
  fatxtools convert <image> <target.img|target.imgc> [--overwrite] [--hash ...]
      Convert between raw and compressed images.
  fatxtools hash <image> [--hash md5,sha1,sha256]
      Hash the drive contents of an image (the same value for a raw image and its .imgc).
  fatxtools mcp [--image <image>] [--out <dir>]
      Run as an MCP server (stdio) so Claude Desktop / Claude Code can use the
      recovery tools with your Claude subscription. No API key needed.
  fatxtools ai <image> [<request>] [--model <id>] [--out <dir>] [--effort low|medium|high|xhigh|max]
      Let Claude drive the analysis. Requires ANTHROPIC_API_KEY.
      Without a request, starts an interactive session.

Options:
  --quiet    Suppress analyzer log output.
";

        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            var opts = Options.Parse(args.Skip(1).ToArray());

            if (args[0] == "mcp")
            {
                // stdout carries the protocol; nothing else may be written to it.
                var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                var stdin = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
                Console.SetOut(TextWriter.Null);
                try
                {
                    var server = new McpServer(stdin, stdout, opts.Get("image", null), opts.Get("out", null));
                    await server.RunAsync();
                    return 0;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"fatxtools mcp: {e.Message}");
                    return 1;
                }
            }

            var log = Console.Out;
            if (opts.Has("quiet") || args[0] == "ai")
            {
                // Library code logs to Console; keep the output readable.
                Console.SetOut(TextWriter.Null);
            }

            try
            {
                switch (args[0])
                {
                    case "info": return Info(opts, log);
                    case "ls": return List(opts, log);
                    case "analyze": return Analyze(opts, log);
                    case "recover": return Recover(opts, log);
                    case "carve": return Carve(opts, log);
                    case "identify": return Identify(opts, log);
                    case "fingerprint": return Fingerprint(opts, log);
                    case "match": return Match(opts, log);
                    case "devices": return Devices(log);
                    case "hash": return Hash(opts, log);
                    case "dump":
                    case "convert": return Dump(opts, log);
                    case "ai": return await Ai(opts, log);
                    default:
                        log.WriteLine($"Unknown command '{args[0]}'.");
                        log.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                log.WriteLine($"Error: {e.Message}");
                return 1;
            }
        }

        private static RecoverySession Open(Options opts) => new RecoverySession(opts.Positional(0, "image"));

        private static ChainStrategy Strategy(Options opts)
        {
            var s = opts.Get("strategy", "smart");
            return s.StartsWith("contig", StringComparison.OrdinalIgnoreCase) ? ChainStrategy.Contiguous : ChainStrategy.SkipOlderAllocated;
        }

        private static IEnumerable<PartitionState> SelectPartitions(RecoverySession session, Options opts, int index)
        {
            var name = opts.PositionalOrNull(index);
            if (name != null)
                return new[] { session.GetPartition(name) };
            return session.Partitions.Where(p => p.Mounted);
        }

        private static int Info(Options opts, TextWriter log)
        {
            using (var session = Open(opts))
            {
                log.WriteLine($"Image: {session.ImageName} ({session.Drive.Length:N0} bytes)");
                foreach (var p in session.Partitions)
                {
                    var v = p.Volume;
                    if (!p.Mounted)
                    {
                        log.WriteLine($"  {p.Name,-24} offset=0x{v.Offset:X} NOT MOUNTED: {p.MountError}");
                        continue;
                    }
                    log.WriteLine($"  {p.Name,-24} offset=0x{v.Offset:X} length=0x{v.Length:X} cluster=0x{v.BytesPerCluster:X} " +
                                  $"{(v.IsFat16 ? "FAT16" : "FAT32")} files={v.CountFiles()} used={v.GetUsedSpace() / (1024 * 1024)}MB free={v.GetFreeSpace() / (1024 * 1024)}MB");
                }
            }
            return 0;
        }

        private static int List(Options opts, TextWriter log)
        {
            using (var session = Open(opts))
            {
                var p = session.GetPartition(opts.Positional(1, "partition"));
                if (opts.Has("analyze") || opts.Has("deleted"))
                {
                    session.SetChainStrategy(p, Strategy(opts));
                    session.RunMetadataAnalyzer(p);
                }

                var filter = opts.Get("filter", null);
                foreach (var f in RecoverySession.AllFiles(p).OrderBy(f => f.GetFullPath(), StringComparer.OrdinalIgnoreCase))
                {
                    if (opts.Has("deleted") && !f.IsDeleted) continue;
                    if (filter != null && !Glob.IsMatch(f.FileName, filter)) continue;
                    log.WriteLine($"{(f.IsDeleted ? "D" : " ")} {(f.IsDirectory() ? "<DIR>" : f.FileSize.ToString("N0")),14}  {f.CreationTime.AsDateTime():yyyy-MM-dd HH:mm}  rank={f.GetRanking()}  {f.GetFullPath()}");
                }
            }
            return 0;
        }

        private static int Analyze(Options opts, TextWriter log)
        {
            using (var session = Open(opts))
            {
                foreach (var p in SelectPartitions(session, opts, 1))
                {
                    p.Database.ChainStrategy = Strategy(opts);
                    session.RunMetadataAnalyzer(p);
                    var deleted = RecoverySession.AllFiles(p).Where(f => f.IsDeleted).ToList();
                    log.WriteLine($"[{p.Name}] metadata analyzer: {p.RecoveredDirentCount} entries in {p.MetadataAnalysisTime.TotalSeconds:F1}s, " +
                                  $"{deleted.Count} deleted ({deleted.Count(f => f.IsDirectory())} directories)");
                    for (int rank = 1; rank <= 4; rank++)
                        log.WriteLine($"    rank {rank}: {deleted.Count(f => f.GetRanking() == rank)}");

                    var exes = deleted.Where(f => !f.IsDirectory() && Regex.IsMatch(f.FileName, @"\.(xex|xbe|xexp)$", RegexOptions.IgnoreCase)).ToList();
                    if (exes.Count > 0)
                    {
                        log.WriteLine($"    deleted executables: {exes.Count}");
                        foreach (var f in exes.Take(50))
                        {
                            var info = FormatDetector.Identify(new ClusterChainSource(p.Volume, f.ClusterChain, f.FileSize), opts.Has("verify"), f.FileSize);
                            log.WriteLine($"      {f.GetFullPath()} rank={f.GetRanking()} {info}");
                        }
                    }

                    if (opts.Has("carve"))
                    {
                        session.RunFileCarver(p, FileCarverInterval.Cluster, opts.Has("free-only"), opts.Has("verify"));
                        log.WriteLine($"[{p.Name}] file carver: {p.CarvedFiles.Count} files in {p.CarveTime.TotalSeconds:F1}s");
                        foreach (var g in p.CarvedFiles.OfType<CarvedFileSignature>().GroupBy(c => c.Info.Format).OrderBy(g => g.Key))
                            log.WriteLine($"    {g.Key}: {g.Count()} ({g.Count(c => !c.InAllocatedSpace)} in free space)");
                    }
                }
            }
            return 0;
        }

        private static int Recover(Options opts, TextWriter log)
        {
            var outDir = opts.Get("out", null) ?? throw new ArgumentException("--out is required.");
            using (var session = Open(opts))
            {
                var p = session.GetPartition(opts.Positional(1, "partition"));
                p.Database.ChainStrategy = Strategy(opts);
                session.RunMetadataAnalyzer(p);

                var recoverer = new FileRecoverer(p.Volume) { VerifyFormats = opts.Has("verify") };
                var filter = opts.Get("filter", null);

                if (filter == null && !opts.Has("deleted-only"))
                {
                    recoverer.SaveAll(p.Database.GetRootFiles(), Path.Combine(outDir, p.Name));
                }
                else
                {
                    var selected = RecoverySession.AllFiles(p)
                        .Where(f => !f.IsDirectory())
                        .Where(f => !opts.Has("deleted-only") || f.IsDeleted)
                        .Where(f => filter == null || Glob.IsMatch(f.FileName, filter));
                    foreach (var f in selected)
                    {
                        // Keep the original folder structure.
                        var parts = f.GetFullPath().Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var dir = Path.Combine(new[] { outDir, p.Name }.Concat(parts.Take(parts.Length - 1).Select(FileRecoverer.SanitizeFileName)).ToArray());
                        recoverer.Save(f, dir);
                    }
                }

                if (opts.Has("carved"))
                {
                    session.RunFileCarver(p, FileCarverInterval.Cluster, true, opts.Has("verify"));
                    foreach (var c in p.CarvedFiles)
                        recoverer.Save(c, Path.Combine(outDir, p.Name, "_carved"));
                }

                int errors = recoverer.Results.Count(r => r.Error != null);
                log.WriteLine($"Recovered {recoverer.FilesSaved} files to {outDir} ({errors} with errors).");
                foreach (var r in recoverer.Results.Where(r => r.Format != null && r.Format.Verification != VerificationStatus.NotChecked))
                    log.WriteLine($"  {r.Format.Verification,-12} {r.SourcePath} -> {r.OutputPath}{(r.Repaired ? "  (cluster chain repaired using checksums)" : "")}");
                foreach (var r in recoverer.Results.Where(r => r.Error != null).Take(20))
                    log.WriteLine($"  ERROR {r.SourcePath}: {r.Error}");
            }
            return 0;
        }

        private static int Carve(Options opts, TextWriter log)
        {
            using (var session = Open(opts))
            {
                var p = session.GetPartition(opts.Positional(1, "partition"));
                var interval = (FileCarverInterval)Enum.Parse(typeof(FileCarverInterval), opts.Get("interval", "cluster"), true);
                List<TypeSignature> learned = null;
                if (opts.Has("ref"))
                    learned = LoadReferenceSets(opts, log).SelectMany(s => s.Types).ToList();
                session.RunFileCarver(p, interval, opts.Has("free-only"), opts.Has("verify"), default, null, learned);
                var baseOffset = p.Volume.Offset + p.Volume.FileAreaByteOffset;
                foreach (var c in p.CarvedFiles.OfType<CarvedFileSignature>())
                {
                    log.WriteLine($"0x{baseOffset + c.Offset:X12} {(c.InAllocatedSpace ? "alloc" : "free "),-5} {c.FileName,-40} {c.Info}");
                }
                log.WriteLine($"{p.CarvedFiles.Count} files in {p.CarveTime.TotalSeconds:F1}s " +
                              $"({p.Volume.FileAreaLength / Math.Max(0.001, p.CarveTime.TotalSeconds) / (1024 * 1024):F0} MB/s)");

                var outDir = opts.Get("out", null);
                if (outDir != null)
                {
                    var recoverer = new FileRecoverer(p.Volume);
                    foreach (var c in p.CarvedFiles)
                        recoverer.Save(c, outDir);
                    log.WriteLine($"Saved {recoverer.FilesSaved} files to {outDir}.");
                }
            }
            return 0;
        }

        private static int Identify(Options opts, TextWriter log)
        {
            var path = opts.Positional(0, "file");
            using (var fs = File.OpenRead(path))
            {
                var info = FormatDetector.Identify(new StreamSource(fs), opts.Has("verify"));
                if (info == null)
                {
                    log.WriteLine("Unknown format.");
                    return 1;
                }
                log.WriteLine(info.ToString());
                foreach (var kv in info.Properties) log.WriteLine($"  {kv.Key}: {kv.Value}");
                if (info.VerificationDetail != null) log.WriteLine($"  Verification: {info.VerificationDetail}");
                foreach (var w in info.Warnings) log.WriteLine($"  Warning: {w}");
                if (info.FileSize.HasValue && info.FileSize != fs.Length)
                    log.WriteLine($"  Note: header describes 0x{info.FileSize:X} bytes, file is 0x{fs.Length:X} bytes.");
            }
            return 0;
        }

        private static int Fingerprint(Options opts, TextWriter log)
        {
            var folder = opts.Positional(0, "folder");
            var output = opts.Positional(1, "out.json");
            int block = int.Parse(opts.Get("block", ReferenceSet.DefaultBlockSize.ToString()));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool interactive = !Console.IsErrorRedirected;
            var last = System.Diagnostics.Stopwatch.StartNew();
            var progress = new SyncProgress<ReferenceBuildProgress>(p =>
            {
                if (last.ElapsedMilliseconds < (interactive ? 250 : 10000) && p.FilesDone < p.FilesTotal)
                    return;
                last.Restart();
                var line = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files, {p.BytesDone / 1048576.0:N0} of {p.BytesTotal / 1048576.0:N0} MB";
                Console.Error.Write(interactive ? "\r" + line.PadRight(70) : line + Environment.NewLine);
            });

            var set = ReferenceSet.Build(folder, block, opts.Get("name", null), progress);
            if (interactive)
                Console.Error.WriteLine();
            set.Save(output);

            log.WriteLine($"Reference set '{set.Name}': {set.Files.Count:N0} files, {set.TotalBytes:N0} bytes, {set.BlockSize} byte blocks, " +
                          $"{set.Types.Count} learned file types, in {sw.Elapsed.TotalSeconds:F1}s -> {output} ({new FileInfo(output).Length:N0} bytes)");
            foreach (var t in set.Types)
                log.WriteLine($"  type {t.Extension}: starts with \"{MagicText(t.Magic)}\" ({t.Files} files, largest {t.MaxSize:N0} bytes)");
            return 0;
        }

        private static string MagicText(uint magic)
        {
            return new string(new[] { (char)(magic >> 24), (char)((magic >> 16) & 0xFF), (char)((magic >> 8) & 0xFF), (char)(magic & 0xFF) });
        }

        private static List<ReferenceSet> LoadReferenceSets(Options opts, TextWriter log)
        {
            var paths = (opts.Get("ref", null) ?? throw new ArgumentException("--ref <set.json> is required."))
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var sets = new List<ReferenceSet>();
            foreach (var path in paths)
            {
                var set = ReferenceSet.Load(path.Trim());
                log.WriteLine($"Reference set '{set.Name}': {set.Files.Count:N0} files, {set.TotalBytes:N0} bytes");
                sets.Add(set);
            }
            return sets;
        }

        private static int Match(Options opts, TextWriter log)
        {
            var sets = LoadReferenceSets(opts, log);
            using (var session = Open(opts))
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                var p = session.GetPartition(opts.Positional(1, "partition"));
                var matcher = new ReferenceMatcher(p.Volume, sets)
                {
                    FreeSpaceOnly = !opts.Has("all-clusters"),
                    MinPercent = double.Parse(opts.Get("min-percent", "20"), System.Globalization.CultureInfo.InvariantCulture),
                };

                bool interactive = !Console.IsErrorRedirected;
                var progress = new SyncProgress<int>(percent =>
                {
                    if (interactive)
                        Console.Error.Write($"\rSearching {p.Name}: {percent}%   ");
                });

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var matches = matcher.Analyze(cts.Token, progress);
                if (interactive)
                    Console.Error.WriteLine();

                var summary = ReferenceReport.Summarize(matches, sets);
                foreach (var m in matches.Take(opts.Has("quiet") ? 0 : 200))
                    log.WriteLine("  " + m);
                if (matches.Count > 200 && !opts.Has("quiet"))
                    log.WriteLine($"  ... {matches.Count - 200:N0} more (use --report for the full list)");

                log.WriteLine($"[{p.Name}] searched {matcher.ClustersScanned:N0} {(matcher.FreeSpaceOnly ? "free " : "")}clusters in {sw.Elapsed.TotalSeconds:F1}s, " +
                              $"{matcher.ClustersHit:N0} hold reference data.");
                foreach (var line in summary)
                    log.WriteLine(line);
                if (matcher.HitsTruncated)
                    log.WriteLine("Warning: the hit limit was reached; some copies may be missing.");

                var report = opts.Get("report", null);
                if (report != null)
                {
                    ReferenceReport.Save(report, session.ImageName, p.Volume, matches);
                    log.WriteLine($"Report: {report}");
                }

                var outDir = opts.Get("out", null);
                if (outDir != null)
                {
                    var saved = ReferenceReport.SaveAll(matcher, matches, outDir, cts.Token);
                    log.WriteLine($"Saved {saved.exact:N0} exact files and {saved.partial:N0} partial files to {outDir}.");
                }
            }
            return 0;
        }

        private static int Devices(TextWriter log)
        {
            var disks = PhysicalDiskSource.ListDisks();
            if (disks.Count == 0)
                log.WriteLine("No drives found.");
            foreach (var d in disks)
                log.WriteLine(d.ToString());
            return 0;
        }

        private static int Hash(Options opts, TextWriter log)
        {
            var path = opts.Positional(0, "image");
            var names = (opts.Get("hash", "sha1") ?? "sha1").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var hashes = names.Select(n =>
            {
                switch (n.Trim().ToLowerInvariant().Replace("-", ""))
                {
                    case "md5": return System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
                    case "sha1": return System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
                    case "sha256": return System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                    case "sha512": return System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA512);
                    default: throw new ArgumentException($"Unknown hash '{n}'.");
                }
            }).ToList();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long total = 0;
            using (var image = ImageFile.OpenRead(path))
            {
                var buffer = new byte[4 * 1024 * 1024];
                int n;
                while ((n = image.Read(buffer, 0, buffer.Length)) > 0)
                {
                    foreach (var h in hashes)
                        h.AppendData(buffer, 0, n);
                    total += n;
                }
            }

            foreach (var h in hashes)
                log.WriteLine($"{h.AlgorithmName.Name}: {BitConverter.ToString(h.GetHashAndReset()).Replace("-", "").ToLowerInvariant()}");
            log.WriteLine($"{total:N0} bytes in {sw.Elapsed.TotalSeconds:F1}s ({total / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} MB/s)");
            return 0;
        }

        private static int Dump(Options opts, TextWriter log)
        {
            var sourcePath = opts.Positional(0, "source");
            var target = opts.Positional(1, "target");

            if (File.Exists(target) && !opts.Has("resume") && !opts.Has("overwrite"))
            {
                log.WriteLine($"{target} already exists. Use --resume to continue an interrupted dump or --overwrite to replace it.");
                return 2;
            }

            var options = new ImagingOptions
            {
                Resume = opts.Has("resume"),
                Retries = int.Parse(opts.Get("retries", "2")),
                ChunkSize = int.Parse(opts.Get("chunk", "1024")) * 1024,
                Length = long.Parse(opts.Get("length", "0")),
                Hashes = (opts.Get("hash", "") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
            };

            using (var source = ImageFile.OpenSource(sourcePath))
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

                log.WriteLine($"Source: {source.Name}  {source.Length:N0} bytes  {source.Model} {source.Serial}".TrimEnd());
                log.WriteLine($"Target: {target} ({(DiskImager.IsCompressedPath(target) ? "compressed imgc" : "raw")})");

                bool interactive = !Console.IsErrorRedirected;
                var lastLine = System.Diagnostics.Stopwatch.StartNew();
                var progress = new SyncProgress<ImagingProgress>(p =>
                {
                    // Progress goes to stderr so that stdout stays clean.
                    if (!interactive && lastLine.ElapsedMilliseconds < 10000)
                        return;
                    lastLine.Restart();
                    var eta = p.Remaining.HasValue ? p.Remaining.Value.ToString(@"hh\:mm\:ss") : "--:--:--";
                    var line = $"{p.Phase}: {100.0 * p.BytesDone / Math.Max(1, p.BytesTotal),6:F2}%  " +
                               $"{p.BytesDone / 1048576.0:N0} / {p.BytesTotal / 1048576.0:N0} MB  " +
                               $"{p.BytesPerSecond / 1048576.0:F1} MB/s  ETA {eta}  unreadable {p.BadBytes:N0} bytes";
                    Console.Error.Write(interactive ? "\r" + line.PadRight(110) : line + Environment.NewLine);
                });

                var result = new DiskImager(source, target, options).Run(progress, cts.Token);
                if (interactive)
                    Console.Error.WriteLine();

                log.WriteLine(result.Completed
                    ? $"Done: {result.BytesTotal:N0} bytes in {result.Elapsed:hh\\:mm\\:ss} ({result.BytesTotal / 1048576.0 / Math.Max(0.001, result.Elapsed.TotalSeconds):F1} MB/s average), image file is {result.TargetFileSize:N0} bytes."
                    : $"Stopped at {result.BytesDone:N0} of {result.BytesTotal:N0} bytes. Run again with --resume to continue.");
                if (result.BadRanges.Count > 0)
                {
                    log.WriteLine($"Unreadable: {result.BadBytes:N0} bytes in {result.BadRanges.Count} areas (zero filled):");
                    foreach (var r in result.BadRanges.Take(50))
                        log.WriteLine($"  0x{r.Offset:X12} + 0x{r.Length:X}");
                    if (result.BadRanges.Count > 50)
                        log.WriteLine($"  ... see {target}.dump.json for the full list.");
                }
                foreach (var h in result.Hashes)
                    log.WriteLine($"{h.Key}: {h.Value}");
                return result.Completed ? 0 : 3;
            }
        }

        /// <summary>
        /// Reports on the calling thread (Progress&lt;T&gt; posts to the thread pool, which reorders lines).
        /// </summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> handler;
            public SyncProgress(Action<T> handler) { this.handler = handler; }
            public void Report(T value) { lock (this) handler(value); }
        }

        private static async Task<int> Ai(Options opts, TextWriter log)
        {
            var image = opts.Positional(0, "image");
            var request = opts.PositionalOrNull(1);
            var config = new AgentConfig
            {
                Model = opts.Get("model", AgentConfig.DefaultModel),
                Effort = opts.Get("effort", "high"),
                OutputDirectory = opts.Get("out", Path.Combine(Directory.GetCurrentDirectory(), "recovered")),
            };

            using (var session = new RecoverySession(image))
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                var agent = new RecoveryAgent(session, config);
                agent.OnText += t => log.WriteLine(t);
                agent.OnToolCall += (name, input) => log.WriteLine($"  > {name} {input}");
                agent.OnToolResult += (name, summary) => log.WriteLine($"    {summary}");

                if (request != null)
                {
                    await agent.RunAsync(request, cts.Token);
                    log.WriteLine($"[tokens in={agent.InputTokens:N0} out={agent.OutputTokens:N0}]");
                    return 0;
                }

                log.WriteLine("Claude recovery assistant. Type a request (empty line to quit).");
                while (true)
                {
                    log.Write("> ");
                    var line = Console.In.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        break;
                    await agent.RunAsync(line, cts.Token);
                }
                log.WriteLine($"[tokens in={agent.InputTokens:N0} out={agent.OutputTokens:N0}]");
            }
            return 0;
        }
    }

    internal static class Glob
    {
        public static bool IsMatch(string name, string pattern)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
        }
    }

    internal class Options
    {
        private readonly List<string> positional = new List<string>();
        private readonly Dictionary<string, string> named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "deleted", "analyze", "carve", "free-only", "verify", "deleted-only", "carved", "quiet", "resume", "overwrite", "all-clusters"
        };

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    var key = args[i].Substring(2);
                    if (Flags.Contains(key) || i + 1 >= args.Length)
                        o.named[key] = "true";
                    else
                        o.named[key] = args[++i];
                }
                else
                {
                    o.positional.Add(args[i]);
                }
            }
            return o;
        }

        public bool Has(string key) => named.ContainsKey(key);
        public string Get(string key, string fallback) => named.TryGetValue(key, out var v) ? v : fallback;
        public string PositionalOrNull(int i) => i < positional.Count ? positional[i] : null;
        public string Positional(int i, string what) => PositionalOrNull(i) ?? throw new ArgumentException($"Missing <{what}> argument.");
    }
}
