using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.Formats;
using FATX.Imaging;
using FATX.Recovery;
using FATX.Testing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Xunit;

namespace FATX.Tests
{
    public class ImagingTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "fatx-imaging-" + Guid.NewGuid().ToString("N"));

        public ImagingTests()
        {
            Directory.CreateDirectory(dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        /// <summary>
        /// Deterministic test data: text-like, repetitive, random and empty stretches.
        /// </summary>
        private static byte[] Sample(int length, int seed = 7)
        {
            var data = new byte[length];
            uint x = (uint)seed * 2654435761u + 1;
            int i = 0;
            while (i < length)
            {
                x = x * 1664525u + 1013904223u;
                int run = (int)(x >> 20) % 3000 + 1;
                int kind = (int)(x >> 8) % 4;
                for (int j = 0; j < run && i < length; j++, i++)
                {
                    switch (kind)
                    {
                        case 0: data[i] = 0; break;
                        case 1: data[i] = (byte)("FATX recovery "[j % 14]); break;
                        case 2: x = x * 1664525u + 1013904223u; data[i] = (byte)(x >> 24); break;
                        default: data[i] = (byte)(j / 7); break;
                    }
                }
            }
            return data;
        }

        private static byte[] Roundtrip(byte[] data)
        {
            var packed = new byte[SynLzo.MaxCompressedLength(data.Length)];
            int n = SynLzo.Compress(data, 0, data.Length, packed, 0);
            var back = new byte[data.Length];
            SynLzo.Decompress(packed, 0, n, back, 0, back.Length);
            return back;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(21)]
        [InlineData(512)]
        [InlineData(65536)]
        [InlineData(200000)]
        public void LzoRoundtrip(int length)
        {
            var data = Sample(length);
            Assert.Equal(data, Roundtrip(data));

            var zeros = new byte[length];
            Assert.Equal(zeros, Roundtrip(zeros));

            var rnd = new byte[length];
            new Random(length).NextBytes(rnd);
            Assert.Equal(rnd, Roundtrip(rnd));
        }

        [Fact]
        public void LzoMatchesReferenceBitstream()
        {
            // These streams were produced by the reference implementation of
            // this compressor and decoded with the unimgc decoder (the tool
            // that reads images written by HDD Raw Copy Tool).
            var input = System.Text.Encoding.ASCII.GetBytes("abcdabcdabcdabcdXYZabcdabcd-0123456789-0123456789!");
            var packed = new byte[SynLzo.MaxCompressedLength(input.Length)];
            int n = SynLzo.Compress(input, 0, input.Length, packed, 0);
            Assert.Equal("056162636461626364ef0058595ae801082d3031323334353637383929290021110000", BitConverter.ToString(packed, 0, n).Replace("-", "").ToLowerInvariant());

            var data = Sample(300000, 3);
            packed = new byte[SynLzo.MaxCompressedLength(data.Length)];
            n = SynLzo.Compress(data, 0, data.Length, packed, 0);
            Assert.Equal(87867, n);
            using (var sha = SHA1.Create())
                Assert.Equal("4204eba7ec71a4bca881c81876cf117a50a03cc6", BitConverter.ToString(sha.ComputeHash(packed, 0, n)).Replace("-", "").ToLowerInvariant());
        }

        [Fact]
        public void LzoRejectsCorruptStreams()
        {
            var data = Sample(5000);
            var packed = new byte[SynLzo.MaxCompressedLength(data.Length)];
            int n = SynLzo.Compress(data, 0, data.Length, packed, 0);
            var back = new byte[data.Length];

            // Truncated, wrong length and garbage must fail cleanly.
            Assert.Throws<InvalidDataException>(() => SynLzo.Decompress(packed, 0, n / 2, back, 0, back.Length));
            Assert.Throws<InvalidDataException>(() => SynLzo.Decompress(packed, 0, n, back, 0, back.Length - 1));
            var garbage = new byte[4000];
            new Random(5).NextBytes(garbage);
            for (int i = 0; i < 50; i++)
            {
                garbage[0] = (byte)(i * 5);
                try { SynLzo.Decompress(garbage, 0, garbage.Length, back, 0, back.Length); }
                catch (InvalidDataException) { }
            }
        }

        private sealed class MemorySource : IDiskSource
        {
            private readonly byte[] data;
            public List<ByteRange> Bad = new List<ByteRange>();
            public int FailFirstAttempts;                 // transient errors
            private readonly Dictionary<long, int> attempts = new Dictionary<long, int>();
            public long BytesRequested;
            public Action<long> OnRead;                   // called with the bytes requested so far

            public MemorySource(byte[] data) { this.data = data; }

            public string Name => "memory";
            public long Length => data.Length;
            public int SectorSize => 512;
            public string Model => "TESTDISK 1";
            public string Revision => "1.0";
            public string Serial => "SN123";

            public void Read(long offset, byte[] buffer, int index, int count)
            {
                BytesRequested += count;
                OnRead?.Invoke(BytesRequested);
                foreach (var b in Bad)
                {
                    if (offset < b.Offset + b.Length && b.Offset < offset + count)
                    {
                        if (FailFirstAttempts > 0 && count == SectorSize)
                        {
                            attempts.TryGetValue(offset, out var a);
                            attempts[offset] = a + 1;
                            if (a >= FailFirstAttempts)
                                break;
                        }
                        throw new IOException("Data error (cyclic redundancy check).");
                    }
                }
                Buffer.BlockCopy(data, (int)offset, buffer, index, count);
            }

            public void Dispose() { }
        }

        private static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static byte[] ReadAll(Stream s)
        {
            var ms = new MemoryStream();
            s.Position = 0;
            s.CopyTo(ms);
            return ms.ToArray();
        }

        [Theory]
        [InlineData(".img")]
        [InlineData(".imgc")]
        public void DumpRoundtripWithHashes(string extension)
        {
            var disk = Sample(5 * 1024 * 1024 + 512 * 3);
            Array.Clear(disk, 1024 * 1024, 2 * 1024 * 1024);   // a long empty stretch
            var target = Path.Combine(dir, "disk" + extension);

            var result = new DiskImager(new MemorySource(disk), target, new ImagingOptions { Hashes = { "sha1", "md5" } }).Run();

            Assert.True(result.Completed);
            Assert.Empty(result.BadRanges);
            Assert.Equal(Sha1(disk), result.Hashes["SHA1"]);
            Assert.True(File.Exists(target + ".dump.json"));

            using (var image = ImageFile.OpenRead(target))
            {
                Assert.Equal(extension == ".imgc", image is ImgcStream);
                Assert.Equal(disk.Length, image.Length);
                Assert.Equal(disk, ReadAll(image));

                // Random access.
                var rnd = new Random(11);
                var buf = new byte[70000];
                for (int i = 0; i < 200; i++)
                {
                    int offset = rnd.Next(disk.Length);
                    int count = Math.Min(rnd.Next(1, buf.Length), disk.Length - offset);
                    image.Position = offset;
                    int got = 0;
                    while (got < count)
                    {
                        int n = image.Read(buf, got, count - got);
                        Assert.True(n > 0);
                        got += n;
                    }
                    Assert.Equal(disk.AsSpan(offset, count).ToArray(), buf.AsSpan(0, count).ToArray());
                }
            }

            if (extension == ".imgc")
            {
                Assert.True(new FileInfo(target).Length < disk.Length * 3 / 4);
                using (var imgc = new ImgcStream(target))
                {
                    Assert.Equal("TESTDISK 1", imgc.Header.Model);
                    Assert.Equal("SN123", imgc.Header.Serial);
                    Assert.Equal(512L, imgc.Header.SectorSize);
                    Assert.Empty(imgc.Warnings);
                }
            }
        }

        [Theory]
        [InlineData(".img")]
        [InlineData(".imgc")]
        public void UnreadableSectorsAreZeroFilledAndReported(string extension)
        {
            var disk = Sample(4 * 1024 * 1024);
            var source = new MemorySource(disk);
            source.Bad.Add(new ByteRange { Offset = 512 * 1000, Length = 512 * 3 });
            source.Bad.Add(new ByteRange { Offset = 3 * 1024 * 1024 + 512, Length = 512 });
            var target = Path.Combine(dir, "bad" + extension);

            var result = new DiskImager(source, target, new ImagingOptions { Hashes = { "sha1" } }).Run();

            var expected = (byte[])disk.Clone();
            Array.Clear(expected, 512 * 1000, 512 * 3);
            Array.Clear(expected, 3 * 1024 * 1024 + 512, 512);

            Assert.True(result.Completed);
            Assert.Equal(512 * 4, result.BadBytes);
            Assert.Equal(2, result.BadRanges.Count);
            Assert.Equal(512 * 1000, result.BadRanges[0].Offset);
            Assert.Equal(Sha1(expected), result.Hashes["SHA1"]);
            using (var image = ImageFile.OpenRead(target))
                Assert.Equal(expected, ReadAll(image));
            Assert.Contains("\"unreadableBytes\": 2048", File.ReadAllText(target + ".dump.json"));
        }

        [Fact]
        public void SectorsThatReadOnRetryAreRecovered()
        {
            var disk = Sample(2 * 1024 * 1024);
            var source = new MemorySource(disk) { FailFirstAttempts = 2 };
            source.Bad.Add(new ByteRange { Offset = 512 * 77, Length = 512 });
            var target = Path.Combine(dir, "retry.img");

            var result = new DiskImager(source, target, new ImagingOptions { Retries = 2 }).Run();

            Assert.Empty(result.BadRanges);
            Assert.Equal(disk, File.ReadAllBytes(target));
        }

        [Theory]
        [InlineData(".img")]
        [InlineData(".imgc")]
        public void InterruptedDumpResumes(string extension)
        {
            var disk = Sample(12 * 1024 * 1024, 9);
            var target = Path.Combine(dir, "resume" + extension);

            using (var cts = new CancellationTokenSource())
            {
                // "Pull the plug" a third of the way in.
                var interrupted = new MemorySource(disk) { OnRead = requested => { if (requested >= 4 * 1024 * 1024) cts.Cancel(); } };
                var first = new DiskImager(interrupted, target, new ImagingOptions()).Run(null, cts.Token);
                Assert.True(first.BytesDone >= 1024 * 1024);
                Assert.False(first.Completed);
                Assert.True(first.BytesDone < disk.Length);
            }

            var source = new MemorySource(disk);
            var second = new DiskImager(source, target, new ImagingOptions { Resume = true, Hashes = { "sha1" } }).Run();
            Assert.True(second.Completed);
            Assert.True(second.Resumed);
            Assert.True(source.BytesRequested < disk.Length);        // did not start over
            Assert.Equal(Sha1(disk), second.Hashes["SHA1"]);
            using (var image = ImageFile.OpenRead(target))
                Assert.Equal(disk, ReadAll(image));
        }

        [Fact]
        public void TruncatedAndCorruptImgcStillOpens()
        {
            var disk = Sample(3 * 1024 * 1024, 5);
            var target = Path.Combine(dir, "t.imgc");
            new DiskImager(new MemorySource(disk), target, new ImagingOptions()).Run();

            var bytes = File.ReadAllBytes(target);

            // Cut the file in the middle of a block.
            var cut = Path.Combine(dir, "cut.imgc");
            File.WriteAllBytes(cut, bytes.Take(bytes.Length * 2 / 3).ToArray());
            using (var image = new ImgcStream(cut))
            {
                Assert.NotEmpty(image.Warnings);
                Assert.Equal(disk.Length, image.Length);
                Assert.True(image.DataLength > 0 && image.DataLength < disk.Length);
                var all = ReadAll(image);
                Assert.Equal(disk.Take((int)image.DataLength).ToArray(), all.Take((int)image.DataLength).ToArray());
                Assert.True(all.Skip((int)image.DataLength).All(v => v == 0));
            }

            // Damage the inside of the first compressed block.
            var corrupt = Path.Combine(dir, "corrupt.imgc");
            for (int i = 0; i < 200; i++)
                bytes[ImgcHeader.Size + 12 + i] = 0xFF;
            File.WriteAllBytes(corrupt, bytes);
            using (var image = new ImgcStream(corrupt))
            {
                var all = ReadAll(image);
                Assert.Equal(disk.Length, all.Length);
                Assert.NotEmpty(image.Warnings);
                // Everything after the damaged block is intact.
                Assert.Equal(disk.Skip(0x10000).ToArray(), all.Skip(0x10000).ToArray());
            }
        }

        [Fact]
        public void RecoveryWorksDirectlyOnCompressedImage()
        {
            var b = new FatxImageBuilder(Platform.X360, 64L * 1024 * 1024);
            var payload = Sample(100000, 4);
            b.AddFile(b.Root, "keep.bin", payload, DateTime.Now);
            var gone = b.AddFile(b.Root, "gone.bin", Sample(50000, 6), DateTime.Now);
            b.Delete(gone);

            // Xbox 360 development kit layout: a partition table (in sectors)
            // at the start of the drive.
            const long partitionOffset = 0x100000;
            var raw = Path.Combine(dir, "x360.img");
            using (var fs = new FileStream(raw, FileMode.Create, FileAccess.ReadWrite))
            {
                var table = new byte[0x200];
                table[1] = 0x02;                                        // 0x00020000
                table[10] = (byte)((partitionOffset / 0x200) >> 8);      // Partition1 offset
                table[11] = (byte)(partitionOffset / 0x200);
                table[13] = 0x02;                                       // Partition1 length: 0x20000 sectors
                fs.Write(table, 0, table.Length);
                b.WriteTo(fs, partitionOffset);
            }

            var packed = Path.Combine(dir, "x360.imgc");
            using (var source = ImageFile.OpenSource(raw))
                Assert.True(new DiskImager(source, packed, new ImagingOptions()).Run().Completed);
            Assert.True(new FileInfo(packed).Length < 8 * 1024 * 1024);

            using (var session = new RecoverySession(packed))
            {
                var p = session.GetPartition("Partition1");
                session.RunMetadataAnalyzer(p);
                var files = RecoverySession.AllFiles(p).ToList();
                Assert.Contains(files, f => f.FileName == "keep.bin" && !f.IsDeleted);
                Assert.Contains(files, f => f.FileName == "gone.bin" && f.IsDeleted);

                var outDir = Path.Combine(dir, "out");
                var recoverer = new FileRecoverer(p.Volume);
                recoverer.Save(files.First(f => f.FileName == "keep.bin"), outDir);
                Assert.Equal(payload, File.ReadAllBytes(Path.Combine(outDir, "keep.bin")));
            }
        }
    }

    public class CarverCompletenessTests
    {
        [Fact]
        public void DamagedHeadersAreKeptAndFlagged()
        {
            var b = new FatxImageBuilder(Platform.X360, 64L * 1024 * 1024);
            var xex = ExecutableBuilders.BuildXex("default.exe", 0x58410889, new[] { 0x9000, 0x4000 });
            // Wipe everything after the magic: the header is gone but the data is still there.
            var damaged = (byte[])xex.Clone();
            for (int i = 4; i < 0x200; i++)
                damaged[i] = 0xEE;
            b.WriteOrphanData(damaged, 100);
            b.WriteOrphanData(xex, 300);
            var volume = b.Mount(out _);

            var carver = new FileCarver(volume, FileCarverInterval.Cluster, 0);
            var found = carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>().ToList();

            var bad = found.Single(f => f.Damaged);
            Assert.Equal(99L * volume.BytesPerCluster, bad.Offset);
            Assert.Contains("damaged", bad.FileName);
            Assert.True(bad.FileSize >= damaged.Length);
            Assert.True(bad.FileSize <= 200L * volume.BytesPerCluster);
            Assert.Single(found, f => !f.Damaged && f.Info.Format == "XEX2");

            carver = new FileCarver(volume, FileCarverInterval.Cluster, 0) { KeepDamaged = false };
            Assert.DoesNotContain(carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>(), f => f.Damaged);
        }

        [Fact]
        public void FragmentedDeletedExecutableIsRebuiltAroundUsedClusters()
        {
            var b = new FatxImageBuilder(Platform.X360, 64L * 1024 * 1024);
            int cluster = (int)b.BytesPerCluster;
            var xex = ExecutableBuilders.BuildXex("frag.exe", 0x12345678, new[] { 0x8000, 0x8000, 0x8000, 0x8000 });

            // An active file sits in the middle of where the deleted file was written.
            var filler = b.AddFile(b.Root, "filler.bin", new byte[cluster * 10], DateTime.Now);
            var blocker = b.AddFile(b.Root, "blocker.bin", new byte[cluster * 2], DateTime.Now);
            b.Delete(filler);   // leaves free clusters in front of the blocker
            uint hole = blocker.Chain[0];
            Assert.Equal(hole + 1, blocker.Chain[1]);

            uint first = hole - 2;
            var head = xex.Take(cluster * 2).ToArray();
            var tail = xex.Skip(cluster * 2).ToArray();
            b.WriteOrphanData(head, first);
            b.WriteOrphanData(tail, hole + 2);
            var volume = b.Mount(out _);

            var carver = new FileCarver(volume, FileCarverInterval.Cluster, 0) { FreeSpaceOnly = true, VerifyFiles = true };
            var found = carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>().ToList();

            var x = found.Single(f => f.Info.Format == "XEX2");
            Assert.NotNull(x.ClusterChain);
            Assert.Equal(VerificationStatus.Verified, x.Info.Verification);
            Assert.Equal(xex.Length, x.FileSize);

            var outDir = Path.Combine(Path.GetTempPath(), "fatx-frag-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saved = new FileRecoverer(volume).Save(x, outDir);
                Assert.Equal(xex, File.ReadAllBytes(saved.OutputPath));
            }
            finally
            {
                Directory.Delete(outDir, true);
            }
        }

        [Fact]
        public void BinkAndPngAreCarvedWithExactSizes()
        {
            var bik = new byte[0x5000];
            System.Text.Encoding.ASCII.GetBytes("BIKi").CopyTo(bik, 0);
            BitConverter.GetBytes((uint)(bik.Length - 8)).CopyTo(bik, 4);
            BitConverter.GetBytes(120u).CopyTo(bik, 8);
            BitConverter.GetBytes(640u).CopyTo(bik, 0x14);
            BitConverter.GetBytes(480u).CopyTo(bik, 0x18);
            var info = FormatDetector.Identify(new ArraySource(bik));
            Assert.Equal("BIK", info.Format);
            Assert.Equal(bik.Length, info.FileSize);

            var png = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
            void Chunk(string type, byte[] data)
            {
                png.AddRange(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
                png.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
                png.AddRange(data);
                png.AddRange(new byte[4]);
            }
            Chunk("IHDR", new byte[] { 0, 0, 1, 0, 0, 0, 0, 0x80, 8, 2, 0, 0, 0 });
            Chunk("IDAT", new byte[3000]);
            Chunk("IEND", new byte[0]);
            int size = png.Count;
            png.AddRange(new byte[500]);   // slack after the file
            info = FormatDetector.Identify(new ArraySource(png.ToArray()));
            Assert.Equal("PNG", info.Format);
            Assert.Equal(size, info.FileSize);
            Assert.True(info.FileSizeExact);
        }
    }
}
