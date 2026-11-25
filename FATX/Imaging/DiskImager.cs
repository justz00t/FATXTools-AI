using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FATX.Imaging
{
    public class ImagingOptions
    {
        /// <summary>Bytes requested from the source per read.</summary>
        public int ChunkSize { get; set; } = 1 << 20;

        /// <summary>Uncompressed size of the blocks of an ".imgc" target.</summary>
        public int ImgcBlockSize { get; set; } = 0x10000;

        /// <summary>Extra attempts for every sector that fails to read.</summary>
        public int Retries { get; set; } = 2;

        /// <summary>Continue an interrupted dump of the same source to the same target.</summary>
        public bool Resume { get; set; }

        /// <summary>Hashes to compute over the image: "md5", "sha1", "sha256", "sha512".</summary>
        public List<string> Hashes { get; set; } = new List<string>();

        /// <summary>Only image the first bytes of the source (0 = everything).</summary>
        public long Length { get; set; }
    }

    public class ImagingProgress
    {
        public string Phase { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public long BadBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan Elapsed { get; set; }

        public TimeSpan? Remaining
        {
            get
            {
                if (BytesPerSecond <= 0 || BytesDone >= BytesTotal)
                    return null;
                return TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond);
            }
        }
    }

    public class ByteRange
    {
        public long Offset { get; set; }
        public long Length { get; set; }
    }

    public class ImagingResult
    {
        public string TargetPath { get; set; }
        public bool Compressed { get; set; }
        public long BytesTotal { get; set; }
        public long BytesDone { get; set; }
        public long TargetFileSize { get; set; }
        public bool Completed { get; set; }
        public bool Resumed { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<ByteRange> BadRanges { get; set; } = new List<ByteRange>();
        public Dictionary<string, string> Hashes { get; set; } = new Dictionary<string, string>();

        public long BadBytes => BadRanges.Sum(r => r.Length);
    }

    /// <summary>
    /// Copies a drive (or an image) to a raw ".img" or compressed ".imgc" image.
    ///
    /// Speed comes from large sequential requests and from never letting the
    /// source wait: reading, compressing and writing run on separate threads
    /// with a bounded queue in between. Unreadable areas never stop a dump.
    /// For raw targets they are skipped first and retried sector by sector
    /// once everything readable is safe; for compressed targets (which can
    /// only be written front to back) they are retried on the spot. What
    /// stays unreadable is zero filled and listed in the dump report
    /// ("&lt;target&gt;.dump.json"), which also makes a dump resumable.
    /// </summary>
    public class DiskImager
    {
        private sealed class Chunk
        {
            public long Offset;
            public int Length;
            public byte[] Data;             // rented
            public Task<Packed> Packed;     // compressed targets only
        }

        private sealed class Packed
        {
            public byte[] Buffer;           // rented
            public List<Segment> Segments = new List<Segment>();
        }

        private struct Segment
        {
            public int Offset;              // in Packed.Buffer, -1 for a zero run
            public int Length;              // payload length
            public int Uncompressed;
        }

        private const int RecoveryPiece = 0x10000;

        private readonly IDiskSource source;
        private readonly string targetPath;
        private readonly ImagingOptions options;
        private readonly bool compressed;
        private readonly long total;
        private readonly string reportPath;

        private readonly List<ByteRange> pending = new List<ByteRange>();
        private readonly List<ByteRange> bad = new List<ByteRange>();
        private readonly object stateLock = new object();
        private long donePosition;          // everything before this is in the target
        private long badBytes;
        private bool finished;
        private Dictionary<string, string> hashes = new Dictionary<string, string>();

        public DiskImager(IDiskSource source, string targetPath, ImagingOptions options = null)
        {
            this.source = source;
            this.targetPath = targetPath;
            this.options = options ?? new ImagingOptions();
            compressed = IsCompressedPath(targetPath);
            reportPath = targetPath + ".dump.json";

            int sector = Math.Max(1, source.SectorSize);
            long length = source.Length;
            if (this.options.Length > 0 && this.options.Length < length)
                length = this.options.Length;
            // Drives are whole sectors; an image file being converted may not be.
            total = length;

            // Requests must be whole sectors, blocks whole pieces of a request.
            this.options.ChunkSize = Math.Max(RecoveryPiece, this.options.ChunkSize / RecoveryPiece * RecoveryPiece);
            if (this.options.ImgcBlockSize < sector || this.options.ImgcBlockSize % sector != 0 || this.options.ChunkSize % this.options.ImgcBlockSize != 0)
                this.options.ImgcBlockSize = RecoveryPiece;
            if (RecoveryPiece % sector != 0)
                throw new NotSupportedException($"Unsupported sector size {sector}.");
        }

        public static bool IsCompressedPath(string path)
        {
            return string.Equals(Path.GetExtension(path), ".imgc", StringComparison.OrdinalIgnoreCase);
        }

        public ImagingResult Run(IProgress<ImagingProgress> progress = null, CancellationToken cancellationToken = default)
        {
            var clock = Stopwatch.StartNew();
            var result = new ImagingResult { TargetPath = targetPath, Compressed = compressed, BytesTotal = total };

            long startPosition = 0;
            bool resumed = false;
            FileStream output = null;
            ImgcWriter writer = null;

            try
            {
                if (options.Resume && File.Exists(targetPath) && LoadReport())
                {
                    output = OpenResume(out startPosition);
                    resumed = true;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath)));
                    output = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    if (compressed)
                    {
                        ImgcWriter.WriteHeader(output, new ImgcHeader
                        {
                            SoftwareName = "FATXTools",
                            SoftwareVersion = "1.0",
                            Model = source.Model,
                            Revision = source.Revision,
                            Serial = source.Serial,
                            SectorCount = total / source.SectorSize,
                            SectorSize = source.SectorSize,
                        });
                    }
                    else
                    {
                        // Reserve the space up front: fails early when the
                        // target is too small and keeps the file contiguous.
                        output.SetLength(total);
                    }
                }

                if (compressed)
                    writer = new ImgcWriter(output);

                donePosition = startPosition;
                result.Resumed = resumed;
                SaveReport(output, writer);

                // Hashing while copying only works when the image is written
                // front to back in one go.
                var inline = resumed ? null : CreateHashes();
                bool cancelled = CopyPass(output, writer, startPosition, inline, progress, clock, cancellationToken);

                if (!cancelled && pending.Count > 0)
                    cancelled = RetryPass(output, progress, clock, cancellationToken);

                writer?.FlushZeros();
                output.Flush(true);

                if (!cancelled)
                {
                    if (compressed)
                        output.SetLength(output.Position);

                    if (options.Hashes.Count > 0)
                    {
                        // What was hashed while copying is exactly what was written,
                        // unless areas were skipped and filled in later.
                        bool inlineValid = inline != null && !pendingEverUsed;
                        try
                        {
                            hashes = inlineValid ? Finish(inline) : HashTarget(output, progress, clock, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // The image itself is complete; only its hash is missing.
                            hashes = new Dictionary<string, string>();
                        }
                    }
                    finished = true;
                }

                SaveReport(output, null);

                result.Completed = finished;
                result.BytesDone = donePosition;
                result.BadRanges = bad.Select(r => new ByteRange { Offset = r.Offset, Length = r.Length }).ToList();
                result.Hashes = hashes;
                result.TargetFileSize = output.Length;
                result.Elapsed = clock.Elapsed;
                return result;
            }
            finally
            {
                output?.Dispose();
            }
        }

        private bool pendingEverUsed;

        // ---- pass 1: sequential copy ----

        private bool CopyPass(FileStream output, ImgcWriter writer, long start, List<KeyValuePair<string, IncrementalHash>> inline,
            IProgress<ImagingProgress> progress, Stopwatch clock, CancellationToken cancellationToken)
        {
            int workers = Math.Max(1, Environment.ProcessorCount);
            var queue = new BlockingCollection<Chunk>(compressed ? workers * 2 : 8);
            var pool = ArrayPool<byte>.Shared;
            Exception writeError = null;
            var passClock = Stopwatch.StartNew();

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var writerTask = Task.Factory.StartNew(() =>
                {
                    var meter = new SpeedMeter(start);
                    var lastSave = Stopwatch.StartNew();
                    try
                    {
                        foreach (var chunk in queue.GetConsumingEnumerable())
                        {
                            if (compressed)
                            {
                                var packed = chunk.Packed.Result;
                                foreach (var s in packed.Segments)
                                {
                                    if (s.Offset < 0)
                                        writer.WriteZeros(s.Uncompressed);
                                    else
                                        writer.WritePacked(packed.Buffer, s.Offset, s.Length, s.Uncompressed);
                                }
                                pool.Return(packed.Buffer);
                            }
                            else
                            {
                                output.Position = chunk.Offset;
                                output.Write(chunk.Data, 0, chunk.Length);
                            }

                            if (inline != null)
                            {
                                foreach (var h in inline)
                                    h.Value.AppendData(chunk.Data, 0, chunk.Length);
                            }

                            pool.Return(chunk.Data);

                            lock (stateLock)
                                donePosition = chunk.Offset + chunk.Length;

                            if (lastSave.ElapsedMilliseconds >= 5000)
                            {
                                SaveReport(output, writer);
                                lastSave.Restart();
                            }

                            if (meter.Due())
                                progress?.Report(meter.Snapshot("Copying", donePosition, total, Interlocked.Read(ref badBytes), clock.Elapsed));
                        }
                    }
                    catch (Exception e)
                    {
                        writeError = e is AggregateException ae ? ae.GetBaseException() : e;
                        stop.Cancel();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                bool cancelled = false;
                try
                {
                    for (long pos = start; pos < total; pos += options.ChunkSize)
                    {
                        stop.Token.ThrowIfCancellationRequested();

                        int n = (int)Math.Min(options.ChunkSize, total - pos);
                        var data = pool.Rent(n);
                        var chunk = new Chunk { Offset = pos, Length = n, Data = data };

                        try
                        {
                            source.Read(pos, data, 0, n);
                        }
                        catch (IOException) when (compressed)
                        {
                            // The target cannot be patched later: recover now.
                            ReadWithRecovery(pos, data, 0, n, stop.Token);
                        }
                        catch (IOException)
                        {
                            // Come back for it after everything readable is safe.
                            Array.Clear(data, 0, n);
                            lock (stateLock)
                            {
                                AddRange(pending, pos, n);
                                pendingEverUsed = true;
                            }
                        }

                        if (compressed)
                        {
                            var c = chunk;
                            chunk.Packed = Task.Run(() => Pack(c.Data, c.Length));
                        }

                        queue.Add(chunk, stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                finally
                {
                    queue.CompleteAdding();
                }

                writerTask.Wait();

                if (writeError != null)
                    throw new IOException($"Writing {targetPath} failed: {writeError.Message}", writeError);

                progress?.Report(new ImagingProgress
                {
                    Phase = "Copying",
                    BytesDone = donePosition,
                    BytesTotal = total,
                    BadBytes = badBytes,
                    BytesPerSecond = (donePosition - start) / Math.Max(0.001, passClock.Elapsed.TotalSeconds),
                    Elapsed = clock.Elapsed,
                });
                return cancelled;
            }
        }

        private Packed Pack(byte[] data, int length)
        {
            int block = options.ImgcBlockSize;
            int blocks = (length + block - 1) / block;
            var packed = new Packed
            {
                Buffer = ArrayPool<byte>.Shared.Rent(blocks * ImgcWriter.MaxPackedLength(block))
            };

            int op = 0;
            for (int offset = 0; offset < length; offset += block)
            {
                int n = Math.Min(block, length - offset);
                if (ImgcWriter.IsZero(data, offset, n))
                {
                    packed.Segments.Add(new Segment { Offset = -1, Uncompressed = n });
                }
                else
                {
                    int size = ImgcWriter.PackBlock(data, offset, n, packed.Buffer, op);
                    packed.Segments.Add(new Segment { Offset = op, Length = size, Uncompressed = n });
                    op += size;
                }
            }

            return packed;
        }

        // ---- bad sector handling ----

        /// <summary>
        /// Reads a range that failed as a whole: first in 64KB pieces, then
        /// sector by sector with retries. Unreadable sectors are zero filled
        /// and recorded.
        /// </summary>
        private void ReadWithRecovery(long offset, byte[] buffer, int index, int count, CancellationToken cancellationToken)
        {
            int sector = source.SectorSize;

            for (int done = 0; done < count; done += RecoveryPiece)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int piece = Math.Min(RecoveryPiece, count - done);

                try
                {
                    source.Read(offset + done, buffer, index + done, piece);
                    continue;
                }
                catch (IOException)
                {
                }

                for (int s = 0; s < piece; s += sector)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = Math.Min(sector, piece - s);
                    bool ok = false;

                    for (int attempt = 0; attempt <= options.Retries && !ok; attempt++)
                    {
                        try
                        {
                            source.Read(offset + done + s, buffer, index + done + s, n);
                            ok = true;
                        }
                        catch (IOException)
                        {
                        }
                    }

                    if (!ok)
                    {
                        Array.Clear(buffer, index + done + s, n);
                        lock (stateLock)
                            AddRange(bad, offset + done + s, n);
                        Interlocked.Add(ref badBytes, n);
                    }
                }
            }
        }

        // ---- pass 2: retry skipped areas of a raw target ----

        private bool RetryPass(FileStream output, IProgress<ImagingProgress> progress, Stopwatch clock, CancellationToken cancellationToken)
        {
            var pool = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(options.ChunkSize);
            var lastSave = Stopwatch.StartNew();
            long retryTotal = pending.Sum(r => r.Length);
            long retried = 0;

            try
            {
                while (pending.Count > 0)
                {
                    var range = pending[0];
                    int n = (int)Math.Min(options.ChunkSize, range.Length);

                    ReadWithRecovery(range.Offset, buffer, 0, n, cancellationToken);
                    output.Position = range.Offset;
                    output.Write(buffer, 0, n);

                    lock (stateLock)
                    {
                        range.Offset += n;
                        range.Length -= n;
                        if (range.Length <= 0)
                            pending.RemoveAt(0);
                    }

                    retried += n;
                    progress?.Report(new ImagingProgress
                    {
                        Phase = "Retrying unreadable areas",
                        BytesDone = retried,
                        BytesTotal = retryTotal,
                        BadBytes = Interlocked.Read(ref badBytes),
                        Elapsed = clock.Elapsed,
                    });

                    if (lastSave.ElapsedMilliseconds >= 5000)
                    {
                        SaveReport(output, null);
                        lastSave.Restart();
                    }
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        private static void AddRange(List<ByteRange> list, long offset, long length)
        {
            if (list.Count > 0)
            {
                var last = list[list.Count - 1];
                if (last.Offset + last.Length == offset)
                {
                    last.Length += length;
                    return;
                }
            }
            list.Add(new ByteRange { Offset = offset, Length = length });
        }

        // ---- hashes ----

        private List<KeyValuePair<string, IncrementalHash>> CreateHashes()
        {
            var list = new List<KeyValuePair<string, IncrementalHash>>();
            foreach (var name in options.Hashes)
            {
                HashAlgorithmName algorithm;
                switch (name.Trim().ToLowerInvariant().Replace("-", ""))
                {
                    case "md5": algorithm = HashAlgorithmName.MD5; break;
                    case "sha1": algorithm = HashAlgorithmName.SHA1; break;
                    case "sha256": algorithm = HashAlgorithmName.SHA256; break;
                    case "sha512": algorithm = HashAlgorithmName.SHA512; break;
                    default: throw new ArgumentException($"Unknown hash '{name}'. Use md5, sha1, sha256 or sha512.");
                }
                list.Add(new KeyValuePair<string, IncrementalHash>(algorithm.Name, IncrementalHash.CreateHash(algorithm)));
            }
            return list;
        }

        private static Dictionary<string, string> Finish(List<KeyValuePair<string, IncrementalHash>> list)
        {
            var result = new Dictionary<string, string>();
            foreach (var h in list)
            {
                result[h.Key] = BitConverter.ToString(h.Value.GetHashAndReset()).Replace("-", "").ToLowerInvariant();
                h.Value.Dispose();
            }
            return result;
        }

        /// <summary>
        /// Hashes the finished image by reading it back (needed after a
        /// resume or when areas were filled in out of order).
        /// </summary>
        private Dictionary<string, string> HashTarget(FileStream output, IProgress<ImagingProgress> progress, Stopwatch clock, CancellationToken cancellationToken)
        {
            var list = CreateHashes();
            output.Flush();
            Stream image = output;
            if (compressed)
                image = new ImgcStream(output, true, cancellationToken, false);

            var buffer = new byte[1 << 20];
            var meter = new SpeedMeter(0);
            long pos = 0;
            image.Position = 0;
            while (pos < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, total - pos);
                int n = image.Read(buffer, 0, want);
                if (n <= 0)
                    break;
                foreach (var h in list)
                    h.Value.AppendData(buffer, 0, n);
                pos += n;
                if (meter.Due())
                    progress?.Report(meter.Snapshot("Hashing", pos, total, badBytes, clock.Elapsed));
            }
            return Finish(list);
        }

        // ---- report / resume ----

        private void SaveReport(FileStream output, ImgcWriter writer)
        {
            // Everything the report claims must be on disk first.
            writer?.FlushZeros();
            output.Flush(true);

            var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                lock (stateLock)
                {
                    json.WriteStartObject();
                    json.WriteString("tool", "FATXTools disk imager");
                    json.WriteString("source", source.Name);
                    json.WriteString("model", source.Model ?? "");
                    json.WriteString("revision", source.Revision ?? "");
                    json.WriteString("serial", source.Serial ?? "");
                    json.WriteNumber("sectorSize", source.SectorSize);
                    json.WriteNumber("length", total);
                    json.WriteString("format", compressed ? "imgc" : "raw");
                    json.WriteBoolean("finished", finished);
                    json.WriteNumber("position", donePosition);
                    json.WriteNumber("targetPosition", compressed ? output.Position : donePosition);
                    json.WriteString("updated", DateTime.UtcNow.ToString("o"));
                    WriteRanges(json, "pending", pending);
                    WriteRanges(json, "unreadable", bad);
                    json.WriteNumber("unreadableBytes", bad.Sum(r => r.Length));
                    json.WriteStartObject("hashes");
                    foreach (var h in hashes)
                        json.WriteString(h.Key, h.Value);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
            }

            // Replace the report in one step so a crash cannot leave half of it.
            var temp = reportPath + ".tmp";
            File.WriteAllBytes(temp, buffer.ToArray());
            if (File.Exists(reportPath))
                File.Delete(reportPath);
            File.Move(temp, reportPath);
        }

        private static void WriteRanges(Utf8JsonWriter json, string name, List<ByteRange> ranges)
        {
            json.WriteStartArray(name);
            foreach (var r in ranges)
            {
                json.WriteStartObject();
                json.WriteNumber("offset", r.Offset);
                json.WriteNumber("length", r.Length);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }

        /// <summary>
        /// Loads the report of an earlier run. Returns false when there is
        /// none or it belongs to a different source or format.
        /// </summary>
        private bool LoadReport()
        {
            if (!File.Exists(reportPath))
                return false;

            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllBytes(reportPath)))
                {
                    var root = doc.RootElement;
                    if (root.GetProperty("length").GetInt64() != total ||
                        root.GetProperty("format").GetString() != (compressed ? "imgc" : "raw") ||
                        root.GetProperty("serial").GetString() != (source.Serial ?? ""))
                    {
                        return false;
                    }

                    donePosition = root.GetProperty("position").GetInt64();
                    resumeTargetPosition = root.GetProperty("targetPosition").GetInt64();
                    ReadRanges(root, "pending", pending);
                    ReadRanges(root, "unreadable", bad);
                    badBytes = bad.Sum(r => r.Length);
                    pendingEverUsed = true;
                    return donePosition >= 0 && donePosition <= total;
                }
            }
            catch (Exception e) when (e is JsonException || e is KeyNotFoundException || e is InvalidOperationException || e is IOException)
            {
                pending.Clear();
                bad.Clear();
                return false;
            }
        }

        private long resumeTargetPosition;

        private static void ReadRanges(JsonElement root, string name, List<ByteRange> ranges)
        {
            ranges.Clear();
            foreach (var r in root.GetProperty(name).EnumerateArray())
                ranges.Add(new ByteRange { Offset = r.GetProperty("offset").GetInt64(), Length = r.GetProperty("length").GetInt64() });
        }

        private FileStream OpenResume(out long startPosition)
        {
            var output = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            try
            {
                if (compressed)
                {
                    // Trust the file over the report: continue after the last
                    // complete block that the report knows about.
                    long dataLength, dataEnd;
                    using (var existing = new ImgcStream(output, true, default, false))
                    {
                        dataLength = existing.DataLength;
                        dataEnd = existing.DataEndFilePosition;
                    }

                    if (dataLength < donePosition || resumeTargetPosition > dataEnd || resumeTargetPosition < ImgcHeader.Size)
                        throw new IOException("The existing image is shorter than its dump report says; it cannot be resumed.");

                    output.SetLength(resumeTargetPosition);
                    output.Position = resumeTargetPosition;
                }
                else if (output.Length < total)
                {
                    output.SetLength(total);
                }

                // Restart on a request boundary.
                startPosition = compressed ? donePosition : donePosition / options.ChunkSize * options.ChunkSize;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        private sealed class SpeedMeter
        {
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private long lastReport = -1000;
            private long windowStart;
            private long windowBytes;
            private double speed;

            public SpeedMeter(long startBytes)
            {
                windowBytes = startBytes;
            }

            public bool Due()
            {
                return clock.ElapsedMilliseconds - lastReport >= 250;
            }

            public ImagingProgress Snapshot(string phase, long done, long total, long badBytes, TimeSpan elapsed)
            {
                long now = clock.ElapsedMilliseconds;
                lastReport = now;

                long span = now - windowStart;
                if (span >= 1000)
                {
                    double current = (done - windowBytes) * 1000.0 / span;
                    speed = speed <= 0 ? current : speed * 0.7 + current * 0.3;
                    windowStart = now;
                    windowBytes = done;
                }
                else if (speed <= 0 && span > 0)
                {
                    speed = (done - windowBytes) * 1000.0 / span;
                }

                return new ImagingProgress
                {
                    Phase = phase,
                    BytesDone = done,
                    BytesTotal = total,
                    BadBytes = badBytes,
                    BytesPerSecond = speed,
                    Elapsed = elapsed,
                };
            }
        }
    }
}
