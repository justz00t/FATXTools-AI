using FATX.FileSystem;
using FATX.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FATX.Reference
{
    /// <summary>
    /// A copy (or an older version) of a reference file found on a volume.
    /// </summary>
    public class ReferenceMatch
    {
        public ReferenceSet Set { get; set; }
        public ReferenceFile File { get; set; }
        internal int FileIndex { get; set; }

        /// <summary>
        /// The cluster holding each cluster-sized piece of the file, in file
        /// order. 0 where the location is unknown.
        /// </summary>
        public List<uint> Chain { get; set; }

        /// <summary>Clusters whose content is identical to the reference file.</summary>
        public int ClustersMatched { get; set; }

        /// <summary>Clusters of the file that can be told apart (not empty or repeated filler).</summary>
        public int ClustersComparable { get; set; }

        public int ClustersTotal => Chain.Count;

        /// <summary>
        /// The whole file was read back from <see cref="Chain"/> and its
        /// SHA-1 equals the reference file's: a byte-exact copy.
        /// </summary>
        public bool Exact { get; set; }

        /// <summary>Whether the file is in more than one piece on disk.</summary>
        public bool Fragmented { get; set; }

        public uint FirstCluster => Chain.FirstOrDefault(c => c != 0);

        public double Percent => ClustersComparable == 0 ? (Exact ? 100 : 0) : 100.0 * ClustersMatched / ClustersComparable;

        public override string ToString()
        {
            return Exact
                ? $"exact    {File.Path} ({File.Size:N0} bytes) at cluster {FirstCluster}{(Fragmented ? ", fragmented" : "")}"
                : $"{Percent,5:F1}%   {File.Path} ({ClustersMatched} of {ClustersComparable} clusters identical) at cluster {FirstCluster}";
        }
    }

    /// <summary>
    /// Finds the files of reference sets on a FATX volume by content: every
    /// cluster is fingerprinted and looked up, hits are joined into files
    /// (also across fragments), and complete files are confirmed with the
    /// SHA-1 of the whole file. Files that only partly match are reported
    /// too; those are typically other versions of the reference file.
    /// </summary>
    public class ReferenceMatcher
    {
        private struct BlockRef
        {
            public int File;
            public int Block;
            public int Next;    // next entry with the same fingerprint, -1 at the end
        }

        private struct Hit
        {
            public int File;
            public int Cluster;     // index of the cluster within the file
            public uint DiskCluster;
        }

        private const int ChunkSize = 8 * 1024 * 1024;

        /// <summary>
        /// A fingerprint shared by more reference blocks than this is filler
        /// (padding, repeated patterns) and says nothing about which file a
        /// cluster belongs to.
        /// </summary>
        private const int MaxShared = 16;

        /// <summary>
        /// Safety limit for pathological volumes (one pattern repeated millions of times).
        /// </summary>
        private const int MaxHits = 40000000;

        private readonly Volume volume;
        private readonly List<ReferenceSet> sets;
        private readonly List<ReferenceFile> files = new List<ReferenceFile>();
        private readonly List<ReferenceSet> fileSets = new List<ReferenceSet>();
        private readonly int blockSize;
        private readonly int blocksPerCluster;
        private readonly int bytesPerCluster;

        private readonly Dictionary<ulong, int> index = new Dictionary<ulong, int>();
        private readonly List<BlockRef> refs = new List<BlockRef>();
        private readonly Dictionary<ulong, List<int>> headIndex = new Dictionary<ulong, List<int>>();
        private bool[][] comparable;    // per file, per file cluster
        private int[] comparableCount;

        // Identical files (the same file in several builds) are searched for
        // once; every match is reported for each of them.
        private int[] canonical;
        private readonly Dictionary<int, List<int>> aliases = new Dictionary<int, List<int>>();

        public ReferenceMatcher(Volume volume, IEnumerable<ReferenceSet> referenceSets)
        {
            this.volume = volume;
            sets = referenceSets.ToList();
            if (sets.Count == 0)
                throw new ArgumentException("No reference set given.");

            blockSize = sets[0].BlockSize;
            if (sets.Any(s => s.BlockSize != blockSize))
                throw new ArgumentException("The reference sets use different block sizes and cannot be searched together.");

            bytesPerCluster = (int)volume.BytesPerCluster;
            if (bytesPerCluster % blockSize != 0)
                throw new ArgumentException($"The reference set uses {blockSize} byte blocks, which does not divide the {bytesPerCluster} byte clusters of {volume.Name}. " +
                                            $"Rebuild it with a block size of {bytesPerCluster} or smaller.");
            blocksPerCluster = bytesPerCluster / blockSize;

            BuildIndex();
        }

        /// <summary>
        /// Only look at clusters that are free in the active file system
        /// (finds deleted files; skips the ones that still exist).
        /// </summary>
        public bool FreeSpaceOnly { get; set; } = true;

        /// <summary>
        /// Report files that are not byte-exact when at least this share of
        /// their clusters is identical to the reference file.
        /// </summary>
        public double MinPercent { get; set; } = 20;

        public int ClustersScanned { get; private set; }
        public int ClustersHit { get; private set; }

        /// <summary>
        /// The hit limit was reached; some copies may be missing from the result.
        /// </summary>
        public bool HitsTruncated { get; private set; }

        private void BuildIndex()
        {
            foreach (var set in sets)
            {
                foreach (var file in set.Files)
                {
                    files.Add(file);
                    fileSets.Add(set);
                }
            }

            ulong zero;
            using (var sha = SHA1.Create())
                zero = ReferenceSet.Fingerprint(sha, new byte[blockSize], 0, blockSize);

            canonical = new int[files.Count];
            var firstWithContent = new Dictionary<string, int>();
            for (int f = 0; f < files.Count; f++)
            {
                string key = files[f].Sha1 + ":" + files[f].Size;
                if (!firstWithContent.TryGetValue(key, out int first))
                    firstWithContent[key] = first = f;
                canonical[f] = first;
                if (first != f)
                {
                    if (!aliases.TryGetValue(first, out var others))
                        aliases[first] = others = new List<int>();
                    others.Add(f);
                }
            }

            for (int f = 0; f < files.Count; f++)
            {
                var file = files[f];
                if (canonical[f] != f)
                    continue;
                for (int b = 0; b < file.Blocks.Length; b++)
                {
                    ulong h = file.Blocks[b];
                    if (h == zero)
                        continue;
                    index.TryGetValue(h, out int next);
                    refs.Add(new BlockRef { File = f, Block = b, Next = index.ContainsKey(h) ? next : -1 });
                    index[h] = refs.Count - 1;
                }

                // Files smaller than a block are found through their first sector.
                if (file.Size >= ReferenceSet.HeadSize && file.Size < blockSize && file.Head != 0)
                {
                    if (!headIndex.TryGetValue(file.Head, out var list))
                        headIndex[file.Head] = list = new List<int>();
                    if (list.Count < MaxShared)
                        list.Add(f);
                }
            }

            // Which clusters of each file can identify it: at least one full,
            // non-empty block whose fingerprint is not filler.
            comparable = new bool[files.Count][];
            comparableCount = new int[files.Count];
            for (int f = 0; f < files.Count; f++)
            {
                var file = files[f];
                long fullBlocks = file.Size / blockSize;
                int clusters = (int)((file.Size + bytesPerCluster - 1) / bytesPerCluster);
                comparable[f] = new bool[clusters];
                for (int b = 0; b < fullBlocks && b < file.Blocks.Length; b++)
                {
                    ulong h = file.Blocks[b];
                    if (h == zero || IsFiller(h))
                        continue;
                    int c = b / blocksPerCluster;
                    if (!comparable[f][c])
                    {
                        comparable[f][c] = true;
                        comparableCount[f]++;
                    }
                }
            }
        }

        private bool IsFiller(ulong fingerprint)
        {
            if (!index.TryGetValue(fingerprint, out int i))
                return false;
            int count = 0;
            for (; i >= 0; i = refs[i].Next)
            {
                if (++count > MaxShared)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Searches the volume. Progress is reported in percent.
        /// </summary>
        public List<ReferenceMatch> Analyze(CancellationToken cancellationToken = default, IProgress<int> progress = null)
        {
            var hits = new List<Hit>();
            var headHits = new List<KeyValuePair<int, uint>>();   // file, disk cluster
            Scan(hits, headHits, cancellationToken, progress);

            var matches = new List<ReferenceMatch>();
            using (var sha = SHA1.Create())
            {
                // Group the hits by file.
                hits.Sort((a, b) => a.File != b.File ? a.File.CompareTo(b.File)
                    : a.DiskCluster != b.DiskCluster ? a.DiskCluster.CompareTo(b.DiskCluster)
                    : a.Cluster.CompareTo(b.Cluster));

                int start = 0;
                while (start < hits.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int end = start;
                    while (end < hits.Count && hits[end].File == hits[start].File)
                        end++;
                    Assemble(hits, start, end, matches, sha, cancellationToken);
                    start = end;
                }

                // Small files found through their first sector.
                var seen = new HashSet<long>(matches.Where(m => m.Exact && m.Chain.Count == 1)
                    .Select(m => ((long)m.FileIndex << 32) | m.Chain[0]));
                foreach (var hit in headHits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long key = ((long)hit.Key << 32) | hit.Value;
                    if (!seen.Add(key))
                        continue;
                    var chain = new List<uint> { hit.Value };
                    if (Verify(files[hit.Key], chain, sha))
                    {
                        matches.Add(new ReferenceMatch
                        {
                            Set = fileSets[hit.Key],
                            File = files[hit.Key],
                            FileIndex = hit.Key,
                            Chain = chain,
                            ClustersMatched = 1,
                            ClustersComparable = 1,
                            Exact = true,
                        });
                    }
                }
            }

            // Report every match for the identical files of other builds too.
            foreach (var m in matches.ToList())
            {
                if (!aliases.TryGetValue(m.FileIndex, out var others))
                    continue;
                foreach (int other in others)
                {
                    matches.Add(new ReferenceMatch
                    {
                        Set = fileSets[other],
                        File = files[other],
                        FileIndex = other,
                        Chain = m.Chain,
                        ClustersMatched = m.ClustersMatched,
                        ClustersComparable = m.ClustersComparable,
                        Exact = m.Exact,
                        Fragmented = m.Fragmented,
                    });
                }
            }

            progress?.Report(100);
            return matches
                .OrderBy(m => sets.IndexOf(m.Set))
                .ThenBy(m => m.File.Path, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(m => m.Exact)
                .ThenByDescending(m => m.Percent)
                .ToList();
        }

        // ---- pass 1: fingerprint the clusters ----

        private void Scan(List<Hit> hits, List<KeyValuePair<int, uint>> headHits, CancellationToken cancellationToken, IProgress<int> progress)
        {
            long length = volume.FileAreaLength / bytesPerCluster * bytesPerCluster;
            int chunkSize = Math.Max(bytesPerCluster, ChunkSize / bytesPerCluster * bytesPerCluster);
            var buffer = new byte[chunkSize];
            int clustersPerChunk = chunkSize / bytesPerCluster;
            var prints = new ulong[clustersPerChunk * blocksPerCluster];
            var heads = new ulong[clustersPerChunk];
            var skip = new bool[clustersPerChunk];
            var reader = volume.GetReader();
            bool wantHeads = headIndex.Count > 0;
            int lastPercent = -1;

            for (long chunkStart = 0; chunkStart < length; chunkStart += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int want = (int)Math.Min(chunkSize, length - chunkStart);
                int clusters = want / bytesPerCluster;
                uint firstCluster = (uint)(chunkStart / bytesPerCluster) + 1;

                bool any = false;
                for (int c = 0; c < clusters; c++)
                {
                    skip[c] = FreeSpaceOnly && volume.IsClusterAllocated(firstCluster + (uint)c);
                    any |= !skip[c];
                }

                if (any)
                {
                    lock (reader)
                    {
                        volume.SeekFileArea(chunkStart);
                        reader.Read(buffer, want);
                    }

                    Parallel.For(0, clusters, () => SHA1.Create(), (c, state, sha) =>
                    {
                        if (skip[c])
                            return sha;

                        int offset = c * bytesPerCluster;
                        if (ImgcWriter.IsZero(buffer, offset, bytesPerCluster))
                        {
                            skip[c] = true;
                            return sha;
                        }

                        for (int b = 0; b < blocksPerCluster; b++)
                            prints[c * blocksPerCluster + b] = ReferenceSet.Fingerprint(sha, buffer, offset + b * blockSize, blockSize);
                        if (wantHeads)
                            heads[c] = ReferenceSet.Fingerprint(sha, buffer, offset, ReferenceSet.HeadSize);
                        return sha;
                    }, sha => sha.Dispose());

                    for (int c = 0; c < clusters; c++)
                    {
                        if (skip[c])
                            continue;

                        ClustersScanned++;
                        uint diskCluster = firstCluster + (uint)c;
                        bool hit = false;

                        for (int b = 0; b < blocksPerCluster; b++)
                        {
                            if (!index.TryGetValue(prints[c * blocksPerCluster + b], out int i))
                                continue;

                            int count = 0;
                            int first = i;
                            for (; i >= 0; i = refs[i].Next)
                            {
                                if (++count > MaxShared)
                                    break;
                            }
                            if (count > MaxShared)
                                continue;   // filler

                            for (i = first; i >= 0; i = refs[i].Next)
                            {
                                // A file starts on a cluster boundary, so its
                                // blocks keep their place within a cluster.
                                if (refs[i].Block % blocksPerCluster != b)
                                    continue;
                                if (hits.Count < MaxHits)
                                    hits.Add(new Hit { File = refs[i].File, Cluster = refs[i].Block / blocksPerCluster, DiskCluster = diskCluster });
                                else
                                    HitsTruncated = true;
                                hit = true;
                            }
                        }

                        if (wantHeads && headIndex.TryGetValue(heads[c], out var small))
                        {
                            foreach (int f in small)
                                headHits.Add(new KeyValuePair<int, uint>(f, diskCluster));
                            hit = true;
                        }

                        if (hit)
                            ClustersHit++;
                    }
                }

                int percent = (int)(95 * (chunkStart + want) / Math.Max(1, length));
                if (percent != lastPercent)
                {
                    progress?.Report(percent);
                    lastPercent = percent;
                }
            }
        }

        // ---- pass 2: join the hits of one file into copies of it ----

        private sealed class Segment
        {
            public long Diagonal;       // disk cluster - file cluster: constant along a contiguous piece
            public int First;           // first and last file cluster of the piece
            public int Last;
            public HashSet<int> Clusters = new HashSet<int>();
            public bool Used;
        }

        private void Assemble(List<Hit> hits, int start, int end, List<ReferenceMatch> matches, SHA1 sha, CancellationToken cancellationToken)
        {
            int f = hits[start].File;
            var file = files[f];
            int total = comparable[f].Length;
            if (total == 0)
                return;

            var byDiagonal = new Dictionary<long, Segment>();
            for (int i = start; i < end; i++)
            {
                long d = (long)hits[i].DiskCluster - hits[i].Cluster;
                if (!byDiagonal.TryGetValue(d, out var s))
                    byDiagonal[d] = s = new Segment { Diagonal = d, First = hits[i].Cluster, Last = hits[i].Cluster };
                s.First = Math.Min(s.First, hits[i].Cluster);
                s.Last = Math.Max(s.Last, hits[i].Cluster);
                s.Clusters.Add(hits[i].Cluster);
            }

            var segments = byDiagonal.Values.OrderBy(s => s.First).ThenBy(s => s.Diagonal).ToList();
            uint maxCluster = volume.ClusterCount;

            foreach (var head in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (head.Used)
                    continue;

                // Follow the file from piece to piece. A fragmented file
                // continues further along the disk, i.e. on a larger diagonal.
                var pieces = new List<Segment> { head };
                head.Used = true;
                var current = head;
                while (current.Last + 1 < total)
                {
                    Segment next = null;
                    foreach (var s in segments)
                    {
                        if (s.Used || s.Diagonal <= current.Diagonal || s.First <= current.Last)
                            continue;
                        if (next == null || s.First < next.First || (s.First == next.First && s.Diagonal < next.Diagonal))
                            next = s;
                    }
                    if (next == null)
                        break;
                    next.Used = true;
                    pieces.Add(next);
                    current = next;
                }

                // Lay out the chain. Between and around the pieces the file
                // is assumed to continue contiguously.
                var chain = new List<uint>(total);
                int piece = 0;
                for (int c = 0; c < total; c++)
                {
                    while (piece + 1 < pieces.Count && c >= pieces[piece + 1].First)
                        piece++;
                    long disk = pieces[piece].Diagonal + c;
                    chain.Add(disk >= 1 && disk <= maxCluster ? (uint)disk : 0);
                }

                int matched = 0;
                foreach (var s in pieces)
                    matched += s.Clusters.Count(c => comparable[f][c]);
                int anyMatched = pieces.Sum(s => s.Clusters.Count);

                var match = new ReferenceMatch
                {
                    Set = fileSets[f],
                    File = file,
                    FileIndex = f,
                    Chain = chain,
                    ClustersMatched = matched,
                    ClustersComparable = comparableCount[f],
                    Fragmented = pieces.Count > 1,
                };

                // Everything that can be compared is identical: confirm it
                // with the hash of the whole file.
                bool complete = comparableCount[f] > 0 ? matched >= comparableCount[f] : anyMatched > 0;
                if (complete && !chain.Contains(0u) && Verify(file, chain, sha))
                {
                    match.Exact = true;
                    matches.Add(match);
                }
                else if (comparableCount[f] > 0 && matched >= Math.Min(2, comparableCount[f]) && match.Percent >= MinPercent)
                {
                    matches.Add(match);
                }
            }
        }

        private bool Verify(ReferenceFile file, List<uint> chain, SHA1 sha)
        {
            sha.Initialize();
            long remaining = file.Size;
            var empty = new byte[0];

            foreach (var run in Runs(chain))
            {
                if (run.Key == 0)
                    return false;
                var data = volume.ReadClusters(run.Key, run.Value);
                int n = (int)Math.Min(data.Length, remaining);
                sha.TransformBlock(data, 0, n, null, 0);
                remaining -= n;
                if (remaining <= 0)
                    break;
            }

            if (remaining > 0)
                return false;
            sha.TransformFinalBlock(empty, 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").Equals(file.Sha1, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Splits a chain into runs of consecutive clusters (first cluster, count).
        /// </summary>
        private static IEnumerable<KeyValuePair<uint, int>> Runs(List<uint> chain)
        {
            int i = 0;
            while (i < chain.Count)
            {
                uint first = chain[i];
                int count = 1;
                if (first != 0)
                {
                    while (i + count < chain.Count && count < 256 && chain[i + count] == first + (uint)count)
                        count++;
                }
                yield return new KeyValuePair<uint, int>(first, count);
                i += count;
            }
        }

        /// <summary>
        /// Writes a found file to <paramref name="path"/>. Clusters whose
        /// location is unknown are written as zeroes.
        /// </summary>
        public long Save(ReferenceMatch match, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            long remaining = match.File.Size;

            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                foreach (var run in Runs(match.Chain))
                {
                    if (remaining <= 0)
                        break;
                    var data = run.Key == 0 ? new byte[bytesPerCluster] : volume.ReadClusters(run.Key, run.Value);
                    int n = (int)Math.Min(data.Length, remaining);
                    output.Write(data, 0, n);
                    remaining -= n;
                }
                return match.File.Size - Math.Max(0, remaining);
            }
        }
    }
}
