// Compares the output of the old and new benchmark harness on the same image
// and writes a Markdown report. Recovered data is judged with the new format
// verifiers (XEX compressed block SHA-1 chain, XBE section digests), which
// give an objective "is this file intact" answer for both versions.
using FATX;
using FATX.FileSystem;
using FATX.Formats;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public static class Compare
{
    public static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: compare <image> <old.json> <new.json> <report.md>");
            return 2;
        }

        Console.SetOut(TextWriter.Null);
        var image = args[0];
        var oldDoc = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
        var newDoc = JsonDocument.Parse(File.ReadAllText(args[2])).RootElement;
        var md = new StringBuilder();

        md.AppendLine("# FATXTools benchmark: original vs. improved");
        md.AppendLine();
        md.AppendLine($"Image: `{Path.GetFileName(image)}` ({new FileInfo(image).Length / (1024.0 * 1024 * 1024):F2} GB)  ");
        md.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"Total harness time: old {Sec(oldDoc, "total_ms")}, new {Sec(newDoc, "total_ms")}. " +
                      $"Peak memory: old {oldDoc.GetProperty("peak_working_set_mb").GetInt64()} MB, new {newDoc.GetProperty("peak_working_set_mb").GetInt64()} MB.");
        md.AppendLine();

        using (var fs = FATX.Imaging.ImageFile.OpenRead(image))
        {
            var reader = new DriveReader(fs);
            reader.Initialize();

            var oldParts = oldDoc.GetProperty("partitions").EnumerateArray().ToList();
            var newParts = newDoc.GetProperty("partitions").EnumerateArray().ToList();

            // Partition detection
            md.AppendLine("## Partitions");
            md.AppendLine();
            md.AppendLine("| Partition | Old | New |");
            md.AppendLine("|---|---|---|");
            foreach (var name in newParts.Select(p => p.GetProperty("name").GetString()).Union(oldParts.Select(p => p.GetProperty("name").GetString())))
            {
                var o = oldParts.FirstOrDefault(p => p.GetProperty("name").GetString() == name);
                var n = newParts.FirstOrDefault(p => p.GetProperty("name").GetString() == name);
                md.AppendLine($"| {name} | {PartStatus(o)} | {PartStatus(n)} |");
            }
            md.AppendLine();

            foreach (var n in newParts)
            {
                var name = n.GetProperty("name").GetString();
                var o = oldParts.FirstOrDefault(p => p.GetProperty("name").GetString() == name);
                if (n.TryGetProperty("mount_error", out _))
                    continue;

                var volume = reader.Partitions.FirstOrDefault(v => v.Name == name);
                if (volume == null) continue;
                try { volume.Mount(); } catch { continue; }

                md.AppendLine($"## {name}");
                md.AppendLine();
                PerformanceTable(md, o, n);
                DeletedEntries(md, o, n);
                ExecutableIntegrity(md, volume, o, n);
                Carver(md, volume, o, n);
                ChainSimulation(md, volume);
            }
        }

        File.WriteAllText(args[3], md.ToString());
        Console.Error.WriteLine($"Report written to {args[3]}");
        return 0;
    }

    private static string Sec(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? $"{v.GetInt64() / 1000.0:F1}s" : "n/a";

    private static string PartStatus(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return "not detected";
        if (p.TryGetProperty("mount_error", out var err)) return "mount failed: " + err.GetString();
        return $"mounted, {p.GetProperty("active_files").GetInt64()} files";
    }

    private static string Cell(JsonElement p, string ms, string error)
    {
        if (p.ValueKind != JsonValueKind.Object) return "n/a";
        if (p.TryGetProperty(error, out var err)) return "**crashed** (" + err.GetString().Split(':')[0] + ")";
        return p.TryGetProperty(ms, out var v) ? $"{v.GetInt64() / 1000.0:F2}s" : "n/a";
    }

    private static string Speedup(JsonElement o, JsonElement n, string key)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(key, out var a) || !n.TryGetProperty(key, out var b)) return "";
        var ov = a.GetInt64(); var nv = b.GetInt64();
        if (nv <= 0) return ov > 0 ? "∞" : "";
        return $"{(double)ov / nv:F1}x";
    }

    private static void PerformanceTable(StringBuilder md, JsonElement o, JsonElement n)
    {
        md.AppendLine("### Speed and stability");
        md.AppendLine();
        md.AppendLine("| Step | Old | New | Speed-up |");
        md.AppendLine("|---|---|---|---|");
        md.AppendLine($"| Mount | {Cell(o, "mount_ms", "mount_error")} | {Cell(n, "mount_ms", "mount_error")} | {Speedup(o, n, "mount_ms")} |");
        md.AppendLine($"| Metadata analyzer | {Cell(o, "metadata_ms", "metadata_error")} | {Cell(n, "metadata_ms", "metadata_error")} | {Speedup(o, n, "metadata_ms")} |");
        md.AppendLine($"| Build file tree | {Cell(o, "link_ms", "link_error")} | {Cell(n, "link_ms", "link_error")} | {Speedup(o, n, "link_ms")} |");
        md.AppendLine($"| Integrity analyzer | {Cell(o, "integrity_ms", "integrity_error")} | {Cell(n, "integrity_ms", "integrity_error")} | {Speedup(o, n, "integrity_ms")} |");
        md.AppendLine($"| File carver (cluster interval) | {Cell(o, "carve_ms", "carve_error")} | {Cell(n, "carve_ms", "carve_error")} | {Speedup(o, n, "carve_ms")} |");
        md.AppendLine();
    }

    private static List<JsonElement> Deleted(JsonElement p)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("deleted", out var d) ? d.EnumerateArray().ToList() : new List<JsonElement>();

    private static void DeletedEntries(StringBuilder md, JsonElement o, JsonElement n)
    {
        var od = Deleted(o);
        var nd = Deleted(n);
        var oOffsets = new HashSet<long>(od.Select(x => x.GetProperty("offset").GetInt64()));
        var nOffsets = new HashSet<long>(nd.Select(x => x.GetProperty("offset").GetInt64()));
        var onlyNew = nd.Where(x => !oOffsets.Contains(x.GetProperty("offset").GetInt64())).ToList();
        var onlyOld = od.Where(x => !nOffsets.Contains(x.GetProperty("offset").GetInt64())).ToList();

        md.AppendLine("### Deleted entries found");
        md.AppendLine();
        md.AppendLine("| | Old | New |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Raw entries reported by metadata analyzer | {Num(o, "metadata_entries")} | {Num(n, "metadata_entries")} |");
        md.AppendLine($"| Deleted files | {od.Count(x => !x.GetProperty("dir").GetBoolean())} | {nd.Count(x => !x.GetProperty("dir").GetBoolean())} |");
        md.AppendLine($"| Deleted directories | {od.Count(x => x.GetProperty("dir").GetBoolean())} | {nd.Count(x => x.GetProperty("dir").GetBoolean())} |");
        md.AppendLine($"| Files with an empty/invalid chain (first cluster 0 or past end) | {od.Count(x => BadChain(x, n))} | {nd.Count(x => BadChain(x, n))} |");
        md.AppendLine($"| Paths with parent loops | {od.Count(x => x.GetProperty("path").GetString().EndsWith("[LOOP]"))} | {nd.Count(x => x.GetProperty("path").GetString().EndsWith("[LOOP]"))} |");
        md.AppendLine();
        if (onlyNew.Count > 0)
        {
            md.AppendLine($"Entries found only by the new version: **{onlyNew.Count}**. Examples: " +
                string.Join(", ", onlyNew.Take(15).Select(x => $"`{x.GetProperty("path").GetString()}`")));
            md.AppendLine();
        }
        if (onlyOld.Count > 0)
        {
            md.AppendLine($"Entries found only by the old version: {onlyOld.Count} (stricter validation rejects them as false positives). Examples: " +
                string.Join(", ", onlyOld.Take(10).Select(x => $"`{x.GetProperty("name").GetString()}`")));
            md.AppendLine();
        }
    }

    private static bool BadChain(JsonElement x, JsonElement newPart)
    {
        var fc = x.GetProperty("first_cluster").GetInt64();
        if (fc == 0) return true;
        foreach (var run in x.GetProperty("chain").EnumerateArray())
        {
            var start = run[0].GetInt64();
            var len = run[1].GetInt64();
            if (start == 0) return true;
            var clusterCount = newPart.GetProperty("file_area_length").GetInt64() / newPart.GetProperty("bytes_per_cluster").GetInt64();
            if (start + len - 1 > clusterCount) return true;
        }
        return false;
    }

    private static string Num(JsonElement p, string key)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) ? v.GetInt64().ToString("N0") : "n/a";

    private static List<uint> Chain(JsonElement x, Volume volume)
    {
        var chain = new List<uint>();
        foreach (var run in x.GetProperty("chain").EnumerateArray())
        {
            var start = run[0].GetInt64();
            var len = run[1].GetInt64();
            for (long i = 0; i < len; i++)
            {
                var c = start + i;
                if (c < 1 || c > volume.ClusterCount) return chain;  // unreadable beyond this point
                chain.Add((uint)c);
            }
        }
        return chain;
    }

    private class Tally
    {
        public int Executables, Verified, Failed, Unverifiable, Unrecognized;
        public HashSet<long> VerifiedOffsets = new HashSet<long>();
    }

    private static Tally VerifyDeleted(Volume volume, List<JsonElement> deleted)
    {
        var t = new Tally();
        foreach (var x in deleted)
        {
            if (x.GetProperty("dir").GetBoolean()) continue;
            var name = x.GetProperty("name").GetString();
            var size = x.GetProperty("size").GetInt64();
            if (size < 0x180) continue;
            var src = new ClusterChainSource(volume, Chain(x, volume), size);
            var magic = src.ReadBytes(0, 4);
            bool exeName = name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xbe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xexp", StringComparison.OrdinalIgnoreCase);
            bool exeMagic = magic.Length == 4 && magic[0] == 'X' && ((magic[1] == 'E' && magic[2] == 'X') || (magic[1] == 'B' && magic[2] == 'E'));
            if (!exeName && !exeMagic) continue;
            t.Executables++;
            if (!exeMagic) { t.Unrecognized++; continue; }
            FormatInfo info = null;
            try { info = FormatDetector.Identify(src, true, size); } catch { }
            switch (info?.Verification)
            {
                case VerificationStatus.Verified: t.Verified++; t.VerifiedOffsets.Add(x.GetProperty("offset").GetInt64()); break;
                case VerificationStatus.Failed: t.Failed++; break;
                default: t.Unverifiable++; break;
            }
        }
        return t;
    }

    private static void ExecutableIntegrity(StringBuilder md, Volume volume, JsonElement o, JsonElement n)
    {
        var ot = VerifyDeleted(volume, Deleted(o));
        var nt = VerifyDeleted(volume, Deleted(n));

        md.AppendLine("### Recovered deleted executables (XEX/XBE), checked with their own SHA-1 hashes");
        md.AppendLine();
        md.AppendLine("| | Old | New |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Deleted executables (by name or header) | {ot.Executables} | {nt.Executables} |");
        md.AppendLine($"| **Verified intact** | **{ot.Verified}** | **{nt.Verified}** |");
        md.AppendLine($"| Failed verification (corrupt / wrong chain) | {ot.Failed} | {nt.Failed} |");
        md.AppendLine($"| Header valid, no checksums to check | {ot.Unverifiable} | {nt.Unverifiable} |");
        md.AppendLine($"| Header missing (overwritten / wrong first cluster) | {ot.Unrecognized} | {nt.Unrecognized} |");
        md.AppendLine();
        var gained = nt.VerifiedOffsets.Count(x => !ot.VerifiedOffsets.Contains(x));
        var lost = ot.VerifiedOffsets.Count(x => !nt.VerifiedOffsets.Contains(x));
        md.AppendLine($"Executables that verify with the new version but not the old: **{gained}**; the reverse: {lost}.");
        md.AppendLine();
        if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty("repair_tried", out var rt))
        {
            md.AppendLine($"Checksum guided chain repair (new only, {n.GetProperty("repair_ms").GetInt64() / 1000.0:F1}s): of {rt.GetInt32()} deleted executables that fail with the predicted chain, " +
                          $"**{n.GetProperty("repair_fixed").GetInt32()}** verify after trying other cluster layouts.");
            var paths = n.GetProperty("repair_paths").EnumerateArray().Select(x => x.GetString()).Take(20).ToList();
            if (paths.Count > 0)
                md.AppendLine("Repaired: " + string.Join(", ", paths.Select(x => $"`{x}`")));
            md.AppendLine();
        }
    }

    private static void Carver(StringBuilder md, Volume volume, JsonElement o, JsonElement n)
    {
        List<JsonElement> Carved(JsonElement p) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("carved", out var c) ? c.EnumerateArray().ToList() : new List<JsonElement>();
        var oc = Carved(o);
        var nc = Carved(n);

        md.AppendLine("### File carver");
        md.AppendLine();
        if (n.TryGetProperty("carve_bytes", out var cb) && cb.GetInt64() < n.GetProperty("file_area_length").GetInt64())
        {
            md.AppendLine($"Scope: first {cb.GetInt64() / (1024 * 1024):N0} MB of the partition (both versions).");
            md.AppendLine();
        }

        (int exes, int verified, int zeroSize) Judge(List<JsonElement> list)
        {
            int exes = 0, verified = 0, zero = 0;
            foreach (var c in list)
            {
                var size = c.GetProperty("size").GetInt64();
                if (size == 0) zero++;
                var src = new VolumeAreaSource(volume, c.GetProperty("offset").GetInt64(), size);
                var magic = src.ReadBytes(0, 4);
                if (magic.Length < 4 || magic[0] != 'X' || !((magic[1] == 'E' && magic[2] == 'X') || (magic[1] == 'B' && magic[2] == 'E'))) continue;
                exes++;
                FormatInfo info = null;
                try { info = FormatDetector.Identify(src, true); } catch { }
                if (info?.Verification == VerificationStatus.Verified && info.FileSize == size) verified++;
            }
            return (exes, verified, zero);
        }

        var oj = Judge(oc);
        var nj = Judge(nc);
        md.AppendLine("| | Old | New |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Files found | {oc.Count} | {nc.Count} |");
        md.AppendLine($"| Executables found | {oj.exes} | {nj.exes} |");
        md.AppendLine($"| Executables saved intact with the exact size (verified) | {oj.verified} | {nj.verified} |");
        md.AppendLine($"| Files with unknown size (saved as 0 bytes) | {oj.zeroSize} | {nj.zeroSize} |");
        int damaged = nc.Count(c => c.TryGetProperty("damaged", out var d) && d.ValueKind == JsonValueKind.True);
        md.AppendLine($"| Of those: damaged headers kept for manual recovery | n/a (not reported) | {damaged} |");
        if (n.TryGetProperty("carve_verify_found", out var vf))
        {
            md.AppendLine();
            md.AppendLine($"Verifying pass over free space (new only, {n.GetProperty("carve_verify_ms").GetInt64() / 1000.0:F1}s): " +
                          $"{vf.GetInt32()} files, {n.GetProperty("carve_verify_verified").GetInt32()} verified intact, " +
                          $"{n.GetProperty("carve_verify_failed").GetInt32()} failed their checksums, " +
                          $"{n.GetProperty("carve_verify_fragmented_rebuilt").GetInt32()} fragmented executables rebuilt from free clusters.");
        }
        md.AppendLine();
        md.AppendLine("Found by type (old): " + string.Join(", ", oc.GroupBy(c => c.GetProperty("type").GetString()).Select(g => $"{g.Key} {g.Count()}")) + "  ");
        md.AppendLine("Found by type (new): " + string.Join(", ", nc.GroupBy(c => TypeOfNew(volume, c)).Select(g => $"{g.Key} {g.Count()}")));
        md.AppendLine();
    }

    private static string TypeOfNew(Volume volume, JsonElement c)
    {
        try
        {
            var info = FormatDetector.Identify(new VolumeAreaSource(volume, c.GetProperty("offset").GetInt64()), false);
            return info?.Format ?? "?";
        }
        catch { return "?"; }
    }

    /// <summary>
    /// Ground truth test on real data: pretend each active file was deleted
    /// (forget its FAT chain) and see whether the chain predictors rebuild it.
    /// </summary>
    private static void ChainSimulation(StringBuilder md, Volume volume)
    {
        var db = new FileDatabase(volume);
        var active = db.GetFiles().Values.Where(f => !f.IsDeleted && !f.IsDirectory() && f.ClusterChain != null && f.ClusterChain.Count > 0 && f.FileSize > 0).ToList();
        if (active.Count == 0) return;

        var predictor = new ChainPredictor(volume);
        foreach (var f in db.GetFiles().Values.Where(f => !f.IsDeleted))
            predictor.AddActiveChain(f.ClusterChain, f.CreationTime.AsDateTime(), f);
        predictor.Seal();

        int fragmented = 0, contigOk = 0, smartOk = 0, contigFragOk = 0, smartFragOk = 0;
        long contigBytes = 0, smartBytes = 0, totalBytes = 0;
        foreach (var f in active)
        {
            var truth = f.ClusterChain;
            bool isFrag = truth.Zip(truth.Skip(1), (a, b) => b != a + 1).Any(x => x);
            if (isFrag) fragmented++;

            var c = predictor.Predict(f.FirstCluster, f.FileSize, false, f.CreationTime.AsDateTime(), ChainStrategy.Contiguous, f);
            var s = predictor.Predict(f.FirstCluster, f.FileSize, false, f.CreationTime.AsDateTime(), ChainStrategy.SkipOlderAllocated, f);
            bool cOk = c.SequenceEqual(truth), sOk = s.SequenceEqual(truth);
            if (cOk) { contigOk++; if (isFrag) contigFragOk++; }
            if (sOk) { smartOk++; if (isFrag) smartFragOk++; }

            long bpc = volume.BytesPerCluster;
            totalBytes += f.FileSize;
            contigBytes += Math.Min(f.FileSize, c.Zip(truth, (a, b) => a == b).Count(x => x) * bpc);
            smartBytes += Math.Min(f.FileSize, s.Zip(truth, (a, b) => a == b).Count(x => x) * bpc);
        }

        md.AppendLine("### Cluster chain reconstruction, ground truth from active files");
        md.AppendLine();
        md.AppendLine("Each active file is treated as deleted (its FAT chain is ignored) and its chain is predicted from the first cluster and size alone. The real FAT chain is the answer key.");
        md.AppendLine();
        md.AppendLine("| | Old (contiguous) | New (skip older allocated) |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Files rebuilt exactly (of {active.Count}) | {contigOk} ({Pct(contigOk, active.Count)}) | {smartOk} ({Pct(smartOk, active.Count)}) |");
        md.AppendLine($"| Fragmented files rebuilt exactly (of {fragmented}) | {contigFragOk} ({Pct(contigFragOk, fragmented)}) | {smartFragOk} ({Pct(smartFragOk, fragmented)}) |");
        md.AppendLine($"| Bytes in the right place | {Pct(contigBytes, totalBytes)} | {Pct(smartBytes, totalBytes)} |");
        md.AppendLine();
    }

    private static string Pct(long a, long b) => b == 0 ? "n/a" : $"{100.0 * a / b:F1}%";
}
