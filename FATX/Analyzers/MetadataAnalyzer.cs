using FATX.FileSystem;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FATX.Analyzers
{
    public class MetadataAnalyzer
    {
        private Volume _volume;
        private long _interval;
        private long _length;

        private int _currentYear;

        private List<DirectoryEntry> _dirents = new List<DirectoryEntry>();

        private const string VALID_CHARS = "abcdefghijklmnopqrstuvwxyz" +
                                           "ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
                                           "0123456789" +
                                           "!#$%&\'()-.@[]^_`{}~ " +
                                           "\xff";

        /// <summary>
        /// Number of clusters read per I/O request while scanning.
        /// </summary>
        private const int ClustersPerRead = 64;

        public MetadataAnalyzer(Volume volume, long interval, long length)
        {
            if (length == 0 || length > volume.FileAreaLength)
            {
                length = volume.FileAreaLength;
            }

            // Directory entries are always cluster aligned, any other
            // interval would make the cluster math below wrong.
            if (interval <= 0 || interval != volume.BytesPerCluster)
            {
                interval = volume.BytesPerCluster;
            }

            _volume = volume;
            _interval = interval;
            _length = length;

            // Allow one year of slack for consoles whose clock was set ahead.
            _currentYear = DateTime.Now.Year + 1;
        }

        /// <summary>
        /// Number of clusters that will be scanned. Use this as the progress maximum.
        /// </summary>
        public long ClusterCount
        {
            get { return Math.Min(_length / _interval, _volume.ClusterCount); }
        }

        public List<DirectoryEntry> Analyze(CancellationToken cancellationToken, IProgress<int> progress)
        {
            var sw = new Stopwatch();
            sw.Start();
            RecoverMetadata(cancellationToken, progress);
            sw.Stop();
            Console.WriteLine($"Execution Time: {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"Found {_dirents.Count} dirents.");

            return _dirents;
        }

        /// <summary>
        /// Searches for dirent's.
        /// </summary>
        private void RecoverMetadata(CancellationToken cancellationToken, IProgress<int> progress)
        {
            // Valid cluster indexes are 1..maxClusters (inclusive). The old
            // loop used '<' and never scanned the last cluster.
            var maxClusters = ClusterCount;
            var direntsPerCluster = _volume.DirentsPerCluster;
            var bytesPerCluster = (int)_volume.BytesPerCluster;
            long lastReport = 0;

            for (long first = 1; first <= maxClusters; first += ClustersPerRead)
            {
                var count = (int)Math.Min(ClustersPerRead, maxClusters - first + 1);
                var data = _volume.ReadClusters((uint)first, count);

                for (int c = 0; c < count; c++)
                {
                    var cluster = (uint)(first + c);
                    var clusterBase = c * bytesPerCluster;

                    for (int i = 0; i < direntsPerCluster; i++)
                    {
                        var direntOffset = i * 0x40;
                        var bufferOffset = clusterBase + direntOffset;

                        // Cheap pre-filter before allocating a DirectoryEntry.
                        if (!IsValidFileNameLength(data[bufferOffset]))
                            continue;

                        try
                        {
                            DirectoryEntry dirent = new DirectoryEntry(_volume.Platform, data, bufferOffset);

                            if (IsValidDirent(dirent))
                            {
                                dirent.Cluster = cluster;
                                dirent.Offset = _volume.ClusterToPhysicalOffset(cluster) + direntOffset;
                                _dirents.Add(dirent);
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                            Console.WriteLine(e.StackTrace);
                        }
                    }
                }

                var done = first + count - 1;
                if (done - lastReport >= 0x100)
                {
                    progress?.Report((int)done);
                    lastReport = done;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // NOTE: even though this part of the analyzer returns,
                    //   it will continue on with the linking step to clean
                    //   up the results while keeping the progress bar
                    //   running to show that we are still working.
                    break;
                }
            }

            progress?.Report((int)maxClusters);
        }

        /// <summary>
        /// Dump a directory to path.
        /// </summary>
        /// <param name="dirent"></param>
        /// <param name="path"></param>
        private void DumpDirectory(DirectoryEntry dirent, string path)
        {
            path = path + "/" + dirent.FileName;
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            foreach (DirectoryEntry child in dirent.Children)
            {
                Dump(child, path);
            }

            Directory.SetCreationTime(path, dirent.CreationTime.AsDateTime());
            Directory.SetLastWriteTime(path, dirent.LastWriteTime.AsDateTime());
            Directory.SetLastAccessTime(path, dirent.LastAccessTime.AsDateTime());
        }

        /// <summary>
        /// Dump a file to path.
        /// </summary>
        /// <param name="dirent"></param>
        /// <param name="path"></param>
        private void DumpFile(DirectoryEntry dirent, string path)
        {
            path = path + "/" + dirent.FileName;
            const int bufsize = 0x100000;
            var remains = dirent.FileSize;
            _volume.SeekToCluster(dirent.FirstCluster);

            using (FileStream file = new FileStream(path, FileMode.Create))
            {
                while (remains > 0)
                {
                    var read = Math.Min(remains, bufsize);
                    remains -= read;
                    byte[] buf = new byte[read];
                    _volume.GetReader().Read(buf, (int)read);
                    file.Write(buf, 0, (int)read);
                }
            }

            File.SetCreationTime(path, dirent.CreationTime.AsDateTime());
            File.SetLastWriteTime(path, dirent.LastWriteTime.AsDateTime());
            File.SetLastAccessTime(path, dirent.LastAccessTime.AsDateTime());
        }

        /// <summary>
        /// Dumps a DirectoryEntry to path.
        /// </summary>
        /// <param name="dirent"></param>
        /// <param name="path"></param>
        public void Dump(DirectoryEntry dirent, string path)
        {
            if (dirent.IsDirectory())
            {
                DumpDirectory(dirent, path);
            }
            else
            {
                DumpFile(dirent, path);
            }
        }

        public List<DirectoryEntry> GetDirents()
        {
            return _dirents;
        }

        public Volume GetVolume()
        {
            return _volume;
        }

        /// <summary>
        /// Validate FileNameBytes.
        /// </summary>
        /// <param name="bytes">The 42 file name bytes.</param>
        /// <param name="fileNameLength">The file name length byte (0xE5 when deleted).</param>
        private bool IsValidFileNameBytes(byte[] bytes, uint fileNameLength)
        {
            int nameLength;
            if (fileNameLength == Constants.DirentDeleted)
            {
                // Deleted entries lose their length byte. The name runs up
                // to the 0xFF padding.
                // to the 0xFF padding (some Xbox 360 kernels pad with 0x00).
                nameLength = Array.IndexOf(bytes, (byte)0xff);
                if (nameLength == -1)
                    nameLength = bytes.Length;
                var nul = Array.IndexOf(bytes, (byte)0x00, 0, nameLength);
                if (nul != -1)
                    nameLength = nul;
            }
            else
            {
                nameLength = (int)fileNameLength;
            }

            if (nameLength == 0)
            {
                return false;
            }

            // Every character of the name itself must be valid (and not padding).
            for (int i = 0; i < nameLength; i++)
            {
                if (bytes[i] == 0xff || VALID_CHARS.IndexOf((char)bytes[i]) == -1)
                {
                    return false;
                }
            }

            // Names consisting only of dots or spaces are not valid file names.
            bool hasNameCharacter = false;
            for (int i = 0; i < nameLength; i++)
            {
                if (bytes[i] != (byte)'.' && bytes[i] != (byte)' ')
                {
                    hasNameCharacter = true;
                    break;
                }
            }
            if (!hasNameCharacter)
            {
                return false;
            }

            // The rest is normally 0xFF padding, but may contain leftovers of
            // a previous (longer) name after a rename. Anything else means
            // this is not a directory entry.
            for (int i = nameLength; i < bytes.Length; i++)
            {
                if (bytes[i] != 0x00 && VALID_CHARS.IndexOf((char)bytes[i]) == -1)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// ReadOnly | Hidden | System | Directory | Archive
        /// </summary>
        private const int ValidAttributes = 0x37;

        /// <summary>
        /// Validate FileAttributes.
        /// </summary>
        /// <param name="attributes"></param>
        /// <returns></returns>
        private bool IsValidAttributes(FileAttribute attributes)
        {
            // NOTE: The attributes are flags. Enum.IsDefined() rejected any
            // combination (e.g. Directory|Archive or Hidden|System), which
            // caused real deleted entries to be skipped.
            return ((int)attributes & ~ValidAttributes) == 0;
        }

        private int[] MaxDays =
        {
              31, // Jan
              29, // Feb
              31, // Mar
              30, // Apr
              31, // May
              30, // Jun
              31, // Jul
              31, // Aug
              30, // Sep
              31, // Oct
              30, // Nov
              31  // Dec
        };

        /// <summary>
        /// Validate a TimeStamp.
        /// </summary>
        /// <param name="dateTime"></param>
        /// <returns></returns>
        private bool IsValidDateTime(TimeStamp dateTime)
        {
            if (dateTime == null)
            {
                return false;
            }

            // TODO: create settings to customize these specifics
            if (dateTime.Year > _currentYear)
            {
                return false;
            }

            if (dateTime.Month > 12 || dateTime.Month < 1)
            {
                return false;
            }

            if (dateTime.Day > MaxDays[dateTime.Month - 1] || dateTime.Day < 1)
            {
                return false;
            }

            if (dateTime.Hour > 23 || dateTime.Hour < 0)
            {
                return false;
            }

            if (dateTime.Minute > 59 || dateTime.Minute < 0)
            {
                return false;
            }

            if (dateTime.Second > 59 || dateTime.Second < 0)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validate FirstCluster.
        /// </summary>
        /// <param name="firstCluster"></param>
        /// <returns></returns>
        private bool IsValidFirstCluster(uint firstCluster)
        {
            if (firstCluster >= _volume.MaxClusters)
            {
                return false;
            }

            // NOTE: deleted files have been found with firstCluster set to 0
            //  To be as thorough as we can, let's include those.
            //if (firstCluster == 0)
            //{
            //    return false;
            //}

            return true;
        }

        /// <summary>
        /// Validate FileNameLength.
        /// </summary>
        /// <param name="fileNameLength"></param>
        /// <returns></returns>
        private bool IsValidFileNameLength(uint fileNameLength)
        {
            if (fileNameLength == 0x00 || fileNameLength == 0xff)
            {
                return false;
            }

            if (fileNameLength > 0x2a && fileNameLength != 0xe5)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if dirent is actually a dirent.
        /// </summary>
        /// <param name="dirent"></param>
        /// <returns></returns>
        private bool IsValidDirent(DirectoryEntry dirent)
        {
            if (!IsValidFileNameLength(dirent.FileNameLength))
            {
                return false;
            }

            if (!IsValidFirstCluster(dirent.FirstCluster))
            {
                return false;
            }

            if (!IsValidFileNameBytes(dirent.FileNameBytes, dirent.FileNameLength))
            {
                return false;
            }

            // A file can never be larger than the volume it lives on.
            if (dirent.FileSize > _volume.FileAreaLength)
            {
                return false;
            }

            if (!IsValidAttributes(dirent.FileAttributes))
            {
                return false;
            }

            if (!IsValidDateTime(dirent.CreationTime) ||
                !IsValidDateTime(dirent.LastAccessTime) ||
                !IsValidDateTime(dirent.LastWriteTime))
            {
                return false;
            }

            return true;
        }
    }
}
