using System.Globalization;
using System.Net;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>Работа с именами сайтов.</summary>
public static class Domain
{
    private static readonly IdnMapping Idn = new();

    /// <summary>Имя сайта из адреса страницы. Только http и https: остальное не блокируется.</summary>
    public static string? FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = Normalize(uri.IdnHost);
        return IsValid(host) ? host : null;
    }

    /// <summary>Нижний регистр, без точки в конце и без www. Русские домены в виде xn--.</summary>
    public static string Normalize(string host)
    {
        var value = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value;
        }

        if (value.Any(symbol => symbol > 127))
        {
            try
            {
                value = Idn.GetAscii(value);
            }
            catch (ArgumentException)
            {
                return value;
            }
        }

        return value.StartsWith("www.", StringComparison.Ordinal) && value.Count(symbol => symbol == '.') >= 2 ? value[4..] : value;
    }

    public static bool IsValid(string host) =>
        host.Length is > 0 and <= 253
        && (IsAddress(host) || host.Split('.').All(label => label.Length is > 0 and <= 63 && label.All(IsLabelSymbol)));

    /// <summary>Совпадает с доменом или является его поддоменом.</summary>
    public static bool IsUnder(string host, string domain) =>
        host == domain || (host.EndsWith(domain, StringComparison.Ordinal) && host[^(domain.Length + 1)] == '.');

    /// <summary>Сколько частей в имени: чем больше, тем точнее запись.</summary>
    public static int Depth(string host) => IsAddress(host) ? 1 : host.Count(symbol => symbol == '.') + 1;

    /// <summary>Сайт целиком: example.com для news.example.com, example.co.uk для a.example.co.uk.</summary>
    public static string Registrable(string host, IReadOnlyCollection<string> multiPartSuffixes)
    {
        if (IsAddress(host))
        {
            return host;
        }

        var labels = host.Split('.');
        if (labels.Length <= 2)
        {
            return host;
        }

        var lastTwo = string.Join('.', labels[^2..]);
        var take = multiPartSuffixes.Contains(lastTwo) ? 3 : 2;
        return string.Join('.', labels[^Math.Min(take, labels.Length)..]);
    }

    private static bool IsAddress(string host) =>
        host.StartsWith('[') || (host.All(symbol => char.IsAsciiDigit(symbol) || symbol == '.') && IPAddress.TryParse(host, out _));

    private static bool IsLabelSymbol(char symbol) => char.IsAsciiLetterOrDigit(symbol) || symbol is '-' or '_';
}
