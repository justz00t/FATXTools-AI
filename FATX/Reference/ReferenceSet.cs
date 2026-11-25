using FATX.Formats;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FATX.Reference
{
    /// <summary>
    /// One file of a reference set: its identity and the fingerprint of every block.
    /// </summary>
    public class ReferenceFile
    {
        /// <summary>Path relative to the scanned folder, with forward slashes.</summary>
        public string Path { get; set; }

        public long Size { get; set; }

        /// <summary>SHA-1 of the whole file (lower case hex).</summary>
        public string Sha1 { get; set; }

        /// <summary>
        /// Fingerprint of every block. A final partial block is fingerprinted
        /// as if it were padded with zeroes to a full block.
        /// </summary>
        public ulong[] Blocks { get; set; }

        /// <summary>Fingerprint of the first 512 bytes (0 for smaller files).</summary>
        public ulong Head { get; set; }
    }

    /// <summary>
    /// The four bytes every file of one type in the scanned folder starts
    /// with. Lets the file carver find other files of that type, including
    /// versions that are not in the reference set.
    /// </summary>
    public class TypeSignature
    {
        public string Extension { get; set; }
        public uint Magic { get; set; }
        public int Files { get; set; }
        public long MaxSize { get; set; }
    }

    public class ReferenceBuildProgress
    {
        public int FilesDone { get; set; }
        public int FilesTotal { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public string CurrentFile { get; set; }
    }

    /// <summary>
    /// Fingerprints of all files in a folder (e.g. one build of a game),
    /// saved as JSON. <see cref="ReferenceMatcher"/> uses it to find deleted
    /// copies and older versions of those files on a FATX volume by content,
    /// with their original names and paths.
    /// </summary>
    public class ReferenceSet
    {
        public const int DefaultBlockSize = 0x4000;
        public const int HeadSize = 512;
        private const int FormatVersion = 1;

        public string Name { get; set; }
        public string SourceFolder { get; set; }
        public DateTime Created { get; set; }
        public int BlockSize { get; set; } = DefaultBlockSize;
        public List<ReferenceFile> Files { get; } = new List<ReferenceFile>();
        public List<TypeSignature> Types { get; } = new List<TypeSignature>();

        public long TotalBytes => Files.Sum(f => f.Size);

        /// <summary>
        /// The fingerprint of a block: the first eight bytes of its SHA-1.
        /// </summary>
        public static ulong Fingerprint(SHA1 sha, byte[] buffer, int offset, int count)
        {
            Span<byte> digest = stackalloc byte[20];
            sha.TryComputeHash(new ReadOnlySpan<byte>(buffer, offset, count), digest, out _);
            return BinaryPrimitives.ReadUInt64BigEndian(digest);
        }

        /// <summary>
        /// Fingerprints every file below <paramref name="folder"/>.
        /// </summary>
        public static ReferenceSet Build(string folder, int blockSize = DefaultBlockSize, string name = null,
            IProgress<ReferenceBuildProgress> progress = null, CancellationToken cancellationToken = default)
        {
            if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 || blockSize > 0x100000)
                throw new ArgumentException("The block size must be a power of two between 512 bytes and 1 MB.", nameof(blockSize));

            folder = System.IO.Path.GetFullPath(folder);
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Folder not found: {folder}");

            var set = new ReferenceSet
            {
                Name = name ?? new DirectoryInfo(folder).Name,
                SourceFolder = folder,
                Created = DateTime.UtcNow,
                BlockSize = blockSize,
            };

            var paths = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var infos = paths.Select(p => new FileInfo(p)).ToList();
            var report = new ReferenceBuildProgress { FilesTotal = paths.Count, BytesTotal = infos.Sum(i => i.Length) };

            int prefix = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Length + 1;
            var buffer = new byte[Math.Max(blockSize, (1 << 20) / blockSize * blockSize)];
            var types = new Dictionary<string, List<(uint magic, long size)>>(StringComparer.OrdinalIgnoreCase);

            using (var sha = SHA1.Create())
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    report.CurrentFile = paths[i].Substring(Math.Min(prefix, paths[i].Length));
                    report.FilesDone = i;
                    progress?.Report(report);

                    if (infos[i].Length == 0)
                        continue;

                    ReferenceFile file;
                    uint magic;
                    try
                    {
                        file = FingerprintFile(paths[i], blockSize, buffer, sha, report, progress, cancellationToken, out magic);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Console.WriteLine($"Skipped {paths[i]}: {e.Message}");
                        continue;
                    }

                    file.Path = report.CurrentFile.Replace('\\', '/');
                    set.Files.Add(file);

                    var extension = System.IO.Path.GetExtension(paths[i]);
                    if (file.Size >= 16 && extension.Length > 1)
                    {
                        if (!types.TryGetValue(extension, out var list))
                            types[extension] = list = new List<(uint, long)>();
                        list.Add((magic, file.Size));
                    }
                }
            }

            foreach (var kv in types.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Only types whose files all start with the same readable tag
                // make a signature that does not drown the carver in noise.
                uint magic = kv.Value[0].magic;
                if (kv.Value.Count < 3 || kv.Value.Any(v => v.magic != magic) || !IsReadableTag(magic) || FormatDetector.Magics.Contains(magic))
                    continue;

                set.Types.Add(new TypeSignature
                {
                    Extension = kv.Key.ToLowerInvariant(),
                    Magic = magic,
                    Files = kv.Value.Count,
                    MaxSize = kv.Value.Max(v => v.size),
                });
            }

            report.FilesDone = paths.Count;
            report.CurrentFile = null;
            progress?.Report(report);
            return set;
        }

        private static bool IsReadableTag(uint magic)
        {
            byte a = (byte)(magic >> 24), b = (byte)(magic >> 16), c = (byte)(magic >> 8), d = (byte)magic;
            foreach (var x in new[] { a, b, c, d })
            {
                if (x < 0x21 || x > 0x7E)
                    return false;
            }
            return !(a == b && b == c && c == d);
        }

        private static ReferenceFile FingerprintFile(string path, int blockSize, byte[] buffer, SHA1 sha, ReferenceBuildProgress report,
            IProgress<ReferenceBuildProgress> progress, CancellationToken cancellationToken, out uint magic)
        {
            magic = 0;
            using (var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            {
                long size = stream.Length;
                var blocks = new List<ulong>((int)Math.Min(1 << 22, size / blockSize + 1));
                ulong head = 0;
                long position = 0;
                var lastReport = System.Diagnostics.Stopwatch.StartNew();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int got = 0;
                    while (got < buffer.Length)
                    {
                        int n = stream.Read(buffer, got, buffer.Length - got);
                        if (n <= 0)
                            break;
                        got += n;
                    }
                    if (got == 0)
                        break;

                    whole.AppendData(buffer, 0, got);

                    if (position == 0)
                    {
                        if (got >= 4)
                            magic = BinaryPrimitives.ReadUInt32BigEndian(buffer);
                        if (got >= HeadSize)
                            head = Fingerprint(sha, buffer, 0, HeadSize);
                    }

                    for (int offset = 0; offset < got; offset += blockSize)
                    {
                        int n = Math.Min(blockSize, got - offset);
                        if (n < blockSize)
                            Array.Clear(buffer, offset + n, blockSize - n);   // pad the final block
                        blocks.Add(Fingerprint(sha, buffer, offset, blockSize));
                    }

                    position += got;
                    report.BytesDone += got;
                    if (lastReport.ElapsedMilliseconds >= 250)
                    {
                        progress?.Report(report);
                        lastReport.Restart();
                    }

                    if (got < buffer.Length)
                        break;
                }

                return new ReferenceFile
                {
                    Size = position,
                    Sha1 = BitConverter.ToString(whole.GetHashAndReset()).Replace("-", "").ToLowerInvariant(),
                    Blocks = blocks.ToArray(),
                    Head = head,
                };
            }
        }

        // ---- JSON ----

        public void Save(string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("format", "FATXTools reference set");
                json.WriteNumber("version", FormatVersion);
                json.WriteString("name", Name);
                json.WriteString("sourceFolder", SourceFolder ?? "");
                json.WriteString("created", Created.ToString("o"));
                json.WriteNumber("blockSize", BlockSize);
                json.WriteString("fingerprint", "first 8 bytes of the SHA-1 of each block; a final partial block is zero padded");

                json.WriteStartArray("types");
                foreach (var t in Types)
                {
                    json.WriteStartObject();
                    json.WriteString("extension", t.Extension);
                    json.WriteString("magic", t.Magic.ToString("x8"));
                    json.WriteNumber("files", t.Files);
                    json.WriteNumber("maxSize", t.MaxSize);
                    json.WriteEndObject();
                }
                json.WriteEndArray();

                json.WriteStartArray("files");
                var hex = new StringBuilder();
                foreach (var f in Files)
                {
                    json.WriteStartObject();
                    json.WriteString("path", f.Path);
                    json.WriteNumber("size", f.Size);
                    json.WriteString("sha1", f.Sha1);
                    json.WriteString("head", f.Head.ToString("x16"));
                    hex.Clear();
                    foreach (var b in f.Blocks)
                        hex.Append(b.ToString("x16"));
                    json.WriteString("blocks", hex.ToString());
                    json.WriteEndObject();
                    json.Flush();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
        }

        public static ReferenceSet Load(string path)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var files) || !root.TryGetProperty("blockSize", out var blockSize))
                    throw new InvalidDataException($"{path} is not a FATXTools reference set.");

                var set = new ReferenceSet
                {
                    Name = root.TryGetProperty("name", out var name) ? name.GetString() : System.IO.Path.GetFileNameWithoutExtension(path),
                    SourceFolder = root.TryGetProperty("sourceFolder", out var source) ? source.GetString() : "",
                    BlockSize = blockSize.GetInt32(),
                };
                if (root.TryGetProperty("created", out var created) && DateTime.TryParse(created.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                    set.Created = when;

                if (root.TryGetProperty("types", out var types))
                {
                    foreach (var t in types.EnumerateArray())
                    {
                        set.Types.Add(new TypeSignature
                        {
                            Extension = t.GetProperty("extension").GetString(),
                            Magic = Convert.ToUInt32(t.GetProperty("magic").GetString(), 16),
                            Files = t.GetProperty("files").GetInt32(),
                            MaxSize = t.GetProperty("maxSize").GetInt64(),
                        });
                    }
                }

                foreach (var f in files.EnumerateArray())
                {
                    var hex = f.GetProperty("blocks").GetString();
                    if (hex.Length % 16 != 0)
                        throw new InvalidDataException($"{path}: damaged block list for {f.GetProperty("path").GetString()}.");
                    var blocks = new ulong[hex.Length / 16];
                    for (int i = 0; i < blocks.Length; i++)
                        blocks[i] = ParseHex(hex, i * 16);

                    set.Files.Add(new ReferenceFile
                    {
                        Path = f.GetProperty("path").GetString(),
                        Size = f.GetProperty("size").GetInt64(),
                        Sha1 = f.GetProperty("sha1").GetString(),
                        Head = Convert.ToUInt64(f.GetProperty("head").GetString(), 16),
                        Blocks = blocks,
                    });
                }

                return set;
            }
        }

        private static ulong ParseHex(string text, int start)
        {
            ulong value = 0;
            for (int i = start; i < start + 16; i++)
            {
                char c = text[i];
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0)
                    throw new InvalidDataException("Invalid fingerprint in reference set.");
                value = (value << 4) | (uint)digit;
            }
            return value;
        }
    }
}
