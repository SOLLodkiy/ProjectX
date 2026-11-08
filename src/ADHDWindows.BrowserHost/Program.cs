using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using ADHDWindows.Core.Safety;
using ADHDWindows.Platform;

namespace ADHDWindows.BrowserHost;

/// <summary>
/// Мост native messaging. Браузер запускает его для расширения, мост пересылает сообщения программе по каналу.
/// Пока программа не запущена, мост ждёт её и сообщает расширению, что блокировка выключена.
/// Первое сообщение программе — номер процесса браузера: по нему программа связывает расширение с окнами.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);
    private static readonly Lock OutputLock = new();
    private static readonly Lock PipeLock = new();
    private static readonly HashSet<string> Launchers = ["cmd.exe", "conhost.exe"];

    private static Stream _output = Stream.Null;
    private static NamedPipeClientStream? _pipe;

    private static int Main()
    {
        _output = Console.OpenStandardOutput();
        var input = Console.OpenStandardInput();
        var browser = FindBrowser();
        new Thread(() => PumpInput(input)) { IsBackground = true, Name = "Input" }.Start();

        var name = ScopedName.BrowserBridge();
        var offlineSent = false;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                pipe.Connect(0);
                MessageFrames.Write(pipe, Json(new JsonObject
                {
                    ["type"] = "host",
                    ["browserPid"] = browser?.Id,
                    ["browserStart"] = browser?.StartTime,
                }));
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
            {
                pipe.Dispose();
                if (!offlineSent)
                {
                    Send(new JsonObject { ["type"] = "offline" });
                    offlineSent = true;
                }

                Thread.Sleep(Retry);
                continue;
            }

            lock (PipeLock)
            {
                _pipe = pipe;
            }

            Send(new JsonObject { ["type"] = "online" });
            offlineSent = false;
            try
            {
                while (MessageFrames.Read(pipe) is { } body)
                {
                    SendRaw(body);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
            {
                // Программа закрылась или сбой канала: ждём её снова.
            }

            lock (PipeLock)
            {
                _pipe = null;
            }

            pipe.Dispose();
            Send(new JsonObject { ["type"] = "offline" });
            offlineSent = true;
            Thread.Sleep(Retry);
        }
    }

    /// <summary>Браузер закрыл поток — значит, расширение отключилось, и мост больше не нужен.</summary>
    private static void PumpInput(Stream input)
    {
        try
        {
            while (MessageFrames.Read(input) is { } body)
            {
                lock (PipeLock)
                {
                    try
                    {
                        if (_pipe is not null)
                        {
                            MessageFrames.Write(_pipe, body);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                    {
                        // Сообщение без программы теряется: расширение узнает об этом из «offline».
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            // Поток браузера сломан, работать дальше незачем.
        }

        Environment.Exit(0);
    }

    /// <summary>Chromium на Windows запускает мост через cmd.exe, поэтому промежуточные процессы пропускаются.</summary>
    private static ProcessStamp? FindBrowser()
    {
        var current = ProcessControl.Stamp(Environment.ProcessId);
        for (var depth = 0; current is { } stamp && depth < 4; depth++)
        {
            if (ProcessControl.Parent(stamp) is not { } parent)
            {
                return null;
            }

            var path = ProcessControl.ImagePath(parent);
            if (path is null || !Launchers.Contains(Path.GetFileName(path).ToLowerInvariant()))
            {
                return parent;
            }

            current = parent;
        }

        return null;
    }

    private static byte[] Json(JsonObject message) => JsonSerializer.SerializeToUtf8Bytes(message);

    private static void Send(JsonObject message) => SendRaw(Json(message));

    private static void SendRaw(byte[] body)
    {
        lock (OutputLock)
        {
            try
            {
                MessageFrames.Write(_output, body);
            }
            catch (IOException)
            {
                Environment.Exit(0);
            }
        }
    }
}
