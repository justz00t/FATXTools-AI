// Benchmark harness. This single source file is compiled twice: once against
// the original FATXTools sources (OLD) and once against the current ones.
// It only uses APIs that exist in both versions and mirrors what the GUI does:
// mount -> metadata analyzer -> file database -> integrity analyzer -> carver.
using FATX;
using FATX.Analyzers;
using FATX.FileSystem;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

public static class Harness
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: harness <image> <output.json> [--no-carve] [--carve-limit-mb N] [--partition name]");
            return 2;
        }

        var image = args[0];
        var output = args[1];
        bool carve = !args.Contains("--no-carve");
        long carveLimit = 0;   // bytes of each partition to carve, 0 = everything
        int ci = Array.IndexOf(args, "--carve-limit-mb");
        if (ci >= 0 && ci + 1 < args.Length) carveLimit = long.Parse(args[ci + 1]) * 1024 * 1024;
        string only = null;
        int pi = Array.IndexOf(args, "--partition");
        if (pi >= 0 && pi + 1 < args.Length) only = args[pi + 1];

        var log = Console.Error;
        // Both versions log heavily to Console.Out; that is not what we measure.
        Console.SetOut(TextWriter.Null);

#if OLD
        string version = "old";
#else
        string version = "new";
#endif
        var result = new Dictionary<string, object> { ["version"] = version, ["image"] = image };
        var partitions = new List<object>();
        result["partitions"] = partitions;

        var total = Stopwatch.StartNew();
#if OLD
        using (Stream fs = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0x10000))
#else
        // Raw images and compressed .imgc images.
        using (Stream fs = FATX.Imaging.ImageFile.OpenRead(image))
#endif
        {
            var reader = new DriveReader(fs);
            reader.Initialize();

            foreach (var volume in reader.Partitions)
            {
                if (only != null && volume.Name != only) continue;
                var p = new Dictionary<string, object> { ["name"] = volume.Name, ["offset"] = volume.Offset };
                partitions.Add(p);
                log.WriteLine($"[{version}] {volume.Name}: mounting");

                var sw = Stopwatch.StartNew();
                try
                {
                    volume.Mount();
                }
                catch (Exception e)
                {
                    p["mount_error"] = e.GetType().Name + ": " + e.Message;
                    continue;
                }
                p["mount_ms"] = sw.ElapsedMilliseconds;
                p["active_files"] = volume.CountFiles();
                p["bytes_per_cluster"] = volume.BytesPerCluster;
                p["file_area_offset"] = volume.FileAreaByteOffset;
                p["file_area_length"] = volume.FileAreaLength;

                FileDatabase db = null;
                try
                {
                    sw.Restart();
                    db = new FileDatabase(volume);
                    p["database_ms"] = sw.ElapsedMilliseconds;
                }
                catch (Exception e)
                {
                    p["database_error"] = e.GetType().Name + ": " + e.Message;
                    continue;
                }

                // Metadata analyzer
                log.WriteLine($"[{version}] {volume.Name}: metadata analyzer");
                sw.Restart();
                List<DirectoryEntry> dirents = null;
                try
                {
                    var analyzer = new MetadataAnalyzer(volume, volume.BytesPerCluster, volume.FileAreaLength);
                    dirents = analyzer.Analyze(CancellationToken.None, null);
                    p["metadata_ms"] = sw.ElapsedMilliseconds;
                    p["metadata_entries"] = dirents.Count;
                }
                catch (Exception e)
                {
                    p["metadata_error"] = e.GetType().Name + ": " + e.Message;
                }

                if (dirents != null)
                {
                    sw.Restart();
                    try
                    {
                        db.Reset();
                        foreach (var d in dirents) db.AddFile(d, true);
                        db.Update();
                        p["link_ms"] = sw.ElapsedMilliseconds;
                    }
                    catch (Exception e)
                    {
                        p["link_error"] = e.GetType().Name + ": " + e.Message;
                    }

                    sw.Restart();
                    try
                    {
                        var integrity = new IntegrityAnalyzer(volume, db);
                        integrity.Update();
                        p["integrity_ms"] = sw.ElapsedMilliseconds;
                    }
                    catch (Exception e)
                    {
                        p["integrity_error"] = e.GetType().Name + ": " + e.Message;
                    }
                }

                var deleted = new List<object>();
                foreach (var f in db.GetFiles().Values.Where(f => f.IsDeleted))
                {
                    deleted.Add(new Dictionary<string, object>
                    {
                        ["offset"] = f.Offset,
                        ["name"] = f.FileName,
                        ["path"] = PathOf(f),
                        ["dir"] = f.IsDirectory(),
                        ["size"] = f.FileSize,
                        ["first_cluster"] = f.FirstCluster,
                        ["created"] = f.CreationTime.AsInteger(),
                        ["rank"] = f.GetRanking(),
                        ["chain"] = Runs(f.ClusterChain),
                    });
                }
                p["deleted"] = deleted;

#if !OLD
                // Checksum guided chain repair (new only): deleted executables whose
                // predicted chain fails their own hashes are re-read with other layouts.
                sw.Restart();
                int repairTried = 0, repairFixed = 0;
                var repairedPaths = new List<string>();
                foreach (var f in db.GetFiles().Values.Where(f => f.IsDeleted && !f.IsDirectory() && f.FileSize >= 0x180))
                {
                    var fname = f.FileName ?? "";
                    if (!(fname.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) || fname.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase) || fname.EndsWith(".xexp", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    try
                    {
                        var finfo = FATX.Formats.FormatDetector.Identify(new FATX.Formats.ClusterChainSource(volume, f.ClusterChain, f.FileSize), true, f.FileSize);
                        if (finfo == null || finfo.Verification != FATX.Formats.VerificationStatus.Failed)
                            continue;
                        repairTried++;
                        if (FATX.Recovery.ChainRepair.TryRepair(volume, f, out _) != null)
                        {
                            repairFixed++;
                            repairedPaths.Add(PathOf(f));
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                p["repair_ms"] = sw.ElapsedMilliseconds;
                p["repair_tried"] = repairTried;
                p["repair_fixed"] = repairFixed;
                p["repair_paths"] = repairedPaths;
#endif

                if (carve)
                {
                    log.WriteLine($"[{version}] {volume.Name}: file carver");
                    sw.Restart();
                    try
                    {
                        var length = carveLimit > 0 ? Math.Min(carveLimit, volume.FileAreaLength) : volume.FileAreaLength;
                        p["carve_bytes"] = length;
                        var carver = new FileCarver(volume, FileCarverInterval.Cluster, length);
                        var files = carver.Analyze(CancellationToken.None, null);
                        p["carve_ms"] = sw.ElapsedMilliseconds;
                        p["carved"] = files.Select(c => new Dictionary<string, object>
                        {
                            ["offset"] = c.Offset,
                            ["name"] = c.FileName,
                            ["size"] = c.FileSize,
                            ["type"] = c.GetType().Name,
#if !OLD
                            ["damaged"] = (c as FATX.Analyzers.Signatures.CarvedFileSignature)?.Damaged ?? false,
#endif
                        }).ToList();
#if !OLD
                        // Second pass the original cannot do: verify what was found in
                        // free space and rebuild fragmented executables.
                        sw.Restart();
                        var verifying = new FileCarver(volume, FileCarverInterval.Cluster, length) { FreeSpaceOnly = true, VerifyFiles = true };
                        var checkedFiles = verifying.Analyze(CancellationToken.None, null).OfType<FATX.Analyzers.Signatures.CarvedFileSignature>().ToList();
                        p["carve_verify_ms"] = sw.ElapsedMilliseconds;
                        p["carve_verify_found"] = checkedFiles.Count;
                        p["carve_verify_verified"] = checkedFiles.Count(c => c.Info.Verification == FATX.Formats.VerificationStatus.Verified);
                        p["carve_verify_failed"] = checkedFiles.Count(c => c.Info.Verification == FATX.Formats.VerificationStatus.Failed);
                        p["carve_verify_fragmented_rebuilt"] = checkedFiles.Count(c => c.ClusterChain != null);
#endif
                    }
                    catch (Exception e)
                    {
                        p["carve_error"] = e.GetType().Name + ": " + e.Message;
                        p["carve_ms"] = sw.ElapsedMilliseconds;
                    }
                }

                GC.Collect();

                // Save after every partition so partial results survive an interrupted run.
                result["total_ms"] = total.ElapsedMilliseconds;
                result["peak_working_set_mb"] = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
                File.WriteAllText(output, JsonSerializer.Serialize(result));
            }
        }

        result["total_ms"] = total.ElapsedMilliseconds;
        result["peak_working_set_mb"] = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false }));
        log.WriteLine($"[{version}] done in {total.Elapsed.TotalSeconds:F1}s -> {output}");
        return 0;
    }

    private static string PathOf(DatabaseFile f)
    {
        var parts = new List<string>();
        int guard = 0;
        for (var x = f; x != null && guard < 256; x = x.GetParent(), guard++)
            parts.Add(x.FileName);
        parts.Reverse();
        return "/" + string.Join("/", parts) + (guard >= 256 ? " [LOOP]" : "");
    }

    private static List<long[]> Runs(List<uint> chain)
    {
        var runs = new List<long[]>();
        if (chain == null) return runs;
        for (int i = 0; i < chain.Count; i++)
        {
            if (runs.Count > 0 && runs[runs.Count - 1][0] + runs[runs.Count - 1][1] == chain[i])
                runs[runs.Count - 1][1]++;
            else
                runs.Add(new long[] { chain[i], 1 });
        }
        return runs;
    }
}
