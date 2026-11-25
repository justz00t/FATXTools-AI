using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FATX.Imaging
{
    /// <summary>
    /// Header of an HDD Raw Copy Tool compressed image (".imgc").
    ///
    /// Layout (0x1000 bytes, little endian): five Pascal strings of 0x100
    /// bytes each (software name, software version, drive model, firmware
    /// revision, serial number), then the sector count and sector size as
    /// 64-bit integers at 0x500 and 0x508. The header is followed by blocks:
    /// "lol!" + size + compressed data, or "omg!" + size + 64-bit zero count.
    /// The block size field includes the 8 byte block header.
    /// </summary>
    public class ImgcHeader
    {
        public const int Size = 0x1000;
        public const int BlockHeaderSize = 8;
        public const uint CompressedMagic = 0x216C6F6C;  // "lol!"
        public const uint ZeroMagic = 0x21676D6F;        // "omg!"

        public string SoftwareName { get; set; } = "";
        public string SoftwareVersion { get; set; } = "";
        public string Model { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Serial { get; set; } = "";
        public long SectorCount { get; set; }
        public long SectorSize { get; set; } = 512;

        /// <summary>
        /// Size of the imaged drive in bytes, or -1 when the header fields are not usable.
        /// </summary>
        public long ImageLength
        {
            get
            {
                if (SectorCount <= 0 || SectorSize <= 0 || SectorSize > 0x100000)
                    return -1;
                if (SectorCount > long.MaxValue / SectorSize)
                    return -1;
                return SectorCount * SectorSize;
            }
        }

        public static ImgcHeader Parse(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 0x521)
                throw new InvalidDataException("IMGC header is too short.");

            return new ImgcHeader
            {
                SoftwareName = ReadPascal(buffer, 0x000),
                SoftwareVersion = ReadPascal(buffer, 0x100),
                Model = ReadPascal(buffer, 0x200),
                Revision = ReadPascal(buffer, 0x300),
                Serial = ReadPascal(buffer, 0x400),
                SectorCount = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0x500)),
                SectorSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0x508)),
            };
        }

        public byte[] ToBytes()
        {
            var buffer = new byte[Size];
            WritePascal(buffer, 0x000, SoftwareName);
            WritePascal(buffer, 0x100, SoftwareVersion);
            WritePascal(buffer, 0x200, Model);
            WritePascal(buffer, 0x300, Revision);
            WritePascal(buffer, 0x400, Serial);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x500), SectorCount);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0x508), SectorSize);
            return buffer;
        }

        private static string ReadPascal(byte[] buffer, int offset)
        {
            int length = buffer[offset];
            return Encoding.ASCII.GetString(buffer, offset + 1, length).TrimEnd('\0', ' ');
        }

        private static void WritePascal(byte[] buffer, int offset, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value ?? "");
            int length = Math.Min(bytes.Length, 0xFE);
            buffer[offset] = (byte)length;
            Buffer.BlockCopy(bytes, 0, buffer, offset + 1, length);
        }

        /// <summary>
        /// Checks whether <paramref name="stream"/> holds an ".imgc" image by
        /// looking for a block header right after the image header. The
        /// stream position is restored.
        /// </summary>
        public static bool IsImgc(Stream stream)
        {
            if (!stream.CanSeek || stream.Length < Size + BlockHeaderSize)
                return false;

            long saved = stream.Position;
            try
            {
                var b = new byte[4];
                stream.Position = Size;
                int total = 0;
                while (total < 4)
                {
                    int n = stream.Read(b, total, 4 - total);
                    if (n <= 0) return false;
                    total += n;
                }
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(b);
                return magic == CompressedMagic || magic == ZeroMagic;
            }
            finally
            {
                stream.Position = saved;
            }
        }
    }
}
