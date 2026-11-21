using FATX.Analyzers.Signatures;
using FATX.FileSystem;
using FATX.Formats;
using FATXTools.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FATX.Recovery
{
    public class RecoveredFile
    {
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public long BytesWritten { get; set; }
        public bool IsDirectory { get; set; }
        public bool Deleted { get; set; }
        public string Error { get; set; }
        public FormatInfo Format { get; set; }
    }

    /// <summary>
    /// Writes files from a FATX volume to the host file system.
    /// </summary>
    public class FileRecoverer
    {
        private readonly Volume volume;
        private readonly CancellationToken cancellationToken;

        public FileRecoverer(Volume volume, CancellationToken cancellationToken = default)
        {
            this.volume = volume;
            this.cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Called before each file is written with the source path. Use it for progress.
        /// </summary>
        public Action<string> OnProgress { get; set; }

        /// <summary>
        /// Called when an I/O error occurs. Return true to retry the operation.
        /// </summary>
        public Func<Exception, string, bool> OnError { get; set; }

        /// <summary>
        /// Identify and verify the format of every recovered file (XEX block
        /// hashes, XBE section digests).
        /// </summary>
        public bool VerifyFormats { get; set; }

        /// <summary>
        /// Overwrite existing files instead of generating a unique name.
        /// </summary>
        public bool Overwrite { get; set; }

        public List<RecoveredFile> Results { get; } = new List<RecoveredFile>();

        public int FilesSaved { get; private set; }

        /// <summary>
        /// Recovers a file or directory (recursively) into <paramref name="outputDirectory"/>.
        /// </summary>
        public void Save(DatabaseFile file, string outputDirectory)
        {
            Save(file, outputDirectory, 0);
        }

        public void SaveAll(IEnumerable<DatabaseFile> files, string outputDirectory)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Save(file, outputDirectory, 0);
            }
        }

        private void Save(DatabaseFile file, string outputDirectory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (depth > 512)
            {
                // Cycles are prevented when linking, this is a last resort.
                return;
            }

            Directory.CreateDirectory(outputDirectory);
            OnProgress?.Invoke(file.GetFullPath());

            var name = SanitizeFileName(file.FileName);

            if (file.IsDirectory())
            {
                var path = Path.Combine(outputDirectory, name);
                // Two directories with the same name (e.g. a deleted and a
                // recreated one) are merged; that is what users expect.
                if (File.Exists(path))
                    path = UniquePath(path);

                TryIO(() => Directory.CreateDirectory(path), path);
                Results.Add(new RecoveredFile { SourcePath = file.GetFullPath(), OutputPath = path, IsDirectory = true, Deleted = file.IsDeleted });

                foreach (var child in file.Children)
                {
                    Save(child, path, depth + 1);
                }

                TryIO(() => SetDirectoryTimes(path, file), path);
            }
            else
            {
                var path = ChooseOutputPath(Path.Combine(outputDirectory, name));

                var result = new RecoveredFile { SourcePath = file.GetFullPath(), OutputPath = path, Deleted = file.IsDeleted };
                TryIO(() =>
                {
                    var source = new ClusterChainSource(volume, file.ClusterChain, file.FileSize);
                    // FileMode.Create truncates. The old code used
                    // File.OpenWrite which left stale bytes behind when an
                    // existing larger file was overwritten.
                    using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        result.BytesWritten = source.CopyTo(output, 0, source.Length);
                    }

                    if (VerifyFormats)
                    {
                        result.Format = FormatDetector.Identify(source, true);
                    }

                    SetFileTimes(path, file);
                }, path, result);

                if (result.BytesWritten < file.FileSize && result.Error == null)
                {
                    result.Error = $"Only 0x{result.BytesWritten:X} of 0x{file.FileSize:X} bytes could be recovered (cluster chain is too short).";
                }

                Results.Add(result);
                FilesSaved++;
            }
        }

        /// <summary>
        /// Saves a carved file.
        /// </summary>
        public RecoveredFile Save(FileSignature signature, string outputDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            OnProgress?.Invoke(signature.FileName);

            var path = ChooseOutputPath(Path.Combine(outputDirectory, SanitizeFileName(signature.FileName)));

            var result = new RecoveredFile { SourcePath = $"carved@0x{signature.Offset:X}", OutputPath = path, Deleted = true };
            TryIO(() =>
            {
                var source = new VolumeAreaSource(volume, signature.Offset, signature.FileSize);
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    result.BytesWritten = source.CopyTo(output, 0, source.Length);
                }
                if (VerifyFormats)
                    result.Format = FormatDetector.Identify(source, true);
            }, path, result);

            Results.Add(result);
            FilesSaved++;
            return result;
        }

        private readonly HashSet<string> writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Picks the output path. Files written earlier in this run are never
        /// overwritten (e.g. a deleted and a re-created "default.xex" in the
        /// same folder both get saved); pre-existing files are overwritten
        /// only when <see cref="Overwrite"/> is set.
        /// </summary>
        private string ChooseOutputPath(string path)
        {
            if (writtenPaths.Contains(Path.GetFullPath(path)) || (!Overwrite && (File.Exists(path) || Directory.Exists(path))))
            {
                var dir = Path.GetDirectoryName(path);
                var stem = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                for (int i = 1; ; i++)
                {
                    var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!writtenPaths.Contains(Path.GetFullPath(candidate)) &&
                        (Overwrite || (!File.Exists(candidate) && !Directory.Exists(candidate))))
                    {
                        path = candidate;
                        break;
                    }
                }
            }

            writtenPaths.Add(Path.GetFullPath(path));
            return path;
        }

        private void TryIO(Action action, string path, RecoveredFile result = null)
        {
            while (true)
            {
                try
                {
                    action();
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
                {
                    if (OnError != null && OnError(e, path))
                        continue;

                    if (result != null)
                        result.Error = e.Message;
                    Console.WriteLine($"Failed to write {path}: {e.Message}");
                    return;
                }
            }
        }

        private static void SetFileTimes(string path, DatabaseFile file)
        {
            try
            {
                File.SetCreationTime(path, file.CreationTime.AsDateTime());
                File.SetLastWriteTime(path, file.LastWriteTime.AsDateTime());
                File.SetLastAccessTime(path, file.LastAccessTime.AsDateTime());
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException || e is PlatformNotSupportedException)
            {
                // Timestamps are best effort.
            }
        }

        private static void SetDirectoryTimes(string path, DatabaseFile file)
        {
            try
            {
                Directory.SetCreationTime(path, file.CreationTime.AsDateTime());
                Directory.SetLastWriteTime(path, file.LastWriteTime.AsDateTime());
                Directory.SetLastAccessTime(path, file.LastAccessTime.AsDateTime());
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException || e is PlatformNotSupportedException)
            {
            }
        }

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Makes a FATX file name safe to use on Windows (and other) hosts.
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "_unnamed";

            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }));
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(c < 0x20 || invalid.Contains(c) ? '_' : c);
            }

            var result = sb.ToString().TrimEnd(' ', '.');
            if (result.Length == 0 || result == "." || result == "..")
                result = "_" + result.Replace('.', '_');

            var stem = Path.GetFileNameWithoutExtension(result);
            if (ReservedNames.Contains(stem))
                result = "_" + result;

            return result;
        }

        /// <summary>
        /// Returns <paramref name="path"/>, or "name (n).ext" if it already exists.
        /// </summary>
        public static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return path;

            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }
        }
    }
}
