using System.IO;
using ADHDWindows.Core.Pixels;

namespace ADHDWindows.AssetTool;

internal static class SpriteCommand
{
    private const string IconSprite = "app_icon";

    public static int Run(string assetsDir)
    {
        var palette = Palette.Parse(File.ReadAllText(Path.Combine(assetsDir, "palette.json")));
        var sources = Directory.GetFiles(Path.Combine(assetsDir, "src", "sprites"), "*.txt");
        Array.Sort(sources, StringComparer.Ordinal);
        var definitions = sources
            .SelectMany(path => SpriteSource.Parse(File.ReadAllText(path), palette, Path.GetFileName(path)))
            .ToList();

        var packed = SpriteAtlasPacker.Pack(definitions, palette);
        var spritesDir = Path.Combine(assetsDir, "sprites");
        Directory.CreateDirectory(spritesDir);
        File.WriteAllText(Path.Combine(spritesDir, "atlas.json"), packed.Atlas.ToJson() + "\n");
        File.WriteAllBytes(
            Path.Combine(spritesDir, "atlas.png"),
            Png.Encode(packed.Pixels, packed.Atlas.Width, packed.Atlas.Height));

        var icon = definitions.Single(sprite => sprite.Name == IconSprite);
        File.WriteAllBytes(Path.Combine(assetsDir, "app.ico"), IconFile.Build(icon, palette, [1, 2, 3, 8]));

        Console.WriteLine($"Спрайтов: {definitions.Count}, атлас {packed.Atlas.Width}x{packed.Atlas.Height}");
        return 0;
    }
}
