using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.FileSystem;
using FATX.Formats;
using FATX.Testing;
using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace FATX.Tests
{
    public class FormatTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void XexSizeAndHashChain(bool encrypted)
        {
            var xex = ExecutableBuilders.BuildXex("halo3.exe", 0x4D5307E6, new[] { 0x8000, 0x8000, 0x3000 }, encrypted);
            var info = FormatDetector.Identify(new ArraySource(xex), verify: true);
            Assert.Equal("XEX2", info.Format);
            Assert.Equal("halo3.exe", info.EmbeddedName);
            Assert.Equal(0x4D5307E6u, info.TitleId);
            Assert.Equal(xex.Length, info.FileSize);
            Assert.True(info.FileSizeExact);
            Assert.Equal(VerificationStatus.Verified, info.Verification);
            Assert.Equal("halo3.xex", info.SuggestFileName("x"));
        }

        [Fact]
        public void XexCorruptionIsDetected()
        {
            var xex = ExecutableBuilders.BuildXex("a.exe", 1, new[] { 0x8000, 0x8000 });
            xex[0x1000 + 0x8000 + 100] ^= 0xFF;   // damage the second block
            var info = FormatDetector.Identify(new ArraySource(xex), verify: true);
            Assert.Equal(VerificationStatus.Failed, info.Verification);
            Assert.False(info.FileSizeExact);
        }

        [Fact]
        public void XbeSizeAndDigests()
        {
            var xbe = ExecutableBuilders.BuildXbe("d:\\build\\default.exe", 0x4D530004, "Test Game", new[] { 0x5000, 0x1234 });
            var info = FormatDetector.Identify(new ArraySource(xbe), verify: true);
            Assert.Equal("XBE", info.Format);
            Assert.Equal(xbe.Length, info.FileSize);
            Assert.Equal("Test Game", info.Title);
            Assert.Equal(0x4D530004u, info.TitleId);
            Assert.Equal(VerificationStatus.Verified, info.Verification);
            Assert.Equal("default.xbe", info.SuggestFileName("x"));

            xbe[0x1000 + 10] ^= 1;
            Assert.Equal(VerificationStatus.Failed, FormatDetector.Identify(new ArraySource(xbe), verify: true).Verification);
        }

        [Fact]
        public void CarverFindsExecutablesWithExactSizes()
        {
            var b = new FatxImageBuilder(Platform.X360, 64L * 1024 * 1024);
            var xex = ExecutableBuilders.BuildXex("default.exe", 0x58410889, new[] { 0x9000, 0x4000 });
            var xbe = ExecutableBuilders.BuildXbe("default.exe", 0x4D530004, "Game", new[] { 0x3000 });
            b.WriteOrphanData(xex, 100);
            b.WriteOrphanData(xbe, 200);
            var active = b.AddFile(b.Root, "active.xex", ExecutableBuilders.BuildXex("act.exe", 2, new[] { 0x100 }), DateTime.Now);

            var volume = b.Mount(out _);
            var carver = new FileCarver(volume, FileCarverInterval.Cluster, 0) { VerifyFiles = true };
            var found = carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>().ToList();

            var x = found.Single(f => f.Info.Format == "XEX2" && !f.InAllocatedSpace);
            Assert.Equal((99L) * volume.BytesPerCluster, x.Offset);
            Assert.Equal(xex.Length, x.FileSize);
            Assert.Equal(VerificationStatus.Verified, x.Info.Verification);

            var e = found.Single(f => f.Info.Format == "XBE");
            Assert.Equal(xbe.Length, e.FileSize);

            Assert.Contains(found, f => f.InAllocatedSpace);

            carver = new FileCarver(volume, FileCarverInterval.Cluster, 0) { FreeSpaceOnly = true };
            found = carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>().ToList();
            Assert.Equal(2, found.Count);
        }

        [Fact]
        public void CarverFindsUnalignedMagicAcrossChunks()
        {
            var b = new FatxImageBuilder(Platform.Xbox, 64L * 1024 * 1024);
            var xbe = ExecutableBuilders.BuildXbe("x.exe", 1, "T", new[] { 0x1000 });
            // Place it so that it starts 2 bytes before an 8MB chunk boundary.
            var bytes = new byte[0x4000 + xbe.Length];
            Buffer.BlockCopy(xbe, 0, bytes, 0x4000 - 2, xbe.Length);
            uint firstCluster = (uint)(8 * 1024 * 1024 / b.BytesPerCluster);  // cluster containing the boundary - 1
            b.WriteOrphanData(bytes, firstCluster);
            var volume = b.Mount(out _);
            var carver = new FileCarver(volume, FileCarverInterval.Byte, 0);
            var found = carver.Analyze(CancellationToken.None, null).Cast<CarvedFileSignature>().ToList();
            Assert.Contains(found, f => f.Info.Format == "XBE" && f.Offset == 8L * 1024 * 1024 - 2);
        }
    }
}
