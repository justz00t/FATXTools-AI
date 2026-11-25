using System;
using System.IO;

namespace FATX.Imaging
{
    /// <summary>
    /// The LZO1X variant used inside HDD Raw Copy Tool ".imgc" images (the
    /// Delphi "SynLZO" codec). The bit stream is LZO1X without the M1 match
    /// forms: a literal run is always followed by a match, and 1-3 literals
    /// after a match are announced in the two state bits of that match.
    ///
    /// The compressor only emits the instruction forms the reference decoder
    /// (unimgc) understands and ends every stream with the LZO end marker.
    /// </summary>
    public static class SynLzo
    {
        private const int HashBits = 14;
        private const int MaxDistance = 0xBFFF;
        private const int M2MaxDistance = 0x800;
        private const int M3MaxDistance = 0x4000;

        /// <summary>
        /// Upper bound of the compressed size of <paramref name="length"/> bytes.
        /// </summary>
        public static int MaxCompressedLength(int length)
        {
            return length + length / 255 + 32;
        }

        [ThreadStatic]
        private static int[] hashTable;

        /// <summary>
        /// Compresses <paramref name="count"/> bytes.
        /// </summary>
        /// <returns>The number of bytes written to <paramref name="dst"/>.</returns>
        public static int Compress(byte[] src, int srcOffset, int count, byte[] dst, int dstOffset)
        {
            if (count <= 0)
                throw new ArgumentException("Nothing to compress.", nameof(count));
            if (dst.Length - dstOffset < MaxCompressedLength(count))
                throw new ArgumentException("Destination buffer is too small.", nameof(dst));

            var table = hashTable;
            if (table == null)
                hashTable = table = new int[1 << HashBits];
            Array.Fill(table, -1);

            int end = srcOffset + count;
            int op = dstOffset;
            int lit = srcOffset;        // start of the pending literal run
            int stateIndex = -1;        // byte holding the state bits of the last match
            int ip = srcOffset + 4;     // the stream must start with a literal run of 4 or more

            while (ip + 4 <= end)
            {
                uint v = (uint)(src[ip] | (src[ip + 1] << 8) | (src[ip + 2] << 16));
                int h = (int)((v * 0x9E3779B1u) >> (32 - HashBits));
                int cand = table[h];
                table[h] = ip;

                if (cand < 0 || ip - cand > MaxDistance ||
                    src[cand] != src[ip] || src[cand + 1] != src[ip + 1] || src[cand + 2] != src[ip + 2])
                {
                    ip++;
                    continue;
                }

                int len = 3;
                while (ip + len < end && src[cand + len] == src[ip + len])
                    len++;

                int dist = ip - cand;
                if (dist > M2MaxDistance && len < 4)
                {
                    // A three byte match this far away costs three bytes.
                    ip++;
                    continue;
                }

                op = FlushLiterals(src, lit, ip - lit, dst, op, stateIndex);

                if (dist <= M2MaxDistance && len <= 8)
                {
                    int d = dist - 1;
                    stateIndex = op;
                    dst[op++] = (byte)(((len - 1) << 5) | ((d & 7) << 2));
                    dst[op++] = (byte)(d >> 3);
                }
                else if (dist <= M3MaxDistance)
                {
                    int t = len - 2;
                    if (t <= 31)
                    {
                        dst[op++] = (byte)(32 | t);
                    }
                    else
                    {
                        dst[op++] = 32;
                        op = WriteExtendedLength(dst, op, t - 31);
                    }
                    int d = (dist - 1) << 2;
                    stateIndex = op;
                    dst[op++] = (byte)d;
                    dst[op++] = (byte)(d >> 8);
                }
                else
                {
                    int d = dist - 0x4000;
                    int t = len - 2;
                    int b = 16 | ((d & 0x4000) >> 11);
                    if (t <= 7)
                    {
                        dst[op++] = (byte)(b | t);
                    }
                    else
                    {
                        dst[op++] = (byte)b;
                        op = WriteExtendedLength(dst, op, t - 7);
                    }
                    int low = (d & 0x3FFF) << 2;
                    stateIndex = op;
                    dst[op++] = (byte)low;
                    dst[op++] = (byte)(low >> 8);
                }

                ip += len;
                lit = ip;
            }

            op = FlushLiterals(src, lit, end - lit, dst, op, stateIndex);

            // End of stream marker.
            dst[op++] = 0x11;
            dst[op++] = 0;
            dst[op++] = 0;
            return op - dstOffset;
        }

        private static int FlushLiterals(byte[] src, int start, int length, byte[] dst, int op, int stateIndex)
        {
            if (length == 0)
                return op;

            if (length <= 3 && stateIndex >= 0)
            {
                // Announced in the state bits of the preceding match.
                dst[stateIndex] |= (byte)length;
            }
            else if (length <= 3)
            {
                // Input shorter than four bytes: standard LZO short initial
                // run. Never happens for whole sectors.
                dst[op++] = (byte)(17 + length);
            }
            else
            {
                int t = length - 3;
                if (t <= 15)
                {
                    dst[op++] = (byte)t;
                }
                else
                {
                    dst[op++] = 0;
                    op = WriteExtendedLength(dst, op, t - 15);
                }
            }

            Buffer.BlockCopy(src, start, dst, op, length);
            return op + length;
        }

        private static int WriteExtendedLength(byte[] dst, int op, int remaining)
        {
            while (remaining > 255)
            {
                dst[op++] = 0;
                remaining -= 255;
            }
            dst[op++] = (byte)remaining;
            return op;
        }

        /// <summary>
        /// Decompresses a stream into exactly <paramref name="dstLength"/> bytes.
        /// </summary>
        /// <exception cref="InvalidDataException">The stream is corrupt.</exception>
        public static void Decompress(byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
        {
            int p = srcOffset;
            int end = srcOffset + srcLength;
            int op = dstOffset;
            int oend = dstOffset + dstLength;
            int state = 0;

            while (p < end)
            {
                int instr = src[p];

                if (p == srcOffset && instr > 17)
                {
                    // Short initial literal run.
                    int n = instr - 17;
                    p++;
                    CopyLiterals(src, ref p, end, dst, ref op, oend, n);
                    state = 4;
                    continue;
                }

                int length;
                int distance;

                if (instr >= 64)
                {
                    if (p + 2 > end) throw Corrupt();
                    length = (instr >> 5) + 1;
                    distance = (src[p + 1] << 3) + ((instr >> 2) & 7) + 1;
                    state = instr & 3;
                    p += 2;
                }
                else if (instr >= 32)
                {
                    length = ReadLength(src, ref p, end, 5) + 2;
                    if (p + 2 > end) throw Corrupt();
                    int follow = src[p] | (src[p + 1] << 8);
                    p += 2;
                    distance = (follow >> 2) + 1;
                    state = follow & 3;
                }
                else if (instr >= 16)
                {
                    length = ReadLength(src, ref p, end, 3) + 2;
                    if (p + 2 > end) throw Corrupt();
                    int follow = src[p] | (src[p + 1] << 8);
                    p += 2;
                    if ((instr & 8) == 0 && (follow >> 2) == 0)
                    {
                        // End of stream marker.
                        break;
                    }
                    distance = ((instr & 8) << 11) + (follow >> 2) + 0x4000;
                    state = follow & 3;
                }
                else if (state == 0)
                {
                    length = ReadLength(src, ref p, end, 4) + 3;
                    CopyLiterals(src, ref p, end, dst, ref op, oend, length);
                    state = 4;
                    continue;
                }
                else
                {
                    throw Corrupt();
                }

                if (distance > op - dstOffset || length > oend - op)
                    throw Corrupt();

                int from = op - distance;
                if (distance >= length)
                {
                    Buffer.BlockCopy(dst, from, dst, op, length);
                    op += length;
                }
                else
                {
                    // Overlapping copy repeats the pattern.
                    for (int i = 0; i < length; i++)
                        dst[op++] = dst[from++];
                }

                if (state > 0 && state < 4)
                    CopyLiterals(src, ref p, end, dst, ref op, oend, state);
            }

            if (op != oend)
                throw Corrupt();
        }

        private static int ReadLength(byte[] src, ref int p, int end, int bits)
        {
            int mask = (1 << bits) - 1;
            int length = src[p++] & mask;
            if (length == 0)
            {
                while (true)
                {
                    if (p >= end) throw Corrupt();
                    int b = src[p++];
                    if (b != 0)
                    {
                        length += b + mask;
                        break;
                    }
                    length += 255;
                    if (length > 0x10000000) throw Corrupt();
                }
            }
            return length;
        }

        private static void CopyLiterals(byte[] src, ref int p, int end, byte[] dst, ref int op, int oend, int count)
        {
            if (count > end - p || count > oend - op)
                throw Corrupt();
            Buffer.BlockCopy(src, p, dst, op, count);
            p += count;
            op += count;
        }

        private static InvalidDataException Corrupt()
        {
            return new InvalidDataException("Corrupt compressed block.");
        }
    }
}
