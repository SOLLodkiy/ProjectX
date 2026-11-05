using System.Diagnostics;
using System.Security.Cryptography;

namespace ADHDWindows.Modules.Locks;

/// <summary>Данные исполняемого файла. Сведения о версии находятся внутри файла и не меняются при переименовании.</summary>
public sealed record ProgramFile(
    string Path,
    string? Company,
    string? Product,
    string? Original,
    string? Description,
    string? Package,
    bool InWindowsFolder,
    Lazy<string> ContentHash)
{
    private const string LocalizedSuffix = ".mui";
    private const int HashedBytes = 16 * 1024 * 1024;
    private const string PackagesFolder = @"\WindowsApps\";

    private static readonly string WindowsFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + '\\';

    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Постоянный код файла: переживает переименование и обновление программы.</summary>
    public string IdentityKey
    {
        get
        {
            var parts = new[] { Company, Product, Original }.Select(part => part?.ToLowerInvariant() ?? string.Empty).ToArray();
            return parts.All(part => part.Length == 0) ? $"hash:{ContentHash.Value}" : $"file:{string.Join('|', parts)}";
        }
    }

    public string DisplayName => Description ?? Product ?? FileName;

    public static ProgramFile Read(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new ProgramFile(
            path,
            Clean(info.CompanyName),
            Clean(info.ProductName),
            StripLocalization(Clean(info.OriginalFilename)),
            Clean(info.FileDescription),
            PackageOf(path),
            path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase),
            new Lazy<string>(() => Hash(path)));
    }

    /// <summary>Имя пакета Магазина из пути вида ...\WindowsApps\Имя_версия_архитектура__издатель\файл.exe.</summary>
    public static string? PackageOf(string path)
    {
        var start = path.IndexOf(PackagesFolder, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var folder = path[(start + PackagesFolder.Length)..];
        var end = folder.IndexOf('_');
        return end > 0 ? folder[..end] : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Системные файлы Windows с языковым пакетом называют себя NOTEPAD.EXE.MUI.
    private static string? StripLocalization(string? name) =>
        name?.EndsWith(LocalizedSuffix, StringComparison.OrdinalIgnoreCase) == true ? name[..^LocalizedSuffix.Length] : name;

    // Берётся начало файла и размер: файл без сведений о версии может весить гигабайты.
    private static string Hash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(stream.Length, HashedBytes)];
            stream.ReadExactly(buffer);
            return $"{stream.Length}-{Convert.ToHexStringLower(SHA256.HashData(buffer))}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return path.ToLowerInvariant();
        }
    }
}
