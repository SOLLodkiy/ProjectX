using ADHDWindows.Core.Data;
using ADHDWindows.Core.Economy;
using ADHDWindows.Core.Events;
using ADHDWindows.Core.Media;
using ADHDWindows.Modules.Time;

namespace ADHDWindows.Tests;

public sealed class TimeRulesTests
{
    private static readonly TimeRules Rules = LoadRules();

    internal static TimeRules LoadRules()
    {
        var data = new GameData(Path.Combine(RepositoryRoot.Find(), "data"), new RecordingLog());
        data.Load();
        return TimeRules.Load(data);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", Activity.Video)]
    [InlineData("https://m.youtube.com/watch?v=abc", Activity.Video)]
    [InlineData("https://music.youtube.com/watch?v=abc", Activity.Music)]
    [InlineData("https://www.twitch.tv/somebody", Activity.Video)]
    [InlineData("https://hd.kinopoisk.ru/film/1", Activity.Video)]
    [InlineData("https://music.yandex.ru/album/1", Activity.Music)]
    [InlineData("https://soundcloud.com/artist/track", Activity.Music)]
    [InlineData("https://vk.com/video-1_2", Activity.Video)]
    [InlineData("https://vk.com/audios1", Activity.Music)]
    [InlineData("https://vk.com/music/playlist/1", Activity.Music)]
    public void Sites_are_classified_by_the_most_precise_entry(string url, Activity expected) =>
        Assert.Equal(expected, Rules.Classify(url));

    [Theory]
    [InlineData("https://vk.com/feed")]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("file:///C:/Movies/film.mkv")]
    [InlineData("http://localhost:8080/")]
    [InlineData("chrome-extension://abc/blocked.html")]
    [InlineData("not a url")]
    [InlineData("https://notyoutube.com/watch")]
    public void Other_pages_and_local_files_bring_nothing(string url) => Assert.Null(Rules.Classify(url));

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    public void Desktop_music_clients_are_known(string appId) => Assert.NotNull(Rules.FindApp(appId));

    [Theory]
    [InlineData("VideoLAN.VLC")]
    [InlineData("vlc.exe")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    [InlineData("Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo")]
    [InlineData("chrome")]
    [InlineData("MSEdge")]
    [InlineData("firefox.exe")]
    [InlineData("PotPlayerMini64.exe")]
    public void Local_players_and_browsers_are_not_desktop_clients(string appId) => Assert.Null(Rules.FindApp(appId));

    [Fact]
    public void Repeats_cost_more_after_the_third_in_a_row()
    {
        Assert.Equal(1m, Rules.RepeatFactor(1));
        Assert.Equal(1m, Rules.RepeatFactor(3));
        Assert.True(Rules.RepeatFactor(4) < 1m);
        Assert.True(Rules.RepeatFactor(5) < Rules.RepeatFactor(4));
        Assert.Equal(Rules.RepeatFactor(100), Rules.RepeatFactors[^1]);
    }

    [Fact]
    public void Rates_follow_the_idea() =>
        Assert.Equal((6m, 2m, 5m), (Rules.RateOf(Activity.Video), Rules.RateOf(Activity.Music), Rules.RateOf(Activity.Game)));

    [Fact]
    public void Bad_rules_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => Rules.WithSites([new SiteRule("Example.com", "video")]));
        Assert.Throws<InvalidDataException>(() => Rules.WithSites([new SiteRule("example.com", "game")]));
        Assert.Throws<InvalidDataException>(() => Rules.WithSites([new SiteRule("youtube.com", "video")]));
        Assert.Throws<InvalidDataException>(() => (Rules with { Apps = [new AppRule("vlc", "VLC", "video")] }).Validate());
    }

    [Fact]
    public void Test_sites_are_parsed()
    {
        var rules = Rules.WithSites(TimeRules.ParseSites("video.test=video; Music.test = music"));
        Assert.Equal(Activity.Video, rules.Classify("http://video.test:5000/"));
        Assert.Equal(Activity.Music, rules.Classify("http://music.test/"));
    }
}

public sealed class DesktopMediaTests
{
    [Fact]
    public void System_media_sessions_are_read_without_errors()
    {
        // Что именно играет на компьютере, неизвестно: проверяется, что чтение работает и ничего не ломает.
        var log = new RecordingLog();
        var reader = new DesktopMedia(_ => true, log);
        var sessions = reader.Read();
        Assert.Empty(log.Warnings);
        Assert.All(sessions, session => Assert.False(string.IsNullOrEmpty(session.AppId)));
    }

    [Fact]
    public void Unwanted_sessions_are_skipped() => Assert.Empty(new DesktopMedia(_ => false, new RecordingLog()).Read());
}

public sealed class PlaybackMeterTests
{
    private readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlaybackSample At(double? position, bool playing = true, double rate = 1, double? duration = 600, string identity = "a") =>
        new(position, duration, playing, rate, identity);

    private (double Credit, bool Completed) Play(PlaybackMeter meter, IEnumerable<(double Time, PlaybackSample Sample)> samples)
    {
        var credit = 0d;
        var completed = false;
        foreach (var (time, sample) in samples)
        {
            var step = meter.Feed(sample, _start.AddSeconds(time));
            credit += step.Credit;
            completed |= step.Completed;
        }

        return (credit, completed);
    }

    private static PlaybackMeter Meter() => new(0.9, 20);

    [Fact]
    public void Normal_playback_counts_real_time()
    {
        var (credit, _) = Play(Meter(), Enumerable.Range(0, 31).Select(i => (i * 2d, At(i * 2d))));
        Assert.Equal(60, credit, 3);
    }

    [Fact]
    public void Pause_stops_counting()
    {
        var samples = Enumerable.Range(0, 6).Select(i => (i * 2d, At(i * 2d)))
            .Concat(Enumerable.Range(6, 10).Select(i => (i * 2d, At(10, playing: false))));
        var (credit, _) = Play(Meter(), samples);
        Assert.Equal(10, credit, 3);
    }

    [Fact]
    public void Double_speed_gives_no_extra_time()
    {
        var (credit, _) = Play(Meter(), Enumerable.Range(0, 31).Select(i => (i * 2d, At(i * 4d, rate: 2))));
        Assert.Equal(60, credit, 3);
    }

    [Fact]
    public void Slow_speed_still_counts_real_time()
    {
        var (credit, _) = Play(Meter(), Enumerable.Range(0, 31).Select(i => (i * 2d, At(i * 1d, rate: 0.5))));
        Assert.Equal(60, credit, 3);
    }

    [Fact]
    public void Reported_speed_cannot_be_faked()
    {
        // Плеер говорит «скорость 1», а позиция идёт вчетверо быстрее: это перемотка.
        var (credit, _) = Play(Meter(), Enumerable.Range(0, 31).Select(i => (i * 2d, At(i * 8d))));
        Assert.Equal(0, credit, 3);
    }

    [Fact]
    public void Seeking_is_not_watched_time()
    {
        // Каждую секунду вперёд на 5 секунд.
        var (credit, _) = Play(Meter(), Enumerable.Range(0, 20).Select(i => (i * 2d, At(i * 12d))));
        Assert.Equal(0, credit, 3);

        // Назад: скачок и пауза 3 секунды после него не засчитываются.
        var back = Play(Meter(), [(0, At(300)), (2, At(302)), (4, At(250)), (6, At(252)), (8, At(254)), (10, At(256))]);
        Assert.Equal(2 + 1 + 2, back.Credit, 3);
    }

    [Fact]
    public void Frequent_seeking_earns_nothing_between_jumps()
    {
        // Отчёт сразу после каждой перемотки и обычный снимок через секунду, как шлёт расширение.
        var samples = new List<(double, PlaybackSample)>();
        var position = 0d;
        for (var second = 0; second < 30; second++)
        {
            position += 1;
            samples.Add((second + 0.1, At(position)));
            position += 5;
            samples.Add((second + 0.2, At(position)));
        }

        var (credit, _) = Play(Meter(), samples);
        Assert.Equal(0, credit, 3);
    }

    [Fact]
    public void Muted_playback_brings_nothing()
    {
        var (credit, _) = Play(Meter(), Enumerable.Range(0, 31).Select(i => (i * 2d, At(i * 2d, playing: false))));
        Assert.Equal(0, credit);
    }

    [Fact]
    public void Long_silence_between_samples_is_not_counted()
    {
        var (credit, _) = Play(Meter(), [(0, At(0)), (2, At(2)), (32, At(32)), (34, At(34))]);
        Assert.Equal(4, credit, 3);
    }

    [Fact]
    public void Player_without_position_counts_time_while_playing()
    {
        var samples = Enumerable.Range(0, 11).Select(i => (i * 2d, At(null, duration: null)))
            .Append((22d, At(null, playing: false, duration: null)))
            .Append((24d, At(null, playing: false, duration: null)));
        var (credit, _) = Play(Meter(), samples);
        Assert.Equal(20, credit, 3);
    }

    [Fact]
    public void Watching_almost_all_completes_once()
    {
        var meter = Meter();
        var steps = Enumerable.Range(0, 31).Select(i => (i * 2d, At(i * 2d, duration: 60))).ToList();
        var completions = steps.Count(item => meter.Feed(item.Item2, _start.AddSeconds(item.Item1)).Completed);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void Seeking_to_the_end_does_not_complete()
    {
        var (_, completed) = Play(Meter(), [(0, At(0, duration: 60)), (2, At(2, duration: 60)), (4, At(58, duration: 60)), (6, At(60, playing: false, duration: 60))]);
        Assert.False(completed);
    }

    [Fact]
    public void Short_clips_do_not_complete()
    {
        var (_, completed) = Play(Meter(), Enumerable.Range(0, 8).Select(i => (i * 2d, At(Math.Min(i * 2d, 10), duration: 10))));
        Assert.False(completed);
    }

    [Fact]
    public void Restart_and_other_track_start_a_new_play()
    {
        var meter = Meter();
        Assert.True(meter.Feed(At(0), _start).NewPlay);
        Assert.False(meter.Feed(At(50), _start.AddSeconds(50)).NewPlay);
        Assert.True(meter.Feed(At(1), _start.AddSeconds(52)).NewPlay);
        Assert.False(meter.Feed(At(3), _start.AddSeconds(54)).NewPlay);
        Assert.True(meter.Feed(At(5, identity: "b"), _start.AddSeconds(56)).NewPlay);
        Assert.True(meter.Feed(At(7, identity: "b", duration: 200), _start.AddSeconds(58)).NewPlay);
    }
}

public sealed class TimeRewardsTests
{
    private const int BrowserId = 4242;
    private const string YouTube = "https://www.youtube.com/watch?v=1";
    private const string Music = "https://music.yandex.ru/album/1";

    private static readonly ForegroundState BrowserInFront = new(BrowserId, @"C:\Program Files\Google\Chrome\Application\chrome.exe");
    private static readonly ForegroundState OtherInFront = new(77, @"C:\Windows\explorer.exe");

    private readonly ManualClock _clock = new();
    private readonly RecordingEconomy _economy = new();
    private readonly EventBus _bus = new(new RecordingLog());
    private readonly List<PresenceCheckChanged> _presence = [];
    private readonly TimeRewards _rewards;

    public TimeRewardsTests()
    {
        _rewards = new TimeRewards(TimeRulesTests.LoadRules(), _economy, _bus, new RecordingLog(), _clock);
        _bus.Subscribe<PresenceCheckChanged>(_presence.Add);
    }

    private static BrowserMediaElement Element(string id, double? position, bool playing = true, double? duration = 3600, bool audible = true, string title = "t", double rate = 1) =>
        new(id, true, position, duration, playing, rate, audible, title);

    private static BrowserTabMedia Tab(int id, string url, bool focused, params BrowserMediaElement[] media) => new(id, url, focused, media);

    /// <summary>Секунда за секундой: осмотр окна каждую секунду, отчёт браузера каждые 2 секунды.</summary>
    private void Run(int seconds, ForegroundState foreground, Func<int, BrowserTabMedia[]>? tabs = null, TimeSpan? idle = null, Func<int, DesktopMediaSession[]>? desktop = null, int from = 0)
    {
        for (var second = from + 1; second <= from + seconds; second++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _rewards.Tick(foreground, idle ?? TimeSpan.Zero);
            if (second % 2 == 0)
            {
                if (tabs is not null)
                {
                    _rewards.OnBrowserMedia(new BrowserMediaReported(1, BrowserId, tabs(second)));
                }

                if (desktop is not null)
                {
                    _rewards.OnDesktopMedia(desktop(second));
                }
            }
        }

        _rewards.Flush();
    }

    [Fact]
    public void Video_in_the_active_window_earns_six_a_minute()
    {
        Run(62, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second))]);
        Assert.Equal(6m, _economy.Total("video"), 1);
        Assert.Equal(0m, _economy.Total("music"));
    }

    [Fact]
    public void Video_in_a_background_tab_or_behind_another_program_earns_nothing()
    {
        Run(62, BrowserInFront, second => [Tab(1, YouTube, focused: false, Element("v", second))]);
        Run(62, OtherInFront, second => [Tab(1, YouTube, true, Element("v", second))], from: 62);
        Assert.Equal(0m, _economy.Total("video"));
    }

    [Fact]
    public void Paused_muted_or_seeking_video_earns_nothing()
    {
        Run(60, BrowserInFront, _ => [Tab(1, YouTube, true, Element("p", 10, playing: false))]);
        Run(60, BrowserInFront, second => [Tab(2, YouTube, true, Element("m", second, audible: false))]);
        Run(60, BrowserInFront, second => [Tab(3, YouTube, true, Element("s", second * 10))]);
        Assert.Equal(0m, _economy.Total("video"));
    }

    [Fact]
    public void Faster_playback_earns_the_same()
    {
        Run(62, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second * 2, rate: 2))]);
        Assert.Equal(6m, _economy.Total("video"), 1);
    }

    [Fact]
    public void Music_plays_in_the_background()
    {
        Run(62, OtherInFront, second => [Tab(1, Music, focused: false, Element("a", second))]);
        Assert.Equal(2m, _economy.Total("music"), 1);
    }

    [Fact]
    public void YouTube_Music_counts_only_as_music()
    {
        Run(62, BrowserInFront, second => [Tab(1, "https://music.youtube.com/watch?v=1", true, Element("a", second))]);
        Assert.Equal(2m, _economy.Total("music"), 1);
        Assert.Equal(0m, _economy.Total("video"));
    }

    [Fact]
    public void Two_music_players_earn_like_one()
    {
        Run(62, OtherInFront, second =>
        [
            Tab(1, Music, false, Element("a", second)),
            Tab(2, "https://soundcloud.com/x/y", false, Element("b", second)),
        ]);
        Assert.Equal(2m, _economy.Total("music"), 1);
    }

    [Fact]
    public void Two_videos_at_once_earn_like_one()
    {
        Run(62, BrowserInFront, second =>
        [
            Tab(1, YouTube, true, Element("a", second), Element("b", second)),
        ]);
        Assert.Equal(6m, _economy.Total("video"), 1);
    }

    [Fact]
    public void Music_and_video_add_up()
    {
        Run(62, BrowserInFront, second =>
        [
            Tab(1, YouTube, true, Element("v", second)),
            Tab(2, Music, false, Element("a", second)),
        ]);
        Assert.Equal(6m, _economy.Total("video"), 1);
        Assert.Equal(2m, _economy.Total("music"), 1);
    }

    [Fact]
    public void Unknown_sites_earn_nothing()
    {
        Run(62, BrowserInFront, second => [Tab(1, "https://example.com/", true, Element("v", second))]);
        Assert.Equal(0m, _economy.Total());
    }

    [Fact]
    public void Desktop_clients_count_and_local_players_do_not()
    {
        Run(62, OtherInFront, desktop: second =>
        [
            new DesktopMediaSession("Spotify.exe", "Song", "Band", true, second, 240, 1),
            new DesktopMediaSession("VideoLAN.VLC", "Film", null, true, second, 7200, 1),
            new DesktopMediaSession("chrome", "Video", null, true, second, 600, 1),
        ]);
        Assert.Equal(2m, _economy.Total("music"), 1);
        Assert.Equal(0m, _economy.Total("video"));
    }

    [Fact]
    public void Paused_desktop_client_earns_nothing()
    {
        Run(62, OtherInFront, desktop: _ => [new DesktopMediaSession("Spotify.exe", "Song", "Band", false, 30, 240, 1)]);
        Assert.Equal(0m, _economy.Total());
    }

    [Fact]
    public void Watching_to_the_end_adds_ten_percent()
    {
        // Ролик на 60 секунд: 6 монеток за минуту и 0,6 за досмотр.
        Run(66, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", Math.Min(second - 2, 60), playing: second - 2 < 60, duration: 60))]);
        Assert.Equal(6.6m, _economy.Total("video"), 1);
    }

    [Fact]
    public void Completion_upgrade_raises_the_bonus()
    {
        // Улучшение на +10%: бонус за досмотр 20%, то есть 1,2 монетки за минутный ролик.
        _rewards.ExtraCompletionBonus = 0.1m;
        Run(66, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", Math.Min(second - 2, 60), playing: second - 2 < 60, duration: 60))]);
        Assert.Equal(7.2m, _economy.Total("video"), 1);
    }

    [Fact]
    public void Fourth_repeat_in_a_row_earns_less()
    {
        // Трек на 30 секунд четыре раза подряд: каждый раз с начала.
        var total = new List<decimal>();
        for (var play = 0; play < 4; play++)
        {
            var before = _economy.Total("music");
            var start = play * 30;
            Run(30, OtherInFront, second => [Tab(1, Music, false, Element("a", Math.Max(0, second - start - 2), duration: 3600, title: "Song"))], from: start);
            total.Add(_economy.Total("music") - before);
        }

        Assert.True(total[3] < total[2] * 0.75m, string.Join(", ", total));
        Assert.True(total[2] > total[0] * 0.9m, string.Join(", ", total));
    }

    [Fact]
    public void Different_tracks_are_not_repeats()
    {
        var total = new List<decimal>();
        for (var play = 0; play < 5; play++)
        {
            var before = _economy.Total("music");
            var start = play * 30;
            var title = $"Song {play}";
            Run(30, OtherInFront, second => [Tab(1, Music, false, Element("a", Math.Max(0, second - start - 2), title: title))], from: start);
            total.Add(_economy.Total("music") - before);
        }

        Assert.True(total[4] > total[0] * 0.9m, string.Join(", ", total));
    }

    [Fact]
    public void Idle_video_asks_are_you_there_and_pauses_until_answered()
    {
        Run(20, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second))], idle: TimeSpan.FromMinutes(31));
        Assert.True(_rewards.WaitingForPresence);
        Assert.Equal([true], _presence.Select(change => change.Waiting));
        Assert.Equal("video", _presence[0].Activity);

        var before = _economy.Total("video");
        Run(60, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second))], idle: TimeSpan.FromMinutes(32), from: 20);
        Assert.Equal(before, _economy.Total("video"));

        _rewards.ConfirmPresence();
        Assert.False(_rewards.WaitingForPresence);
        Run(60, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second))], idle: TimeSpan.FromMinutes(33), from: 80);
        Assert.Equal(6m, _economy.Total("video") - before, 1);
    }

    [Fact]
    public void Music_does_not_ask_and_keeps_playing()
    {
        Run(62, OtherInFront, second => [Tab(1, Music, false, Element("a", second))], idle: TimeSpan.FromHours(2));
        Assert.False(_rewards.WaitingForPresence);
        Assert.Equal(2m, _economy.Total("music"), 1);
    }

    [Fact]
    public void Video_with_recent_input_does_not_ask()
    {
        Run(62, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second))], idle: TimeSpan.FromMinutes(29));
        Assert.False(_rewards.WaitingForPresence);
    }

    [Fact]
    public void Game_in_the_active_window_earns_five_a_minute()
    {
        _rewards.OnGames([new GameInstall("game:steam:1", "Game", @"C:\Games\steamapps\common\Game")]);
        Run(61, new ForegroundState(9, @"C:\Games\steamapps\common\Game\bin\game.exe"));
        Assert.Equal(5m, _economy.Total("game"), 1);
    }

    [Fact]
    public void Game_in_the_background_or_a_similar_folder_earns_nothing()
    {
        _rewards.OnGames([new GameInstall("game:steam:1", "Game", @"C:\Games\steamapps\common\Game")]);
        Run(60, OtherInFront);
        Run(60, new ForegroundState(9, @"C:\Games\steamapps\common\Game2\game.exe"), from: 60);
        Assert.Equal(0m, _economy.Total());
    }

    [Fact]
    public void Idle_game_asks_after_three_minutes()
    {
        _rewards.OnGames([new GameInstall("game:steam:1", "Game", @"C:\Games\Game")]);
        var game = new ForegroundState(9, @"C:\Games\Game\game.exe");
        Run(60, game, idle: TimeSpan.FromMinutes(2));
        Assert.False(_rewards.WaitingForPresence);

        Run(10, game, idle: TimeSpan.FromMinutes(3), from: 60);
        Assert.True(_rewards.WaitingForPresence);
        Assert.Equal("game", _presence.Single().Activity);
        var before = _economy.Total("game");
        Run(60, game, idle: TimeSpan.FromMinutes(4), from: 70);
        Assert.Equal(before, _economy.Total("game"));
    }

    [Fact]
    public void Answer_resets_the_idle_timer()
    {
        _rewards.OnGames([new GameInstall("game:steam:1", "Game", @"C:\Games\Game")]);
        var game = new ForegroundState(9, @"C:\Games\Game\game.exe");
        Run(5, game, idle: TimeSpan.FromMinutes(5));
        Assert.True(_rewards.WaitingForPresence);

        // Ответ засчитывается как действие: следующий вопрос — снова через 3 минуты, даже если мышь не двигалась.
        _rewards.ConfirmPresence();
        Run(170, game, idle: TimeSpan.FromMinutes(10), from: 5);
        Assert.False(_rewards.WaitingForPresence);
        Run(15, game, idle: TimeSpan.FromMinutes(10), from: 175);
        Assert.True(_rewards.WaitingForPresence);
    }

    [Fact]
    public void Disconnected_browser_stops_counting()
    {
        Run(20, BrowserInFront, second => [Tab(1, YouTube, true, Element("v", second))]);
        var before = _economy.Total("video");
        _rewards.OnBrowserMedia(new BrowserMediaReported(1, BrowserId, [], Disconnected: true));
        Run(60, BrowserInFront, from: 20);
        Assert.Equal(before, _economy.Total("video"));
    }

    [Fact]
    public void Coins_are_credited_in_portions()
    {
        for (var second = 1; second <= 30; second++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            _rewards.Tick(BrowserInFront, TimeSpan.Zero);
            if (second % 2 == 0)
            {
                _rewards.OnBrowserMedia(new BrowserMediaReported(1, BrowserId, [Tab(1, YouTube, true, Element("v", second))]));
            }
        }

        // Не чаще раза в 10 секунд: иначе HUD сыпал бы монетками.
        Assert.InRange(_economy.Calls.Count, 2, 3);
    }

    internal sealed class RecordingEconomy : IEconomy
    {
        public List<(string Source, decimal Amount)> Calls { get; } = [];

        public long Balance => 0;

        public long IncomePerMinute => 0;

        public decimal Total(string? source = null) => Calls.Where(call => source is null || call.Source == source).Sum(call => call.Amount);

        public decimal Preview(string source, decimal baseAmount) => baseAmount;

        public long Earn(string source, decimal baseAmount)
        {
            Calls.Add((source, baseAmount));
            return (long)baseAmount;
        }

        public bool TrySpend(long amount, string reason) => false;

        public IDisposable AddModifier(IncomeModifier modifier) => throw new NotSupportedException();
    }
}
