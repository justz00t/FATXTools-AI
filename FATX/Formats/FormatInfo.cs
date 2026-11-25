using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FATX.Formats
{
    public enum VerificationStatus
    {
        /// <summary>No verification was attempted.</summary>
        NotChecked,
        /// <summary>The file's internal checksums/structure match its contents.</summary>
        Verified,
        /// <summary>The file's internal checksums/structure do not match: data is corrupt or overwritten.</summary>
        Failed,
        /// <summary>The format has no checksums that can be validated (or they are encrypted).</summary>
        Unverifiable,
    }

    /// <summary>
    /// What was learned about a file from its contents.
    /// </summary>
    public class FormatInfo
    {
        /// <summary>Short format name, e.g. "XEX2", "XBE", "STFS LIVE".</summary>
        public string Format { get; set; }

        /// <summary>Default file extension including the dot.</summary>
        public string Extension { get; set; }

        /// <summary>Name embedded in the file (e.g. original PE name or debug file name), if any.</summary>
        public string EmbeddedName { get; set; }

        /// <summary>On-disk size of the file computed from its headers, if known.</summary>
        public long? FileSize { get; set; }

        /// <summary>Whether <see cref="FileSize"/> is exact or only an upper bound / estimate.</summary>
        public bool FileSizeExact { get; set; }

        public uint? TitleId { get; set; }
        public string Title { get; set; }
        public string Version { get; set; }

        /// <summary>
        /// False when the magic matched but the header is nonsensical (most
        /// likely a false positive).
        /// </summary>
        public bool Plausible { get; set; } = true;

        public VerificationStatus Verification { get; set; } = VerificationStatus.NotChecked;
        public string VerificationDetail { get; set; }

        public Dictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public List<string> Warnings { get; } = new List<string>();

        public string TitleIdString => TitleId.HasValue ? TitleId.Value.ToString("X8") : null;

        /// <summary>
        /// A file name suitable for saving this file.
        /// </summary>
        public string SuggestFileName(string fallbackBase)
        {
            string name = EmbeddedName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                // Embedded names are often paths ("d:\build\foo.exe").
                var idx = name.LastIndexOfAny(new[] { '\\', '/' });
                if (idx >= 0)
                    name = name.Substring(idx + 1);
                name = System.IO.Path.ChangeExtension(name, Extension);
            }

            if (string.IsNullOrWhiteSpace(name))
                name = fallbackBase + Extension;

            return name;
        }

        /// <summary>
        /// Checks the identified file against the size recorded in its
        /// directory entry. A file whose header describes a different size is
        /// not the file the entry refers to (its clusters were reused by
        /// another file), even when that other file's checksums pass.
        /// </summary>
        public void CheckExpectedSize(long expectedSize)
        {
            if (!FileSize.HasValue || expectedSize <= 0)
                return;

            bool mismatch = FileSizeExact ? FileSize.Value != expectedSize : FileSize.Value < expectedSize;
            if (!mismatch)
                return;

            var detail = $"Header describes 0x{FileSize.Value:X} bytes but the directory entry says 0x{expectedSize:X}: the clusters hold a different file (overwritten).";
            Warnings.Add(detail);
            if (Verification != VerificationStatus.NotChecked)
            {
                Verification = VerificationStatus.Failed;
                VerificationDetail = detail;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Format);
            if (TitleId.HasValue) sb.Append($" title={TitleIdString}");
            if (!string.IsNullOrEmpty(Title)) sb.Append($" \"{Title}\"");
            if (!string.IsNullOrEmpty(Version)) sb.Append($" v{Version}");
            if (!string.IsNullOrEmpty(EmbeddedName)) sb.Append($" name={EmbeddedName}");
            if (FileSize.HasValue) sb.Append($" size=0x{FileSize.Value:X}{(FileSizeExact ? "" : "~")}");
            if (Verification != VerificationStatus.NotChecked) sb.Append($" verify={Verification}");
            if (!Plausible) sb.Append(" DAMAGED-HEADER");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Identifies Xbox related file formats from their contents.
    /// </summary>
    public static class FormatDetector
    {
        private delegate FormatInfo Parser(IByteSource source, bool verify);

        private static readonly Parser[] Parsers =
        {
            XexParser.TryParse,
            XbeParser.TryParse,
            StfsParser.TryParse,
            SimpleFormats.TryParsePdb,
            SimpleFormats.TryParseXdbf,
            SimpleFormats.TryParseRiff,
            SimpleFormats.TryParseXpr0,
            SimpleFormats.TryParsePe,
            SimpleFormats.TryParseBink,
            SimpleFormats.TryParsePng,
        };

        /// <summary>
        /// Magic values (first four bytes, read big endian) that start a known format.
        /// </summary>
        public static readonly IReadOnlyCollection<uint> Magics = new HashSet<uint>
        {
            0x58455832, // XEX2
            0x58455831, // XEX1
            0x58455825, // XEX%
            0x5845582D, // XEX-
            0x58455830, // XEX0
            0x58424548, // XBEH
            0x434F4E20, // "CON "
            0x4C495645, // LIVE
            0x50495253, // PIRS
            0x4D696372, // "Micr" (PDB)
            0x58444246, // XDBF
            0x52494646, // RIFF
            0x58505230, // XPR0
            0x4D5A9000, // MZ\x90\0
            0x42494B62, 0x42494B64, 0x42494B66, 0x42494B67, 0x42494B68, 0x42494B69, 0x42494B6B, // BIKb..BIKk (Bink video)
            0x4B423261, 0x4B423264, 0x4B423266, 0x4B423267, 0x4B423268, 0x4B423269, 0x4B42326A, // KB2a..KB2j (Bink 2)
            0x89504E47, // PNG
        };

        /// <summary>
        /// Identifies the format of the data in <paramref name="source"/>.
        /// </summary>
        /// <param name="verify">Also verify embedded checksums (slower, reads the whole file).</param>
        /// <returns>Null if the format is not recognized.</returns>
        /// <summary>
        /// Identifies a file recovered through a directory entry and checks it
        /// against the size recorded in that entry.
        /// </summary>
        public static FormatInfo Identify(IByteSource source, bool verify, long expectedSize)
        {
            var info = Identify(source, verify);
            info?.CheckExpectedSize(expectedSize);
            return info;
        }

        public static FormatInfo Identify(IByteSource source, bool verify = false)
        {
            if (source.Length < 4)
                return null;

            foreach (var parser in Parsers)
            {
                try
                {
                    var info = parser(source, verify);
                    if (info != null)
                        return info;
                }
                catch (Exception e)
                {
                    // A matching magic with unreadable headers: report what we can.
                    var magic = source.ReadBytes(0, 4);
                    if (Magics.Contains(magic.Length == 4 ? (uint)(magic[0] << 24 | magic[1] << 16 | magic[2] << 8 | magic[3]) : 0u))
                    {
                        var info = new FormatInfo { Format = Encoding.ASCII.GetString(magic.Select(b => b >= 0x20 && b < 0x7f ? b : (byte)'.').ToArray()), Extension = ".bin" };
                        info.Warnings.Add("Header could not be parsed: " + e.Message);
                        info.Plausible = false;
                        info.Verification = verify ? VerificationStatus.Failed : VerificationStatus.NotChecked;
                        return info;
                    }
                }
            }

            return null;
        }
    }
}
