using FATX.FileSystem;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FATX.Testing
{
    public enum AllocationPolicy
    {
        /// <summary>Always take the lowest free clusters (fragments into holes).</summary>
        FirstFree,
        /// <summary>Continue after the last allocated cluster, wrapping around.</summary>
        NextFree,
    }

    /// <summary>
    /// Builds FATX volumes in memory for tests. Mirrors what the console
    /// does: deletion marks the entry 0xE5 and zeroes the FAT chain.
    /// </summary>
    public class FatxImageBuilder
    {
        public class Node
        {
            public string Name;
            public bool IsDirectory;
            public byte Attributes;
            public Node Parent;
            public List<Node> Children = new List<Node>();
            public List<uint> Chain = new List<uint>();
            public byte[] Data = new byte[0];
            public DateTime Created;
            public bool Deleted;
            public uint DirentCluster;   // directory cluster holding this entry
            public int DirentIndex;      // index of the entry within the directory data
            public long DirentOffset;    // filled in by Build()
        }

        private readonly Platform platform;
        private readonly long volumeLength;
        private readonly uint bytesPerCluster;
        private readonly uint maxClusters;
        private readonly bool fat16;
        private readonly uint[] fat;
        private readonly Dictionary<uint, byte[]> clusters = new Dictionary<uint, byte[]>();
        private uint nextHint = 1;

        public AllocationPolicy Policy { get; set; } = AllocationPolicy.FirstFree;

        public Node Root { get; }

        public FatxImageBuilder(Platform platform, long volumeLength, uint sectorsPerCluster = 32)
        {
            this.platform = platform;
            this.volumeLength = volumeLength;
            bytesPerCluster = sectorsPerCluster * 0x200;
            maxClusters = (uint)(volumeLength / bytesPerCluster) + 1;
            fat16 = maxClusters < 0xFFF0;
            fat = new uint[maxClusters];
            fat[0] = fat16 ? 0xFFF8u : 0xFFFFFFF8u;
            SectorsPerCluster = sectorsPerCluster;

            Root = new Node { Name = "", IsDirectory = true, Attributes = 0x10, Created = new DateTime(2005, 1, 1) };
            Root.Chain = Allocate(1);
            InitDirectoryCluster(Root.Chain[0]);
        }

        public uint SectorsPerCluster { get; }
        public uint BytesPerCluster => bytesPerCluster;
        public bool IsFat16 => fat16;
        public uint ClusterCount => (uint)((volumeLength - FileAreaOffset) / bytesPerCluster);

        public long FileAreaOffset
        {
            get
            {
                long fatBytes = (long)maxClusters * (fat16 ? 2 : 4);
                fatBytes = (fatBytes + 0xFFF) & ~0xFFFL;
                return 0x1000 + fatBytes;
            }
        }

        private void InitDirectoryCluster(uint cluster)
        {
            var data = new byte[bytesPerCluster];
            for (int i = 0; i < data.Length; i++) data[i] = 0xFF;
            clusters[cluster] = data;
        }

        private List<uint> Allocate(long count)
        {
            var result = new List<uint>();
            uint start = Policy == AllocationPolicy.FirstFree ? 1u : nextHint;
            uint c = start;
            uint limit = ClusterCount;
            long scanned = 0;
            while (result.Count < count)
            {
                if (c > limit) c = 1;
                if (fat[c] == 0 && !result.Contains(c))
                    result.Add(c);
                c++;
                if (++scanned > limit + 1)
                    throw new InvalidOperationException("Volume is full.");
            }

            uint last = fat16 ? 0xFFFFu : 0xFFFFFFFFu;
            for (int i = 0; i < result.Count; i++)
                fat[result[i]] = i + 1 < result.Count ? result[i + 1] : last;
            nextHint = result[result.Count - 1] + 1;
            return result;
        }

        private void AddEntry(Node parent, Node node)
        {
            // Find a free slot in the parent's directory data.
            int perCluster = (int)(bytesPerCluster / 0x40);
            int index = parent.Children.Count;
            if (index >= perCluster * parent.Chain.Count)
            {
                var more = Allocate(1);
                fat[parent.Chain[parent.Chain.Count - 1]] = more[0];
                parent.Chain.Add(more[0]);
                InitDirectoryCluster(more[0]);
            }
            node.Parent = parent;
            node.DirentIndex = index;
            node.DirentCluster = parent.Chain[index / perCluster];
            parent.Children.Add(node);
            WriteEntry(node);
        }

        public Node AddDirectory(Node parent, string name, DateTime created, byte attributes = 0x10)
        {
            var node = new Node { Name = name, IsDirectory = true, Attributes = attributes, Created = created };
            node.Chain = Allocate(1);
            InitDirectoryCluster(node.Chain[0]);
            AddEntry(parent, node);
            return node;
        }

        public Node AddFile(Node parent, string name, byte[] data, DateTime created, byte attributes = 0x00)
        {
            var node = new Node { Name = name, Data = data, Attributes = attributes, Created = created };
            long count = (data.Length + bytesPerCluster - 1) / bytesPerCluster;
            node.Chain = count > 0 ? Allocate(count) : new List<uint>();
            for (int i = 0; i < node.Chain.Count; i++)
            {
                var buf = new byte[bytesPerCluster];
                int n = (int)Math.Min(bytesPerCluster, data.Length - (long)i * bytesPerCluster);
                Buffer.BlockCopy(data, (int)(i * bytesPerCluster), buf, 0, n);
                clusters[node.Chain[i]] = buf;
            }
            AddEntry(parent, node);
            return node;
        }

        /// <summary>
        /// Deletes like the console: children first, entry marked 0xE5, FAT chain zeroed.
        /// </summary>
        public void Delete(Node node)
        {
            if (node.Deleted) return;
            foreach (var child in node.Children)
                Delete(child);
            node.Deleted = true;
            foreach (var c in node.Chain)
                fat[c] = 0;
            WriteEntry(node);
        }

        /// <summary>
        /// Overwrites the raw 64 byte entry of a node (to craft corrupt entries).
        /// </summary>
        public void PatchEntry(Node node, Action<byte[]> patch)
        {
            var dir = clusters[node.DirentCluster];
            int perCluster = (int)(bytesPerCluster / 0x40);
            int off = (node.DirentIndex % perCluster) * 0x40;
            var entry = new byte[0x40];
            Buffer.BlockCopy(dir, off, entry, 0, 0x40);
            patch(entry);
            Buffer.BlockCopy(entry, 0, dir, off, 0x40);
        }

        public static uint EncodeTime(DateTime t, Platform platform)
        {
            int yearBase = platform == Platform.Xbox ? 2000 : 1980;
            return (uint)((t.Year - yearBase) << 25 | t.Month << 21 | t.Day << 16 | t.Hour << 11 | t.Minute << 5 | (t.Second / 2));
        }

        private void WriteEntry(Node node)
        {
            var dir = clusters[node.DirentCluster];
            int perCluster = (int)(bytesPerCluster / 0x40);
            int off = (node.DirentIndex % perCluster) * 0x40;
            var name = Encoding.ASCII.GetBytes(node.Name);
            dir[off + 0] = node.Deleted ? (byte)0xE5 : (byte)name.Length;
            dir[off + 1] = node.Attributes;
            for (int i = 0; i < 42; i++)
                dir[off + 2 + i] = i < name.Length ? name[i] : (byte)0xFF;

            uint first = node.Chain.Count > 0 ? node.Chain[0] : 0;
            uint size = node.IsDirectory ? 0 : (uint)node.Data.Length;
            uint time = EncodeTime(node.Created, platform);
            var span = new Span<byte>(dir, off, 0x40);
            if (platform == Platform.Xbox)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x2C), first);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x30), size);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x34), time);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x38), time);
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x3C), time);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x2C), first);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x30), size);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x34), time);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x38), time);
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x3C), time);
            }
        }

        /// <summary>
        /// Writes raw bytes into a free area (for carving tests) and returns the first cluster.
        /// The clusters are left unallocated, like a deleted file's data.
        /// </summary>
        public uint WriteOrphanData(byte[] data, uint firstCluster)
        {
            long count = (data.Length + bytesPerCluster - 1) / bytesPerCluster;
            for (int i = 0; i < count; i++)
            {
                var buf = new byte[bytesPerCluster];
                int n = (int)Math.Min(bytesPerCluster, data.Length - (long)i * bytesPerCluster);
                Buffer.BlockCopy(data, (int)(i * bytesPerCluster), buf, 0, n);
                clusters[(uint)(firstCluster + i)] = buf;
            }
            return firstCluster;
        }

        public long ClusterOffset(uint cluster) => FileAreaOffset + (long)(cluster - 1) * bytesPerCluster;

        /// <summary>
        /// Writes the volume to <paramref name="stream"/> at <paramref name="baseOffset"/>.
        /// </summary>
        public void WriteTo(Stream stream, long baseOffset = 0)
        {
            bool be = platform == Platform.X360;
            var header = new byte[0x1000];
            Encoding.ASCII.GetBytes(be ? "XTAF" : "FATX").CopyTo(header, 0);
            void W32(byte[] b, int o, uint v) { if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); }
            W32(header, 4, 0x12345678);
            W32(header, 8, SectorsPerCluster);
            W32(header, 12, Root.Chain[0]);
            stream.Position = baseOffset;
            stream.Write(header, 0, header.Length);

            long fatBytes = FileAreaOffset - 0x1000;
            var fatData = new byte[fatBytes];
            for (int i = 0; i < maxClusters; i++)
            {
                if (fat16)
                {
                    if (be) BinaryPrimitives.WriteUInt16BigEndian(fatData.AsSpan(i * 2), (ushort)fat[i]);
                    else BinaryPrimitives.WriteUInt16LittleEndian(fatData.AsSpan(i * 2), (ushort)fat[i]);
                }
                else W32(fatData, i * 4, fat[i]);
            }
            stream.Write(fatData, 0, fatData.Length);

            foreach (var kv in clusters.OrderBy(k => k.Key))
            {
                stream.Position = baseOffset + ClusterOffset(kv.Key);
                stream.Write(kv.Value, 0, kv.Value.Length);
            }

            if (stream.Length < baseOffset + volumeLength)
                stream.SetLength(baseOffset + volumeLength);

            // Record entry offsets for assertions.
            void Visit(Node n)
            {
                foreach (var c in n.Children)
                {
                    int perCluster = (int)(bytesPerCluster / 0x40);
                    c.DirentOffset = baseOffset + ClusterOffset(c.DirentCluster) + (c.DirentIndex % perCluster) * 0x40;
                    Visit(c);
                }
            }
            Visit(Root);
        }

        public MemoryStream ToStream()
        {
            var ms = new MemoryStream();
            WriteTo(ms, 0);
            ms.Position = 0;
            return ms;
        }

        /// <summary>
        /// Opens the built volume as a mounted <see cref="Volume"/>.
        /// </summary>
        public Volume Mount(out DriveReader reader)
        {
            reader = new DriveReader(ToStream());
            reader.ByteOrder = platform == Platform.X360 ? ByteOrder.Big : ByteOrder.Little;
            reader.AddPartition("Test", 0, volumeLength);
            var volume = reader.Partitions[0];
            volume.Mount();
            return volume;
        }
    }
}
