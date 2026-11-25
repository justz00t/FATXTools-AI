using System.IO;

namespace FATX.Imaging
{
    /// <summary>
    /// Opens drive image files of any supported format.
    /// </summary>
    public static class ImageFile
    {
        /// <summary>
        /// Opens a raw image or an HDD Raw Copy Tool compressed image
        /// (".imgc", detected by content) read-only. Either way the returned
        /// stream reads as the raw drive.
        /// </summary>
        public static Stream OpenRead(string path)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0x10000);
            try
            {
                if (ImgcHeader.IsImgc(file))
                {
                    var imgc = new ImgcStream(file);
                    foreach (var warning in imgc.Warnings)
                        System.Console.WriteLine($"{Path.GetFileName(path)}: {warning}");
                    return imgc;
                }
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens a physical drive or an image file as a source for imaging.
        /// </summary>
        public static IDiskSource OpenSource(string path)
        {
            if (PhysicalDiskSource.IsDevicePath(path))
                return PhysicalDiskSource.Open(path);
            return new StreamDiskSource(OpenRead(path), path);
        }
    }
}
