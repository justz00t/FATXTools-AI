using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FATX.Testing
{
    /// <summary>
    /// Creates structurally valid Xbox executables for tests. The payload is
    /// random data (not real code), but all size fields and hashes are
    /// consistent so parsers and verifiers can be exercised.
    /// </summary>
    public static class ExecutableBuilders
    {
        /// <summary>
        /// Builds an XEX2 with normal (LZX block) compression.
        /// </summary>
        /// <param name="blockSizes">Sizes of the compressed blocks (each &gt;= 0x18).</param>
        /// <param name="devkitEncrypted">Encrypt with the all-zero development kit key.</param>
        public static byte[] BuildXex(string originalName, uint titleId, int[] blockSizes, bool devkitEncrypted = false, int seed = 1)
        {
            var rng = new Random(seed);
            const int headerSize = 0x1000;
            const int securityOffset = 0x200;
            const int fileFormatOffset = 0x400;
            const int nameOffset = 0x480;
            const int execOffset = 0x500;

            var header = new byte[headerSize];
            void W32(int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(o), v);
            void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(o), v);

            Encoding.ASCII.GetBytes("XEX2").CopyTo(header, 0);
            W32(4, 0x00000001);          // title module
            W32(8, headerSize);
            W32(0x10, securityOffset);
            W32(0x14, 3);
            W32(0x18, 0x000003FF); W32(0x1C, fileFormatOffset);
            W32(0x20, 0x000183FF); W32(0x24, nameOffset);
            W32(0x28, 0x00040006); W32(0x2C, execOffset);

            // Build blocks back to front so each block can carry the hash of the next.
            var blocks = new List<byte[]>();
            for (int i = 0; i < blockSizes.Length; i++)
            {
                var b = new byte[blockSizes[i]];
                rng.NextBytes(b);
                blocks.Add(b);
            }
            using (var sha = SHA1.Create())
            {
                for (int i = blocks.Count - 1; i >= 0; i--)
                {
                    uint nextSize = i + 1 < blocks.Count ? (uint)blocks[i + 1].Length : 0;
                    var nextHash = i + 1 < blocks.Count ? sha.ComputeHash(blocks[i + 1]) : new byte[20];
                    BinaryPrimitives.WriteUInt32BigEndian(blocks[i].AsSpan(0), nextSize);
                    Buffer.BlockCopy(nextHash, 0, blocks[i], 4, 20);
                }

                // File format info
                W32(fileFormatOffset, 0x24);
                W16(fileFormatOffset + 4, (ushort)(devkitEncrypted ? 1 : 0));
                W16(fileFormatOffset + 6, 2);
                W32(fileFormatOffset + 8, 0x8000);
                W32(fileFormatOffset + 12, (uint)blocks[0].Length);
                Buffer.BlockCopy(sha.ComputeHash(blocks[0]), 0, header, fileFormatOffset + 16, 20);
            }

            var name = Encoding.ASCII.GetBytes(originalName);
            W32(nameOffset, (uint)(name.Length + 1 + 4));
            name.CopyTo(header, nameOffset + 4);

            W32(execOffset + 0, 0xAABBCCDD);   // media id
            W32(execOffset + 4, 0x20034500);   // version 2.0.13.0
            W32(execOffset + 8, 0);
            W32(execOffset + 12, titleId);

            int total = 0;
            foreach (var b in blocks) total += b.Length;
            W32(securityOffset + 0, 0x184);
            W32(securityOffset + 4, (uint)(total * 3));   // image size (decompressed, larger)

            var encryptedKey = new byte[16];
            rng.NextBytes(encryptedKey);
            Buffer.BlockCopy(encryptedKey, 0, header, securityOffset + 0x150, 16);

            var data = new byte[total];
            int pos = 0;
            foreach (var b in blocks) { Buffer.BlockCopy(b, 0, data, pos, b.Length); pos += b.Length; }

            if (devkitEncrypted)
            {
                // Session key = AES-ECB-decrypt(devkit key, file key); data = AES-CBC-encrypt(session key, IV 0).
                byte[] sessionKey;
                using (var aes = Aes.Create())
                {
                    aes.Key = new byte[16];
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.None;
                    using (var dec = aes.CreateDecryptor())
                        sessionKey = dec.TransformFinalBlock(encryptedKey, 0, 16);
                }
                int padded = (data.Length + 15) & ~15;
                Array.Resize(ref data, padded);
                using (var aes = Aes.Create())
                {
                    aes.Key = sessionKey;
                    aes.IV = new byte[16];
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    using (var enc = aes.CreateEncryptor())
                        data = enc.TransformFinalBlock(data, 0, data.Length);
                }
                Array.Resize(ref data, total);
            }

            var file = new byte[headerSize + data.Length];
            Buffer.BlockCopy(header, 0, file, 0, headerSize);
            Buffer.BlockCopy(data, 0, file, headerSize, data.Length);
            return file;
        }

        /// <summary>
        /// Builds an XBE with the given section sizes and valid section digests.
        /// </summary>
        public static byte[] BuildXbe(string debugFileName, uint titleId, string titleName, int[] sectionSizes, int seed = 2)
        {
            var rng = new Random(seed);
            const uint baseAddress = 0x10000;
            const int headerSize = 0x1000;
            const int certOffset = 0x180;
            const int sectionTableOffset = 0x400;
            const int debugNameOffset = 0x800;

            int pos = headerSize;
            var rawAddresses = new int[sectionSizes.Length];
            for (int i = 0; i < sectionSizes.Length; i++)
            {
                rawAddresses[i] = pos;
                pos += (sectionSizes[i] + 0xFFF) & ~0xFFF;
            }
            int fileSize = rawAddresses[sectionSizes.Length - 1] + sectionSizes[sectionSizes.Length - 1];

            var file = new byte[fileSize];
            void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(o), v);

            Encoding.ASCII.GetBytes("XBEH").CopyTo(file, 0);
            W32(0x104, baseAddress);
            W32(0x108, headerSize);
            W32(0x10C, (uint)(pos + 0x10000));
            W32(0x118, baseAddress + certOffset);
            W32(0x11C, (uint)sectionSizes.Length);
            W32(0x120, baseAddress + sectionTableOffset);
            W32(0x150, baseAddress + debugNameOffset);

            Encoding.ASCII.GetBytes(debugFileName).CopyTo(file, debugNameOffset);
            W32(certOffset + 8, titleId);
            Encoding.Unicode.GetBytes(titleName).CopyTo(file, certOffset + 0x0C);

            using (var sha = SHA1.Create())
            {
                for (int i = 0; i < sectionSizes.Length; i++)
                {
                    var data = new byte[sectionSizes[i]];
                    rng.NextBytes(data);
                    Buffer.BlockCopy(data, 0, file, rawAddresses[i], data.Length);

                    int h = sectionTableOffset + i * 0x38;
                    W32(h + 0x04, (uint)(baseAddress + rawAddresses[i]));
                    W32(h + 0x08, (uint)sectionSizes[i]);
                    W32(h + 0x0C, (uint)rawAddresses[i]);
                    W32(h + 0x10, (uint)sectionSizes[i]);

                    sha.Initialize();
                    sha.TransformBlock(BitConverter.GetBytes((uint)sectionSizes[i]), 0, 4, null, 0);
                    sha.TransformFinalBlock(data, 0, data.Length);
                    Buffer.BlockCopy(sha.Hash, 0, file, h + 0x24, 20);
                }
            }

            return file;
        }
    }
}
