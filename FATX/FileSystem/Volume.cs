using System;
using System.Collections.Generic;
using System.IO;

namespace FATX.FileSystem
{

    public class Volume
    {
        private readonly DriveReader _reader;
        private readonly string _partitionName;
        private readonly long _partitionOffset;
        private readonly long _partitionLength;

        public const uint VolumeSignature = 0x58544146;

        private uint _signature;
        private uint _serialNumber;
        private uint _sectorsPerCluster;
        private uint _rootDirFirstCluster;

        private uint _bytesPerCluster;
        private uint _maxClusters;
        private uint _bytesPerFat;
        private bool _isFat16;
        private uint _fatByteOffset;
        private uint _fileAreaByteOffset;

        private bool _usesLegacyFormat;
        private uint _jump;
        private ushort _bytesPerSector;
        private ushort _reservedSectors;
        private ushort _sectorsPerTrack;
        private ushort _heads;
        private uint _hiddenSectors;
        private uint _largeSectors;
        private uint _largeSectorsPerFat;

        private List<DirectoryEntry> _root = new List<DirectoryEntry>();
        private uint[] _fileAllocationTable;
        private long _fileAreaLength;
        private Platform _platform;
        private HashSet<uint> _visitedDirectoryClusters;

        /// <summary>
        /// Creates a new FATX volume.
        /// </summary>
        /// <param name="reader">The stream that contains the FATX file system.</param>
        /// <param name="name">The name of this volume.</param>
        /// <param name="offset">The offset within the <paramref name="reader"/> this partition exists at.</param>
        /// <param name="length">The length in bytes of this partition.</param>
        /// <param name="legacy">
        /// Set this to true if this is the legacy FATX file system. 
        /// It appears to only be used in older DVT3 xbox systems with kernel 3633 or earlier.
        /// </param>
        public Volume(DriveReader reader, string name, long offset, long length, bool legacy = false)
        {
            this._reader = reader;
            this._partitionName = name;
            this._partitionLength = length;
            this._partitionOffset = offset;
            this._usesLegacyFormat = legacy;

            this._platform = (reader.ByteOrder == ByteOrder.Big) ?
                Platform.X360 : Platform.Xbox;

            Mounted = false;
        }

        public string Name
        {
            get { return _partitionName; }
        }

        public uint RootDirFirstCluster
        {
            get { return _rootDirFirstCluster; }
        }

        public long Length
        {
            get { return _partitionLength; }
        }

        public long FileAreaLength
        {
            get { return _fileAreaLength; }
        }

        public uint MaxClusters
        {
            get { return _maxClusters; }
        }

        public uint BytesPerCluster
        {
            get { return _bytesPerCluster; }
        }

        /// <summary>
        /// Number of directory entries that fit into one cluster.
        /// </summary>
        public int DirentsPerCluster
        {
            get { return (int)(_bytesPerCluster / DirectoryEntry.DirectoryEntrySize); }
        }

        /// <summary>
        /// Number of clusters in the file area (valid cluster indexes are 1..ClusterCount).
        /// </summary>
        public uint ClusterCount
        {
            get { return (uint)Math.Min(_maxClusters - 1, _fileAreaLength / Math.Max(1, _bytesPerCluster)); }
        }

        public bool IsFat16
        {
            get { return _isFat16; }
        }

        public uint SerialNumber
        {
            get { return _serialNumber; }
        }

        /// <summary>
        /// Whether a cluster is marked as in use by the active file allocation table.
        /// </summary>
        public bool IsClusterAllocated(uint cluster)
        {
            if (_fileAllocationTable == null || cluster >= _fileAllocationTable.Length)
                return false;
            return _fileAllocationTable[cluster] != Constants.ClusterAvailable;
        }

        public DriveReader GetReader()
        { return _reader; }

        public uint[] FileAllocationTable
        {
            get { return _fileAllocationTable; }
        }

        public long FileAreaByteOffset
        {
            get { return _fileAreaByteOffset; }
        }

        public long Offset
        {
            get { return _partitionOffset; }
        }

        public List<DirectoryEntry> GetRoot()
        {
            return _root;
        }

        public Platform Platform
        {
            get { return _platform; }
        }

        public bool Mounted { get; private set; }

        /// <summary>
        /// Loads the FATX file system.
        /// </summary>
        public void Mount()
        {
            Mounted = false;

            // Read and verify volume metadata.
            ReadBootSector();
            CalculateOffsets();
            ReadFileAllocationTable();

            if (_rootDirFirstCluster == 0 || _rootDirFirstCluster >= _maxClusters)
            {
                throw new FormatException($"Invalid root directory cluster for {_partitionName}: {_rootDirFirstCluster}");
            }

            _visitedDirectoryClusters = new HashSet<uint>();
            _root = new List<DirectoryEntry>();
            foreach (var cluster in GetClusterChain(_rootDirFirstCluster))
            {
                if (!_visitedDirectoryClusters.Add(cluster))
                    break;
                var stream = ReadDirectoryStream(cluster);
                _root.AddRange(stream);
                PopulateDirentStream(stream, cluster);
                if (stream.Count < DirentsPerCluster)
                    break;
            }
            _visitedDirectoryClusters = null;

            Mounted = true;
        }

        /// <summary>
        /// Read and verifies the FATX header.
        /// </summary>
        private void ReadBootSector()
        {
            if (_partitionOffset < 0 || _partitionOffset + Constants.ReservedBytes > _reader.Length)
            {
                throw new FormatException($"{_partitionName} at 0x{_partitionOffset:X} lies outside of the image (length 0x{_reader.Length:X}).");
            }

            _reader.Seek(_partitionOffset);
            Console.WriteLine("Attempting to load FATX volume at {0:X16}", _reader.Position);

            if (!_usesLegacyFormat)
            {
                ReadVolumeMetadata();
            }
            else
            {
                ReadLegacyVolumeMetadata();
            }
        }

        private void ReadVolumeMetadata()
        {
            _signature = _reader.ReadUInt32();
            _serialNumber = _reader.ReadUInt32();
            _sectorsPerCluster = _reader.ReadUInt32();
            _rootDirFirstCluster = _reader.ReadUInt32();

            if (_signature != VolumeSignature)
            {
                throw new FormatException($"Invalid FATX Signature for {_partitionName}: {_signature:X8}");
            }
        }

        private void ReadLegacyVolumeMetadata()
        {
            // Read _BOOT_SECTOR
            _jump = _reader.ReadUInt32();                   // EB FE
            _signature = _reader.ReadUInt32();              // FATX
            _serialNumber = _reader.ReadUInt32();           // 05C29C00

            if (_signature != VolumeSignature)
            {
                throw new FormatException($"Invalid FATX Signature for {_partitionName}: {_signature:X8}");
            }

            // Read BIOS_PARAMETER_BLOCK
            // These fields are currently not being used by this reader. With some testing, we'll see if these fields
            // are truly necessary.
            _bytesPerSector = _reader.ReadUInt16();         // 0200
            _sectorsPerCluster = _reader.ReadByte();        // 20
            _reservedSectors = _reader.ReadUInt16();        // 08
            _sectorsPerTrack = _reader.ReadUInt16();        // 3F00
            _heads = _reader.ReadUInt16();                  // FF00
            _hiddenSectors = _reader.ReadUInt32();          // 00000400
            _largeSectors = _reader.ReadUInt32();           // 009896B0
            _largeSectorsPerFat = _reader.ReadUInt32();     // 00000990
            _rootDirFirstCluster = _reader.ReadUInt32();    // 00000001
        }

        /// <summary>
        /// Calculate offsets needed to perform work on this file system.
        /// </summary>
        private void CalculateOffsets()
        {
            // Valid FATX volumes use a power of two sectors per cluster
            // (Xbox: 32 = 16KB, Xbox 360 up to 0x80+). Anything else means
            // the superblock is corrupt and every following computation
            // would be garbage.
            if (_sectorsPerCluster == 0 || _sectorsPerCluster > 0x400 ||
                (_sectorsPerCluster & (_sectorsPerCluster - 1)) != 0)
            {
                throw new FormatException($"Invalid sectors per cluster for {_partitionName}: {_sectorsPerCluster}");
            }

            _bytesPerCluster = _sectorsPerCluster * Constants.SectorSize;
            _maxClusters = (uint)(_partitionLength / (long)_bytesPerCluster)
                + Constants.ReservedClusters;

            uint bytesPerFat;
            if (_maxClusters < 0xfff0)
            {
                bytesPerFat = _maxClusters * 2;
                _isFat16 = true;
            }
            else
            {
                bytesPerFat = _maxClusters * 4;
                _isFat16 = false;
            }

            _bytesPerFat = (bytesPerFat + (Constants.PageSize - 1)) &
                ~(Constants.PageSize - 1);

            this._fatByteOffset = Constants.ReservedBytes;
            this._fileAreaByteOffset = this._fatByteOffset + this._bytesPerFat;
            this._fileAreaLength = this.Length - this.FileAreaByteOffset;
        }

        /// <summary>
        /// Read either the 16 or 32 bit file allocation table.
        /// </summary>
        private void ReadFileAllocationTable()
        {
            _fileAllocationTable = new uint[_maxClusters];

            var fatOffset = ByteOffsetToPhysicalOffset(this._fatByteOffset);
            _reader.Seek(fatOffset);
            if (this._isFat16)
            {
                byte[] _tempFat = new byte[_maxClusters * 2];
                _reader.Read(_tempFat, (int)(_maxClusters * 2));

                if (_reader.ByteOrder == ByteOrder.Big)
                {
                    for (int i = 0; i < _maxClusters; i++)
                    {
                        Array.Reverse(_tempFat, i * 2, 2);
                    }
                }

                for (int i = 0; i < _maxClusters; i++)
                {
                    _fileAllocationTable[i] = BitConverter.ToUInt16(_tempFat, i * 2);
                }
            }
            else
            {
                byte[] _tempFat = new byte[_maxClusters * 4];
                _reader.Read(_tempFat, (int)(_maxClusters * 4));

                if (_reader.ByteOrder == ByteOrder.Big)
                {
                    for (int i = 0; i < _maxClusters; i++)
                    {
                        Array.Reverse(_tempFat, i * 4, 4);
                    }
                }

                for (int i = 0; i < _maxClusters; i++)
                {
                    _fileAllocationTable[i] = BitConverter.ToUInt32(_tempFat, i * 4);
                }
            }
        }

        /// <summary>
        /// Read a single directory stream.
        /// </summary>
        /// <param name="cluster"></param>
        /// <returns></returns>
        private List<DirectoryEntry> ReadDirectoryStream(uint cluster)
        {
            List<DirectoryEntry> stream = new List<DirectoryEntry>();

            byte[] data = ReadCluster(cluster);
            long clusterOffset = ClusterToPhysicalOffset(cluster);

            // Cluster size varies between volumes, so do not assume 256
            // entries (16KB clusters).
            int direntsPerCluster = DirentsPerCluster;
            for (int i = 0; i < direntsPerCluster; i++)
            {
                DirectoryEntry dirent = new DirectoryEntry(this.Platform, data, (i * 0x40));

                if (dirent.FileNameLength == Constants.DirentNeverUsed ||
                    dirent.FileNameLength == Constants.DirentNeverUsed2)
                {
                    // We are at the last dirent.
                    break;
                }

                dirent.Offset = clusterOffset + (i * 0x40);

                stream.Add(dirent);

            }

            return stream;
        }

        /// <summary>
        /// Iterates dirent's from stream and populates directories with its child
        /// dirents. 
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="clusterIndex"></param>
        private void PopulateDirentStream(List<DirectoryEntry> stream, uint clusterIndex)
        {
            foreach (DirectoryEntry dirent in stream)
            {
                dirent.Cluster = clusterIndex;
                //Console.WriteLine(String.Format("{0}", dirent.FileName));

                if (dirent.IsDirectory() && !dirent.IsDeleted())
                {
                    List<uint> chainMap = GetClusterChain(dirent);

                    foreach (uint cluster in chainMap)
                    {
                        // Protect against corrupted file systems where a
                        // directory points back at one of its ancestors,
                        // which used to recurse until the stack overflowed.
                        if (_visitedDirectoryClusters != null && !_visitedDirectoryClusters.Add(cluster))
                        {
                            Console.WriteLine($"Directory {dirent.FileName} references an already visited cluster {cluster}, skipping.");
                            continue;
                        }

                        List<DirectoryEntry> direntStream = ReadDirectoryStream(cluster);

                        dirent.AddChildren(direntStream);

                        PopulateDirentStream(direntStream, cluster);
                    }
                }
            }
        }

        /// <summary>
        /// Seek to any offset in the file area.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="origin"></param>
        public void SeekFileArea(long offset, SeekOrigin origin = SeekOrigin.Begin)
        {
            offset += FileAreaByteOffset + _partitionOffset;
            _reader.Seek(offset, origin);
        }

        /// <summary>
        /// Seek to a cluster.
        /// </summary>
        /// <param name="cluster"></param>
        public void SeekToCluster(uint cluster)
        {
            var offset = ClusterToPhysicalOffset(cluster);
            _reader.Seek(offset);
        }

        /// <summary>
        /// Reads a cluster and returns the data.
        /// </summary>
        /// <param name="cluster"></param>
        /// <returns></returns>
        public byte[] ReadCluster(uint cluster)
        {
            return ReadClusters(cluster, 1);
        }

        /// <summary>
        /// Reads a run of consecutive clusters in one I/O request.
        /// </summary>
        public byte[] ReadClusters(uint firstCluster, int count)
        {
            if (firstCluster == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(firstCluster), "Cluster indexes start at 1.");
            }

            var clusterOffset = ClusterToPhysicalOffset(firstCluster);
            byte[] clusterData = new byte[(long)_bytesPerCluster * count];
            lock (_reader)
            {
                _reader.Seek(clusterOffset);
                _reader.Read(clusterData, clusterData.Length);
            }
            return clusterData;
        }

        /// <summary>
        /// Get a cluster chain for a dirent from the file allocation table.
        /// </summary>
        /// <param name="dirent">The DirectoryEntry to get the cluster chain for.</param>
        /// <returns></returns>
        public List<uint> GetClusterChain(DirectoryEntry dirent)
        {
            var firstCluster = dirent.FirstCluster;

            if (firstCluster == 0 || firstCluster >= this.MaxClusters)
            {
                Console.WriteLine($" {dirent.GetFullPath()}: First cluster is invalid (FirstCluster={firstCluster} MaxClusters={this.MaxClusters})");
                return new List<uint>();
            }

            if (dirent.IsDeleted())
            {
                return new List<uint> { firstCluster };
            }

            var chain = GetClusterChain(firstCluster);
            if (chain == null)
            {
                // TODO: Warn user.
                Console.WriteLine($"File {dirent.FileName} has a corrupt cluster chain!");
                return new List<uint> { firstCluster };
            }

            return chain;
        }

        /// <summary>
        /// Follows the file allocation table starting at <paramref name="firstCluster"/>.
        /// </summary>
        /// <returns>The cluster chain, or null if the chain is corrupt (out of range link, free link or a loop).</returns>
        public List<uint> GetClusterChain(uint firstCluster)
        {
            List<uint> clusterChain = new List<uint>();
            if (firstCluster == 0 || firstCluster >= _fileAllocationTable.Length)
            {
                return clusterChain;
            }

            clusterChain.Add(firstCluster);

            uint fatEntry = firstCluster;
            uint reservedIndexes = (_isFat16) ? Constants.Cluster16Reserved : Constants.ClusterReserved;
            while (true)
            {
                fatEntry = _fileAllocationTable[fatEntry];
                if (fatEntry >= reservedIndexes)
                {
                    break;
                }

                // NOTE: previously this used '>' which allowed an index equal
                // to the table length through and crashed with an
                // IndexOutOfRangeException.
                if (fatEntry == 0 || fatEntry >= _fileAllocationTable.Length)
                {
                    return null;
                }

                // A chain can never be longer than the number of clusters,
                // anything longer contains a loop.
                if (clusterChain.Count >= _fileAllocationTable.Length)
                {
                    return null;
                }

                // Get next cluster.
                clusterChain.Add(fatEntry);
            }

            return clusterChain;
        }

        /// <summary>
        /// Convert a byte offset (relative to the volume) to an offset into the
        /// image file it belongs to.
        /// </summary>
        /// <param name="offset"></param>
        /// <returns></returns>
        private long ByteOffsetToPhysicalOffset(long offset)
        {
            return this._partitionOffset + offset;
        }

        /// <summary>
        /// Convert a cluster index to an offset into the image file it belongs to.
        /// </summary>
        /// <param name="cluster"></param>
        /// <returns></returns>
        public long ClusterToPhysicalOffset(uint cluster)
        {
            var physicalOffset = ByteOffsetToPhysicalOffset(FileAreaByteOffset);
            long clusterOffset = (long)_bytesPerCluster * ((long)cluster - 1);
            return (physicalOffset + clusterOffset);
        }

        private long CountFiles(List<DirectoryEntry> dirents)
        {
            long numFiles = 0;

            foreach (var dirent in dirents)
            {
                numFiles += dirent.CountFiles();
            }

            return numFiles;
        }

        public long CountFiles()
        {
            return CountFiles(_root);
        }

        public long GetTotalSpace()
        {
            return _fileAreaLength;
        }

        public long GetFreeSpace()
        {
            return (_fileAreaLength) - GetUsedSpace();
        }

        public long GetUsedSpace()
        {
            // Count number of used clusters
            long clustersUsed = 0;

            // Entry 0 is the media descriptor, not a data cluster.
            for (long i = 1; i < FileAllocationTable.Length; i++)
            {
                if (FileAllocationTable[i] != Constants.ClusterAvailable)
                {
                    clustersUsed++;
                }
            }

            return (clustersUsed * BytesPerCluster);
        }
    }
}
