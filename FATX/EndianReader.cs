using System;
using System.IO;

namespace FATX
{
    public enum ByteOrder
    {
        Big,
        Little
    }

    public class EndianReader : BinaryReader
    {
        private ByteOrder byteOrder;

        public EndianReader(Stream stream, ByteOrder byteOrder)
            : base(stream)
        {
            this.byteOrder = byteOrder;
        }

        public EndianReader(Stream stream)
            : base(stream)
        {
            this.byteOrder = ByteOrder.Little;
        }

        public ByteOrder ByteOrder
        {
            get { return this.byteOrder; }
            set { this.byteOrder = value; }
        }

        public virtual long Length
        {
            get { return BaseStream.Length; }
        }

        public virtual long Position
        {
            get { return BaseStream.Position; }
        }

        public virtual long Seek(long offset)
        {
            BaseStream.Position = offset;
            return BaseStream.Position;
        }

        public virtual long Seek(long offset, SeekOrigin origin)
        {
            return BaseStream.Seek(offset, origin);
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>.
        /// Stream.Read is allowed to return fewer bytes than requested, so loop
        /// until the request is satisfied or the end of the stream is reached.
        /// Any bytes past the end of the stream are left zeroed.
        /// </summary>
        /// <returns>The number of bytes actually read.</returns>
        public virtual int Read(byte[] buffer, int count)
        {
            return Read(buffer, 0, count);
        }

        public override int Read(byte[] buffer, int index, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = BaseStream.Read(buffer, index + total, count - total);
                if (n <= 0)
                {
                    break;
                }
                total += n;
            }

            if (total < count)
            {
                Array.Clear(buffer, index + total, count - total);
            }

            return total;
        }

        public override byte[] ReadBytes(int count)
        {
            var buf = new byte[count];
            Read(buf, count);
            return buf;
        }

        public override byte ReadByte()
        {
            var buf = new byte[1];
            Read(buf, 1);
            return buf[0];
        }

        private byte[] ReadOrdered(int size)
        {
            var temp = new byte[size];
            Read(temp, size);
            if (byteOrder == ByteOrder.Big)
            {
                Array.Reverse(temp);
            }
            return temp;
        }

        public override short ReadInt16() => BitConverter.ToInt16(ReadOrdered(2), 0);

        public override ushort ReadUInt16() => BitConverter.ToUInt16(ReadOrdered(2), 0);

        public override int ReadInt32() => BitConverter.ToInt32(ReadOrdered(4), 0);

        public override uint ReadUInt32() => BitConverter.ToUInt32(ReadOrdered(4), 0);

        public override long ReadInt64() => BitConverter.ToInt64(ReadOrdered(8), 0);

        public override ulong ReadUInt64() => BitConverter.ToUInt64(ReadOrdered(8), 0);
    }
}
