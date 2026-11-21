using FATX.AI;
using FATX.FileSystem;
using FATX.Recovery;
using FATX.Testing;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace FATX.Tests
{
    /// <summary>
    /// Exercises the tools Claude uses, without calling the API.
    /// </summary>
    public class ToolsetTests
    {
        private static (RecoverySession session, RecoveryToolset tools, string outDir) Create()
        {
            var b = new FatxImageBuilder(Platform.X360, 64L * 1024 * 1024) { Policy = AllocationPolicy.NextFree };
            var t = new DateTime(2011, 1, 1);
            var build = b.AddDirectory(b.Root, "build_01", t);
            b.AddFile(build, "default.xex", ExecutableBuilders.BuildXex("game.exe", 0x41560817, new[] { 0x8000, 0x6000 }), t);
            b.AddFile(build, "default.pdb", new byte[50000], t);
            b.Delete(build);

            // In-memory volume (a full retail layout would need a multi-GB file).
            var reader = new DriveReader(b.ToStream()) { ByteOrder = ByteOrder.Big };
            reader.AddPartition("Partition1", 0, 64L * 1024 * 1024);
            var session = new RecoverySession(reader, "test");
            var outDir = Path.Combine(Path.GetTempPath(), "fatx-tools-" + Guid.NewGuid());
            return (session, new RecoveryToolset(session, outDir), outDir);
        }

        private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

        [Fact]
        public void EndToEnd()
        {
            var (session, tools, outDir) = Create();
            try
            {
                Assert.All(tools.Definitions, d => Assert.False(string.IsNullOrEmpty(d.Description)));

                var parts = JsonDocument.Parse(tools.Execute("list_partitions", Args(new { }))).RootElement;
                Assert.Contains(parts.EnumerateArray(), p => p.GetProperty("name").GetString() == "Partition1");

                var analysis = JsonDocument.Parse(tools.Execute("run_metadata_analysis", Args(new { partition = "Partition1" }))).RootElement;
                Assert.True(analysis.GetProperty("deleted_files").GetInt32() >= 2);

                var exes = JsonDocument.Parse(tools.Execute("find_executables", Args(new { partition = "Partition1" }))).RootElement;
                Assert.Equal(1, exes.GetProperty("verified").GetInt32());
                var exe = exes.GetProperty("results")[0];
                Assert.Equal("41560817", exe.GetProperty("info").GetProperty("title_id").GetString());

                var builds = JsonDocument.Parse(tools.Execute("find_game_builds", Args(new { partition = "Partition1" }))).RootElement;
                var build = builds.GetProperty("builds")[0];
                Assert.Equal("/build_01", build.GetProperty("folder").GetString());
                Assert.Equal(2, build.GetProperty("file_count").GetInt32());

                var inspect = JsonDocument.Parse(tools.Execute("inspect_file", Args(new { partition = "Partition1", id = exe.GetProperty("id").GetString() }))).RootElement;
                Assert.Equal("XEX2", inspect.GetProperty("format").GetProperty("format").GetString());

                var rec = JsonDocument.Parse(tools.Execute("recover_files", Args(new
                {
                    partition = "Partition1",
                    ids = new[] { build.GetProperty("folder_id").GetString() },
                    subfolder = "../../escape/41560817",
                }))).RootElement;
                Assert.Equal(2, rec.GetProperty("files_written").GetInt32());
                Assert.Equal(1, rec.GetProperty("verified").GetInt32());
                // Path traversal is neutralized.
                Assert.StartsWith(Path.GetFullPath(outDir), rec.GetProperty("output_directory").GetString());
                Assert.True(File.Exists(Path.Combine(outDir, "escape", "41560817", "build_01", "default.xex")));

                var carve = JsonDocument.Parse(tools.Execute("run_file_carver", Args(new { partition = "Partition1" }))).RootElement;
                Assert.True(carve.GetProperty("found").GetInt32() >= 1);
                var carved = JsonDocument.Parse(tools.Execute("list_carved", Args(new { partition = "Partition1", format = "XEX" }))).RootElement;
                Assert.Equal("c:0", carved.GetProperty("rows")[0].GetProperty("id").GetString());

                var hex = JsonDocument.Parse(tools.Execute("hex_dump", Args(new { partition = "Partition1", cluster = 1, length = 32 }))).RootElement;
                Assert.Contains("0000:", hex.GetProperty("hex").GetString());

                Assert.Throws<ToolException>(() => tools.Execute("inspect_file", Args(new { partition = "Partition1", id = "DEAD" })));
                Assert.Throws<ToolException>(() => tools.Execute("list_files", Args(new { partition = "Nope" })));
            }
            finally
            {
                session.Dispose();
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            }
        }
    }
}
