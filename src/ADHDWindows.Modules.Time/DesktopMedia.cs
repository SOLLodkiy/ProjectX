using ADHDWindows.Core.Logging;
using Windows.Media.Control;

namespace ADHDWindows.Modules.Time;

/// <summary>
/// Системные сессии мультимедиа Windows: так видно настольные клиенты вроде Spotify.
/// Читаются только сессии из списка клиентов, остальные, в том числе браузеры и локальные плееры, пропускаются.
/// </summary>
internal sealed class DesktopMedia(Func<string, bool> wanted, ILog log)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryPause = TimeSpan.FromMinutes(1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _reportedFailure;

    public IReadOnlyList<DesktopMediaSession> Read()
    {
        if (Manager() is not { } manager)
        {
            return [];
        }

        var sessions = new List<DesktopMediaSession>();
        try
        {
            foreach (var session in manager.GetSessions())
            {
                var appId = session.SourceAppUserModelId ?? string.Empty;
                if (wanted(appId))
                {
                    sessions.Add(Describe(appId, session));
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Сессия могла закрыться посреди чтения. Менеджер получается заново.
            log.Warn($"Не удалось прочитать сессии мультимедиа: {exception.Message}");
            _manager = null;
        }

        return sessions;
    }

    private static DesktopMediaSession Describe(string appId, GlobalSystemMediaTransportControlsSession session)
    {
        var playback = session.GetPlaybackInfo();
        var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var rate = playback.PlaybackRate is { } value && double.IsFinite(value) && value > 0 ? value : 1;

        double? position = null;
        double? duration = null;
        var timeline = session.GetTimelineProperties();
        var length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        var age = DateTimeOffset.Now - timeline.LastUpdatedTime;
        if (length > 0 && timeline.LastUpdatedTime.Year >= 2000 && age >= TimeSpan.Zero && age < TimeSpan.FromDays(1))
        {
            // Приложение сообщает позицию на момент последнего обновления, дальше она идёт сама.
            duration = length;
            position = (timeline.Position - timeline.StartTime).TotalSeconds + (playing ? age.TotalSeconds * rate : 0);
        }

        string? title = null;
        string? artist = null;
        try
        {
            var properties = session.TryGetMediaPropertiesAsync().AsTask();
            if (properties.Wait(CallTimeout))
            {
                title = properties.Result.Title;
                artist = properties.Result.Artist;
            }
        }
        catch (AggregateException)
        {
            // Без названия повторы не опознаются, но время считается.
        }

        return new DesktopMediaSession(appId, title, artist, playing, position, duration, rate);
    }

    private GlobalSystemMediaTransportControlsSessionManager? Manager()
    {
        if (_manager is not null || DateTimeOffset.UtcNow < _nextAttempt)
        {
            return _manager;
        }

        try
        {
            var request = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            if (request.Wait(CallTimeout))
            {
                _manager = request.Result;
                _reportedFailure = false;
                return _manager;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_reportedFailure)
            {
                log.Warn($"Системные сессии мультимедиа недоступны: {exception.Message}");
                _reportedFailure = true;
            }
        }

        _nextAttempt = DateTimeOffset.UtcNow + RetryPause;
        return null;
    }
}
