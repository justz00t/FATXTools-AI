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
