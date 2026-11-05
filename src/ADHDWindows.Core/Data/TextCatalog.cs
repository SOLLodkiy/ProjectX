using System.Globalization;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Core.Data;

/// <summary>Тексты интерфейса из data/texts/{язык}.json. Вместо пропущенного текста показывается его ключ.</summary>
public sealed class TextCatalog(GameData data, string fallbackLanguage, ILog log)
{
    private Dictionary<string, string> _texts = [];

    public void Use(string language)
    {
        if (!data.TryGet<Dictionary<string, string>>($"texts/{language}", out var texts) || texts is null)
        {
            log.Warn($"Нет текстов для языка {language}, используется {fallbackLanguage}");
            data.TryGet($"texts/{fallbackLanguage}", out texts);
        }

        _texts = texts ?? [];
    }

    public string Get(string key) => _texts.GetValueOrDefault(key, key);

    public string Get(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
