using System.Runtime.InteropServices;

namespace ADHDWindows.Platform;

/// <summary>
/// Короткие звуки из файлов WAV. Звук идёт через системный микшер, поэтому громкость не выше системной,
/// а выключенный звук Windows выключает и его. Одновременно играет один звук: новый прерывает прежний.
/// </summary>
public static class Sounds
{
    private const uint Async = 0x0001;
    private const uint NoDefault = 0x0002;
    private const uint FileName = 0x00020000;

    /// <summary>Папка звуков Windows на этом компьютере.</summary>
    public static string SystemFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    /// <summary>Начинает играть файл и сразу возвращается. False — файла нет или система не смогла его открыть.</summary>
    public static bool Play(string path) => File.Exists(path) && PlaySoundW(path, IntPtr.Zero, Async | NoDefault | FileName);

    /// <summary>Останавливает звук этой программы, если он играет.</summary>
    public static void Stop() => PlaySoundW(null, IntPtr.Zero, 0);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string? sound, IntPtr module, uint flags);
}
