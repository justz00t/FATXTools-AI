using FATX.FileSystem;
using FATX.Formats;

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

        public override bool Test() => true;

        public override void Parse()
        {
        }
    }
}
