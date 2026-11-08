using System.Globalization;

namespace ADHDWindows.Core.Locks;

/// <summary>
/// Код страницы для заданий и пропусков: имя сайта и путь без схемы, запроса и якоря.
/// <c>https://www.Ru.Wikipedia.org/wiki/%D0%9E%D0%B3%D1%83%D1%80%D0%B5%D1%86/?x=1#a</c> → <c>ru.wikipedia.org/wiki/Огурец</c>.
/// Имя сайта — в нижнем регистре, русские домены в виде xn--, без точки в конце и без www (как у блокировки сайтов);
/// путь раскодирован, без завершающей косой черты. Расширение повторяет это правило в rules.js,
/// общие примеры лежат в browser-extension/test/page-cases.json.
/// </summary>
public static class PageAddress
{
    private static readonly IdnMapping Idn = new();

    /// <summary>Код страницы или null для адресов, которые не http и не https.</summary>
    public static string? Normalize(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = Host(uri.IdnHost);
        if (host.Length == 0)
        {
            return null;
        }

        string path;
        try
        {
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        catch (UriFormatException)
        {
            path = uri.AbsolutePath;
        }

        path = path.TrimEnd('/');
        return host + path;
    }

    /// <summary>Один и тот же код страницы у двух адресов.</summary>
    public static bool IsSamePage(string? first, string? second) =>
        Normalize(first) is { } left && left == Normalize(second);

    /// <summary>Имя сайта из кода страницы.</summary>
    public static string HostOf(string page)
    {
        var slash = page.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? page : page[..slash];
    }

    private static string Host(string host)
    {
        var value = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Any(symbol => symbol > 127))
        {
            try
            {
                value = Idn.GetAscii(value);
            }
            catch (ArgumentException)
            {
                // Неверное имя остаётся как есть: такой страницы всё равно нет.
            }
        }

        return value.StartsWith("www.", StringComparison.Ordinal) && value.Count(symbol => symbol == '.') >= 2 ? value[4..] : value;
    }
}
