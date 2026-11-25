using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FATX.Imaging
{
    /// <summary>
    /// Something a drive image can be read from: a physical drive or an image file.
    /// </summary>
    public interface IDiskSource : IDisposable
    {
        string Name { get; }
        long Length { get; }
        int SectorSize { get; }
        string Model { get; }
        string Revision { get; }
        string Serial { get; }

        /// <summary>
        /// Reads <paramref name="count"/> bytes at <paramref name="offset"/>
        /// (both multiples of the sector size, within the source).
        /// </summary>
        /// <exception cref="IOException">The range could not be read (e.g. bad sectors).</exception>
        void Read(long offset, byte[] buffer, int index, int count);
    }

    /// <summary>
    /// A drive that can be imaged.
    /// </summary>
    public class DiskInfo
    {
        public string Path { get; set; }
        public long Length { get; set; }
        public int SectorSize { get; set; } = 512;
        public string Model { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Serial { get; set; } = "";
        public string Bus { get; set; } = "";
        public bool Removable { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Path);
            sb.Append($"  {Length / (1024.0 * 1024 * 1024):F1} GB");
            if (!string.IsNullOrEmpty(Bus)) sb.Append($"  {Bus}");
            if (!string.IsNullOrEmpty(Model)) sb.Append($"  {Model}");
            if (!string.IsNullOrEmpty(Serial)) sb.Append($"  s/n {Serial}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Reads from any seekable stream (raw image files, <see cref="ImgcStream"/>).
    /// </summary>
    public class StreamDiskSource : IDiskSource
    {
        private readonly Stream stream;

        public StreamDiskSource(Stream stream, string name, int sectorSize = 512)
        {
            this.stream = stream;
            Name = name;
            SectorSize = sectorSize;
            Length = stream.Length;

            if (stream is ImgcStream imgc)
            {
                Model = imgc.Header.Model;
                Revision = imgc.Header.Revision;
                Serial = imgc.Header.Serial;
                if (imgc.Header.SectorSize > 0 && imgc.Header.SectorSize <= 0x10000)
                    SectorSize = (int)imgc.Header.SectorSize;
            }
        }

        public string Name { get; }
        public long Length { get; protected set; }
        public int SectorSize { get; protected set; }
        public string Model { get; protected set; } = "";
        public string Revision { get; protected set; } = "";
        public string Serial { get; protected set; } = "";

        public virtual void Read(long offset, byte[] buffer, int index, int count)
        {
            stream.Position = offset;
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, index + total, count - total);
                if (n <= 0)
                    throw new EndOfStreamException($"Unexpected end of {Name} at 0x{offset + total:X}.");
                total += n;
            }
        }

        public void Dispose()
        {
            stream.Dispose();
        }
    }

    /// <summary>
    /// Physical drives ("\\.\PhysicalDrive1" on Windows, "/dev/sdb" on Linux).
    /// </summary>
    public class PhysicalDiskSource : StreamDiskSource
    {
        private PhysicalDiskSource(Stream stream, DiskInfo info)
            : base(new FixedLengthStream(stream, info.Length), info.Path, info.SectorSize)
        {
            Length = info.Length;
            Model = info.Model;
            Revision = info.Revision;
            Serial = info.Serial;
        }

        public static bool IsDevicePath(string path)
        {
            return path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith("/dev/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Opens a drive for reading. Needs administrator / root rights.
        /// </summary>
        public static PhysicalDiskSource Open(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var handle = Win32.CreateFileW(path, Win32.GenericRead, Win32.ShareReadWrite, IntPtr.Zero,
                    Win32.OpenExisting, Win32.FlagSequentialScan, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(error == 5
                        ? $"Access to {path} was denied. Run as Administrator to read physical drives."
                        : $"Cannot open {path} (Windows error {error}).");
                }

                var info = Win32.Query(handle, path);
                if (info.Length <= 0)
                {
                    handle.Dispose();
                    throw new IOException($"Cannot determine the size of {path}.");
                }

                // Raw device reads are not cached by Windows; a FileStream
                // buffer would only add a copy.
                return new PhysicalDiskSource(new FileStream(handle, FileAccess.Read, 1, false), info);
            }
            else
            {
                var info = LinuxQuery(path);
                if (info.Length <= 0)
                    throw new IOException($"Cannot determine the size of {path}.");
                return new PhysicalDiskSource(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1), info);
            }
        }

        /// <summary>
        /// Lists the drives of this computer.
        /// </summary>
        public static List<DiskInfo> ListDisks()
        {
            var result = new List<DiskInfo>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                for (int i = 0; i < 64; i++)
                {
                    string path = $@"\\.\PhysicalDrive{i}";
                    // No access rights are needed to query a drive.
                    using (var handle = Win32.CreateFileW(path, 0, Win32.ShareReadWrite, IntPtr.Zero, Win32.OpenExisting, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                            continue;
                        try
                        {
                            var info = Win32.Query(handle, path);
                            if (info.Length > 0)
                                result.Add(info);
                        }
                        catch (Exception e) when (e is IOException || e is ArgumentException)
                        {
                        }
                    }
                }
            }
            else if (Directory.Exists("/sys/block"))
            {
                foreach (var dir in Directory.GetDirectories("/sys/block"))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("zram") || name.StartsWith("dm-"))
                        continue;
                    try
                    {
                        var info = LinuxQuery("/dev/" + name);
                        if (info.Length > 0)
                            result.Add(info);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException)
                    {
                    }
                }
                result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            }

            return result;
        }

        private static DiskInfo LinuxQuery(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            var sys = "/sys/class/block/" + name;
            var info = new DiskInfo { Path = path };

            // "size" is always in 512 byte units.
            info.Length = long.Parse(ReadSysFile(sys + "/size", "0")) * 512;
            info.SectorSize = int.Parse(ReadSysFile(sys + "/queue/logical_block_size", "512"));
            info.Model = ReadSysFile(sys + "/device/model", "");
            info.Revision = ReadSysFile(sys + "/device/rev", "");
            info.Serial = ReadSysFile(sys + "/device/serial", "");
            info.Removable = ReadSysFile(sys + "/removable", "0") == "1";
            return info;
        }

        private static string ReadSysFile(string path, string fallback)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return fallback;
            }
        }

        /// <summary>
        /// Device handles do not report a length; supply the one from the drive geometry.
        /// </summary>
        private sealed class FixedLengthStream : Stream
        {
            private readonly Stream inner;
            private readonly long length;

            public FixedLengthStream(Stream inner, long length)
            {
                this.inner = inner;
                this.length = length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private static class Win32
        {
            public const uint GenericRead = 0x80000000;
            public const uint ShareReadWrite = 3;
            public const uint OpenExisting = 3;
            public const uint FlagSequentialScan = 0x08000000;

            private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
            private const uint IoctlStorageQueryProperty = 0x002D1400;

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
                IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
                byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

            public static DiskInfo Query(SafeFileHandle handle, string path)
            {
                var info = new DiskInfo { Path = path };

                // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes, BytesPerSector at 20), DiskSize at 24.
                var geometry = new byte[256];
                if (DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, geometry, geometry.Length, out int returned, IntPtr.Zero) && returned >= 32)
                {
                    info.SectorSize = BitConverter.ToInt32(geometry, 20);
                    info.Length = BitConverter.ToInt64(geometry, 24);
                    if (info.SectorSize <= 0)
                        info.SectorSize = 512;
                }

                // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
                var query = new byte[12];
                var descriptor = new byte[1024];
                if (DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out returned, IntPtr.Zero) && returned >= 36)
                {
                    info.Removable = descriptor[10] != 0;
                    string vendor = ReadString(descriptor, BitConverter.ToInt32(descriptor, 12), returned);
                    string product = ReadString(descriptor, BitConverter.ToInt32(descriptor, 16), returned);
                    info.Model = (vendor + " " + product).Trim();
                    info.Revision = ReadString(descriptor, BitConverter.ToInt32(descriptor, 20), returned);
                    info.Serial = ReadString(descriptor, BitConverter.ToInt32(descriptor, 24), returned);
                    info.Bus = BusName(BitConverter.ToInt32(descriptor, 28));
                }

                return info;
            }

            private static string ReadString(byte[] buffer, int offset, int limit)
            {
                if (offset <= 0 || offset >= limit)
                    return "";
                int end = offset;
                while (end < limit && buffer[end] != 0)
                    end++;
                return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
            }

            private static string BusName(int bus)
            {
                switch (bus)
                {
                    case 1: return "SCSI";
                    case 2: return "ATAPI";
                    case 3: return "ATA";
                    case 4: return "1394";
                    case 7: return "USB";
                    case 8: return "RAID";
                    case 10: return "SAS";
                    case 11: return "SATA";
                    case 12: return "SD";
                    case 13: return "MMC";
                    case 17: return "NVMe";
                    default: return "";
                }
            }
        }
    }
}
