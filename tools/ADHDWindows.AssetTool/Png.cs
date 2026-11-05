using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADHDWindows.AssetTool;

internal static class Png
{
    public static byte[] Encode(uint[] argb, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, argb, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
