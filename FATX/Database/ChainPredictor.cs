using FATX.FileSystem;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FATXTools.Database
{
    /// <summary>
    /// Predicts the cluster chain of a deleted file. FATX zeroes a file's FAT
    /// chain on deletion, leaving only its first cluster and size.
    /// </summary>
    public class ChainPredictor
    {
        private struct Extent
        {
            public uint Start;
            public uint End;          // inclusive
            public DateTime Created;  // creation time of the owner
            public object Owner;
        }

        private readonly Volume volume;
        private readonly List<Extent> pending = new List<Extent>();
        private Extent[] extents = new Extent[0];

        public ChainPredictor(Volume volume)
        {
            this.volume = volume;
        }

        /// <summary>
        /// Registers the clusters of an active file. <paramref name="owner"/>
        /// identifies it so it can be excluded from its own prediction.
        /// </summary>
        public void AddActiveChain(List<uint> chain, DateTime created, object owner)
        {
            if (chain == null || chain.Count == 0)
                return;

            uint start = chain[0];
            uint prev = start;
            for (int i = 1; i < chain.Count; i++)
            {
                var c = chain[i];
                if (c == prev + 1)
                {
                    prev = c;
                    continue;
                }

                pending.Add(new Extent { Start = start, End = prev, Created = created, Owner = owner });
                start = prev = c;
            }
            pending.Add(new Extent { Start = start, End = prev, Created = created, Owner = owner });
        }

        /// <summary>
        /// Finishes registration. The root directory (created at format time) is added automatically.
        /// </summary>
        public void Seal()
        {
            var rootChain = volume.GetClusterChain(volume.RootDirFirstCluster);
            if (rootChain != null)
                AddActiveChain(rootChain, DateTime.MinValue, null);

            extents = pending.OrderBy(e => e.Start).ToArray();
            pending.Clear();
        }

        private bool TryFind(uint cluster, object exclude, out uint extentEnd, out DateTime created)
        {
            int lo = 0, hi = extents.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (extents[mid].Start <= cluster)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            // hi is the last extent starting at or before the cluster. Extents
            // may overlap on corrupted file systems, so look back a little.
            for (int i = hi; i >= 0 && i > hi - 8; i--)
            {
                var e = extents[i];
                if (e.Start <= cluster && cluster <= e.End && (exclude == null || !ReferenceEquals(e.Owner, exclude)))
                {
                    extentEnd = e.End;
                    created = e.Created;
                    return true;
                }
            }

            extentEnd = 0;
            created = default;
            return false;
        }

        /// <summary>
        /// Predicts the chain of a deleted file.
        /// </summary>
        /// <param name="exclude">Owner to ignore (used when simulating deletion of an active file).</param>
        public List<uint> Predict(uint firstCluster, long fileSize, bool isDirectory, DateTime created,
            ChainStrategy strategy, object exclude = null)
        {
            // A zeroed or out of range first cluster means that the data
            // location is unknown. The old code produced a chain starting at
            // cluster 0, which maps in front of the file area, and chains
            // running off the end of the volume which crashed the integrity
            // analyzer.
            var maxCluster = volume.ClusterCount;
            if (firstCluster == 0 || firstCluster > maxCluster)
                return new List<uint>();

            // NOTE: Directories with more than one cluster worth of entries
            // would have multiple clusters.
            if (isDirectory)
                return new List<uint> { firstCluster };

            long bytesPerCluster = volume.BytesPerCluster;
            long clusterCount = Math.Max(1, (fileSize + bytesPerCluster - 1) / bytesPerCluster);

            var chain = new List<uint>((int)Math.Min(clusterCount, 1 << 20));
            chain.Add(firstCluster);

            long cluster = (long)firstCluster + 1;
            while (chain.Count < clusterCount && cluster <= maxCluster)
            {
                if (strategy == ChainStrategy.SkipOlderAllocated &&
                    TryFind((uint)cluster, exclude, out var extentEnd, out var ownerCreated) &&
                    ownerCreated <= created)
                {
                    // This run belonged to a file that existed before the
                    // deleted file was created, so the allocator could not
                    // have handed it out. Jump over it.
                    cluster = (long)extentEnd + 1;
                    continue;
                }

                chain.Add((uint)cluster);
                cluster++;
            }

            return chain;
        }
    }
}
