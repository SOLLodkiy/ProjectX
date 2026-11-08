using System.Text.Json;
using ADHDWindows.Core.Locks;
using Xunit;

namespace ADHDWindows.Tests;

/// <summary>Код страницы совпадает с тем, что считает расширение: оба читают общие примеры page-cases.json.</summary>
public sealed class PageAddressTests
{
    public static TheoryData<string, string?> Cases()
    {
        var path = Path.Combine(RepositoryRoot.Find(), "browser-extension", "test", "page-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var data = new TheoryData<string, string?>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var page = item.GetProperty("page");
            data.Add(item.GetProperty("url").GetString()!, page.ValueKind == JsonValueKind.Null ? null : page.GetString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Page_code_matches_the_shared_cases(string url, string? page) =>
        Assert.Equal(page, PageAddress.Normalize(url));

    [Fact]
    public void Same_page_ignores_query_and_case_of_the_site_name()
    {
        Assert.True(PageAddress.IsSamePage("https://SoundCloud.com/a/b?in=x", "http://soundcloud.com/a/b/"));
        Assert.False(PageAddress.IsSamePage("https://soundcloud.com/a/b", "https://soundcloud.com/a/c"));
        Assert.False(PageAddress.IsSamePage(null, null));
        Assert.Equal("ru.wikipedia.org", PageAddress.HostOf("ru.wikipedia.org/wiki/Огурец"));
        Assert.Equal("soundcloud.com", PageAddress.HostOf("soundcloud.com"));
    }
}
