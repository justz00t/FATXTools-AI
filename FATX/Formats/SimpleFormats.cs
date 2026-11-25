using System;
using System.Text;

namespace FATX.Formats
{
    /// <summary>
    /// Parsers for formats whose size is stored directly in their header.
    /// </summary>
    public static class SimpleFormats
    {
        private const string Pdb7Magic = "Microsoft C/C++ MSF 7.00\r\n\x1A\x44\x53\0\0\0";
        private const string Pdb2Magic = "Microsoft C/C++ program database 2.00\r\n\x1A\x4A\x47\0\0";

        public static FormatInfo TryParsePdb(IByteSource src, bool verify)
        {
            var head = src.ReadBytes(0, 0x2C);
            var text = Encoding.ASCII.GetString(head);

            if (text.StartsWith(Pdb7Magic))
            {
                uint blockSize = src.ReadUInt32LE(0x20);
                uint numBlocks = src.ReadUInt32LE(0x28);
                var info = new FormatInfo { Format = "PDB (MSF 7.00)", Extension = ".pdb" };
                if (blockSize == 0 || (blockSize & (blockSize - 1)) != 0 || blockSize > 0x10000)
                {
                    info.Plausible = false;
                    info.Warnings.Add($"Invalid block size {blockSize}.");
                    return info;
                }
                info.FileSize = (long)blockSize * numBlocks;
                info.FileSizeExact = true;
                if (verify) info.Verification = VerificationStatus.Unverifiable;
                return info;
            }

            if (head.Length >= 0x2C && text.StartsWith(Pdb2Magic))
            {
                uint pageSize = src.ReadUInt32LE(0x2C);
                ushort numPages = src.ReadUInt16LE(0x32);
                var info = new FormatInfo { Format = "PDB (2.00)", Extension = ".pdb" };
                if (pageSize == 0 || (pageSize & (pageSize - 1)) != 0 || pageSize > 0x10000)
                {
                    info.Plausible = false;
                    info.Warnings.Add($"Invalid page size {pageSize}.");
                    return info;
                }
                info.FileSize = (long)pageSize * numPages;
                info.FileSizeExact = true;
                if (verify) info.Verification = VerificationStatus.Unverifiable;
                return info;
            }

            return null;
        }

        public static FormatInfo TryParseXdbf(IByteSource src, bool verify)
        {
            if (!src.TryReadUInt32(0, true, out var magic) || magic != 0x58444246) // XDBF
                return null;

            var info = new FormatInfo { Format = "XDBF", Extension = ".gpd" };
            uint entryTableLength = src.ReadUInt32BE(0x08);
            uint entryCount = src.ReadUInt32BE(0x0C);
            uint freeTableLength = src.ReadUInt32BE(0x10);

            if (entryTableLength > 0x100000 || entryCount > entryTableLength || freeTableLength > 0x100000)
            {
                info.Plausible = false;
                info.Warnings.Add("Implausible XDBF header.");
                return info;
            }

            long dataStart = 0x18 + (long)entryTableLength * 0x12 + (long)freeTableLength * 8;
            long end = 0;
            var table = src.ReadBytes(0x18, (int)(entryCount * 0x12));
            for (int i = 0; i + 0x12 <= table.Length; i += 0x12)
            {
                uint off = (uint)(table[i + 10] << 24 | table[i + 11] << 16 | table[i + 12] << 8 | table[i + 13]);
                uint len = (uint)(table[i + 14] << 24 | table[i + 15] << 16 | table[i + 16] << 8 | table[i + 17]);
                end = Math.Max(end, (long)off + len);
            }

            info.FileSize = dataStart + end;
            info.FileSizeExact = true;
            info.Properties["Entries"] = entryCount.ToString();
            if (verify) info.Verification = VerificationStatus.Unverifiable;
            return info;
        }

        public static FormatInfo TryParseRiff(IByteSource src, bool verify)
        {
            if (!src.TryReadUInt32(0, true, out var magic) || magic != 0x52494646) // RIFF
                return null;

            uint size = src.ReadUInt32LE(4);
            var form = Encoding.ASCII.GetString(src.ReadBytes(8, 4));
            var info = new FormatInfo { Format = "RIFF " + form.Trim(), Extension = form == "WAVE" ? ".wav" : form == "AVI " ? ".avi" : ".riff" };
            if (size < 4 || size > 0x40000000)
            {
                info.Plausible = false;
                info.Warnings.Add("Implausible RIFF size.");
                return info;
            }
            info.FileSize = (long)size + 8;
            info.FileSizeExact = true;
            if (verify) info.Verification = VerificationStatus.Unverifiable;
            return info;
        }

        public static FormatInfo TryParseXpr0(IByteSource src, bool verify)
        {
            if (!src.TryReadUInt32(0, true, out var magic) || magic != 0x58505230) // XPR0
                return null;

            uint totalSize = src.ReadUInt32LE(4);
            uint headerSize = src.ReadUInt32LE(8);
            var info = new FormatInfo { Format = "XPR0", Extension = ".xpr" };
            if (totalSize < headerSize || totalSize > 0x40000000)
            {
                info.Plausible = false;
                info.Warnings.Add("Implausible XPR0 header.");
                return info;
            }
            info.FileSize = totalSize;
            info.FileSizeExact = true;
            if (verify) info.Verification = VerificationStatus.Unverifiable;
            return info;
        }

        /// <summary>
        /// Bink video ("BIK?" / "KB2?"): the header stores the file size minus 8.
        /// </summary>
        public static FormatInfo TryParseBink(IByteSource src, bool verify)
        {
            var head = src.ReadBytes(0, 0x24);
            if (head.Length < 0x24)
                return null;

            bool bink1 = head[0] == 'B' && head[1] == 'I' && head[2] == 'K';
            bool bink2 = head[0] == 'K' && head[1] == 'B' && head[2] == '2';
            if ((!bink1 && !bink2) || head[3] < 'a' || head[3] > 'z')
                return null;

            uint size = BitConverter.ToUInt32(head, 4);
            uint frames = BitConverter.ToUInt32(head, 8);
            uint width = BitConverter.ToUInt32(head, 0x14);
            uint height = BitConverter.ToUInt32(head, 0x18);

            var info = new FormatInfo { Format = bink1 ? "BIK" : "BK2", Extension = bink1 ? ".bik" : ".bk2" };
            if (size < 0x24 || size > 0xF0000000 || frames == 0 || frames > 10000000 ||
                width == 0 || width > 16384 || height == 0 || height > 16384)
            {
                info.Plausible = false;
                info.Warnings.Add("Implausible Bink header.");
                return info;
            }

            info.FileSize = (long)size + 8;
            info.FileSizeExact = true;
            info.Properties["Video"] = $"{width}x{height}, {frames} frames";
            if (verify) info.Verification = VerificationStatus.Unverifiable;
            return info;
        }

        /// <summary>
        /// PNG: the size is found by walking the chunks up to IEND.
        /// </summary>
        public static FormatInfo TryParsePng(IByteSource src, bool verify)
        {
            var head = src.ReadBytes(0, 24);
            if (head.Length < 24 || head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G' ||
                head[4] != 0x0D || head[5] != 0x0A || head[6] != 0x1A || head[7] != 0x0A)
                return null;

            var info = new FormatInfo { Format = "PNG", Extension = ".png" };
            if (src.ReadUInt32BE(8) != 13 || src.ReadUInt32BE(12) != 0x49484452) // IHDR
            {
                info.Plausible = false;
                info.Warnings.Add("Missing IHDR chunk.");
                return info;
            }

            info.Properties["Image"] = $"{src.ReadUInt32BE(16)}x{src.ReadUInt32BE(20)}";

            long pos = 8;
            for (int i = 0; i < 100000; i++)
            {
                if (!src.TryReadUInt32(pos, true, out var length) || !src.TryReadUInt32(pos + 4, true, out var type) || length > 0x7FFFFFFF)
                    break;

                pos += 12 + (long)length;
                if (type == 0x49454E44) // IEND
                {
                    info.FileSize = pos;
                    info.FileSizeExact = true;
                    if (verify) info.Verification = VerificationStatus.Unverifiable;
                    return info;
                }

                // Chunk types are four ASCII letters.
                for (int shift = 0; shift < 32; shift += 8)
                {
                    uint c = (type >> shift) & 0xFF;
                    if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                    {
                        pos = -1;
                        break;
                    }
                }

                if (pos < 0 || pos > 0x20000000)
                    break;
            }

            info.Plausible = false;
            info.Warnings.Add("PNG chunk list is broken or the IEND chunk is missing.");
            return info;
        }

        public static FormatInfo TryParsePe(IByteSource src, bool verify)
        {
            var mz = src.ReadBytes(0, 4);
            if (mz.Length < 4 || mz[0] != 0x4D || mz[1] != 0x5A || mz[2] != 0x90 || mz[3] != 0x00)
                return null;

            var info = new FormatInfo { Format = "PE", Extension = ".exe" };
            uint lfanew = src.ReadUInt32LE(0x3C);
            if (lfanew > 0x10000 || src.ReadUInt32LE(lfanew) != 0x00004550)
            {
                info.Plausible = false;
                info.Warnings.Add("Missing PE header.");
                return info;
            }

            ushort machine = src.ReadUInt16LE(lfanew + 4);
            ushort numSections = src.ReadUInt16LE(lfanew + 6);
            ushort optionalHeaderSize = src.ReadUInt16LE(lfanew + 0x14);
            ushort characteristics = src.ReadUInt16LE(lfanew + 0x16);
            if ((characteristics & 0x2000) != 0)
                info.Extension = ".dll";
            info.Properties["Machine"] = $"0x{machine:X4}";

            long sectionTable = lfanew + 0x18 + optionalHeaderSize;
            long end = sectionTable + numSections * 40L;
            var table = src.ReadBytes(sectionTable, numSections * 40);
            for (int i = 0; i + 40 <= table.Length; i += 40)
            {
                uint rawSize = BitConverter.ToUInt32(table, i + 0x10);
                uint rawPtr = BitConverter.ToUInt32(table, i + 0x14);
                if (rawPtr != 0)
                    end = Math.Max(end, (long)rawPtr + rawSize);
            }

            // Authenticode signatures are appended after the sections.
            if (optionalHeaderSize >= 0x60)
            {
                ushort optMagic = src.ReadUInt16LE(lfanew + 0x18);
                long dirBase = lfanew + 0x18 + (optMagic == 0x20B ? 0x70 : 0x60);
                long certEntry = dirBase + 4 * 8;
                if (certEntry + 8 <= sectionTable)
                {
                    uint certOff = src.ReadUInt32LE(certEntry);
                    uint certSize = src.ReadUInt32LE(certEntry + 4);
                    if (certOff != 0 && certSize != 0)
                        end = Math.Max(end, (long)certOff + certSize);
                }
            }

            info.FileSize = end;
            info.FileSizeExact = true;
            if (verify) info.Verification = VerificationStatus.Unverifiable;
            return info;
        }
    }
}
