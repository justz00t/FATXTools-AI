using FATX.FileSystem;
using FATX.Recovery;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FATX.Reference
{
    /// <summary>
    /// Summaries, reports and bulk recovery for <see cref="ReferenceMatcher"/> results.
    /// </summary>
    public static class ReferenceReport
    {
        /// <summary>
        /// One line per reference set: how much of it was found.
        /// </summary>
        public static List<string> Summarize(List<ReferenceMatch> matches, IEnumerable<ReferenceSet> sets)
        {
            var lines = new List<string>();
            foreach (var set in sets)
            {
                var mine = matches.Where(m => m.Set == set).ToList();
                var exactFiles = new HashSet<ReferenceFile>(mine.Where(m => m.Exact).Select(m => m.File));
                var partialFiles = new HashSet<ReferenceFile>(mine.Where(m => !m.Exact).Select(m => m.File));
                long exactBytes = exactFiles.Sum(f => f.Size);
                int nonEmpty = set.Files.Count;

                lines.Add($"Reference set '{set.Name}': {exactFiles.Count:N0} of {nonEmpty:N0} files found as exact copies " +
                          $"({exactBytes:N0} of {set.TotalBytes:N0} bytes), {mine.Count(m => m.Exact):N0} exact copies in total " +
                          $"({mine.Count(m => m.Exact && m.Fragmented):N0} fragmented); " +
                          $"{mine.Count(m => !m.Exact):N0} partial matches of {partialFiles.Count:N0} files (likely other versions or damaged copies).");
            }
            return lines;
        }

        /// <summary>
        /// Writes all matches with their cluster chains as JSON.
        /// </summary>
        public static void Save(string path, string imageName, Volume volume, List<ReferenceMatch> matches)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("image", imageName ?? "");
                json.WriteString("partition", volume.Name);
                json.WriteNumber("bytesPerCluster", volume.BytesPerCluster);
                json.WriteStartArray("matches");
                foreach (var m in matches)
                {
                    json.WriteStartObject();
                    json.WriteString("set", m.Set.Name);
                    json.WriteString("path", m.File.Path);
                    json.WriteNumber("size", m.File.Size);
                    json.WriteString("sha1", m.File.Sha1);
                    json.WriteBoolean("exact", m.Exact);
                    json.WriteBoolean("fragmented", m.Fragmented);
                    json.WriteNumber("clustersMatched", m.ClustersMatched);
                    json.WriteNumber("clustersComparable", m.ClustersComparable);
                    json.WriteNumber("percent", Math.Round(m.Percent, 1));
                    json.WriteNumber("firstCluster", m.FirstCluster);
                    json.WriteString("chain", DescribeChain(m.Chain));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
        }

        /// <summary>
        /// A chain as runs: "100-163,200-210" (0 = unknown).
        /// </summary>
        public static string DescribeChain(List<uint> chain)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < chain.Count)
            {
                int j = i;
                while (j + 1 < chain.Count && chain[i] != 0 && chain[j + 1] == chain[j] + 1)
                    j++;
                parts.Add(i == j ? chain[i].ToString() : $"{chain[i]}-{chain[j]}");
                i = j + 1;
            }
            return string.Join(",", parts);
        }

        /// <summary>
        /// Saves every match: exact copies under "set name/original path"
        /// (one per file), partial matches under "set name/_partial".
        /// </summary>
        public static (int exact, int partial) SaveAll(ReferenceMatcher matcher, List<ReferenceMatch> matches, string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            int exact = 0, partial = 0;
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parts = m.File.Path.Split('/').Select(FileRecoverer.SanitizeFileName).ToArray();
                var setDir = Path.Combine(outputDirectory, FileRecoverer.SanitizeFileName(m.Set.Name));

                if (m.Exact)
                {
                    // Every exact copy is the same data; one is enough.
                    var path = Path.Combine(new[] { setDir }.Concat(parts).ToArray());
                    if (!done.Add(path))
                        continue;
                    matcher.Save(m, path);
                    exact++;
                }
                else
                {
                    var name = parts[parts.Length - 1];
                    var tagged = $"{Path.GetFileNameWithoutExtension(name)}.{m.Percent:F0}pct-cluster{m.FirstCluster}{Path.GetExtension(name)}";
                    var path = Path.Combine(new[] { setDir, "_partial" }.Concat(parts.Take(parts.Length - 1)).Concat(new[] { tagged }).ToArray());
                    matcher.Save(m, FileRecoverer.UniquePath(path));
                    partial++;
                }
            }

            return (exact, partial);
        }
    }
}
