using System.IO;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.AssetTool;

/// <summary>Собирает .ico из спрайта: каждый размер — целое увеличение без сглаживания.</summary>
internal static class IconFile
{
    public static byte[] Build(SpriteDefinition sprite, Palette palette, int[] scales)
    {
        var images = scales.Select(scale => Render(sprite, palette, scale)).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)images.Count);

        var offset = 6 + (16 * images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            var size = sprite.Width * scales[index];
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(images[index].Length);
            writer.Write(offset);
            offset += images[index].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }

        return stream.ToArray();
    }

    public static byte[] Render(SpriteDefinition sprite, Palette palette, int scale)
    {
        var width = sprite.Width * scale;
        var height = sprite.Height * scale;
        var pixels = new uint[width * height];
        var rows = sprite.Frames[0];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (palette.TryGetByKey(rows[y / scale][x / scale], out var color))
                {
                    pixels[(y * width) + x] = color.Argb;
                }
            }
        }

        return Png.Encode(pixels, width, height);
    }
}
