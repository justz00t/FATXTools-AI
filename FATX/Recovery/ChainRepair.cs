using FATX.FileSystem;
using FATX.Formats;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FATX.Recovery
{
    /// <summary>
    /// Uses a file's own checksums to choose its cluster layout. FATX keeps
    /// only the first cluster and the size of a deleted file, so the rest of
    /// its chain is a prediction. For formats that can be verified (XEX block
    /// hashes, XBE section digests) a wrong prediction shows: when the
    /// predicted chain fails, the other plausible layouts are tried and the
    /// first one whose checksums pass is used.
    /// </summary>
    public static class ChainRepair
    {
        /// <summary>
        /// Tries the alternative layouts of a deleted file whose current
        /// chain fails verification.
        /// </summary>
        /// <param name="info">Format information of the repaired file.</param>
        /// <returns>A chain that verifies, or null when none does.</returns>
        public static List<uint> TryRepair(Volume volume, DatabaseFile file, out FormatInfo info)
        {
            info = null;
            if (file == null || file.IsDirectory() || file.FileSize == 0)
                return null;

            uint first = file.FirstCluster;
            if (first == 0 || first > volume.ClusterCount)
                return null;

            long size = file.FileSize;
            var current = file.ClusterChain ?? new List<uint>();

            foreach (var candidate in Candidates(volume, first, size))
            {
                if (candidate.Count == 0 || candidate.SequenceEqual(current))
                    continue;

                FormatInfo result;
                try
                {
                    result = FormatDetector.Identify(new ClusterChainSource(volume, candidate, size), true, size);
                }
                catch (Exception e) when (e is System.IO.IOException || e is ArgumentException || e is IndexOutOfRangeException)
                {
                    continue;
                }

                if (result != null && result.Plausible && result.Verification == VerificationStatus.Verified)
                {
                    info = result;
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<List<uint>> Candidates(Volume volume, uint first, long size)
        {
            long bytesPerCluster = volume.BytesPerCluster;
            long needed = Math.Max(1, (size + bytesPerCluster - 1) / bytesPerCluster);
            uint max = volume.ClusterCount;

            // The file was written in one piece.
            var contiguous = new List<uint>();
            for (long c = first; contiguous.Count < needed && c <= max; c++)
                contiguous.Add((uint)c);
            yield return contiguous;

            // The file was written around everything that is in use now,
            // whatever the timestamps of those files say.
            var free = new List<uint> { first };
            for (long c = (long)first + 1; free.Count < needed && c <= max; c++)
            {
                if (!volume.IsClusterAllocated((uint)c))
                    free.Add((uint)c);
            }
            yield return free;
        }
    }
}
