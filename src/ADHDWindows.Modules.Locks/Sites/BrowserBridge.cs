using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using ADHDWindows.Core.Logging;
using ADHDWindows.Platform;

namespace ADHDWindows.Modules.Locks.Sites;

/// <summary>Один браузер на другом конце канала. Отправка безопасна из любого потока.</summary>
public interface IBrowserPeer
{
    int Id { get; }

    void Send(JsonObject message);
}

/// <summary>
/// Канал, к которому подключаются мосты браузеров. Доступен только текущему пользователю.
/// Сообщения приходят в <paramref name="onMessage"/>, а null вместо сообщения означает, что браузер отключился.
/// </summary>
public sealed class BrowserBridge(string pipeName, Action<IBrowserPeer, JsonObject?> onMessage, ILog log) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<Connection> _connections = [];
    private int _nextId;
    private Task? _accept;

    public void Start() => _accept = Task.Run(AcceptAsync);

    public void Dispose()
    {
        _stop.Cancel();
        lock (_lock)
        {
            _connections.ForEach(connection => connection.Close());
            _connections.Clear();
        }

        try
        {
            _accept?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Ожидание прервано остановкой.
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException exception)
            {
                log.Error("Не удалось открыть канал браузера", exception);
                await Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            var connection = new Connection(Interlocked.Increment(ref _nextId), pipe);
            lock (_lock)
            {
                if (_stop.IsCancellationRequested)
                {
                    connection.Close();
                    return;
                }

                _connections.Add(connection);
            }

            var thread = new Thread(() => Read(connection)) { IsBackground = true, Name = $"Browser {connection.Id}" };
            thread.Start();
        }
    }

    private void Read(Connection connection)
    {
        try
        {
            while (!_stop.IsCancellationRequested && MessageFrames.Read(connection.Pipe) is { } body)
            {
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(body) as JsonObject;
                }
                catch (JsonException)
                {
                    message = null;
                }

                if (message is not null)
                {
                    onMessage(connection, message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException)
        {
            // Браузер закрылся.
        }

        lock (_lock)
        {
            _connections.Remove(connection);
        }

        connection.Close();
        if (!_stop.IsCancellationRequested)
        {
            onMessage(connection, null);
        }
    }

    private async Task Delay(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Остановка.
        }
    }

    private sealed class Connection(int id, NamedPipeServerStream pipe) : IBrowserPeer
    {
        private readonly Lock _write = new();

        public int Id => id;

        public NamedPipeServerStream Pipe => pipe;

        public void Send(JsonObject message)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            lock (_write)
            {
                try
                {
                    MessageFrames.Write(pipe, body);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Отключение заметит поток чтения.
                }
            }
        }

        public void Close() => pipe.Dispose();
    }
}
