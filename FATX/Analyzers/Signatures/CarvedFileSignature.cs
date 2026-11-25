using FATX.FileSystem;
using FATX.Formats;
using System.Collections.Generic;

namespace FATX.Analyzers.Signatures
{
    /// <summary>
    /// A file found by the <see cref="FileCarver"/>, described by the
    /// <see cref="FormatInfo"/> parsed from its header.
    /// </summary>
    public class CarvedFileSignature : FileSignature
    {
        public CarvedFileSignature(Volume volume, long offset, FormatInfo info)
            : base(volume, offset)
        {
            Info = info;
            FileSize = info.FileSize ?? 0;
        }

        public FormatInfo Info { get; }

        /// <summary>
        /// Whether the first cluster of this file is allocated in the active
        /// file system (i.e. the file is probably not deleted).
        /// </summary>
        public bool InAllocatedSpace { get; set; }

        /// <summary>
        /// The signature matched but the header could not be parsed or makes
        /// no sense: the file is damaged (or a false positive).
        /// </summary>
        public bool Damaged => !Info.Plausible;

        /// <summary>
        /// The clusters holding the file when it is not contiguous; null for
        /// a contiguous file starting at <see cref="FileSignature.Offset"/>.
        /// </summary>
        public List<uint> ClusterChain { get; set; }

        /// <summary>
        /// For files whose size cannot be read from a header: the most to
        /// save (0 = not applicable). The carver then sizes the file up to
        /// the next found file or used cluster.
        /// </summary>
        public long GuessLimit { get; set; }

        public override bool Test() => true;

        public override void Parse()
        {
        }
    }
}
