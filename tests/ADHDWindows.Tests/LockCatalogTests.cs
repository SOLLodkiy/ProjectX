using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Locks;
using ADHDWindows.Core.Modules;
using ADHDWindows.Core.Safety;
using ADHDWindows.Core.Storage;
using ADHDWindows.Modules.Locks;

namespace ADHDWindows.Tests;

public sealed class LockCatalogTests
{
    private const string OwnFile = @"C:\Program Files\ADHDWindows\ADHDWindows.exe";

    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private readonly RecordingLog _log = new();
    private readonly ProgramCatalog _catalog;
    private readonly ProgramClassifier _classifier;

    public LockCatalogTests()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), _log);
        data.Load();
        _catalog = ProgramCatalog.Load(data);
        _classifier = new ProgramClassifier(_catalog, [OwnFile]);
    }

    [Theory]
    [InlineData(@"explorer.exe")]
    [InlineData(@"System32\Taskmgr.exe")]
    [InlineData(@"System32\cmd.exe")]
    [InlineData(@"System32\conhost.exe")]
    [InlineData(@"System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"System32\control.exe")]
    [InlineData(@"System32\svchost.exe")]
    public void Real_system_files_are_free(string relative)
    {
        var path = Path.Combine(Windows, relative);

        Assert.Null(_classifier.Classify(ProgramFile.Read(path)).Offer);
    }

    [Fact]
    public void Real_notepad_has_a_catalog_price_despite_the_language_pack_name()
    {
        var file = ProgramFile.Read(Path.Combine(Windows, "System32", "notepad.exe"));

        var offer = _classifier.Classify(file).Offer;

        Assert.NotNull(offer);
        Assert.Equal("program:notepad", offer.Key);
        Assert.Equal(50, offer.Price);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_4.16336.0.0_x64__pzs8sxrjxfjjc\app\claude.exe", "Anthropic", "Claude", "claude.exe")]
    [InlineData(@"C:\Users\Me\AppData\Local\AnthropicClaude\claude.exe", "Anthropic, PBC", "Claude", "claude.exe")]
    [InlineData(@"C:\Program Files\Yandex\YandexBrowser\Application\browser.exe", "YANDEX LLC", "Yandex", "browser.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google LLC", "Google Chrome", "chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Corporation", "Microsoft Edge", "msedge.exe")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", "Mozilla Corporation", "Firefox", "firefox.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.20_x64__8wekyb3d8bbwe\WindowsTerminal.exe", "Microsoft Corporation", "Windows Terminal", "WindowsTerminal.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "Microsoft Corporation", "PowerShell", "pwsh.dll")]
    [InlineData(@"C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA Overlay.exe", "NVIDIA Corporation", "NVIDIA App", "NVIDIA Overlay.exe")]
    [InlineData(OwnFile, null, null, null)]
    public void Always_free_programs_are_recognized(string path, string? company, string? product, string? original)
    {
        Assert.Null(_classifier.Classify(File(path, company, product, original)).Offer);
    }

    [Theory]
    [InlineData(@"C:\Users\Me\AppData\Local\Discord\app-1.0\Discord.exe", "Discord Inc.", "Discord", "Discord.exe", "program:discord", 500)]
    [InlineData(@"C:\Users\Me\AppData\Roaming\Telegram Desktop\Telegram.exe", "Telegram FZ-LLC", "Telegram Desktop", null, "program:telegram", 500)]
    [InlineData(@"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.26_x64__cv1g1gvanyjgm\WhatsApp.Root.exe", "WhatsApp.Root", "WhatsApp.Root", "WhatsApp.Root.dll", "program:whatsapp", 500)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.26_x64__8wekyb3d8bbwe\CalculatorApp.exe", "Microsoft Corporation", "Microsoft Calculator", "CalculatorApp.exe", "program:calculator", 50)]
    [InlineData(@"D:\Program Files (x86)\Steam\steam.exe", "Valve Corporation", "Steam", "steam.exe", "program:steam", 3000)]
    [InlineData(@"D:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe", "Valve Corporation", "Steam Client WebHelper", "steamwebhelper.exe", "program:steam", 3000)]
    public void Catalog_prices_match_real_file_data(string path, string company, string product, string? original, string key, long price)
    {
        var offer = _classifier.Classify(File(path, company, product, original)).Offer;

        Assert.NotNull(offer);
        Assert.Equal(key, offer.Key);
        Assert.Equal(price, offer.Price);
    }

    [Fact]
    public void Unknown_program_gets_the_default_price()
    {
        var offer = _classifier.Classify(File(@"D:\tools\thing.exe", "Acme", "Thing", "thing.exe")).Offer;

        Assert.NotNull(offer);
        Assert.Equal(300, offer.Price);
        Assert.Equal(LockKind.Program, offer.Kind);
    }

    [Fact]
    public void Renaming_a_free_looking_file_does_not_help()
    {
        foreach (var name in new[] { "claude.exe", "explorer.exe", "cmd.exe", "setup.exe", "chrome.exe" })
        {
            var offer = _classifier.Classify(File($@"D:\tools\{name}", "Acme", "Thing", "thing.exe")).Offer;

            Assert.NotNull(offer);
            Assert.Equal("file:acme|thing|thing.exe", offer.Key);
        }
    }

    [Fact]
    public void Copy_of_a_paid_system_program_outside_windows_keeps_its_price()
    {
        var offer = _classifier.Classify(File(@"D:\tools\cmd.exe", "Microsoft Corporation", "Windows", "notepad.exe")).Offer;

        Assert.NotNull(offer);
        Assert.Equal("program:notepad", offer.Key);
    }

    [Fact]
    public void Installer_is_recognized_by_file_data()
    {
        var file = File(@"D:\downloads\thing.exe", "Acme", "Thing", "thing.exe") with { Description = "Thing Setup" };

        Assert.Null(_classifier.Classify(file).Offer);
    }

    [Fact]
    public void File_without_version_data_is_identified_by_content()
    {
        var first = File(@"D:\a.exe", null, null, null);
        var renamed = File(@"E:\other\b.exe", null, null, null);

        Assert.Equal(first.IdentityKey, renamed.IdentityKey);
        Assert.StartsWith("hash:", first.IdentityKey);
    }

    [Fact]
    public void Update_keeps_identity_because_version_is_not_part_of_it()
    {
        var old = File(@"C:\app\app-1.0\app.exe", "Acme", "App", "app.exe");
        var updated = File(@"C:\app\app-2.0\app.exe", "Acme", "App", "app.exe");

        Assert.Equal(old.IdentityKey, updated.IdentityKey);
    }

    [Fact]
    public void Scope_limits_locks_to_one_folder()
    {
        var scoped = new ProgramClassifier(_catalog, [], @"D:\sandbox");

        Assert.Null(scoped.Classify(File(@"D:\sandboxed\thing.exe", "Acme", "Thing", "thing.exe")).Offer);
        Assert.NotNull(scoped.Classify(File(@"D:\sandbox\thing.exe", "Acme", "Thing", "thing.exe")).Offer);
    }

    [Fact]
    public void Package_name_is_read_from_the_path()
    {
        Assert.Equal(
            "Microsoft.WindowsCalculator",
            ProgramFile.PackageOf(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2_x64__8wek\Calc.exe"));
        Assert.Null(ProgramFile.PackageOf(@"C:\Program Files\App\app.exe"));
    }

    [Fact]
    public void Missing_catalog_stops_the_module_instead_of_locking_blindly()
    {
        using var temp = new TempDirectory();
        var data = new GameData(temp.Path, _log);
        data.Load();

        Assert.Throws<InvalidDataException>(() => ProgramCatalog.Load(data));
    }

    [Fact]
    public void Developer_mode_without_scope_never_locks_anything()
    {
        // Каталога нет намеренно: без защиты модуль завершился бы ошибкой, а не заморозил программы.
        using var temp = new TempDirectory();
        var data = new GameData(temp.Path, _log);
        var store = new JsonFileStore(temp.Combine("store.json"), _log);
        var bus = new EventBus(_log);
        var host = new ModuleHost(
            bus,
            data,
            new TextCatalog(data, "ru", _log),
            new CoinEconomy(store, bus, TimeProvider.System),
            store,
            store,
            new RecoveryService(new ChangeJournal(temp.Combine("recovery.json"), _log), () => [], _log),
            _log,
            developerMode: true);

        host.Start([new LocksModule()]);
        host.StopAll();

        Assert.Empty(_log.Errors);
        Assert.Contains(_log.Warnings, warning => warning.Contains("блокировка выключена"));
    }

    [Fact]
    public void Empty_condition_is_rejected()
    {
        var catalog = new ProgramCatalog(300, [], [], [new FreeEntry("all", [new FileMatch()])]);

        Assert.Throws<InvalidDataException>(catalog.Validate);
    }

    [Fact]
    public void Duplicate_id_is_rejected()
    {
        var match = new FileMatch(Company: "Acme");
        var catalog = new ProgramCatalog(300, [], [new ProgramEntry("a", "A", 1, [match])], [new FreeEntry("a", [match])]);

        Assert.Throws<InvalidDataException>(catalog.Validate);
    }

    private static ProgramFile File(string path, string? company, string? product, string? original) =>
        new(path, company, product, original, null, ProgramFile.PackageOf(path), false, new Lazy<string>(() => "same-content"));
}
