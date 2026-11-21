using FATX.AI;
using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.Formats;
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
                    log.WriteLine($"  {r.Format.Verification,-12} {r.SourcePath} -> {r.OutputPath}");
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
                session.RunFileCarver(p, interval, opts.Has("free-only"), opts.Has("verify"));
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
            "deleted", "analyze", "carve", "free-only", "verify", "deleted-only", "carved", "quiet"
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
