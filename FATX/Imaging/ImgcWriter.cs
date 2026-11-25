using System;
using System.Buffers.Binary;
using System.IO;

namespace FATX.Imaging
{
    /// <summary>
    /// Writes the block stream of an ".imgc" image.
    /// </summary>
    public sealed class ImgcWriter
    {
        private readonly Stream output;
        private readonly byte[] head = new byte[16];
        private long pendingZeros;

        /// <param name="output">Target, positioned where the next block goes.</param>
        public ImgcWriter(Stream output)
        {
            this.output = output;
        }

        /// <summary>
        /// Uncompressed bytes written so far, including zero runs that are still pending.
        /// </summary>
        public long UncompressedLength { get; private set; }

        public static void WriteHeader(Stream output, ImgcHeader header)
        {
            var bytes = header.ToBytes();
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Adds a run of zero bytes. Consecutive runs are merged into one block.
        /// </summary>
        public void WriteZeros(long count)
        {
            pendingZeros += count;
            UncompressedLength += count;
        }

        /// <summary>
        /// Adds a block payload produced by <see cref="PackBlock"/>.
        /// </summary>
        public void WritePacked(byte[] payload, int offset, int payloadLength, int uncompressedLength)
        {
            FlushZeros();
            BinaryPrimitives.WriteUInt32LittleEndian(head, ImgcHeader.CompressedMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)(payloadLength + ImgcHeader.BlockHeaderSize));
            output.Write(head, 0, ImgcHeader.BlockHeaderSize);
            output.Write(payload, offset, payloadLength);
            UncompressedLength += uncompressedLength;
        }

        /// <summary>
        /// Writes out a pending zero run so that the file ends on a block boundary.
        /// </summary>
        public void FlushZeros()
        {
            if (pendingZeros == 0)
                return;

            BinaryPrimitives.WriteUInt32LittleEndian(head, ImgcHeader.ZeroMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), 16);
            BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(8), pendingZeros);
            output.Write(head, 0, 16);
            pendingZeros = 0;
        }

        /// <summary>
        /// Upper bound of the payload size of a block of <paramref name="length"/> bytes.
        /// </summary>
        public static int MaxPackedLength(int length)
        {
            return 4 + SynLzo.MaxCompressedLength(length);
        }

        /// <summary>
        /// Compresses one block into its payload (size prefix + compressed data).
        /// </summary>
        /// <returns>The payload length.</returns>
        public static int PackBlock(byte[] src, int srcOffset, int count, byte[] dst, int dstOffset)
        {
            int p = dstOffset;
            if (count < 0x8000)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(p), (ushort)count);
                p += 2;
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(p), (ushort)((count & 0x7FFF) | 0x8000));
                BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(p + 2), (ushort)(count >> 15));
                p += 4;
            }

            p += SynLzo.Compress(src, srcOffset, count, dst, p);
            return p - dstOffset;
        }

        /// <summary>
        /// Whether a range holds only zero bytes.
        /// </summary>
        public static bool IsZero(byte[] buffer, int offset, int count)
        {
            var span = new ReadOnlySpan<byte>(buffer, offset, count);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(span);
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i] != 0)
                    return false;
            }
            for (int i = words.Length * 8; i < span.Length; i++)
            {
                if (span[i] != 0)
                    return false;
            }
            return true;
        }
    }
}
