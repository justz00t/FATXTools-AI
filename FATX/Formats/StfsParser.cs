namespace FATX.Formats
{
    /// <summary>
    /// Xbox 360 STFS/SVOD package (CON, LIVE, PIRS) header parser.
    /// </summary>
    public static class StfsParser
    {
        public static FormatInfo TryParse(IByteSource src, bool verify)
        {
            if (!src.TryReadUInt32(0, true, out var magic))
                return null;

            string kind;
            switch (magic)
            {
                case 0x434F4E20: kind = "CON"; break;
                case 0x4C495645: kind = "LIVE"; break;
                case 0x50495253: kind = "PIRS"; break;
                default: return null;
            }

            var info = new FormatInfo { Format = "STFS " + kind, Extension = "" };

            uint headerSize = src.ReadUInt32BE(0x340);
            uint contentType = src.ReadUInt32BE(0x344);
            uint titleId = src.ReadUInt32BE(0x360);
            uint descriptorType = src.ReadUInt32BE(0x3A9);

            // Reject false positives early: header sizes are small and fixed.
            if (headerSize < 0x971A || headerSize > 0x20000)
            {
                info.Plausible = false;
                info.Warnings.Add($"Implausible package header size 0x{headerSize:X}.");
                if (verify) info.Verification = VerificationStatus.Failed;
                return info;
            }

            info.TitleId = titleId;
            info.Properties["ContentType"] = $"0x{contentType:X8}";
            info.Properties["HeaderSize"] = $"0x{headerSize:X}";

            var displayName = src.ReadUtf16String(0x411, 0x40, true).Trim();
            if (!string.IsNullOrEmpty(displayName))
                info.EmbeddedName = displayName;
            var titleName = src.ReadUtf16String(0x1691, 0x40, true).Trim();
            if (!string.IsNullOrEmpty(titleName))
                info.Title = titleName;

            long firstHashTable = (headerSize + 0xFFF) & 0xFFFFF000;

            if (descriptorType == 1)
            {
                // SVOD: the data lives in separate Data#### files, the header
                // file only contains the header.
                info.Format = "SVOD " + kind;
                info.FileSize = firstHashTable;
                info.FileSizeExact = false;
                if (verify) info.Verification = VerificationStatus.Unverifiable;
                return info;
            }

            byte blockSeparation = src.ReadBytes(0x37B, 1)[0];
            uint allocatedBlocks = src.ReadUInt32BE(0x395);
            info.Properties["AllocatedBlocks"] = allocatedBlocks.ToString();

            if (allocatedBlocks == 0 || allocatedBlocks > 0x4AF768)
            {
                info.FileSize = firstHashTable;
                info.FileSizeExact = allocatedBlocks == 0;
                if (verify) info.Verification = VerificationStatus.Unverifiable;
                return info;
            }

            // Packages with one hash table per level ("female") have
            // bit 0 of the block separation set.
            int shift = ((~blockSeparation) & 1);
            long lastBlock = allocatedBlocks - 1;
            info.FileSize = firstHashTable + ((BackingDataBlock(lastBlock, shift) + 1) << 12);
            info.FileSizeExact = true;
            if (verify) info.Verification = VerificationStatus.Unverifiable;
            return info;
        }

        /// <summary>
        /// Converts a data block number into its position in the package,
        /// accounting for the interleaved hash tables.
        /// </summary>
        private static long BackingDataBlock(long block, int shift)
        {
            long result = (((block + 0xAA) / 0xAA) << shift) + block;
            if (block < 0xAA)
                return result;
            if (block < 0x70E4)
                return result + (((block + 0x70E4) / 0x70E4) << shift);
            return (1L << shift) + result + (((block + 0x70E4) / 0x70E4) << shift);
        }
    }
}
