using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Text;

namespace FATX.FileSystem
{
    public class DirectoryEntry
    {
        private byte _fileNameLength;
        private byte _fileAttributes;
        private byte[] _fileNameBytes;
        private uint _firstCluster;
        private uint _fileSize;
        private uint _creationTimeAsInt;
        private uint _lastWriteTimeAsInt;
        private uint _lastAccessTimeAsInt;
        private TimeStamp _creationTime;
        private TimeStamp _lastWriteTime;
        private TimeStamp _lastAccessTime;
        private string _fileName;

        private DirectoryEntry _parent;
        private List<DirectoryEntry> _children = new List<DirectoryEntry>();
        private uint _cluster;
        private long _offset;

        public DirectoryEntry(Platform platform, byte[] data, int offset)
        {
            this._parent = null;

            ReadDirectoryEntry(platform, data, offset);
        }

        private void ReadDirectoryEntry(Platform platform, byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + DirectoryEntrySize > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Not enough data for a directory entry.");
            }

            this._fileNameLength = data[offset + 0];
            this._fileAttributes = data[offset + 1];

            this._fileNameBytes = new byte[MaxFileNameLength];
            Buffer.BlockCopy(data, offset + 2, this._fileNameBytes, 0, MaxFileNameLength);

            // NOTE: Never modify the source buffer in place. Previously the
            // Xbox 360 path byte-swapped the caller's buffer, which corrupted
            // it for any later consumer (and for a second parse of the same
            // entry).
            var span = new ReadOnlySpan<byte>(data, offset, DirectoryEntrySize);
            if (platform == Platform.Xbox)
            {
                this._firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x2C));
                this._fileSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x30));
                this._creationTimeAsInt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x34));
                this._lastWriteTimeAsInt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x38));
                this._lastAccessTimeAsInt = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x3C));
                this._creationTime = new XTimeStamp(this._creationTimeAsInt);
                this._lastWriteTime = new XTimeStamp(this._lastWriteTimeAsInt);
                this._lastAccessTime = new XTimeStamp(this._lastAccessTimeAsInt);
            }
            else
            {
                this._firstCluster = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x2C));
                this._fileSize = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x30));
                this._creationTimeAsInt = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x34));
                this._lastWriteTimeAsInt = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x38));
                this._lastAccessTimeAsInt = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0x3C));
                this._creationTime = new X360TimeStamp(this._creationTimeAsInt);
                this._lastWriteTime = new X360TimeStamp(this._lastWriteTimeAsInt);
                this._lastAccessTime = new X360TimeStamp(this._lastAccessTimeAsInt);
            }
        }

        public const int DirectoryEntrySize = 0x40;
        public const int MaxFileNameLength = 42;

        public uint Cluster { get => _cluster; set => _cluster = value; }

        public long Offset { get => _offset; set => _offset = value; }

        public uint FileNameLength => _fileNameLength;

        public FileAttribute FileAttributes
        {
            get { return (FileAttribute)_fileAttributes; }
        }

        public string FileName
        {
            get
            {
                if (_fileName == null)
                {
                    int length;
                    if (_fileNameLength == Constants.DirentDeleted)
                    {
                        // The length byte is overwritten on deletion, so the
                        // real length is wherever the 0xFF padding starts.
                        length = Array.IndexOf(_fileNameBytes, (byte)0xff);
                        if (length == -1)
                        {
                            length = MaxFileNameLength;
                        }
                    }
                    else
                    {
                        length = Math.Min((int)_fileNameLength, MaxFileNameLength);
                    }

                    // Stop at an embedded NUL; it can never be part of a name.
                    var nul = Array.IndexOf(_fileNameBytes, (byte)0x00, 0, length);
                    if (nul != -1)
                    {
                        length = nul;
                    }

                    // Latin1 keeps a 1:1 mapping of bytes to chars so that
                    // nothing is silently replaced with '?'.
                    _fileName = Latin1.GetString(_fileNameBytes, 0, length);
                }

                return _fileName;
            }
        }

        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        public byte[] FileNameBytes => _fileNameBytes;

        public uint FirstCluster => _firstCluster;

        public uint FileSize => _fileSize;

        public TimeStamp CreationTime => _creationTime;

        public TimeStamp LastWriteTime => _lastWriteTime;

        public TimeStamp LastAccessTime => _lastAccessTime;

        /// <summary>
        /// Get all dirents from this directory.
        /// </summary>
        /// <returns></returns>
        public List<DirectoryEntry> Children
        {
            get
            {
                if (!this.IsDirectory())
                {
                    Console.WriteLine("Trying to get children from non directory.");
                }

                return _children;
            }
        }

        /// <summary>
        /// Add a single dirent to this directory.
        /// </summary>
        /// <param name="child"></param>
        public void AddChild(DirectoryEntry child)
        {
            if (!this.IsDirectory())
            {
                Console.WriteLine("Trying to add child to non directory.");
            }

            _children.Add(child);
        }

        /// <summary>
        /// Add list of dirents to this directory.
        /// </summary>
        /// <param name="children">List of dirents</param>
        public void AddChildren(List<DirectoryEntry> children)
        {
            if (!IsDirectory())
            {
                // TODO: warn user
                return;
            }

            foreach (DirectoryEntry dirent in children)
            {
                dirent.SetParent(this);
                _children.Add(dirent);
            }
        }

        public void SetParent(DirectoryEntry parent)
        {
            this._parent = parent;
        }

        public DirectoryEntry GetParent()
        {
            return this._parent;
        }

        public bool HasParent()
        {
            return this._parent != null;
        }

        public bool IsDirectory()
        {
            return FileAttributes.HasFlag(FileAttribute.Directory);
        }

        public bool IsDeleted()
        {
            return _fileNameLength == Constants.DirentDeleted;
        }

        /// <summary>
        /// Get only the path of this dirent.
        /// </summary>
        /// <returns></returns>
        public string GetPath()
        {
            List<string> ancestry = new List<string>();

            for (DirectoryEntry parent = _parent; parent != null; parent = parent._parent)
            {
                ancestry.Add(parent.FileName);
            }

            ancestry.Reverse();
            return String.Join("/", ancestry.ToArray());
        }

        /// <summary>
        /// Get full path including file name.
        /// </summary>
        /// <returns></returns>
        public string GetFullPath()
        {
            return GetPath() + "/" + FileName;
        }

        public DirectoryEntry GetRootDirectoryEntry()
        {
            DirectoryEntry parent = GetParent();

            if (parent == null)
            {
                return this;
            }

            while (parent.GetParent() != null)
            {
                parent = parent.GetParent();
            }

            return parent;
        }

        public long CountFiles()
        {
            if (IsDeleted())
            {
                return 0;
            }

            if (IsDirectory())
            {
                long numFiles = 1;

                foreach (var dirent in Children)
                {
                    numFiles += dirent.CountFiles();
                }

                return numFiles;
            }
            else
            {
                return 1;
            }
        }
    }
}
