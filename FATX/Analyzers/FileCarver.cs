using FATX.Analyzers.Signatures;
using FATX.Analyzers.Signatures.Blank;
using FATX.FileSystem;
using FATX.Formats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FATX.Analyzers
{
    public enum FileCarverInterval
    {
        Byte = 0x1,
        Align = 0x10,
        Sector = 0x200,
        Page = 0x1000,
        Cluster = 0x4000,
    }

    public class FileCarver
    {
        private readonly Volume _volume;
        private readonly FileCarverInterval _interval;
        private readonly long _length;
        private List<FileSignature> _carvedFiles;

        public FileCarver(Volume volume)
            : this(volume, FileCarverInterval.Cluster, 0)
        {
        }

        public FileCarver(Volume volume, FileCarverInterval interval, long length)
        {
            // NOTE: offsets are relative to the file area, so never scan past
            // its end (previously this was set to the partition length which
            // read past the end of the partition).
            if (length <= 0 || length > volume.FileAreaLength)
            {
                length = volume.FileAreaLength;
            }

            _volume = volume;
            _interval = interval;
            _length = length;
        }

        public void LoadFromDatabase(JsonElement fileCarverList)
        {
            _carvedFiles = new List<FileSignature>();

            foreach (var file in fileCarverList.EnumerateArray())
            {
                JsonElement offsetElement;
                if (!file.TryGetProperty("Offset", out offsetElement))
                {
                    Console.WriteLine("Failed to load signature from database: Missing offset field");
                    continue;
                }

                var fileSignature = new BlankSignature(_volume, offsetElement.GetInt64());

                if (file.TryGetProperty("Name", out var nameElement))
                {
                    fileSignature.FileName = nameElement.GetString();
                }

                if (file.TryGetProperty("Size", out var sizeElement))
                {
                    fileSignature.FileSize = sizeElement.GetInt64();
                }

                _carvedFiles.Add(fileSignature);
            }
        }

        public List<FileSignature> GetCarvedFiles()
        {
            return _carvedFiles;
        }

        public Volume GetVolume()
        {
            return _volume;
        }

        /// <summary>
        /// Only scan clusters that are free in the active file allocation
        /// table (faster, finds only deleted files).
        /// </summary>
        public bool FreeSpaceOnly { get; set; }

        /// <summary>
        /// Verify embedded checksums of found files (XEX block hashes, XBE
        /// section digests). Slower since whole files are read.
        /// </summary>
        public bool VerifyFiles { get; set; }

        /// <summary>
        /// Keep files whose signature matched but whose header is damaged
        /// (e.g. partly overwritten) instead of dropping them. They are
        /// marked <see cref="CarvedFileSignature.Damaged"/> and saved up to
        /// the next found file or used cluster, so nothing that might still
        /// be recoverable by hand is lost.
        /// </summary>
        public bool KeepDamaged { get; set; } = true;

        /// <summary>
        /// Files larger than this are not re-read to test an alternative
        /// cluster layout.
        /// </summary>
        private const long MaxFragmentRetrySize = 4L * 1024 * 1024 * 1024;

        /// <summary>
        /// Upper bound for the amount saved for a file of unknown size.
        /// </summary>
        private const long MaxUnknownSize = 64L * 1024 * 1024;

        /// <summary>
        /// Bytes read per I/O request.
        /// </summary>
        private const int ChunkSize = 8 * 1024 * 1024;

        public List<FileSignature> Analyze(CancellationToken cancellationToken, IProgress<int> progress)
        {
            // NOTE: The old implementation created an instance of every
            // signature type through reflection and issued several seeks and
            // small reads for every interval (marked "too slow"). Now the area
            // is read in large chunks and only the first four bytes at each
            // interval are compared against a set of known magics; headers are
            // parsed only on a match.
            _carvedFiles = new List<FileSignature>();
            var counters = new Dictionary<string, int>();

            long interval = (long)_interval;
            var magics = new HashSet<uint>(FormatDetector.Magics);
            var bytesPerCluster = (long)_volume.BytesPerCluster;
            var chunkSize = (int)Math.Max(interval, (ChunkSize / interval) * interval);

            // Read 3 extra bytes so that a magic straddling a chunk boundary is
            // still seen (only matters for byte/16 byte intervals).
            var buffer = new byte[chunkSize + 3];
            var reader = _volume.GetReader();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            long lastReport = 0;
            long reportEvery = Math.Max(1, (0x200 * interval));

            for (long chunkStart = 0; chunkStart < _length; chunkStart += chunkSize)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                int want = (int)Math.Min(chunkSize + 3, _length - chunkStart);

                // Skip fully allocated chunks without reading them.
                if (FreeSpaceOnly && ChunkFullyAllocated(chunkStart, Math.Min(chunkSize, want), bytesPerCluster))
                {
                    ReportProgress(progress, chunkStart + chunkSize, interval, ref lastReport, reportEvery);
                    continue;
                }

                lock (reader)
                {
                    _volume.SeekFileArea(chunkStart);
                    reader.Read(buffer, want);
                }

                int scanEnd = Math.Min(chunkSize, want);
                for (int pos = 0; pos < scanEnd; pos += (int)interval)
                {
                    if (pos + 4 > want)
                        break;

                    uint magic = (uint)(buffer[pos] << 24 | buffer[pos + 1] << 16 | buffer[pos + 2] << 8 | buffer[pos + 3]);
                    if (!magics.Contains(magic))
                        continue;

                    long offset = chunkStart + pos;
                    uint cluster = (uint)(offset / bytesPerCluster) + 1;
                    bool allocated = _volume.IsClusterAllocated(cluster);
                    if (FreeSpaceOnly && allocated)
                        continue;

                    try
                    {
                        var info = FormatDetector.Identify(new VolumeAreaSource(_volume, offset), VerifyFiles);
                        if (info == null)
                            continue;
                        if (!info.Plausible && !KeepDamaged)
                            continue;

                        // A deleted file that was written around files which
                        // existed at the time is not contiguous. If its own
                        // checksums fail when read contiguously, try reading
                        // it from the free clusters only.
                        List<uint> chain = null;
                        if (VerifyFiles && info.Plausible && !allocated && offset % bytesPerCluster == 0 &&
                            info.Verification == VerificationStatus.Failed &&
                            info.FileSize.HasValue && info.FileSize.Value > bytesPerCluster && info.FileSize.Value <= MaxFragmentRetrySize)
                        {
                            var candidate = BuildFreeChain(cluster, info.FileSize.Value, bytesPerCluster);
                            if (candidate != null)
                            {
                                var alt = FormatDetector.Identify(new ClusterChainSource(_volume, candidate, info.FileSize.Value), true);
                                if (alt != null && alt.Plausible && alt.Verification == VerificationStatus.Verified)
                                {
                                    alt.Properties["Layout"] = "fragmented (rebuilt from free clusters)";
                                    // The size of a file that fails verification is an
                                    // upper bound; now the exact size is known.
                                    int used = (int)((alt.FileSize.GetValueOrDefault(info.FileSize.Value) + bytesPerCluster - 1) / bytesPerCluster);
                                    if (used > 0 && used < candidate.Count)
                                        candidate.RemoveRange(used, candidate.Count - used);
                                    info = alt;
                                    chain = candidate;
                                }
                            }
                        }

                        var signature = new CarvedFileSignature(_volume, offset, info)
                        {
                            InAllocatedSpace = allocated,
                            ClusterChain = chain,
                        };

                        var baseName = new string(info.Format.Split(' ')[0].Where(ch => char.IsLetterOrDigit(ch)).ToArray());
                        if (baseName.Length == 0)
                            baseName = "file";
                        counters.TryGetValue(baseName, out var n);
                        counters[baseName] = ++n;
                        signature.FileName = info.Plausible
                            ? info.SuggestFileName($"{baseName}_{n:D4}")
                            : $"{baseName}_{n:D4}_damaged{info.Extension}";

                        _carvedFiles.Add(signature);
                        Console.WriteLine($"Found {info} at 0x{offset:X}.");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Exception thrown at 0x{offset:X}: {e.Message}");
                    }
                }

                ReportProgress(progress, chunkStart + scanEnd, interval, ref lastReport, reportEvery);
            }

            SizeDamagedFiles(bytesPerCluster);

            // Fill up the progress bar
            progress?.Report((int)Math.Min(int.MaxValue, _length / interval));

            sw.Stop();
            Console.WriteLine($"Complete! Found {_carvedFiles.Count} files in {sw.ElapsedMilliseconds} ms.");

            return _carvedFiles;
        }

        /// <summary>
        /// The clusters a file of <paramref name="size"/> bytes starting at
        /// <paramref name="firstCluster"/> occupies when clusters that are in
        /// use are skipped. Null when that is the same as a contiguous read.
        /// </summary>
        private List<uint> BuildFreeChain(uint firstCluster, long size, long bytesPerCluster)
        {
            long needed = (size + bytesPerCluster - 1) / bytesPerCluster;
            var chain = new List<uint>((int)Math.Min(needed, 1 << 20));
            bool skipped = false;
            uint max = _volume.ClusterCount;

            for (long c = firstCluster; chain.Count < needed; c++)
            {
                if (c > max)
                    break;
                if (_volume.IsClusterAllocated((uint)c))
                {
                    skipped = true;
                    continue;
                }
                chain.Add((uint)c);
            }

            return skipped && chain.Count > 0 ? chain : null;
        }

        /// <summary>
        /// Gives files with a damaged header a size: up to the next found
        /// file, the next used cluster (for files in free space) or
        /// <see cref="MaxUnknownSize"/>.
        /// </summary>
        private void SizeDamagedFiles(long bytesPerCluster)
        {
            for (int i = 0; i < _carvedFiles.Count; i++)
            {
                var file = _carvedFiles[i] as CarvedFileSignature;
                if (file == null || !file.Damaged)
                    continue;

                long limit = Math.Min(MaxUnknownSize, _length - file.Offset);
                if (i + 1 < _carvedFiles.Count && _carvedFiles[i + 1].Offset > file.Offset)
                    limit = Math.Min(limit, _carvedFiles[i + 1].Offset - file.Offset);

                if (!file.InAllocatedSpace)
                {
                    long end = (file.Offset / bytesPerCluster + 1) * bytesPerCluster;
                    while (end - file.Offset < limit && !_volume.IsClusterAllocated((uint)(end / bytesPerCluster) + 1))
                        end += bytesPerCluster;
                    limit = Math.Min(limit, end - file.Offset);
                }

                file.FileSize = Math.Max(0, limit);
                file.Info.FileSize = file.FileSize;
                file.Info.FileSizeExact = false;
                file.Info.Warnings.Add("Header is damaged; the size is a guess (up to the next file or used cluster).");
            }
        }

        private bool ChunkFullyAllocated(long start, long length, long bytesPerCluster)
        {
            uint first = (uint)(start / bytesPerCluster) + 1;
            uint last = (uint)((start + length - 1) / bytesPerCluster) + 1;
            for (uint c = first; c <= last; c++)
            {
                if (!_volume.IsClusterAllocated(c))
                    return false;
            }
            return true;
        }

        private static void ReportProgress(IProgress<int> progress, long position, long interval, ref long lastReport, long reportEvery)
        {
            if (position - lastReport >= reportEvery)
            {
                progress?.Report((int)Math.Min(int.MaxValue, position / interval));
                lastReport = position;
            }
        }
    }
}
