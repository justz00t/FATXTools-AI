using FATX.Analyzers;
using FATX.Analyzers.Signatures;
using FATX.FileSystem;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FATX.Recovery
{
    /// <summary>
    /// The state of one partition during a recovery session.
    /// </summary>
    public class PartitionState
    {
        public PartitionState(Volume volume)
        {
            Volume = volume;
        }

        public Volume Volume { get; }
        public string Name => Volume.Name;
        public string MountError { get; set; }
        public bool Mounted => Volume.Mounted;

        public FileDatabase Database { get; set; }
        public IntegrityAnalyzer Integrity { get; set; }
        public bool MetadataAnalyzed { get; set; }
        public int RecoveredDirentCount { get; set; }
        public TimeSpan MetadataAnalysisTime { get; set; }

        public List<FileSignature> CarvedFiles { get; set; }
        public TimeSpan CarveTime { get; set; }
    }

    /// <summary>
    /// Headless recovery workflow: open an image, mount its partitions, run
    /// the analyzers and recover files. Used by the command line tool and the
    /// Claude assistant.
    /// </summary>
    public class RecoverySession : IDisposable
    {
        private readonly Stream stream;

        public RecoverySession(string imagePath)
            : this(FATX.Imaging.ImageFile.OpenRead(imagePath), imagePath)
        {
        }

        public RecoverySession(Stream stream, string name)
            : this(CreateDrive(stream), name)
        {
            this.stream = stream;
        }

        private static DriveReader CreateDrive(Stream stream)
        {
            var drive = new DriveReader(stream);
            drive.Initialize();
            return drive;
        }

        /// <summary>
        /// Uses an already initialized drive (e.g. the one opened in the GUI).
        /// Partitions that are already mounted are reused.
        /// </summary>
        public RecoverySession(DriveReader drive, string name)
        {
            ImageName = name;
            Drive = drive;

            foreach (var volume in Drive.Partitions)
            {
                var state = new PartitionState(volume);
                try
                {
                    if (!volume.Mounted)
                        volume.Mount();
                    state.Database = new FileDatabase(volume);
                    state.Database.Update();
                    state.Integrity = new IntegrityAnalyzer(volume, state.Database);
                    state.Integrity.Update();
                }
                catch (Exception e)
                {
                    state.MountError = e.Message;
                }
                Partitions.Add(state);
            }
        }

        public string ImageName { get; }
        public DriveReader Drive { get; }
        public List<PartitionState> Partitions { get; } = new List<PartitionState>();

        public PartitionState GetPartition(string name)
        {
            var p = Partitions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p == null)
                throw new ArgumentException($"Unknown partition '{name}'. Known partitions: {string.Join(", ", Partitions.Select(x => x.Name))}");
            if (!p.Mounted)
                throw new InvalidOperationException($"Partition '{name}' is not mounted: {p.MountError}");
            return p;
        }

        /// <summary>
        /// Scans the partition for deleted directory entries and merges them into its database.
        /// </summary>
        public void RunMetadataAnalyzer(PartitionState p, CancellationToken token = default, IProgress<int> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var analyzer = new MetadataAnalyzer(p.Volume, p.Volume.BytesPerCluster, p.Volume.FileAreaLength);
            var dirents = analyzer.Analyze(token, progress);

            p.Database.Reset();
            foreach (var dirent in dirents)
            {
                p.Database.AddFile(dirent, true);
            }
            p.Database.Update();
            p.Integrity = new IntegrityAnalyzer(p.Volume, p.Database);
            p.Integrity.Update();

            p.RecoveredDirentCount = dirents.Count;
            p.MetadataAnalyzed = true;
            p.MetadataAnalysisTime = sw.Elapsed;
        }

        /// <summary>
        /// Rebuilds the deleted file cluster chains with a different strategy.
        /// </summary>
        public void SetChainStrategy(PartitionState p, ChainStrategy strategy)
        {
            p.Database.ChainStrategy = strategy;
            p.Database.Update();
            p.Integrity.Update();
        }

        public void RunFileCarver(PartitionState p, FileCarverInterval interval = FileCarverInterval.Cluster, bool freeSpaceOnly = false,
            bool verify = false, CancellationToken token = default, IProgress<int> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var carver = new FileCarver(p.Volume, interval, p.Volume.FileAreaLength)
            {
                FreeSpaceOnly = freeSpaceOnly,
                VerifyFiles = verify,
            };
            p.CarvedFiles = carver.Analyze(token, progress);
            p.CarveTime = sw.Elapsed;
        }

        /// <summary>
        /// Enumerates every file in the partition's database (active and recovered).
        /// </summary>
        public static IEnumerable<DatabaseFile> AllFiles(PartitionState p)
        {
            return p.Database.GetFiles().Values;
        }

        public void Dispose()
        {
            stream?.Dispose();
        }
    }
}
