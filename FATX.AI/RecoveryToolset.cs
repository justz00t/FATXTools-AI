using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.Formats;
using FATX.Recovery;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace FATX.AI
{
    /// <summary>
    /// A tool the assistant can call.
    /// </summary>
    public class ToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>JSON schema "properties" object.</summary>
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        public List<string> Required { get; set; } = new List<string>();
    }

    /// <summary>
    /// Data recovery operations exposed to Claude. Independent of the API
    /// client so it can be tested and reused (e.g. by an MCP server).
    /// Results are compact JSON strings.
    /// </summary>
    public class RecoveryToolset
    {
        private readonly RecoverySession session;
        private readonly string outputRoot;
        private const int MaxRows = 200;

        public RecoveryToolset(RecoverySession session, string outputDirectory)
        {
            this.session = session;
            this.outputRoot = Path.GetFullPath(outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "recovered"));
        }

        public CancellationToken CancellationToken { get; set; }

        private static Dictionary<string, object> Str(string description) => new Dictionary<string, object> { ["type"] = "string", ["description"] = description };
        private static Dictionary<string, object> Bool(string description) => new Dictionary<string, object> { ["type"] = "boolean", ["description"] = description };
        private static Dictionary<string, object> Int(string description) => new Dictionary<string, object> { ["type"] = "integer", ["description"] = description };
        private static Dictionary<string, object> Enum(string description, params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values, ["description"] = description };
        private static Dictionary<string, object> StrArray(string description) => new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = description };

        public IReadOnlyList<ToolDefinition> Definitions { get; } = new List<ToolDefinition>
        {
            new ToolDefinition
            {
                Name = "list_partitions",
                Description = "List the partitions of the opened Xbox/Xbox 360 drive image with size, cluster size, file counts, free space and whether the metadata analyzer / file carver already ran.",
            },
            new ToolDefinition
            {
                Name = "run_metadata_analysis",
                Description = "Scan every cluster of a partition for deleted FATX directory entries and merge them into the file database, rebuilding deleted folder trees. Needed before deleted files show up in list_files/find_executables. Takes seconds to minutes depending on partition size.",
                Properties = { ["partition"] = Str("Partition name from list_partitions.") },
                Required = { "partition" },
            },
            new ToolDefinition
            {
                Name = "set_chain_strategy",
                Description = "Choose how cluster chains of deleted files are predicted (FATX erases them on delete). 'smart' (default) skips clusters owned by active files that are older than the deleted file; 'contiguous' assumes consecutive clusters. Use find_executables with verify=true to compare which gives more verified files.",
                Properties = { ["partition"] = Str("Partition name."), ["strategy"] = Enum("Chain strategy.", "smart", "contiguous") },
                Required = { "partition", "strategy" },
            },
            new ToolDefinition
            {
                Name = "list_files",
                Description = "List files in the partition's database (active and recovered). Each row has an id usable with inspect_file/recover_files. Rank: 0 active, 1 deleted no conflicts, 2 deleted with conflicts but newest, 3 partially overwritten, 4 overwritten/unknown location.",
                Properties =
                {
                    ["partition"] = Str("Partition name."),
                    ["path_prefix"] = Str("Only files whose full path starts with this (case-insensitive), e.g. '/Devkit/Builds'."),
                    ["name_pattern"] = Str("Glob on the file name, e.g. '*.xex' or 'default.*'."),
                    ["deleted_only"] = Bool("Only deleted files."),
                    ["directories_only"] = Bool("Only directories."),
                    ["offset"] = Int("Skip this many rows (paging)."),
                    ["limit"] = Int($"Maximum rows (default 100, max {MaxRows})."),
                },
                Required = { "partition" },
            },
            new ToolDefinition
            {
                Name = "find_executables",
                Description = "Find Xbox executables (XEX/XBE and patches) in the file database by reading file headers, so renamed files are found too. Returns title id, title/original name, version, computed size vs. directory size and, with verify=true, whether the data passes the executable's own SHA-1 checks (proves the recovered cluster chain is correct).",
                Properties =
                {
                    ["partition"] = Str("Partition name."),
                    ["deleted_only"] = Bool("Only deleted files (default true)."),
                    ["verify"] = Bool("Verify XEX block hashes / XBE section digests (reads whole files, slower). Default true."),
                    ["limit"] = Int("Maximum results (default 100)."),
                },
                Required = { "partition" },
            },
            new ToolDefinition
            {
                Name = "find_game_builds",
                Description = "Group executables into probable game builds: folders holding XEX/XBE files together with their companion files (PDB, MAP, media). Reports per folder the title ids, executables, file count, total size and how much is deleted. Requires run_metadata_analysis first for deleted builds.",
                Properties = { ["partition"] = Str("Partition name."), ["deleted_only"] = Bool("Only builds containing deleted executables (default false).") },
                Required = { "partition" },
            },
            new ToolDefinition
            {
                Name = "inspect_file",
                Description = "Details for one file: path, timestamps, size, rank, cluster chain summary, colliding clusters, identified format with all header properties and verification, and a hex preview of the first bytes.",
                Properties = { ["partition"] = Str("Partition name."), ["id"] = Str("File id from list_files/find_executables.") },
                Required = { "partition", "id" },
            },
            new ToolDefinition
            {
                Name = "run_file_carver",
                Description = "Scan the partition's data for known file signatures (XEX, XBE, STFS CON/LIVE/PIRS, PDB, XDBF, RIFF, XPR0, PE) to find files whose directory entries are gone. Sizes are computed from headers. Use free_space_only to skip allocated clusters (faster, only deleted data).",
                Properties =
                {
                    ["partition"] = Str("Partition name."),
                    ["free_space_only"] = Bool("Only scan unallocated clusters (default true)."),
                    ["verify"] = Bool("Verify checksums of found executables (default true)."),
                    ["interval"] = Enum("Scan granularity (default cluster; files always start on a cluster boundary in FATX unless embedded in another file).", "cluster", "page", "sector"),
                },
                Required = { "partition" },
            },
            new ToolDefinition
            {
                Name = "list_carved",
                Description = "List results of the last file carver run. Ids look like 'c:<n>' and can be passed to recover_files.",
                Properties =
                {
                    ["partition"] = Str("Partition name."),
                    ["format"] = Str("Only this format prefix, e.g. 'XEX', 'XBE', 'STFS'."),
                    ["offset"] = Int("Skip rows."),
                    ["limit"] = Int("Maximum rows (default 100)."),
                },
                Required = { "partition" },
            },
            new ToolDefinition
            {
                Name = "hex_dump",
                Description = "Hex dump of raw bytes from a partition cluster (for manual inspection).",
                Properties =
                {
                    ["partition"] = Str("Partition name."),
                    ["cluster"] = Int("Cluster index (1 based)."),
                    ["offset"] = Int("Byte offset inside the cluster (default 0)."),
                    ["length"] = Int("Bytes to dump (max 512, default 256)."),
                },
                Required = { "partition", "cluster" },
            },
            new ToolDefinition
            {
                Name = "recover_files",
                Description = "Save files to the recovery output folder. Accepts file ids (directories are saved recursively) and carved ids ('c:<n>'). Folder structure below the given subfolder is preserved. Returns written paths, sizes, verification results and errors.",
                Properties =
                {
                    ["partition"] = Str("Partition name."),
                    ["ids"] = StrArray("File ids and/or carved ids to recover."),
                    ["subfolder"] = Str("Subfolder of the output directory to write into (e.g. a title id or build name)."),
                    ["verify"] = Bool("Verify executables after writing (default true)."),
                },
                Required = { "partition", "ids" },
            },
        };

        public string Execute(string name, JsonElement input)
        {
            try
            {
                object result;
                switch (name)
                {
                    case "list_partitions": result = ListPartitions(); break;
                    case "run_metadata_analysis": result = RunMetadataAnalysis(input); break;
                    case "set_chain_strategy": result = SetChainStrategy(input); break;
                    case "list_files": result = ListFiles(input); break;
                    case "find_executables": result = FindExecutables(input); break;
                    case "find_game_builds": result = FindGameBuilds(input); break;
                    case "inspect_file": result = InspectFile(input); break;
                    case "run_file_carver": result = RunFileCarver(input); break;
                    case "list_carved": result = ListCarved(input); break;
                    case "hex_dump": result = HexDump(input); break;
                    case "recover_files": result = RecoverFiles(input); break;
                    default: throw new ArgumentException($"Unknown tool '{name}'.");
                }
                return JsonSerializer.Serialize(result, JsonOptions);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                throw new ToolException(e.Message, e);
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // ----- argument helpers -----

        private static string GetString(JsonElement e, string key, string fallback = null)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

        private static bool GetBool(JsonElement e, string key, bool fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

        private static long GetLong(JsonElement e, string key, long fallback)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : fallback;

        private PartitionState Partition(JsonElement input)
        {
            var name = GetString(input, "partition") ?? throw new ArgumentException("'partition' is required.");
            return session.GetPartition(name);
        }

        private static string Id(DatabaseFile f) => f.Offset.ToString("X");

        private static DatabaseFile FindFile(PartitionState p, string id)
        {
            if (id == null) throw new ArgumentException("'id' is required.");
            if (long.TryParse(id.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var offset))
            {
                var f = p.Database.GetFile(offset);
                if (f != null) return f;
            }
            // Allow a full path as id too.
            var byPath = p.Database.GetFiles().Values.Where(x => string.Equals(x.GetFullPath(), id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPath.Count == 1) return byPath[0];
            if (byPath.Count > 1) throw new ArgumentException($"Path '{id}' is ambiguous ({byPath.Count} files); use an id.");
            throw new ArgumentException($"No file with id '{id}'.");
        }

        private static object Row(DatabaseFile f) => new
        {
            id = Id(f),
            path = f.GetFullPath(),
            dir = f.IsDirectory() ? true : (bool?)null,
            size = f.IsDirectory() ? (long?)null : f.FileSize,
            deleted = f.IsDeleted,
            rank = f.GetRanking(),
            created = f.CreationTime.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
            modified = f.LastWriteTime.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
        };

        private static bool GlobMatch(string name, string pattern)
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
        }

        private static ClusterChainSource Source(PartitionState p, DatabaseFile f) => new ClusterChainSource(p.Volume, f.ClusterChain, f.FileSize);

        private static object Describe(FormatInfo info) => info == null ? null : new
        {
            format = info.Format,
            title_id = info.TitleIdString,
            title = info.Title,
            name = info.EmbeddedName,
            version = info.Version,
            computed_size = info.FileSize,
            size_exact = info.FileSizeExact,
            verification = info.Verification == VerificationStatus.NotChecked ? null : info.Verification.ToString(),
            verification_detail = info.VerificationDetail,
            warnings = info.Warnings.Count > 0 ? info.Warnings : null,
        };

        // ----- tools -----

        private object ListPartitions()
        {
            return session.Partitions.Select(p => p.Mounted
                ? (object)new
                {
                    name = p.Name,
                    offset = $"0x{p.Volume.Offset:X}",
                    size_mb = p.Volume.Length / (1024 * 1024),
                    cluster_size = p.Volume.BytesPerCluster,
                    platform = p.Volume.Platform.ToString(),
                    active_files = p.Volume.CountFiles(),
                    free_mb = p.Volume.GetFreeSpace() / (1024 * 1024),
                    metadata_analyzed = p.MetadataAnalyzed,
                    deleted_files = p.Database.GetFiles().Values.Count(f => f.IsDeleted),
                    carved_files = p.CarvedFiles?.Count,
                }
                : new { name = p.Name, error = p.MountError }).ToList();
        }

        private object RunMetadataAnalysis(JsonElement input)
        {
            var p = Partition(input);
            session.RunMetadataAnalyzer(p, CancellationToken);
            var deleted = p.Database.GetFiles().Values.Where(f => f.IsDeleted).ToList();
            return new
            {
                partition = p.Name,
                entries_found = p.RecoveredDirentCount,
                seconds = Math.Round(p.MetadataAnalysisTime.TotalSeconds, 1),
                deleted_files = deleted.Count(f => !f.IsDirectory()),
                deleted_directories = deleted.Count(f => f.IsDirectory()),
                by_rank = Enumerable.Range(1, 4).ToDictionary(r => r.ToString(), r => deleted.Count(f => f.GetRanking() == r)),
                deleted_by_extension = deleted.Where(f => !f.IsDirectory())
                    .GroupBy(f => Path.GetExtension(f.FileName).ToLowerInvariant())
                    .OrderByDescending(g => g.Count()).Take(20)
                    .ToDictionary(g => g.Key == "" ? "(none)" : g.Key, g => g.Count()),
            };
        }

        private object SetChainStrategy(JsonElement input)
        {
            var p = Partition(input);
            var s = GetString(input, "strategy", "smart");
            var strategy = s == "contiguous" ? ChainStrategy.Contiguous : ChainStrategy.SkipOlderAllocated;
            session.SetChainStrategy(p, strategy);
            return new { partition = p.Name, strategy = s };
        }

        private object ListFiles(JsonElement input)
        {
            var p = Partition(input);
            var prefix = GetString(input, "path_prefix");
            var pattern = GetString(input, "name_pattern");
            bool deletedOnly = GetBool(input, "deleted_only", false);
            bool dirsOnly = GetBool(input, "directories_only", false);
            int offset = (int)GetLong(input, "offset", 0);
            int limit = (int)Math.Min(MaxRows, Math.Max(1, GetLong(input, "limit", 100)));

            var q = p.Database.GetFiles().Values.AsEnumerable();
            if (deletedOnly) q = q.Where(f => f.IsDeleted);
            if (dirsOnly) q = q.Where(f => f.IsDirectory());
            if (pattern != null) q = q.Where(f => GlobMatch(f.FileName, pattern));
            var rows = q.Select(f => (f, path: f.GetFullPath()));
            if (prefix != null) rows = rows.Where(r => r.path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var list = rows.OrderBy(r => r.path, StringComparer.OrdinalIgnoreCase).ToList();

            return new
            {
                total = list.Count,
                offset,
                rows = list.Skip(offset).Take(limit).Select(r => Row(r.f)).ToList(),
                truncated = list.Count > offset + limit ? true : (bool?)null,
            };
        }

        private static readonly Regex ExecutableName = new Regex(@"\.(xex|xexp|xbe)$", RegexOptions.IgnoreCase);

        private IEnumerable<(DatabaseFile file, FormatInfo info)> Executables(PartitionState p, bool deletedOnly, bool verify)
        {
            foreach (var f in p.Database.GetFiles().Values)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (f.IsDirectory() || f.FileSize < 0x180 || (deletedOnly && !f.IsDeleted) || f.ClusterChain == null || f.ClusterChain.Count == 0)
                    continue;

                var src = Source(p, f);
                var magic = src.ReadBytes(0, 4);
                if (magic.Length < 4 || magic[0] != 'X' || magic[1] != 'E' || !(magic[2] == 'X' || magic[2] == 'B'))
                {
                    // Name says executable but header does not match: data overwritten or chain wrong.
                    if (ExecutableName.IsMatch(f.FileName))
                    {
                        var bad = new FormatInfo { Format = "unrecognized", Verification = verify ? VerificationStatus.Failed : VerificationStatus.NotChecked };
                        bad.Warnings.Add("Header does not match an executable: data overwritten or wrong cluster chain.");
                        yield return (f, bad);
                    }
                    continue;
                }

                FormatInfo info = null;
                try { info = FormatDetector.Identify(src, verify, f.FileSize); } catch (Exception) { }
                if (info != null && (info.Format.StartsWith("XEX") || info.Format == "XBE"))
                    yield return (f, info);
            }
        }

        private object FindExecutables(JsonElement input)
        {
            var p = Partition(input);
            bool deletedOnly = GetBool(input, "deleted_only", true);
            bool verify = GetBool(input, "verify", true);
            int limit = (int)Math.Min(MaxRows, Math.Max(1, GetLong(input, "limit", 100)));

            var all = Executables(p, deletedOnly, verify).ToList();
            return new
            {
                total = all.Count,
                verified = all.Count(x => x.info.Verification == VerificationStatus.Verified),
                failed = all.Count(x => x.info.Verification == VerificationStatus.Failed),
                note = p.MetadataAnalyzed ? null : "Metadata analysis has not run; only deleted entries in active folders are known.",
                results = all.OrderBy(x => x.file.GetFullPath(), StringComparer.OrdinalIgnoreCase).Take(limit).Select(x => new
                {
                    id = Id(x.file),
                    path = x.file.GetFullPath(),
                    deleted = x.file.IsDeleted,
                    rank = x.file.GetRanking(),
                    dirent_size = x.file.FileSize,
                    created = x.file.CreationTime.AsDateTime().ToString("yyyy-MM-dd HH:mm"),
                    info = Describe(x.info),
                }).ToList(),
            };
        }

        private static readonly Regex Companion = new Regex(@"\.(pdb|map|xdb|ini|xzp|xpr|xtf|bin|dat|xma|wav|xwb|xsb|xgs|ttf|cfg|xml)$", RegexOptions.IgnoreCase);

        private object FindGameBuilds(JsonElement input)
        {
            var p = Partition(input);
            bool deletedOnly = GetBool(input, "deleted_only", false);
            var exes = Executables(p, false, false).ToList();

            var builds = exes.GroupBy(x => x.file.GetParent())
                .Select(g =>
                {
                    var folder = g.Key;
                    var siblings = folder != null ? AllDescendants(folder).ToList() : new List<DatabaseFile>();
                    var files = siblings.Where(s => !s.IsDirectory()).ToList();
                    return new
                    {
                        folder_id = folder != null ? Id(folder) : null,
                        folder = folder?.GetFullPath() ?? "/",
                        folder_deleted = folder?.IsDeleted,
                        title_ids = g.Select(x => x.info.TitleIdString).Where(t => t != null).Distinct().ToList(),
                        executables = g.Select(x => new { id = Id(x.file), name = x.file.FileName, x.info.Format, deleted = x.file.IsDeleted, x.info.Version, x.info.Title, original_name = x.info.EmbeddedName }).ToList(),
                        any_deleted_executable = g.Any(x => x.file.IsDeleted),
                        file_count = files.Count,
                        deleted_files = files.Count(f => f.IsDeleted),
                        companion_files = files.Count(f => Companion.IsMatch(f.FileName)),
                        total_mb = Math.Round(files.Sum(f => (double)f.FileSize) / (1024 * 1024), 1),
                        newest = files.Count > 0 ? files.Max(f => f.LastWriteTime.AsDateTime()).ToString("yyyy-MM-dd") : null,
                    };
                })
                .Where(b => !deletedOnly || b.any_deleted_executable)
                .OrderByDescending(b => b.any_deleted_executable).ThenBy(b => b.folder)
                .Take(MaxRows)
                .ToList();

            return new { builds_found = builds.Count, builds };
        }

        private static IEnumerable<DatabaseFile> AllDescendants(DatabaseFile dir, int depth = 0)
        {
            if (depth > 64) yield break;
            foreach (var c in dir.Children)
            {
                yield return c;
                if (c.IsDirectory())
                    foreach (var d in AllDescendants(c, depth + 1))
                        yield return d;
            }
        }

        private object InspectFile(JsonElement input)
        {
            var p = Partition(input);
            var f = FindFile(p, GetString(input, "id"));
            FormatInfo info = null;
            string hex = null;
            if (!f.IsDirectory() && f.ClusterChain.Count > 0)
            {
                var src = Source(p, f);
                info = FormatDetector.Identify(src, true, f.FileSize);
                hex = HexFormat(src.ReadBytes(0, 128), 0);
            }

            var chain = f.ClusterChain ?? new List<uint>();
            int runs = chain.Count == 0 ? 0 : 1 + Enumerable.Range(1, Math.Max(0, chain.Count - 1)).Count(i => chain[i] != chain[i - 1] + 1);
            return new
            {
                row = Row(f),
                first_cluster = f.FirstCluster,
                clusters = chain.Count,
                fragments = runs,
                chain_preview = string.Join(",", chain.Take(16)) + (chain.Count > 16 ? ",..." : ""),
                custom_chain = f.HasCustomClusterChain ? true : (bool?)null,
                colliding_clusters = f.GetCollisions()?.Count ?? 0,
                colliding_with = f.GetCollisions()?.Take(32).SelectMany(c => p.Integrity.GetClusterOccupants(c) ?? new List<DatabaseFile>())
                    .Where(o => o != f).Distinct().Take(10).Select(o => new { id = Id(o), path = o.GetFullPath(), deleted = o.IsDeleted }).ToList(),
                children = f.IsDirectory() ? f.Children.Count : (int?)null,
                format = Describe(info),
                properties = info?.Properties,
                hex_preview = hex,
            };
        }

        private object RunFileCarver(JsonElement input)
        {
            var p = Partition(input);
            bool freeOnly = GetBool(input, "free_space_only", true);
            bool verify = GetBool(input, "verify", true);
            var interval = GetString(input, "interval", "cluster");
            var iv = interval == "page" ? FileCarverInterval.Page : interval == "sector" ? FileCarverInterval.Sector : FileCarverInterval.Cluster;
            session.RunFileCarver(p, iv, freeOnly, verify, CancellationToken);
            var carved = p.CarvedFiles.OfType<CarvedFileSignature>().ToList();
            return new
            {
                partition = p.Name,
                seconds = Math.Round(p.CarveTime.TotalSeconds, 1),
                found = carved.Count,
                by_format = carved.GroupBy(c => c.Info.Format).ToDictionary(g => g.Key, g => g.Count()),
                verified_executables = carved.Count(c => c.Info.Verification == VerificationStatus.Verified),
                failed_executables = carved.Count(c => c.Info.Verification == VerificationStatus.Failed),
            };
        }

        private object ListCarved(JsonElement input)
        {
            var p = Partition(input);
            if (p.CarvedFiles == null)
                throw new InvalidOperationException("The file carver has not run on this partition.");
            var format = GetString(input, "format");
            int offset = (int)GetLong(input, "offset", 0);
            int limit = (int)Math.Min(MaxRows, Math.Max(1, GetLong(input, "limit", 100)));
            var baseOffset = p.Volume.Offset + p.Volume.FileAreaByteOffset;
            var rows = p.CarvedFiles.OfType<CarvedFileSignature>()
                .Select((c, i) => (c, i))
                .Where(x => format == null || x.c.Info.Format.StartsWith(format, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new
            {
                total = rows.Count,
                rows = rows.Skip(offset).Take(limit).Select(x => new
                {
                    id = $"c:{x.i}",
                    name = x.c.FileName,
                    image_offset = $"0x{baseOffset + x.c.Offset:X}",
                    cluster = x.c.Offset / p.Volume.BytesPerCluster + 1,
                    in_allocated_space = x.c.InAllocatedSpace,
                    info = Describe(x.c.Info),
                }).ToList(),
            };
        }

        private object HexDump(JsonElement input)
        {
            var p = Partition(input);
            var cluster = GetLong(input, "cluster", 0);
            if (cluster < 1 || cluster > p.Volume.ClusterCount)
                throw new ArgumentException($"Cluster must be between 1 and {p.Volume.ClusterCount}.");
            int offset = (int)Math.Max(0, GetLong(input, "offset", 0));
            int length = (int)Math.Min(512, Math.Max(1, GetLong(input, "length", 256)));
            var data = p.Volume.ReadCluster((uint)cluster);
            offset = Math.Min(offset, data.Length);
            length = Math.Min(length, data.Length - offset);
            var slice = new byte[length];
            Buffer.BlockCopy(data, offset, slice, 0, length);
            return new
            {
                cluster,
                allocated = p.Volume.IsClusterAllocated((uint)cluster),
                occupants = p.Integrity.GetClusterOccupants((uint)cluster)?.Select(o => o.GetFullPath()).Take(5).ToList(),
                hex = HexFormat(slice, offset),
            };
        }

        private static string HexFormat(byte[] data, int baseOffset)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < data.Length; i += 16)
            {
                sb.Append((baseOffset + i).ToString("X4")).Append(": ");
                for (int j = 0; j < 16; j++)
                    sb.Append(i + j < data.Length ? data[i + j].ToString("X2") + " " : "   ");
                sb.Append(' ');
                for (int j = 0; j < 16 && i + j < data.Length; j++)
                {
                    var b = data[i + j];
                    sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private string SafeOutputDirectory(string subfolder)
        {
            var dir = outputRoot;
            if (!string.IsNullOrWhiteSpace(subfolder))
            {
                foreach (var part in subfolder.Split('/', '\\'))
                {
                    if (string.IsNullOrWhiteSpace(part) || part == "." || part == "..")
                        continue;
                    dir = Path.Combine(dir, FileRecoverer.SanitizeFileName(part));
                }
            }
            var full = Path.GetFullPath(dir);
            if (!full.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Subfolder must be inside the output directory.");
            return full;
        }

        private object RecoverFiles(JsonElement input)
        {
            var p = Partition(input);
            var outDir = SafeOutputDirectory(GetString(input, "subfolder"));
            bool verify = GetBool(input, "verify", true);
            var ids = new List<string>();
            if (input.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
                ids.AddRange(idsElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
            if (ids.Count == 0)
                throw new ArgumentException("'ids' must contain at least one id.");

            var recoverer = new FileRecoverer(p.Volume, CancellationToken) { VerifyFormats = verify };
            var errors = new List<string>();
            foreach (var id in ids)
            {
                try
                {
                    if (id.StartsWith("c:"))
                    {
                        if (p.CarvedFiles == null) throw new InvalidOperationException("The file carver has not run.");
                        var index = int.Parse(id.Substring(2));
                        if (index < 0 || index >= p.CarvedFiles.Count) throw new ArgumentException($"No carved file {id}.");
                        recoverer.Save(p.CarvedFiles[index], Path.Combine(outDir, "_carved"));
                    }
                    else
                    {
                        recoverer.Save(FindFile(p, id), outDir);
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    errors.Add($"{id}: {e.Message}");
                }
            }

            var files = recoverer.Results.Where(r => !r.IsDirectory).ToList();
            return new
            {
                output_directory = outDir,
                files_written = files.Count,
                bytes_written = files.Sum(r => r.BytesWritten),
                verified = files.Count(r => r.Format?.Verification == VerificationStatus.Verified),
                failed_verification = files.Where(r => r.Format?.Verification == VerificationStatus.Failed).Select(r => r.SourcePath).Take(50).ToList(),
                errors = errors.Concat(files.Where(r => r.Error != null).Select(r => $"{r.SourcePath}: {r.Error}")).Take(50).ToList(),
                sample = files.Take(30).Select(r => new { from = r.SourcePath, to = Path.GetRelativePath(outputRoot, r.OutputPath), bytes = r.BytesWritten, verification = r.Format?.Verification.ToString() }).ToList(),
            };
        }
    }

    public class ToolException : Exception
    {
        public ToolException(string message, Exception inner) : base(message, inner) { }
    }
}
