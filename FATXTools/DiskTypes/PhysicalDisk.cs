using FATX;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;

namespace FATXTools.DiskTypes
{
    public class PhysicalDisk : DriveReader
    {
        private long _length;
        private long _sectorLength;
        private long _position;
        public PhysicalDisk(SafeFileHandle handle, long length, long sectorLength)
            : base(new FileStream(handle, FileAccess.Read))
        {
            this._length = length;
            this._sectorLength = sectorLength;
            this.Initialize();
        }

        public override long Length => _length;

        public override long Position => _position;

        public override long Seek(long offset)
        {
            _position = offset;
            return _position;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    _position = offset;
                    break;
                case SeekOrigin.Current:
                    _position += offset;
                    break;
                case SeekOrigin.End:
                    _position = _length - offset;
                    break;
            }
            return _position;
        }

        public override int Read(byte[] buffer, int count)
        {
            return Read(buffer, 0, count);
        }

        public override int Read(byte[] buffer, int index, int count)
        {
            // Raw devices only accept sector aligned reads. Align the start
            // down and the end up so that a read straddling a sector
            // boundary is fully covered.
            var misalignment = _position % _sectorLength;
            var alignedOffset = _position - misalignment;
            var alignedCount = (int)((misalignment + count + (_sectorLength - 1)) & ~(_sectorLength - 1));

            BaseStream.Seek(alignedOffset, SeekOrigin.Begin);

            var tempBuf = new byte[alignedCount];
            int total = 0;
            while (total < alignedCount)
            {
                int n = BaseStream.Read(tempBuf, total, alignedCount - total);
                if (n <= 0)
                    break;
                total += n;
            }

            // Only copy the bytes we need
            Buffer.BlockCopy(tempBuf, (int)misalignment, buffer, index, count);

            // Increment the position by how much we took
            _position += count;

            return (int)Math.Max(0, Math.Min(count, total - misalignment));
        }

        public override byte[] ReadBytes(int count)
        {
            byte[] buf = new byte[count];
            Read(buf, count);
            return buf;
        }

        public override byte ReadByte()
        {
            byte[] buf = new byte[1];
            Read(buf, 1);
            return buf[0];
        }
    }
}
