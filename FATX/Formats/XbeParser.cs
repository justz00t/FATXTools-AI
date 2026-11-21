using System;
using System.Security.Cryptography;

namespace FATX.Formats
{
    /// <summary>
    /// Original Xbox executable (XBE) header parser.
    /// </summary>
    public static class XbeParser
    {
        private const int SectionHeaderSize = 0x38;

        public static FormatInfo TryParse(IByteSource src, bool verify)
        {
            if (!src.TryReadUInt32(0, true, out var magic) || magic != 0x58424548) // "XBEH"
                return null;

            var info = new FormatInfo { Format = "XBE", Extension = ".xbe" };

            uint baseAddress = src.ReadUInt32LE(0x104);
            uint sizeOfHeaders = src.ReadUInt32LE(0x108);
            uint sizeOfImage = src.ReadUInt32LE(0x10C);
            uint certAddress = src.ReadUInt32LE(0x118);
            uint numSections = src.ReadUInt32LE(0x11C);
            uint sectionHeadersAddress = src.ReadUInt32LE(0x120);
            uint debugFileNameAddress = src.ReadUInt32LE(0x150);

            info.Properties["BaseAddress"] = $"0x{baseAddress:X8}";
            info.Properties["SizeOfHeaders"] = $"0x{sizeOfHeaders:X}";
            info.Properties["SizeOfImage"] = $"0x{sizeOfImage:X}";

            if (baseAddress != 0x00010000)
                info.Warnings.Add($"Unusual base address 0x{baseAddress:X8}.");

            if (sizeOfHeaders < 0x178 || sizeOfHeaders > 0x1000000 || numSections > 0x400)
            {
                info.Warnings.Add("Implausible XBE header.");
                info.Plausible = false;
                if (verify) info.Verification = VerificationStatus.Failed;
                return info;
            }

            long Rva(uint va) => (long)va - baseAddress;

            if (debugFileNameAddress >= baseAddress && Rva(debugFileNameAddress) < sizeOfHeaders)
            {
                var name = src.ReadCString(Rva(debugFileNameAddress), 260);
                if (!string.IsNullOrEmpty(name))
                    info.EmbeddedName = name;
            }

            if (certAddress >= baseAddress && Rva(certAddress) + 0x5C <= sizeOfHeaders)
            {
                long cert = Rva(certAddress);
                info.TitleId = src.ReadUInt32LE(cert + 0x08);
                var title = src.ReadUtf16String(cert + 0x0C, 40, false).Trim();
                if (!string.IsNullOrEmpty(title))
                    info.Title = title;
                if (Rva(certAddress) + 0xB0 <= sizeOfHeaders)
                {
                    var version = src.ReadUInt32LE(cert + 0xAC);
                    info.Version = $"{version:X8}";
                }
            }

            // On-disk size: the end of the furthest section's raw data.
            long fileSize = sizeOfHeaders;
            long sectionTable = Rva(sectionHeadersAddress);
            bool sectionsOk = sectionHeadersAddress >= baseAddress &&
                              sectionTable + (long)numSections * SectionHeaderSize <= sizeOfHeaders;

            if (!sectionsOk)
            {
                info.Warnings.Add("Section headers are outside of the header area.");
                info.FileSize = sizeOfImage;
                info.FileSizeExact = false;
                if (verify) info.Verification = VerificationStatus.Failed;
                return info;
            }

            var table = src.ReadBytes(sectionTable, (int)(numSections * SectionHeaderSize));
            for (int i = 0; i < numSections; i++)
            {
                int h = i * SectionHeaderSize;
                uint rawAddress = BitConverter.ToUInt32(table, h + 0x0C);
                uint rawSize = BitConverter.ToUInt32(table, h + 0x10);
                fileSize = Math.Max(fileSize, (long)rawAddress + rawSize);
            }

            info.FileSize = fileSize;
            info.FileSizeExact = true;
            info.Properties["Sections"] = numSections.ToString();

            if (verify)
            {
                // Each section header holds the SHA-1 of the section's raw
                // data (prefixed with its 32-bit little endian size).
                int ok = 0, bad = 0, empty = 0;
                string firstBad = null;
                using (var sha = SHA1.Create())
                {
                    for (int i = 0; i < numSections; i++)
                    {
                        int h = i * SectionHeaderSize;
                        uint rawAddress = BitConverter.ToUInt32(table, h + 0x0C);
                        uint rawSize = BitConverter.ToUInt32(table, h + 0x10);
                        var expected = new byte[20];
                        Buffer.BlockCopy(table, h + 0x24, expected, 0, 20);

                        if (rawSize == 0)
                        {
                            empty++;
                            continue;
                        }

                        var data = src.ReadBytes(rawAddress, (int)Math.Min(rawSize, 0x10000000));
                        if (data.Length != rawSize)
                        {
                            bad++;
                            firstBad = firstBad ?? $"section {i} truncated";
                            continue;
                        }

                        sha.Initialize();
                        var prefix = BitConverter.GetBytes(rawSize);
                        sha.TransformBlock(prefix, 0, 4, null, 0);
                        sha.TransformFinalBlock(data, 0, data.Length);
                        bool match = true;
                        for (int j = 0; j < 20; j++)
                            if (sha.Hash[j] != expected[j]) { match = false; break; }

                        if (match) ok++;
                        else
                        {
                            bad++;
                            firstBad = firstBad ?? $"section {i} at 0x{rawAddress:X} digest mismatch";
                        }
                    }
                }

                if (bad == 0 && ok > 0)
                {
                    info.Verification = VerificationStatus.Verified;
                    info.VerificationDetail = $"All {ok} section digests match.";
                }
                else if (bad == 0)
                {
                    info.Verification = VerificationStatus.Unverifiable;
                }
                else
                {
                    info.Verification = VerificationStatus.Failed;
                    info.VerificationDetail = $"{bad} of {ok + bad} section digests mismatch ({firstBad}).";
                }
            }

            return info;
        }
    }
}
