using FATX.FileSystem;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FATX.Formats
{
    /// <summary>
    /// Random access view over the bytes of a (possibly not yet recovered) file.
    /// </summary>
    public interface IByteSource
    {
        /// <summary>
        /// Number of bytes that can be read from this source.
        /// </summary>
        long Length { get; }

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>.
        /// </summary>
        /// <returns>Number of bytes read.</returns>
        int Read(long offset, byte[] buffer, int index, int count);
    }

    public static class ByteSourceExtensions
    {
        public static byte[] ReadBytes(this IByteSource source, long offset, int count)
        {
            if (offset < 0 || count < 0 || offset >= source.Length)
                return new byte[0];

            count = (int)Math.Min(count, source.Length - offset);
            var buf = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = source.Read(offset + total, buf, total, count - total);
                if (n <= 0)
                    break;
                total += n;
            }

            if (total != count)
                Array.Resize(ref buf, total);
            return buf;
        }

        public static bool TryReadUInt32(this IByteSource source, long offset, bool bigEndian, out uint value)
        {
            var b = source.ReadBytes(offset, 4);
            if (b.Length < 4)
            {
                value = 0;
                return false;
            }

            value = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
            return true;
        }

        public static uint ReadUInt32BE(this IByteSource source, long offset)
        {
            if (!source.TryReadUInt32(offset, true, out var v))
                throw new EndOfStreamException($"Unable to read at 0x{offset:X}");
            return v;
        }

        public static uint ReadUInt32LE(this IByteSource source, long offset)
        {
            if (!source.TryReadUInt32(offset, false, out var v))
                throw new EndOfStreamException($"Unable to read at 0x{offset:X}");
            return v;
        }

        public static ushort ReadUInt16BE(this IByteSource source, long offset)
        {
            var b = source.ReadBytes(offset, 2);
            if (b.Length < 2)
                throw new EndOfStreamException($"Unable to read at 0x{offset:X}");
            return BinaryPrimitives.ReadUInt16BigEndian(b);
        }

        public static ushort ReadUInt16LE(this IByteSource source, long offset)
        {
            var b = source.ReadBytes(offset, 2);
            if (b.Length < 2)
                throw new EndOfStreamException($"Unable to read at 0x{offset:X}");
            return BinaryPrimitives.ReadUInt16LittleEndian(b);
        }

        /// <summary>
        /// Reads a NUL terminated ASCII string of at most <paramref name="maxLength"/> characters.
        /// </summary>
        public static string ReadCString(this IByteSource source, long offset, int maxLength = 260)
        {
            var b = source.ReadBytes(offset, maxLength);
            int len = Array.IndexOf(b, (byte)0);
            if (len < 0)
                len = b.Length;
            return Encoding.ASCII.GetString(b, 0, len);
        }

        /// <summary>
        /// Reads a NUL terminated UTF-16 string of at most <paramref name="maxChars"/> characters.
        /// </summary>
        public static string ReadUtf16String(this IByteSource source, long offset, int maxChars, bool bigEndian)
        {
            var b = source.ReadBytes(offset, maxChars * 2);
            var enc = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
            var s = enc.GetString(b, 0, b.Length & ~1);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        /// <summary>
        /// Copies <paramref name="length"/> bytes starting at <paramref name="offset"/> into <paramref name="destination"/>.
        /// </summary>
        public static long CopyTo(this IByteSource source, Stream destination, long offset, long length, int bufferSize = 0x100000)
        {
            var buf = new byte[bufferSize];
            long copied = 0;
            length = Math.Min(length, Math.Max(0, source.Length - offset));
            while (copied < length)
            {
                int want = (int)Math.Min(bufferSize, length - copied);
                int n = source.Read(offset + copied, buf, 0, want);
                if (n <= 0)
                    break;
                destination.Write(buf, 0, n);
                copied += n;
            }
            return copied;
        }
    }

    public class ArraySource : IByteSource
    {
        private readonly byte[] data;

        public ArraySource(byte[] data)
        {
            this.data = data;
        }

        public long Length => data.Length;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset >= data.Length)
                return 0;
            count = (int)Math.Min(count, data.Length - offset);
            Buffer.BlockCopy(data, (int)offset, buffer, index, count);
            return count;
        }
    }

    public class StreamSource : IByteSource
    {
        private readonly Stream stream;
        private readonly long baseOffset;
        private readonly long length;

        public StreamSource(Stream stream, long baseOffset = 0, long length = -1)
        {
            this.stream = stream;
            this.baseOffset = baseOffset;
            this.length = length >= 0 ? length : stream.Length - baseOffset;
        }

        public long Length => length;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset >= length)
                return 0;
            count = (int)Math.Min(count, length - offset);
            lock (stream)
            {
                stream.Position = baseOffset + offset;
                int total = 0;
                while (total < count)
                {
                    int n = stream.Read(buffer, index + total, count - total);
                    if (n <= 0)
                        break;
                    total += n;
                }
                return total;
            }
        }
    }

    /// <summary>
    /// Bytes of a volume's file area starting at a given offset (used for
    /// carving, where the file is assumed to be contiguous).
    /// </summary>
    public class VolumeAreaSource : IByteSource
    {
        private readonly Volume volume;
        private readonly long start;
        private readonly long length;

        public VolumeAreaSource(Volume volume, long fileAreaOffset, long length = -1)
        {
            this.volume = volume;
            this.start = fileAreaOffset;
            var available = Math.Max(0, volume.FileAreaLength - fileAreaOffset);
            this.length = length >= 0 ? Math.Min(length, available) : available;
        }

        public long Length => length;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset >= length)
                return 0;
            count = (int)Math.Min(count, length - offset);
            var reader = volume.GetReader();
            lock (reader)
            {
                volume.SeekFileArea(start + offset);
                var tmp = index == 0 ? buffer : new byte[count];
                int n = reader.Read(tmp, count);
                if (index != 0)
                    Buffer.BlockCopy(tmp, 0, buffer, index, count);
                return count;
            }
        }
    }

    /// <summary>
    /// Bytes of a file stored in a list of clusters.
    /// </summary>
    public class ClusterChainSource : IByteSource
    {
        private readonly Volume volume;
        private readonly List<uint> chain;
        private readonly long length;

        public ClusterChainSource(Volume volume, List<uint> chain, long fileSize)
        {
            this.volume = volume;
            this.chain = chain ?? new List<uint>();
            this.length = Math.Min(fileSize, (long)this.chain.Count * volume.BytesPerCluster);
        }

        public long Length => length;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset >= length)
                return 0;
            count = (int)Math.Min(count, length - offset);

            long bytesPerCluster = volume.BytesPerCluster;
            int done = 0;
            while (done < count)
            {
                long pos = offset + done;
                int chainIndex = (int)(pos / bytesPerCluster);
                int within = (int)(pos % bytesPerCluster);

                // Merge consecutive clusters into one read.
                uint first = chain[chainIndex];
                int run = 1;
                long want = within + (count - done);
                while (chainIndex + run < chain.Count && chain[chainIndex + run] == first + run &&
                       run * bytesPerCluster < want && run < 256)
                {
                    run++;
                }

                var data = volume.ReadClusters(first, run);
                int n = (int)Math.Min(data.Length - within, count - done);
                Buffer.BlockCopy(data, within, buffer, index + done, n);
                done += n;
            }

            return done;
        }
    }

    /// <summary>
    /// A view of a range of another source.
    /// </summary>
    public class SubSource : IByteSource
    {
        private readonly IByteSource source;
        private readonly long start;
        private readonly long length;

        public SubSource(IByteSource source, long start, long length)
        {
            this.source = source;
            this.start = start;
            this.length = Math.Max(0, Math.Min(length, source.Length - start));
        }

        public long Length => length;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || offset >= length)
                return 0;
            count = (int)Math.Min(count, length - offset);
            return source.Read(start + offset, buffer, index, count);
        }
    }
}
