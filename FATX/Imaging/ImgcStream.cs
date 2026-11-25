using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FATX.Imaging
{
    /// <summary>
    /// Read-only, seekable view of the drive stored in an HDD Raw Copy Tool
    /// compressed image (".imgc"), so the image can be analysed directly
    /// without unpacking it to a raw image first.
    ///
    /// Opening the image walks the block headers once to build an index.
    /// Damaged or truncated images are still opened: unreadable blocks and
    /// the missing tail read as zeroes and are listed in <see cref="Warnings"/>.
    /// </summary>
    public sealed class ImgcStream : Stream
    {
        private struct Block
        {
            public long Start;      // offset in the uncompressed image
            public long FilePos;    // offset of the block payload in the file
            public int PayloadSize; // compressed payload size, 0 for zero blocks
        }

        private const int CacheSize = 8;

        private readonly Stream file;
        private readonly bool leaveOpen;
        private readonly Block[] blocks;
        private readonly long dataLength;   // bytes covered by blocks
        private readonly long length;
        private long position;
        private int lastIndex;

        private readonly int[] cacheIndex = new int[CacheSize];
        private readonly byte[][] cacheData = new byte[CacheSize][];
        private int cacheNext;
        private byte[] payload = new byte[0];
        private readonly HashSet<int> corruptBlocks = new HashSet<int>();

        public ImgcStream(string path)
            : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0x10000), false)
        {
        }

        /// <summary>
        /// Where block indexes of large images are cached so that only the
        /// first open has to walk the whole file. Null disables the cache.
        /// </summary>
        public static string IndexCacheDirectory { get; set; } = DefaultIndexCacheDirectory();

        /// <summary>
        /// Images with fewer blocks are quick to index and are not cached.
        /// </summary>
        public static int IndexCacheMinBlocks { get; set; } = 20000;

        /// <summary>
        /// Whether the block index came from the cache.
        /// </summary>
        public bool IndexFromCache { get; }

        private static string DefaultIndexCacheDirectory()
        {
            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "FATXTools", "imgc-index");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public ImgcStream(Stream file, bool leaveOpen = false, CancellationToken cancellationToken = default, bool useIndexCache = true)
        {
            this.file = file;
            this.leaveOpen = leaveOpen;

            for (int i = 0; i < CacheSize; i++)
                cacheIndex[i] = -1;

            var headerBytes = new byte[ImgcHeader.Size];
            file.Position = 0;
            if (ReadFull(headerBytes, ImgcHeader.Size) < ImgcHeader.Size)
                throw new InvalidDataException("Not an IMGC image: the file is shorter than the header.");
            Header = ImgcHeader.Parse(headerBytes);

            long fileLength = file.Length;
            string cachePath = useIndexCache ? IndexCachePath(file) : null;
            long expected = Header.ImageLength;

            if (cachePath != null && TryLoadIndex(cachePath, fileLength, out blocks, out dataLength, out long cachedEnd))
            {
                DataEndFilePosition = cachedEnd;
                length = Math.Max(dataLength, expected);
                if (expected > dataLength)
                    Warnings.Add($"The image holds 0x{dataLength:X} of 0x{expected:X} bytes; the rest reads as zeroes.");
                IndexFromCache = true;
                return;
            }

            var list = new List<Block>();
            long pos = ImgcHeader.Size;
            long start = 0;
            var head = new byte[16];

            while (pos + ImgcHeader.BlockHeaderSize <= fileLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Array.Clear(head, 0, head.Length);
                file.Position = pos;
                int got = ReadFull(head, 16);
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));

                if (magic != ImgcHeader.CompressedMagic && magic != ImgcHeader.ZeroMagic)
                {
                    Warnings.Add($"Unknown block at file offset 0x{pos:X}; the image ends there.");
                    break;
                }

                if (size < ImgcHeader.BlockHeaderSize + 2 || pos + size > fileLength)
                {
                    Warnings.Add($"Truncated block at file offset 0x{pos:X}; the image ends there.");
                    break;
                }

                long count;
                int payloadSize;
                if (magic == ImgcHeader.ZeroMagic)
                {
                    if (size < 16 || got < 16)
                    {
                        Warnings.Add($"Truncated block at file offset 0x{pos:X}; the image ends there.");
                        break;
                    }
                    count = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8));
                    payloadSize = 0;
                }
                else
                {
                    count = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
                    if ((count & 0x8000) != 0)
                        count = (count & 0x7FFF) | ((long)BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10)) << 15);
                    payloadSize = (int)(size - ImgcHeader.BlockHeaderSize);
                }

                if (count < 0 || count > long.MaxValue - start)
                {
                    Warnings.Add($"Invalid block at file offset 0x{pos:X}; the image ends there.");
                    break;
                }

                if (count > 0)
                    list.Add(new Block { Start = start, FilePos = pos + ImgcHeader.BlockHeaderSize, PayloadSize = payloadSize });

                start += count;
                pos += size;
            }

            blocks = list.ToArray();
            dataLength = start;
            DataEndFilePosition = pos;

            if (cachePath != null && Warnings.Count == 0 && blocks.Length >= IndexCacheMinBlocks)
                SaveIndex(cachePath, fileLength);

            if (expected > dataLength)
            {
                Warnings.Add($"The image holds 0x{dataLength:X} of 0x{expected:X} bytes; the rest reads as zeroes.");
                length = expected;
            }
            else
            {
                length = dataLength;
            }
        }

        // ---- index cache ----

        private const uint IndexMagic = 0x31584946;   // "FIX1"

        /// <summary>
        /// The cache file for an image, named after its path, size and
        /// modification time so that a changed image never matches.
        /// </summary>
        private static string IndexCachePath(Stream file)
        {
            var directory = IndexCacheDirectory;
            var name = (file as FileStream)?.Name;
            if (directory == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                var info = new FileInfo(name);
                var key = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
                    return Path.Combine(directory, BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant() + ".imgcidx");
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return null;
            }
        }

        private static bool TryLoadIndex(string path, long fileLength, out Block[] blocks, out long dataLength, out long dataEnd)
        {
            blocks = null;
            dataLength = 0;
            dataEnd = 0;

            try
            {
                if (!File.Exists(path))
                    return false;

                using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)))
                {
                    if (reader.ReadUInt32() != IndexMagic || reader.ReadInt64() != fileLength)
                        return false;

                    dataLength = reader.ReadInt64();
                    dataEnd = reader.ReadInt64();
                    int count = reader.ReadInt32();
                    if (count < 0 || dataLength < 0 || dataEnd < ImgcHeader.Size || dataEnd > fileLength ||
                        reader.BaseStream.Length != 32 + (long)count * 20)
                        return false;

                    var result = new Block[count];
                    long previous = -1;
                    for (int i = 0; i < count; i++)
                    {
                        result[i].Start = reader.ReadInt64();
                        result[i].FilePos = reader.ReadInt64();
                        result[i].PayloadSize = reader.ReadInt32();

                        // Never trust a cache file blindly.
                        if (result[i].Start <= previous || result[i].Start >= dataLength || result[i].PayloadSize < 0 ||
                            result[i].FilePos < ImgcHeader.Size || result[i].FilePos + result[i].PayloadSize > fileLength)
                            return false;
                        previous = result[i].Start;
                    }

                    blocks = result;
                    return true;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                blocks = null;
                return false;
            }
        }

        private void SaveIndex(string path, long fileLength)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                using (var writer = new BinaryWriter(new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)))
                {
                    writer.Write(IndexMagic);
                    writer.Write(fileLength);
                    writer.Write(dataLength);
                    writer.Write(DataEndFilePosition);
                    writer.Write(blocks.Length);
                    foreach (var b in blocks)
                    {
                        writer.Write(b.Start);
                        writer.Write(b.FilePos);
                        writer.Write(b.PayloadSize);
                    }
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // The cache is only an optimisation.
            }
        }

        public ImgcHeader Header { get; }

        /// <summary>
        /// Problems found while opening or reading the image.
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// Number of bytes of the drive that are actually stored in the image.
        /// </summary>
        public long DataLength => dataLength;

        /// <summary>
        /// File offset right after the last complete block.
        /// </summary>
        public long DataEndFilePosition { get; }

        public int BlockCount => blocks.Length;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: Position = offset; break;
                case SeekOrigin.Current: Position = position + offset; break;
                case SeekOrigin.End: Position = length + offset; break;
            }
            return position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length || count <= 0)
                return 0;

            count = (int)Math.Min(count, length - position);
            int done = 0;

            while (done < count)
            {
                if (position >= dataLength)
                {
                    // Tail that is missing from a truncated image.
                    int n = count - done;
                    Array.Clear(buffer, offset + done, n);
                    done += n;
                    position += n;
                    break;
                }

                int index = FindBlock(position);
                long blockStart = blocks[index].Start;
                long blockEnd = index + 1 < blocks.Length ? blocks[index + 1].Start : dataLength;
                int within = (int)Math.Min(int.MaxValue, position - blockStart);
                int take = (int)Math.Min(count - done, blockEnd - position);

                if (blocks[index].PayloadSize == 0)
                {
                    Array.Clear(buffer, offset + done, take);
                }
                else
                {
                    var data = GetBlock(index, (int)(blockEnd - blockStart));
                    Buffer.BlockCopy(data, within, buffer, offset + done, take);
                }

                done += take;
                position += take;
            }

            return done;
        }

        private int FindBlock(long offset)
        {
            // Sequential reads hit the same or the next block.
            int i = lastIndex;
            if (i < blocks.Length && blocks[i].Start <= offset)
            {
                if (i + 1 >= blocks.Length || offset < blocks[i + 1].Start)
                    return i;
                if (i + 2 >= blocks.Length || offset < blocks[i + 2].Start)
                    return lastIndex = i + 1;
            }

            int lo = 0, hi = blocks.Length - 1;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo + 1) >> 1);
                if (blocks[mid].Start <= offset)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lastIndex = lo;
        }

        private byte[] GetBlock(int index, int blockLength)
        {
            for (int i = 0; i < CacheSize; i++)
            {
                if (cacheIndex[i] == index)
                    return cacheData[i];
            }

            int slot = cacheNext;
            cacheNext = (cacheNext + 1) % CacheSize;
            cacheIndex[slot] = -1;

            var data = cacheData[slot];
            if (data == null || data.Length < blockLength)
                cacheData[slot] = data = new byte[blockLength];

            var block = blocks[index];
            if (payload.Length < block.PayloadSize)
                payload = new byte[block.PayloadSize];

            bool ok = false;
            try
            {
                file.Position = block.FilePos;
                if (ReadFull(payload, block.PayloadSize) == block.PayloadSize)
                {
                    int prefix = (payload[1] & 0x80) != 0 ? 4 : 2;
                    SynLzo.Decompress(payload, prefix, block.PayloadSize - prefix, data, 0, blockLength);
                    ok = true;
                }
            }
            catch (InvalidDataException)
            {
            }

            if (!ok)
            {
                // Keep going: one damaged block must not stop a recovery.
                Array.Clear(data, 0, blockLength);
                if (corruptBlocks.Add(index))
                {
                    var message = $"Corrupt block at image offset 0x{block.Start:X} (0x{blockLength:X} bytes) reads as zeroes.";
                    Warnings.Add(message);
                    Console.WriteLine(message);
                }
            }

            cacheIndex[slot] = index;
            return data;
        }

        private int ReadFull(byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = file.Read(buffer, total, count - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return total;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                file.Dispose();
            base.Dispose(disposing);
        }
    }
}
