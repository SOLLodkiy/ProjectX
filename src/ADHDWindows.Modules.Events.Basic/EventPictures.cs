using ADHDWindows.Modules.Hardships.Screen;

namespace ADHDWindows.Modules.Events.Basic;

/// <summary>Пиксельные картинки событий в цветах палитры. Контур светлый, чтобы картинка была видна на тёмном фоне.</summary>
internal static class EventPictures
{
    public static readonly Picture GoldenCoin = Make(
        "....KKKKKK....",
        "..KKYYYYYYKK..",
        ".KYYTTYYYYYOK.",
        ".KYTYYKKYYYOK.",
        "KYYTYKYYKYYYOK",
        "KYYYYKYYYYYYOK",
        "KYYYYYKKYYYYOK",
        "KYYYYYYYKYYYOK",
        "KYYYYKYYKYYYOK",
        "KYYYYYKKYYYYOK",
        ".KYYYYYYYYYOK.",
        ".KYYYYYYYYOOK.",
        "..KKOOOOOOKK..",
        "....KKKKKK....");

    public static readonly Picture GoldenCoinEdge = Make(
        ".....KKKK.....",
        "....KYYYOK....",
        "...KYTYYYOK...",
        "...KYTYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYYOK...",
        "...KYYYYOOK...",
        "....KOOOOK....",
        ".....KKKK.....");

    public static readonly Picture SmallCoin = Make(
        "..KKKK..",
        ".KYTYYK.",
        "KYTYYYOK",
        "KYYYYYOK",
        "KYYYYYOK",
        "KYYYYOOK",
        ".KOOOOK.",
        "..KKKK..");

    public static readonly Picture GoblinRun1 = Make(
        "....KKKK.....",
        "...KGGGGK....",
        "..KGWKGWKK...",
        "KKGGGGGGGGK..",
        ".KGGrrrrGK...",
        "..KKGGGGK.KK.",
        "..KOKGGKKKYYK",
        ".KOOOKGGKYYYK",
        ".KOOOKGGGKYK.",
        "..KKKGGKGGK..",
        "....KGK.KGK..",
        "...KGK...KGK.",
        "...KK.....KK.");

    public static readonly Picture GoblinRun2 = Make(
        "....KKKK.....",
        "...KGGGGK....",
        "..KGWKGWKK...",
        "KKGGGGGGGGK..",
        ".KGGrrrrGK...",
        "..KKGGGGK.KK.",
        "..KOKGGKKKYYK",
        ".KOOOKGGKYYYK",
        ".KOOOKGGGKYK.",
        "..KKKGGGGK...",
        ".....KGGK....",
        "....KGKKGK...",
        "....KK..KK...");

    public static readonly Picture BossIdle = Make(
        "......KKKKKKKK......",
        "....KKVVVVVVVVKK....",
        "...KVVVVVVVVVVVVK...",
        "..KVVTTVVVVVVVVVVK..",
        ".KVVTVVVVVVVVVVVVVK.",
        ".KVVVKKKVVVVKKKVVVK.",
        "KVVVKWWKKVVKKWWKVVVK",
        "KVVVKWKKKVVKKKWKVVVK",
        "KVVVVKKKVVVVKKKVVVVK",
        "KVVVVVVVVVVVVVVVVVVK",
        "KVVVVKKKKKKKKKKVVVVK",
        "KVVVKRWKWKWKWRRKVVVK",
        "KVVVVKKKKKKKKKKVVVVK",
        ".KVVVVVVVVVVVVVVVVK.",
        ".KVVVVVVVVVVVVVVVVK.",
        "..KKVVVVKKKKVVVVKK..",
        "...KVVVK....KVVVK...",
        "...KKKKK....KKKKK...");

    public static readonly Picture BossHurt = Recolor(BossIdle, Colors.Violet, Colors.Red);

    public static readonly Picture Gift = Make(
        "...KK....KK...",
        "..KYYK..KYYK..",
        "...KYYKKYYK...",
        "KKKKKKYYKKKKKK",
        "KRRRRKYYKRRRRK",
        "KRRRRKYYKRRRRK",
        "KKKKKKYYKKKKKK",
        ".KRRRKYYKRRRK.",
        ".KRRRKYYKRRRK.",
        ".KRRRKYYKRRRK.",
        ".KrrrKYYKrrrK.",
        ".KKKKKKKKKKKK.");

    public static readonly Picture Pumpkin = Make(
        "......KK......",
        ".....KgK......",
        "...KKKKKKKK...",
        "..KOOOOOOOOK..",
        ".KOOKKOOKKOOK.",
        "KOOOKKOOKKOOOK",
        "KOOOOOOOOOOOOK",
        "KOOKOOOOOOKOOK",
        "KOOOKKKKKKOOOK",
        ".KOOOOOOOOOOK.",
        "..KKOOOOOOKK..",
        "....KKKKKK....");

    public static readonly Picture Heart = Make(
        "..KKK...KKK..",
        ".KNNNK.KNNNK.",
        "KNWNNNKNNNNNK",
        "KNWNNNNNNNNNK",
        "KNNNNNNNNNNNK",
        ".KNNNNNNNNNK.",
        "..KNNNNNNNK..",
        "...KNNNNNK...",
        "....KNNNK....",
        ".....KNK.....",
        "......K......");

    public static readonly Picture Flower = Make(
        "....KK.KK....",
        "...KNNKNNK...",
        "..KKNNKNNKK..",
        ".KNNKKYKKNNK.",
        ".KNNKYYYKNNK.",
        "..KKKKYKKKK..",
        "...KNNKNNK...",
        "....KKgKK....",
        ".KK...gK.....",
        "KGGK..gK.....",
        ".KGGK.gK.....",
        "..KKKKgK.....",
        "......KK.....");

    public static readonly Picture Snowflake = Make(
        "......K......",
        "...K..W..K...",
        "....W.W.W....",
        ".K...WWW...K.",
        "..WW.WMW.WW..",
        "....WWMWW....",
        "KWWWMMWMMWWWK",
        "....WWMWW....",
        "..WW.WMW.WW..",
        ".K...WWW...K.",
        "....W.W.W....",
        "...K..W..K...",
        "......K......");

    public static Picture Chest(string tier, bool open) => tier switch
    {
        "legendary" => open ? LegendaryOpen : Legendary,
        "rare" => open ? RareOpen : Rare,
        _ => open ? CommonOpen : Common,
    };

    private static readonly string[] ChestRows =
    [
        "..KKKKKKKKKKKK..",
        ".KBBBBBBBBBBBBK.",
        "KBDDDDDDDDDDDDBK",
        "KBDDDDDDDDDDDDBK",
        "KKKKKKKYYKKKKKKK",
        "KBBBBBKYYKBBBBBK",
        "KBDDDDKKKKDDDDBK",
        "KBDDDDDDDDDDDDBK",
        "KBDDDDDDDDDDDDBK",
        "KBBBBBBBBBBBBBBK",
        "KKKKKKKKKKKKKKKK",
    ];

    private static readonly string[] ChestOpenRows =
    [
        "..KKKKKKKKKKKK..",
        ".KBDDDDDDDDDDBK.",
        "KBDDDDDDDDDDDDBK",
        "KKKKKKKKKKKKKKKK",
        ".T.Y..T..Y..T.Y.",
        "KKYYTYYYYYTYYOKK",
        "KBYYYYOYYYYYYOBK",
        "KBDYYYYYOYYYYDBK",
        "KBDDDDDDDDDDDDBK",
        "KBBBBBBBBBBBBBBK",
        "KKKKKKKKKKKKKKKK",
    ];

    private static readonly Picture Common = ChestOf(ChestRows, Colors.Amber, Colors.RedDark);
    private static readonly Picture CommonOpen = ChestOf(ChestOpenRows, Colors.Amber, Colors.RedDark);
    private static readonly Picture Rare = ChestOf(ChestRows, Colors.Violet, Colors.Panel);
    private static readonly Picture RareOpen = ChestOf(ChestOpenRows, Colors.Violet, Colors.Panel);
    private static readonly Picture Legendary = ChestOf(ChestRows, Colors.Gold, Colors.Red);
    private static readonly Picture LegendaryOpen = ChestOf(ChestOpenRows, Colors.Gold, Colors.Red);

    private static Picture Make(params string[] rows) => Picture.FromRows(rows, Colors.Keys).Outlined(Colors.Mist);

    private static Picture ChestOf(string[] rows, uint band, uint wood)
    {
        var colors = new Dictionary<char, uint>(Colors.Keys) { ['B'] = band, ['D'] = wood };
        return Picture.FromRows(rows, colors).Outlined(Colors.Mist);
    }

    private static Picture Recolor(Picture source, uint from, uint to)
    {
        var result = new Picture(source.Width, source.Height);
        for (var index = 0; index < source.Pixels.Length; index++)
        {
            result.Pixels[index] = source.Pixels[index] == from ? to : source.Pixels[index];
        }

        return result;
    }
}
