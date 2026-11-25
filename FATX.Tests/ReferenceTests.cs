using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.FileSystem;
using FATX.Reference;
using FATX.Testing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;

namespace FATX.Tests
{
    public class ReferenceTests : IDisposable
    {
        private const int Cluster = 0x4000;
        private readonly string dir = Path.Combine(Path.GetTempPath(), "fatx-reference-" + Guid.NewGuid().ToString("N"));
        private readonly string folder;

        private readonly byte[] big = Bytes(Cluster * 5 + 1234, 1);
        private readonly byte[] frag = Bytes(Cluster * 5, 2);
        private readonly byte[] small = Bytes(300, 3);
        private readonly byte[] mid = Bytes(2000, 4);
        private readonly byte[] active = Bytes(Cluster * 3, 5);
        private readonly byte[] sparse;

        public ReferenceTests()
        {
            folder = Path.Combine(dir, "build_01");
            Directory.CreateDirectory(Path.Combine(folder, "data", "levels"));

            // Mostly empty: only two of its six clusters can identify it.
            sparse = new byte[Cluster * 6];
            Bytes(Cluster, 6).CopyTo(sparse, 0);
            Bytes(Cluster, 7).CopyTo(sparse, Cluster * 4);

            File.WriteAllBytes(Path.Combine(folder, "default.xex"), big);
            File.WriteAllBytes(Path.Combine(folder, "data", "frag.pak"), frag);
            File.WriteAllBytes(Path.Combine(folder, "data", "small.txt"), small);
            File.WriteAllBytes(Path.Combine(folder, "data", "levels", "mid.dat"), mid);
            File.WriteAllBytes(Path.Combine(folder, "data", "active.bin"), active);
            File.WriteAllBytes(Path.Combine(folder, "data", "sparse.bin"), sparse);
            File.WriteAllBytes(Path.Combine(folder, "empty.txt"), new byte[0]);
            for (int i = 0; i < 3; i++)
                File.WriteAllBytes(Path.Combine(folder, "data", $"pack{i}.fpk"), Tagged("FPK1", 9000 + i * 500, 20 + i));
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        private static byte[] Bytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static byte[] Tagged(string tag, int length, int seed)
        {
            var data = Bytes(length, seed);
            Encoding.ASCII.GetBytes(tag).CopyTo(data, 0);
            return data;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        [Fact]
        public void ReferenceSetRoundtripsThroughJson()
        {
            var set = ReferenceSet.Build(folder);
            Assert.Equal("build_01", set.Name);
            Assert.Equal(9, set.Files.Count);                       // the empty file is left out
            Assert.Contains(set.Files, f => f.Path == "data/levels/mid.dat");

            var xex = set.Files.Single(f => f.Path == "default.xex");
            Assert.Equal(big.Length, xex.Size);
            Assert.Equal(6, xex.Blocks.Length);                     // five full blocks and the padded tail
            Assert.Equal(40, xex.Sha1.Length);

            var type = Assert.Single(set.Types);
            Assert.Equal(".fpk", type.Extension);
            Assert.Equal(0x46504B31u, type.Magic);
            Assert.Equal(3, type.Files);

            var path = Path.Combine(dir, "set.json");
            set.Save(path);
            var loaded = ReferenceSet.Load(path);

            Assert.Equal(set.Name, loaded.Name);
            Assert.Equal(set.BlockSize, loaded.BlockSize);
            Assert.Equal(set.Files.Count, loaded.Files.Count);
            for (int i = 0; i < set.Files.Count; i++)
            {
                Assert.Equal(set.Files[i].Path, loaded.Files[i].Path);
                Assert.Equal(set.Files[i].Size, loaded.Files[i].Size);
                Assert.Equal(set.Files[i].Sha1, loaded.Files[i].Sha1);
                Assert.Equal(set.Files[i].Head, loaded.Files[i].Head);
                Assert.Equal(set.Files[i].Blocks, loaded.Files[i].Blocks);
            }
            Assert.Equal(type.Magic, loaded.Types.Single().Magic);
            Assert.Throws<InvalidDataException>(() => { File.WriteAllText(path, "{\"hello\":1}"); ReferenceSet.Load(path); });
        }

        private Volume BuildVolume()
        {
            var b = new FatxImageBuilder(Platform.X360, 64L * 1024 * 1024) { Policy = AllocationPolicy.NextFree };

            // Still present on the volume.
            b.AddFile(b.Root, "active.bin", active, DateTime.Now);

            // Deleted long ago: no directory entries left, only the data.
            b.WriteOrphanData(big, 200);

            // An older build of the same file: clusters 1 and 3 were different.
            var older = (byte[])big.Clone();
            Bytes(Cluster, 50).CopyTo(older, Cluster);
            Bytes(Cluster, 51).CopyTo(older, Cluster * 3);
            b.WriteOrphanData(older, 300);

            // Small files. The second one has stale data after its end.
            b.WriteOrphanData(small, 400);
            b.WriteOrphanData(Concat(mid, Bytes(Cluster - mid.Length, 52)), 410);

            // In two pieces with two foreign clusters in between.
            b.WriteOrphanData(frag.Take(Cluster * 2).ToArray(), 1000);
            b.WriteOrphanData(Bytes(Cluster * 2, 53), 1002);
            b.WriteOrphanData(frag.Skip(Cluster * 2).ToArray(), 1004);

            b.WriteOrphanData(sparse, 1500);

            // Same type as the .fpk files of the set, but a file the set does not have.
            b.WriteOrphanData(Tagged("FPK1", 30000, 54), 2000);
            b.WriteOrphanData(Bytes(Cluster, 55), 2003);   // unrelated data ...
            b.AddFile(b.Root, "after.bin", new byte[100], DateTime.Now);

            return b.Mount(out _);
        }

        [Fact]
        public void FindsExactFragmentedSmallAndOlderCopies()
        {
            var set = ReferenceSet.Build(folder);
            var volume = BuildVolume();
            var matcher = new ReferenceMatcher(volume, new[] { set });
            var matches = matcher.Analyze();

            ReferenceMatch Exact(string path) => matches.Single(m => m.Exact && m.File.Path == path);

            var xex = Exact("default.xex");
            Assert.Equal(200u, xex.FirstCluster);
            Assert.False(xex.Fragmented);
            Assert.Equal(6, xex.Chain.Count);

            var pak = Exact("data/frag.pak");
            Assert.True(pak.Fragmented);
            Assert.Equal(new uint[] { 1000, 1001, 1004, 1005, 1006 }, pak.Chain.ToArray());

            Assert.Equal(400u, Exact("data/small.txt").FirstCluster);
            Assert.Equal(410u, Exact("data/levels/mid.dat").FirstCluster);
            Assert.Equal(1500u, Exact("data/sparse.bin").FirstCluster);

            // The older build: three of five comparable clusters are identical.
            var older = matches.Single(m => !m.Exact && m.File.Path == "default.xex");
            Assert.Equal(300u, older.FirstCluster);
            Assert.Equal(3, older.ClustersMatched);
            Assert.Equal(5, older.ClustersComparable);
            Assert.Equal(60, older.Percent, 1);

            // The file that still exists is not reported from free space ...
            Assert.DoesNotContain(matches, m => m.File.Path == "data/active.bin");
            Assert.DoesNotContain(matches, m => m.File.Path.EndsWith(".fpk"));

            // ... but is when every cluster is searched.
            var everywhere = new ReferenceMatcher(volume, new[] { set }) { FreeSpaceOnly = false }.Analyze();
            Assert.Contains(everywhere, m => m.Exact && m.File.Path == "data/active.bin");

            // Recovery: original paths, byte-exact content.
            var outDir = Path.Combine(dir, "out");
            var saved = ReferenceReport.SaveAll(matcher, matches, outDir);
            Assert.Equal(5, saved.exact);
            Assert.Equal(1, saved.partial);
            Assert.Equal(big, File.ReadAllBytes(Path.Combine(outDir, "build_01", "default.xex")));
            Assert.Equal(frag, File.ReadAllBytes(Path.Combine(outDir, "build_01", "data", "frag.pak")));
            Assert.Equal(mid, File.ReadAllBytes(Path.Combine(outDir, "build_01", "data", "levels", "mid.dat")));
            Assert.Equal(small, File.ReadAllBytes(Path.Combine(outDir, "build_01", "data", "small.txt")));

            var partial = Directory.GetFiles(Path.Combine(outDir, "build_01", "_partial")).Single();
            Assert.Contains("60pct", Path.GetFileName(partial));
            var partialBytes = File.ReadAllBytes(partial);
            Assert.Equal(big.Length, partialBytes.Length);
            Assert.Equal(big.Take(Cluster).ToArray(), partialBytes.Take(Cluster).ToArray());
            Assert.NotEqual(big.Skip(Cluster).Take(Cluster).ToArray(), partialBytes.Skip(Cluster).Take(Cluster).ToArray());

            var report = Path.Combine(dir, "report.json");
            ReferenceReport.Save(report, "test.img", volume, matches);
            Assert.Contains("1000-1001,1004-1006", File.ReadAllText(report));
            Assert.Contains("5 of 9 files found as exact copies", ReferenceReport.Summarize(matches, new[] { set }).Single());
        }

        [Fact]
        public void BlockSizeMustFitTheClusterSize()
        {
            var set = ReferenceSet.Build(folder, 0x8000);
            var volume = BuildVolume();
            Assert.Throws<ArgumentException>(() => new ReferenceMatcher(volume, new[] { set }));

            // Smaller blocks than a cluster work.
            var fine = ReferenceSet.Build(folder, 0x1000);
            var matches = new ReferenceMatcher(volume, new[] { fine }).Analyze();
            Assert.Contains(matches, m => m.Exact && m.File.Path == "default.xex" && m.FirstCluster == 200);
            Assert.Contains(matches, m => m.Exact && m.File.Path == "data/frag.pak" && m.Fragmented);
        }

        [Fact]
        public void CarverFindsOtherFilesOfLearnedTypes()
        {
            var set = ReferenceSet.Build(folder);
            var volume = BuildVolume();

            var plain = new FileCarver(volume, FileCarverInterval.Cluster, 0).Analyze(CancellationToken.None, null);
            Assert.DoesNotContain(plain, f => f.Offset == 1999L * Cluster);

            var carver = new FileCarver(volume, FileCarverInterval.Cluster, 0) { ExtraSignatures = set.Types };
            var found = carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>().ToList();
            var fpk = found.Single(f => f.Offset == 1999L * Cluster);
            Assert.Equal("FPK", fpk.Info.Format);
            Assert.EndsWith(".fpk", fpk.FileName);
            Assert.False(fpk.Damaged);
            // Sized up to twice the largest known file of the type, within the free space.
            Assert.Equal(20000, fpk.FileSize);
        }
    }
}
