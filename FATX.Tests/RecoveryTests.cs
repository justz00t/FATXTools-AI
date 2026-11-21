using FATX;
using FATX.Analyzers;
using FATX.FileSystem;
using FATX.Formats;
using FATX.Recovery;
using FATX.Testing;
using FATXTools.Database;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace FATX.Tests
{
    public class RecoveryTests
    {
        private static readonly DateTime T0 = new DateTime(2010, 5, 1, 12, 0, 0);
        private const long VolumeSize = 64L * 1024 * 1024;

        private static byte[] Pattern(int length, int seed)
        {
            var b = new byte[length];
            new Random(seed).NextBytes(b);
            return b;
        }

        private static FileDatabase Analyze(Volume volume, ChainStrategy strategy = ChainStrategy.SkipOlderAllocated)
        {
            var analyzer = new MetadataAnalyzer(volume, volume.BytesPerCluster, volume.FileAreaLength);
            var dirents = analyzer.Analyze(CancellationToken.None, null);
            var db = new FileDatabase(volume) { ChainStrategy = strategy };
            foreach (var d in dirents) db.AddFile(d, true);
            db.Update();
            return db;
        }

        private static byte[] ReadFile(Volume volume, DatabaseFile f)
        {
            var src = new ClusterChainSource(volume, f.ClusterChain, f.FileSize);
            return src.ReadBytes(0, (int)src.Length);
        }

        [Theory]
        [InlineData(Platform.Xbox)]
        [InlineData(Platform.X360)]
        public void MountsActiveFileSystem(Platform platform)
        {
            var b = new FatxImageBuilder(platform, VolumeSize);
            var dir = b.AddDirectory(b.Root, "Content", T0);
            var data = Pattern(40000, 1);
            b.AddFile(dir, "default.xex", data, T0);

            var volume = b.Mount(out _);
            var root = volume.GetRoot();
            Assert.Single(root);
            Assert.Equal("Content", root[0].FileName);
            var file = root[0].Children.Single();
            Assert.Equal("default.xex", file.FileName);
            Assert.Equal((uint)data.Length, file.FileSize);
            Assert.Equal(T0, file.CreationTime.AsDateTime());
        }

        [Theory]
        [InlineData(Platform.Xbox)]
        [InlineData(Platform.X360)]
        public void RecoversDeletedFile(Platform platform)
        {
            var b = new FatxImageBuilder(platform, VolumeSize);
            var dir = b.AddDirectory(b.Root, "Build", T0);
            var data = Pattern(100000, 2);
            var node = b.AddFile(dir, "game.xex", data, T0.AddMinutes(1));
            b.Delete(node);

            var volume = b.Mount(out _);
            var db = Analyze(volume);
            var recovered = db.GetFiles().Values.Single(f => f.FileName == "game.xex");
            Assert.True(recovered.IsDeleted);
            Assert.Equal("/Build/game.xex", recovered.GetFullPath());
            Assert.Equal(data, ReadFile(volume, recovered));
        }

        /// <summary>
        /// A file written into fragmented free space and then deleted. The old
        /// contiguous assumption returns the data of the file sitting in the
        /// middle; skipping older allocated clusters gets the right data.
        /// </summary>
        [Fact]
        public void RecoversFragmentedDeletedFile()
        {
            var b = new FatxImageBuilder(Platform.X360, VolumeSize) { Policy = AllocationPolicy.FirstFree };
            int cluster = (int)b.BytesPerCluster;
            var a = b.AddFile(b.Root, "a.bin", Pattern(cluster * 2, 10), T0);
            var hole = b.AddFile(b.Root, "hole.bin", Pattern(cluster * 2, 11), T0.AddMinutes(1));
            var keep = b.AddFile(b.Root, "keep.bin", Pattern(cluster * 3, 12), T0.AddMinutes(2));
            b.Delete(hole);

            // Written later: fills the 2 cluster hole, skips keep.bin, continues after it.
            var fragmentedData = Pattern(cluster * 5, 13);
            var fragmented = b.AddFile(b.Root, "fragmented.xex", fragmentedData, T0.AddMinutes(3));
            Assert.NotEqual(fragmented.Chain[2], fragmented.Chain[1] + 1);
            b.Delete(fragmented);

            var volume = b.Mount(out _);

            var smart = Analyze(volume, ChainStrategy.SkipOlderAllocated);
            var smartFile = smart.GetFiles().Values.Single(f => f.FileName == "fragmented.xex");
            Assert.Equal(fragmented.Chain, smartFile.ClusterChain);
            Assert.Equal(fragmentedData, ReadFile(volume, smartFile));

            var old = Analyze(volume, ChainStrategy.Contiguous);
            var oldFile = old.GetFiles().Values.Single(f => f.FileName == "fragmented.xex");
            Assert.NotEqual(fragmentedData, ReadFile(volume, oldFile));
        }

        [Fact]
        public void FindsDeletedEntriesWithCombinedAttributes()
        {
            var b = new FatxImageBuilder(Platform.Xbox, VolumeSize);
            var dir = b.AddDirectory(b.Root, "Saves", T0, attributes: 0x30); // Directory | Archive
            var f = b.AddFile(dir, "save.dat", Pattern(1000, 3), T0, attributes: 0x06); // Hidden | System
            b.Delete(dir);

            var volume = b.Mount(out _);
            var db = Analyze(volume);
            Assert.Contains(db.GetFiles().Values, x => x.FileName == "Saves" && x.IsDeleted);
            var save = db.GetFiles().Values.Single(x => x.FileName == "save.dat");
            Assert.Equal("/Saves/save.dat", save.GetFullPath());
        }

        [Fact]
        public void HandlesNonDefaultClusterSize()
        {
            // 4KB clusters: the old code assumed 256 entries (16KB) per
            // directory cluster and crashed.
            var b = new FatxImageBuilder(Platform.X360, VolumeSize, sectorsPerCluster: 8);
            var dir = b.AddDirectory(b.Root, "Dir", T0);
            for (int i = 0; i < 70; i++)   // more than one 4KB cluster of entries
                b.AddFile(dir, $"file{i:D2}.bin", Pattern(5000, i), T0);
            var victim = dir.Children[65];
            b.Delete(victim);

            var volume = b.Mount(out _);
            Assert.Equal(4096u, volume.BytesPerCluster);
            Assert.Equal(70, volume.GetRoot()[0].Children.Count);

            var db = Analyze(volume);
            var rec = db.GetFiles().Values.Single(x => x.FileName == "file65.bin");
            Assert.True(rec.IsDeleted);
            Assert.Equal(victim.Data, ReadFile(volume, rec));
        }

        [Fact]
        public void ScansLastCluster()
        {
            var b = new FatxImageBuilder(Platform.Xbox, VolumeSize);
            var dir = b.AddDirectory(b.Root, "Dir", T0);
            b.AddFile(dir, "x.bin", Pattern(10, 1), T0);
            b.Delete(dir);
            var volume0 = b.Mount(out var reader0);

            // Move the deleted directory's cluster (containing x.bin's entry) to the very last cluster.
            var last = volume0.ClusterCount;
            var ms = (MemoryStream)reader0.BaseStream;
            var data = volume0.ReadCluster(dir.Chain[0]);
            ms.Position = volume0.ClusterToPhysicalOffset(last);
            ms.Write(data, 0, data.Length);

            var reader = new DriveReader(new MemoryStream(ms.ToArray()));
            reader.AddPartition("Test", 0, VolumeSize);
            var volume = reader.Partitions[0];
            volume.Mount();
            var analyzer = new MetadataAnalyzer(volume, volume.BytesPerCluster, volume.FileAreaLength);
            var found = analyzer.Analyze(CancellationToken.None, null);
            Assert.Contains(found, d => d.FileName == "x.bin" && d.Cluster == last);
        }

        [Fact]
        public void X360DirectoryEntryDoesNotModifyBuffer()
        {
            var buf = new byte[0x40];
            new Random(5).NextBytes(buf);
            var copy = (byte[])buf.Clone();
            var d1 = new DirectoryEntry(Platform.X360, buf, 0);
            var d2 = new DirectoryEntry(Platform.X360, buf, 0);
            Assert.Equal(copy, buf);
            Assert.Equal(d1.FirstCluster, d2.FirstCluster);
            Assert.Equal(d1.FileSize, d2.FileSize);
        }

        [Fact]
        public void SelfReferencingDeletedDirectoryDoesNotLoop()
        {
            var b = new FatxImageBuilder(Platform.Xbox, VolumeSize);
            var dir = b.AddDirectory(b.Root, "Loop", T0);
            var child = b.AddDirectory(dir, "Child", T0);
            b.Delete(dir);
            // Point the deleted child at the cluster that holds its own entry.
            b.PatchEntry(child, e => BitConverter.GetBytes(dir.Chain[0]).CopyTo(e, 0x2C));

            var volume = b.Mount(out _);
            var db = Analyze(volume);
            var c = db.GetFiles().Values.Single(x => x.FileName == "Child");
            Assert.NotSame(c, c.GetParent());
            // Must terminate.
            Assert.True(c.CountFiles() >= 0);
            Assert.False(string.IsNullOrEmpty(c.GetFullPath()));
        }

        [Fact]
        public void HugeDeletedFileNearEndDoesNotCrashIntegrityAnalyzer()
        {
            var b = new FatxImageBuilder(Platform.Xbox, VolumeSize);
            var node = b.AddFile(b.Root, "big.bin", Pattern(100, 1), T0);
            b.Delete(node);
            // Move the first cluster to the end and claim a size larger than what is left.
            b.PatchEntry(node, e =>
            {
                BitConverter.GetBytes(b.ClusterCount - 2).CopyTo(e, 0x2C);
                BitConverter.GetBytes(50u * 1024 * 1024).CopyTo(e, 0x30);
            });

            var volume = b.Mount(out _);
            var db = Analyze(volume);
            var integrity = new IntegrityAnalyzer(volume, db);
            integrity.Update();
            var f = db.GetFiles().Values.Single(x => x.FileName == "big.bin");
            Assert.All(f.ClusterChain, c => Assert.True(c <= volume.ClusterCount));
        }

        [Fact]
        public void ZeroFirstClusterProducesEmptyChain()
        {
            var b = new FatxImageBuilder(Platform.Xbox, VolumeSize);
            var node = b.AddFile(b.Root, "zero.bin", Pattern(100, 1), T0);
            b.Delete(node);
            b.PatchEntry(node, e => BitConverter.GetBytes(0u).CopyTo(e, 0x2C));
            var volume = b.Mount(out _);
            var db = Analyze(volume);
            var f = db.GetFiles().Values.Single(x => x.FileName == "zero.bin");
            Assert.Empty(f.ClusterChain);
        }

        [Fact]
        public void RecovererCreatesUniqueNamesAndTruncates()
        {
            var b = new FatxImageBuilder(Platform.X360, VolumeSize) { Policy = AllocationPolicy.NextFree };
            var dir = b.AddDirectory(b.Root, "Dir", T0);
            var first = b.AddFile(dir, "default.xex", Pattern(3000, 1), T0);
            b.Delete(first);
            var second = b.AddFile(dir, "default.xex", Pattern(1000, 2), T0.AddMinutes(5));

            var volume = b.Mount(out _);
            var db = Analyze(volume);
            var outDir = Path.Combine(Path.GetTempPath(), "fatx-test-" + Guid.NewGuid());
            try
            {
                // A stale larger file must not leave trailing bytes behind.
                Directory.CreateDirectory(Path.Combine(outDir, "Dir"));
                File.WriteAllBytes(Path.Combine(outDir, "Dir", "default.xex"), new byte[10000]);

                var recoverer = new FileRecoverer(volume) { Overwrite = true };
                recoverer.SaveAll(db.GetRootFiles(), outDir);
                var written = Directory.GetFiles(Path.Combine(outDir, "Dir")).Select(File.ReadAllBytes).ToList();
                Assert.Equal(2, written.Count);
                Assert.Contains(written, w => w.SequenceEqual(first.Data));
                Assert.Contains(written, w => w.SequenceEqual(second.Data));
            }
            finally
            {
                Directory.Delete(outDir, true);
            }
        }

        [Fact]
        public void SanitizesFileNames()
        {
            Assert.Equal("a_b", FileRecoverer.SanitizeFileName("a:b"));
            Assert.Equal("_CON.txt", FileRecoverer.SanitizeFileName("CON.txt"));
            Assert.Equal("name", FileRecoverer.SanitizeFileName("name. "));
        }

        [Fact]
        public void CorruptFatLoopIsDetected()
        {
            var b = new FatxImageBuilder(Platform.Xbox, VolumeSize);
            var f = b.AddFile(b.Root, "loop.bin", Pattern((int)b.BytesPerCluster * 3, 1), T0);
            var volume = b.Mount(out _);
            // Create a loop in the FAT in memory.
            volume.FileAllocationTable[f.Chain[2]] = f.Chain[0];
            Assert.Null(volume.GetClusterChain(f.Chain[0]));
            volume.FileAllocationTable[f.Chain[2]] = (uint)volume.FileAllocationTable.Length;
            Assert.Null(volume.GetClusterChain(f.Chain[0]));
        }
    }
}
