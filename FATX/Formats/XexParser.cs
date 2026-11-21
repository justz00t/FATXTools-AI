using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace FATX.Formats
{
    /// <summary>
    /// Xbox 360 executable (XEX) header parser.
    /// </summary>
    public static class XexParser
    {
        public const uint OptFileFormatInfo = 0x000003FF;
        public const uint OptOriginalPeName = 0x000183FF;
        public const uint OptExecutionInfo = 0x00040006;
        public const uint OptExecutionInfoXex1 = 0x00040005; // older header revision (assumed same layout)
        public const uint OptImageBaseAddress = 0x00010201;
        public const uint OptEntryPoint = 0x00010100;

        /// <summary>
        /// Optional key used to decrypt retail encrypted XEX files so that
        /// their block hashes can be verified. Not shipped with the tool;
        /// can be provided by the user. Development kit executables use an
        /// all zero key and are always handled.
        /// </summary>
        public static byte[] RetailKey { get; set; }

        private static readonly byte[] DevkitKey = new byte[16];

        private const int MaxOptionalHeaders = 64;

        public class OptionalHeader
        {
            public uint Id;
            public uint Value;
        }

        public static FormatInfo TryParse(IByteSource src, bool verify)
        {
            if (!src.TryReadUInt32(0, true, out var magic))
                return null;

            string format;
            switch (magic)
            {
                case 0x58455832: format = "XEX2"; break;
                case 0x58455831: format = "XEX1"; break;
                case 0x58455825: format = "XEX%"; break;
                case 0x5845582D: format = "XEX-"; break;
                case 0x58455830: format = "XEX0"; break;
                default: return null;
            }

            var info = new FormatInfo { Format = format, Extension = ".xex" };

            uint moduleFlags = src.ReadUInt32BE(4);
            uint peDataOffset = src.ReadUInt32BE(8);
            uint securityOffset = src.ReadUInt32BE(0x10);
            uint headerCount = src.ReadUInt32BE(0x14);

            info.Properties["ModuleFlags"] = $"0x{moduleFlags:X8}";
            info.Properties["HeaderSize"] = $"0x{peDataOffset:X}";

            if (headerCount > MaxOptionalHeaders || peDataOffset < 0x18 + headerCount * 8 || peDataOffset > 0x1000000)
            {
                info.Plausible = false;
                info.Warnings.Add($"Implausible XEX header (header count {headerCount}, PE data offset 0x{peDataOffset:X}).");
                info.Verification = verify ? VerificationStatus.Failed : VerificationStatus.NotChecked;
                return info;
            }

            if (moduleFlags != 0)
            {
                var kinds = new List<string>();
                if ((moduleFlags & 0x01) != 0) kinds.Add("title");
                if ((moduleFlags & 0x02) != 0) kinds.Add("exports");
                if ((moduleFlags & 0x04) != 0) kinds.Add("debugger");
                if ((moduleFlags & 0x08) != 0) kinds.Add("dll");
                if ((moduleFlags & 0x10) != 0) kinds.Add("module patch");
                if ((moduleFlags & 0x20) != 0) kinds.Add("full patch");
                if ((moduleFlags & 0x40) != 0) kinds.Add("delta patch");
                info.Properties["ModuleType"] = string.Join(", ", kinds);
                if ((moduleFlags & 0x70) != 0)
                    info.Extension = ".xexp";
            }

            var headers = new Dictionary<uint, uint>();
            for (uint i = 0; i < headerCount; i++)
            {
                var id = src.ReadUInt32BE(0x18 + i * 8);
                var value = src.ReadUInt32BE(0x18 + i * 8 + 4);
                headers[id] = value;
            }

            // Original PE name
            if (headers.TryGetValue(OptOriginalPeName, out var nameOffset) && nameOffset < peDataOffset)
            {
                var name = src.ReadCString(nameOffset + 4, 260);
                if (!string.IsNullOrEmpty(name))
                    info.EmbeddedName = name;
            }

            // Execution info: media id, version, base version, title id, ...
            uint execOffset;
            if ((headers.TryGetValue(OptExecutionInfo, out execOffset) || headers.TryGetValue(OptExecutionInfoXex1, out execOffset)) &&
                execOffset + 0x18 <= peDataOffset)
            {
                var mediaId = src.ReadUInt32BE(execOffset);
                var version = src.ReadUInt32BE(execOffset + 4);
                var baseVersion = src.ReadUInt32BE(execOffset + 8);
                var titleId = src.ReadUInt32BE(execOffset + 12);
                info.TitleId = titleId;
                info.Version = FormatVersion(version);
                info.Properties["MediaId"] = $"{mediaId:X8}";
                info.Properties["BaseVersion"] = FormatVersion(baseVersion);
                var disc = src.ReadBytes(execOffset + 0x12, 2);
                if (disc.Length == 2)
                    info.Properties["Disc"] = $"{disc[0]}/{disc[1]}";
            }

            // Security info: image size and the encrypted file key.
            uint imageSize = 0;
            byte[] fileKey = null;
            if (securityOffset != 0 && securityOffset < peDataOffset)
            {
                imageSize = src.ReadUInt32BE(securityOffset + 4);
                info.Properties["ImageSize"] = $"0x{imageSize:X}";
                if (magic == 0x58455832 && securityOffset + 0x160 <= peDataOffset)
                    fileKey = src.ReadBytes(securityOffset + 0x150, 16);
            }

            // File format info: encryption and compression.
            ushort encryption = 0;
            ushort compression = 1; // basic/raw when the header is missing
            uint fileFormatOffset = 0;
            uint fileFormatSize = 0;
            if (headers.TryGetValue(OptFileFormatInfo, out fileFormatOffset) && fileFormatOffset < peDataOffset)
            {
                fileFormatSize = src.ReadUInt32BE(fileFormatOffset);
                encryption = src.ReadUInt16BE(fileFormatOffset + 4);
                compression = src.ReadUInt16BE(fileFormatOffset + 6);
            }
            else
            {
                fileFormatOffset = 0;
            }

            info.Properties["Encryption"] = encryption == 0 ? "none" : encryption == 1 ? "normal" : encryption.ToString();
            info.Properties["Compression"] = compression == 0 ? "none" : compression == 1 ? "basic" : compression == 2 ? "normal (LZX)" : compression == 3 ? "delta" : compression.ToString();

            try
            {
                ComputeSize(src, info, verify, peDataOffset, imageSize, encryption, compression, fileFormatOffset, fileFormatSize, fileKey);
            }
            catch (Exception e)
            {
                info.Warnings.Add("Unable to compute the file size: " + e.Message);
                if (verify)
                    info.Verification = VerificationStatus.Failed;
            }

            return info;
        }

        private static string FormatVersion(uint v)
        {
            // major:4 minor:4 build:16 qfe:8
            return $"{v >> 28}.{(v >> 24) & 0xF}.{(v >> 8) & 0xFFFF}.{v & 0xFF}";
        }

        private static void ComputeSize(IByteSource src, FormatInfo info, bool verify, uint peDataOffset, uint imageSize,
            ushort encryption, ushort compression, uint fileFormatOffset, uint fileFormatSize, byte[] fileKey)
        {
            if (fileFormatOffset == 0 || compression == 0)
            {
                // Uncompressed: the image follows the headers.
                info.FileSize = (long)peDataOffset + imageSize;
                info.FileSizeExact = imageSize != 0;
                if (verify) info.Verification = VerificationStatus.Unverifiable;
                return;
            }

            if (compression == 1)
            {
                // Basic compression: list of (data size, zero size) blocks.
                long data = 0;
                long image = 0;
                uint count = (fileFormatSize - 8) / 8;
                if (count > 0x100000)
                    throw new FormatException("Too many basic compression blocks.");
                for (uint i = 0; i < count; i++)
                {
                    var dataSize = src.ReadUInt32BE(fileFormatOffset + 8 + i * 8);
                    var zeroSize = src.ReadUInt32BE(fileFormatOffset + 8 + i * 8 + 4);
                    data += dataSize;
                    image += (long)dataSize + zeroSize;
                }

                info.FileSize = peDataOffset + data;
                info.FileSizeExact = true;
                if (imageSize != 0 && image != imageSize)
                    info.Warnings.Add($"Basic compression blocks describe 0x{image:X} bytes but image size is 0x{imageSize:X}.");
                if (verify)
                {
                    if (src.Length < info.FileSize)
                    {
                        info.Verification = VerificationStatus.Failed;
                        info.VerificationDetail = $"File is truncated (0x{src.Length:X} < 0x{info.FileSize:X}).";
                    }
                    else
                    {
                        info.Verification = VerificationStatus.Unverifiable;
                        info.VerificationDetail = "Basic compression has no block hashes.";
                    }
                }
                return;
            }

            if (compression == 2)
            {
                // Normal (LZX) compression: a chain of blocks. Each block starts
                // with the size and SHA-1 of the next block. The first block's
                // size and hash are in the file format header.
                uint windowSize = src.ReadUInt32BE(fileFormatOffset + 8);
                uint firstBlockSize = src.ReadUInt32BE(fileFormatOffset + 12);
                byte[] firstHash = src.ReadBytes(fileFormatOffset + 16, 20);
                info.Properties["WindowSize"] = $"0x{windowSize:X}";

                Func<long, int, byte[]> readBlock = null;
                bool encrypted = encryption != 0;
                string keyKind = null;

                if (!encrypted)
                {
                    readBlock = (off, len) => src.ReadBytes(off, len);
                }
                else if (fileKey != null)
                {
                    // Try the development kit key first (game builds), then the
                    // optional user supplied retail key.
                    foreach (var candidate in new[] { ("devkit", DevkitKey), ("retail", RetailKey) })
                    {
                        if (candidate.Item2 == null)
                            continue;
                        var sessionKey = DecryptKey(candidate.Item2, fileKey);
                        var reader = new CbcBlockReader(src, peDataOffset, sessionKey);
                        var first = reader.Read(peDataOffset, (int)Math.Min(firstBlockSize, 0x100000));
                        if (first.Length == firstBlockSize && Sha1Matches(first, firstHash))
                        {
                            readBlock = reader.Read;
                            keyKind = candidate.Item1;
                            break;
                        }
                    }
                }

                if (readBlock == null)
                {
                    // Encrypted with an unknown key: the block chain can't be
                    // followed. The compressed data is never larger than the
                    // image, so use that as an upper bound.
                    info.FileSize = (long)peDataOffset + imageSize;
                    info.FileSizeExact = false;
                    info.Warnings.Add("Encrypted with an unknown key; file size is an upper bound.");
                    if (verify)
                    {
                        info.Verification = VerificationStatus.Unverifiable;
                        info.VerificationDetail = "Encrypted with an unknown key.";
                    }
                    return;
                }

                if (keyKind != null)
                    info.Properties["Key"] = keyKind;

                long offset = peDataOffset;
                uint blockSize = firstBlockSize;
                byte[] expectedHash = firstHash;
                int blocks = 0;
                bool hashesOk = true;
                string failure = null;

                while (blockSize != 0)
                {
                    if (blockSize < 0x18 || blockSize > 0x200000)
                    {
                        hashesOk = false;
                        failure = $"Block {blocks} at 0x{offset:X} has an invalid size 0x{blockSize:X}.";
                        break;
                    }

                    if (offset + blockSize > src.Length)
                    {
                        hashesOk = false;
                        failure = $"Block {blocks} at 0x{offset:X} runs past the end of the data.";
                        break;
                    }

                    var block = readBlock(offset, (int)blockSize);
                    if (block.Length < blockSize)
                    {
                        hashesOk = false;
                        failure = $"Block {blocks} at 0x{offset:X} is truncated.";
                        break;
                    }

                    if (!Sha1Matches(block, expectedHash))
                    {
                        hashesOk = false;
                        failure = $"Block {blocks} at 0x{offset:X} fails its SHA-1 check.";
                        break;
                    }

                    uint next = (uint)(block[0] << 24 | block[1] << 16 | block[2] << 8 | block[3]);
                    expectedHash = new byte[20];
                    Buffer.BlockCopy(block, 4, expectedHash, 0, 20);

                    offset += blockSize;
                    blockSize = next;
                    blocks++;

                    if (blocks > 0x100000)
                    {
                        hashesOk = false;
                        failure = "Too many blocks.";
                        break;
                    }
                }

                info.Properties["Blocks"] = blocks.ToString();

                if (hashesOk)
                {
                    info.FileSize = offset;
                    info.FileSizeExact = true;
                    if (verify)
                    {
                        info.Verification = VerificationStatus.Verified;
                        info.VerificationDetail = $"All {blocks} compressed blocks match their SHA-1 hashes.";
                    }
                }
                else
                {
                    // Without a valid chain we only know an upper bound.
                    info.FileSize = (long)peDataOffset + imageSize;
                    info.FileSizeExact = false;
                    info.Warnings.Add(failure);
                    if (verify)
                    {
                        info.Verification = VerificationStatus.Failed;
                        info.VerificationDetail = failure;
                    }
                }

                return;
            }

            // Delta patches and unknown compression types.
            info.FileSize = (long)peDataOffset + imageSize;
            info.FileSizeExact = false;
            if (verify) info.Verification = VerificationStatus.Unverifiable;
        }

        private static bool Sha1Matches(byte[] data, byte[] expected)
        {
            if (expected == null || expected.Length != 20)
                return false;
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(data);
                for (int i = 0; i < 20; i++)
                    if (hash[i] != expected[i])
                        return false;
                return true;
            }
        }

        private static byte[] DecryptKey(byte[] key, byte[] encrypted)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                    return dec.TransformFinalBlock(encrypted, 0, 16);
            }
        }

        /// <summary>
        /// Random access reader over AES-128-CBC (IV = 0) encrypted data that
        /// starts at <c>dataStart</c>.
        /// </summary>
        private class CbcBlockReader
        {
            private readonly IByteSource src;
            private readonly long dataStart;
            private readonly byte[] key;

            public CbcBlockReader(IByteSource src, long dataStart, byte[] key)
            {
                this.src = src;
                this.dataStart = dataStart;
                this.key = key;
            }

            public byte[] Read(long offset, int length)
            {
                long rel = offset - dataStart;
                long alignedStart = rel & ~15L;
                int pre = (int)(rel - alignedStart);
                int total = (pre + length + 15) & ~15;

                var iv = new byte[16];
                if (alignedStart > 0)
                {
                    var prev = src.ReadBytes(dataStart + alignedStart - 16, 16);
                    if (prev.Length == 16) iv = prev;
                }

                var cipher = src.ReadBytes(dataStart + alignedStart, total);
                int usable = cipher.Length & ~15;
                if (usable == 0)
                    return new byte[0];

                byte[] plain;
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    using (var dec = aes.CreateDecryptor())
                        plain = dec.TransformFinalBlock(cipher, 0, usable);
                }

                int n = Math.Max(0, Math.Min(length, plain.Length - pre));
                var result = new byte[n];
                Buffer.BlockCopy(plain, pre, result, 0, n);
                return result;
            }
        }
    }
}
