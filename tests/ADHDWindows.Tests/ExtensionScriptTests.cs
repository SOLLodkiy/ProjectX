using System.Diagnostics;
using Xunit;

namespace ADHDWindows.Tests;

/// <summary>Тесты правил расширения написаны на JavaScript и запускаются через Node, если он установлен.</summary>
public sealed class ExtensionScriptTests
{
    [Fact]
    public async Task Extension_rule_tests_pass()
    {
        var script = Path.Combine(RepositoryRoot.Find(), "browser-extension", "test", "rules.test.mjs");
        var info = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--test");
        info.ArgumentList.Add(script);

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Без Node проверить нечего: расширение от него не зависит.
            return;
        }

        using (process)
        {
            var reading = process.StandardOutput.ReadToEndAsync();
            var errors = await process.StandardError.ReadToEndAsync();
            var output = await reading;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
            Assert.True(process.ExitCode == 0, output + errors);
            Assert.Contains("# fail 0", output);
        }
    }

    [Fact]
    public void Both_manifests_use_the_same_files_and_version()
    {
        var root = Path.Combine(RepositoryRoot.Find(), "browser-extension");
        var chromium = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest.chromium.json")))!;
        var firefox = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest.firefox.json")))!;

        Assert.Equal((string?)chromium["version"], (string?)firefox["version"]);
        Assert.Equal("adhdwindows@adhdwindows.app", (string?)firefox["browser_specific_settings"]!["gecko"]!["id"]);
        Assert.Equal(ADHDWindows.Modules.Locks.Sites.NativeHost.FirefoxId, (string?)firefox["browser_specific_settings"]!["gecko"]!["id"]);
        // Код расширения Chromium — первые 16 байт SHA-256 открытого ключа, записанные буквами a–p.
        var hash = System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String((string)chromium["key"]!));
        var id = string.Concat(hash.Take(16).SelectMany(value => new[] { (char)('a' + (value >> 4)), (char)('a' + (value & 15)) }));
        Assert.Equal(ADHDWindows.Modules.Locks.Sites.NativeHost.ChromiumId, id);

        foreach (var icon in chromium["icons"]!.AsObject())
        {
            Assert.True(File.Exists(Path.Combine(root, "src", (string)icon.Value!)), (string?)icon.Value);
        }
    }
}
