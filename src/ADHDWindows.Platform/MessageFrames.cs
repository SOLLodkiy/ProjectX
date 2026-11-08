using System.Buffers.Binary;

namespace ADHDWindows.Platform;

/// <summary>
/// Сообщения в формате native messaging: длина в четырёх байтах, затем JSON в UTF-8.
/// Тот же формат идёт по каналу между мостом браузера и программой.
/// </summary>
public static class MessageFrames
{
    /// <summary>Браузер не принимает от моста сообщения больше 1 МБ.</summary>
    public const int MaxLength = 1024 * 1024;

    /// <summary>Следующее сообщение или null, если поток закрыт.</summary>
    /// <exception cref="InvalidDataException">Длина вне допустимых границ.</exception>
    public static byte[]? Read(Stream stream)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxLength)
        {
            throw new InvalidDataException($"Неверная длина сообщения: {length}");
        }

        var body = new byte[length];
        return ReadExactly(stream, body) ? body : null;
    }

    public static void Write(Stream stream, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxLength)
        {
            throw new InvalidDataException($"Сообщение слишком большое: {body.Length}");
        }

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Flush();
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
