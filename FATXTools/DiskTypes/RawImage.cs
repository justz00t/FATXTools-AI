using System.IO;

namespace FATXTools.DiskTypes
{
    public class RawImage : FATX.DriveReader
    {
        // Raw images and HDD Raw Copy Tool compressed images (.imgc).
        public RawImage(string fileName)
            : base(FATX.Imaging.ImageFile.OpenRead(fileName))
        {
            base.Initialize();
        }
    }
}
