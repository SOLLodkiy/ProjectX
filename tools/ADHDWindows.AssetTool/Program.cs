using ADHDWindows.AssetTool;

return args switch
{
    ["sprites", var assets] => SpriteCommand.Run(assets),
    ["font", var ttf, var size, var name, var output] =>
        FontCommand.Run(ttf, int.Parse(size), name, output, previewPath: null),
    ["font", var ttf, var size, var name, var output, var preview] =>
        FontCommand.Run(ttf, int.Parse(size), name, output, preview),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        Команды:
          sprites <папка assets>                      собрать атлас и значок из assets/src/sprites
          font <ttf> <размер> <имя> <json> [png]      перевести пиксельный TTF в растровый шрифт
        """);
    return 1;
}
