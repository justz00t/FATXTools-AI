using FATX.FileSystem;
using FATX.Testing;
using System;
using System.IO;

// Simulates a dev kit's life: builds copied, older builds deleted, new builds
// written into the fragmented free space, saves and packages churned.
public static class Program
{
    public static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : "synthetic360.img";
        int seed = args.Length > 1 ? int.Parse(args[1]) : 1234;
        var rng = new Random(seed);
        const long partitionOffset = 0x130EB0000;
        const long dataSize = 2L * 1024 * 1024 * 1024;   // 2GB data partition

        var b = new FatxImageBuilder(Platform.X360, dataSize) { Policy = AllocationPolicy.FirstFree };
        var t = new DateTime(2008, 3, 1, 9, 0, 0);
        var devkit = b.AddDirectory(b.Root, "Devkit", t);
        var content = b.AddDirectory(b.Root, "Content", t);
        var junk = b.AddDirectory(b.Root, "Temp", t);
        int cluster = (int)b.BytesPerCluster;

        byte[] Random(int n) { var d = new byte[n]; rng.NextBytes(d); return d; }

        FatxImageBuilder.Node Build(string name, uint titleId, DateTime when)
        {
            var dir = b.AddDirectory(devkit, name, when);
            int blocks = rng.Next(20, 120);
            var sizes = new int[blocks];
            for (int i = 0; i < blocks; i++) sizes[i] = rng.Next(0x4000, 0x10000);
            b.AddFile(dir, "default.xex", ExecutableBuilders.BuildXex(name + ".exe", titleId, sizes, rng.Next(2) == 0, rng.Next()), when);
            b.AddFile(dir, "default.pdb", Random(rng.Next(cluster * 10, cluster * 200)), when.AddSeconds(2));
            b.AddFile(dir, "default.map", Random(rng.Next(cluster, cluster * 20)), when.AddSeconds(4));
            var media = b.AddDirectory(dir, "media", when.AddSeconds(6));
            for (int i = 0; i < rng.Next(5, 25); i++)
                b.AddFile(media, $"asset{i:D3}.xpr", Random(rng.Next(1000, cluster * 40)), when.AddSeconds(8 + i));
            return dir;
        }

        var builds = new System.Collections.Generic.List<FatxImageBuilder.Node>();
        for (int round = 0; round < 12; round++)
        {
            t = t.AddDays(3);
            builds.Add(Build($"build_{round:D2}", 0x58410000u + (uint)(round % 3), t));

            // churn: temp files and saves
            for (int i = 0; i < 30; i++)
            {
                var f = b.AddFile(junk, $"tmp{round:D2}_{i:D2}.dat", Random(rng.Next(100, cluster * 30)), t.AddMinutes(10 + i));
                if (rng.Next(3) != 0) b.Delete(f);
            }
            var save = b.AddFile(content, $"save{round:D2}.dat", Random(rng.Next(cluster, cluster * 8)), t.AddHours(2));

            // delete older builds (keeping a few)
            if (round >= 3)
            {
                var victim = builds[round - 3];
                if (rng.Next(4) != 0) b.Delete(victim);
            }
        }

        // A couple of original Xbox executables (backward compatibility tests)
        for (int i = 0; i < 4; i++)
        {
            var f = b.AddFile(content, $"xbox{i}.xbe", ExecutableBuilders.BuildXbe($"d:\\xbox{i}.exe", 0x4D530000u + (uint)i, "Compat Test", new[] { rng.Next(0x10000, 0x80000), rng.Next(0x1000, 0x20000) }, rng.Next()), t.AddDays(1).AddMinutes(i));
            if (i % 2 == 0) b.Delete(f);
        }

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            fs.SetLength(partitionOffset + dataSize);
            // A blank FATX system partition so detection works like on a real drive.
            var sys = new FatxImageBuilder(Platform.X360, 0x10000000);
            sys.WriteTo(fs, 0x120EB0000);
            b.WriteTo(fs, partitionOffset);
        }

        Console.WriteLine($"Wrote {path}");
        return 0;
    }
}
