using System.Runtime.InteropServices;
using System.Text;

namespace ADHDWindows.Platform;

/// <summary>
/// Смешивает короткие звуки и зацикленную музыку и проигрывает их через waveOut. Звук идёт через системный микшер,
/// поэтому не громче системного. В тишине устройство закрывается: открытый звуковой поток не даёт компьютеру уснуть.
/// Методы можно вызывать из любого потока.
/// </summary>
public sealed class SoundMixer : IDisposable
{
    public const int SampleRate = 22050;

    private const int BufferSamples = 1024;
    private const int BufferCount = 4;
    private const int MaxVoices = 8;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint HeaderDone = 0x00000001;
    private const int HeaderSize = 48;
    private static readonly TimeSpan CloseAfterSilence = TimeSpan.FromSeconds(1.5);

    private readonly Lock _lock = new();
    private readonly List<Voice> _voices = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private short[]? _music;
    private int _musicPosition;
    private float _musicVolume;

    public SoundMixer()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SoundMixer" };
        _thread.Start();
    }

    /// <summary>Устройство вывода открыто и звук сейчас идёт.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Устройство не открылось: звуковой карты нет или она занята. Звуки пропускаются.</summary>
    public bool Failed { get; private set; }

    /// <summary>Играет звук поверх остальных. <paramref name="volume"/> от 0 до 1.</summary>
    public void Play(short[] samples, float volume)
    {
        if (samples.Length == 0 || volume <= 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_voices.Count >= MaxVoices)
            {
                _voices.RemoveAt(0);
            }

            _voices.Add(new Voice(samples, volume));
        }

        _wake.Set();
    }

    /// <summary>Зацикленная музыка. Null или нулевая громкость выключает её.</summary>
    public void SetMusic(short[]? samples, float volume)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(samples, _music))
            {
                _musicPosition = 0;
            }

            _music = samples is { Length: > 0 } && volume > 0 ? samples : null;
            _musicVolume = volume;
        }

        _wake.Set();
    }

    /// <summary>Сразу замолкает: звуки сбрасываются, музыка выключается.</summary>
    public void Silence()
    {
        lock (_lock)
        {
            _voices.Clear();
            _music = null;
        }

        _wake.Set();
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _wake.Dispose();
    }

    private bool HasSound()
    {
        lock (_lock)
        {
            return _voices.Count > 0 || _music is not null;
        }
    }

    private void Run()
    {
        while (!_stopping)
        {
            if (!HasSound())
            {
                _wake.WaitOne();
                continue;
            }

            Failed = !Stream();
            if (Failed)
            {
                // Без устройства звуки не копятся: иначе они прозвучат разом, когда устройство появится.
                lock (_lock)
                {
                    _voices.Clear();
                }

                _wake.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>Открывает устройство и пишет буферы, пока есть звук и не прошла пауза тишины.</summary>
    private bool Stream()
    {
        using var done = new AutoResetEvent(false);
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * 2,
            BlockAlign = 2,
            BitsPerSample = 16,
            Size = 0,
        };
        if (waveOutOpen(out var device, WaveMapper, ref format, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
        {
            return false;
        }

        IsOpen = true;
        var headers = new IntPtr[BufferCount];
        var data = new IntPtr[BufferCount];
        var queued = new bool[BufferCount];
        var mix = new short[BufferSamples];
        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                data[i] = Marshal.AllocHGlobal(BufferSamples * 2);
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                ClearHeader(headers[i], data[i]);
                waveOutPrepareHeader(device, headers[i], HeaderSize);
            }

            var silentSince = DateTime.UtcNow;
            while (!_stopping)
            {
                var anyQueued = false;
                for (var i = 0; i < BufferCount; i++)
                {
                    if (queued[i] && (Flags(headers[i]) & HeaderDone) == 0)
                    {
                        anyQueued = true;
                        continue;
                    }

                    var audible = Mix(mix);
                    if (audible)
                    {
                        silentSince = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - silentSince > CloseAfterSilence)
                    {
                        queued[i] = false;
                        continue;
                    }

                    Marshal.Copy(mix, 0, data[i], BufferSamples);
                    SetFlags(headers[i], Flags(headers[i]) & ~HeaderDone);
                    queued[i] = waveOutWrite(device, headers[i], HeaderSize) == 0;
                    anyQueued |= queued[i];
                }

                if (!anyQueued)
                {
                    break;
                }

                done.WaitOne(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            waveOutReset(device);
            for (var i = 0; i < BufferCount; i++)
            {
                if (headers[i] != IntPtr.Zero)
                {
                    waveOutUnprepareHeader(device, headers[i], HeaderSize);
                    Marshal.FreeHGlobal(headers[i]);
                }

                if (data[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(data[i]);
                }
            }

            waveOutClose(device);
            IsOpen = false;
        }

        return true;
    }

    /// <summary>Заполняет буфер следующими точками. False — в буфере тишина.</summary>
    private bool Mix(short[] buffer)
    {
        var sum = new int[buffer.Length];
        var audible = false;
        lock (_lock)
        {
            for (var v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                var count = Math.Min(buffer.Length, voice.Samples.Length - voice.Position);
                for (var i = 0; i < count; i++)
                {
                    sum[i] += (int)(voice.Samples[voice.Position + i] * voice.Volume);
                }

                voice.Position += count;
                audible |= count > 0;
                if (voice.Position >= voice.Samples.Length)
                {
                    _voices.RemoveAt(v);
                }
            }

            if (_music is { } music)
            {
                for (var i = 0; i < buffer.Length; i++)
                {
                    sum[i] += (int)(music[_musicPosition] * _musicVolume);
                    _musicPosition = (_musicPosition + 1) % music.Length;
                }

                audible = true;
            }
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);
        }

        return audible;
    }

    private static void ClearHeader(IntPtr header, IntPtr data)
    {
        for (var offset = 0; offset < HeaderSize; offset += 4)
        {
            Marshal.WriteInt32(header, offset, 0);
        }

        Marshal.WriteIntPtr(header, 0, data);
        Marshal.WriteInt32(header, 8, BufferSamples * 2);
    }

    // Поле dwFlags структуры WAVEHDR: после lpData (8), dwBufferLength (4), dwBytesRecorded (4) и dwUser (8).
    private static uint Flags(IntPtr header) => (uint)Marshal.ReadInt32(header, 24);

    private static void SetFlags(IntPtr header, uint flags) => Marshal.WriteInt32(header, 24, (int)flags);

    private sealed class Voice(short[] samples, float volume)
    {
        public short[] Samples { get; } = samples;

        public float Volume { get; } = volume;

        public int Position { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}

/// <summary>Чтение WAV в 16-битные точки моно с частотой <see cref="SoundMixer.SampleRate"/>.</summary>
public static class WaveFile
{
    /// <summary>Точки звука. Поддерживается PCM 8 и 16 бит, моно и стерео, любая частота.</summary>
    public static short[] Read(string path) => Decode(File.ReadAllBytes(path));

    public static short[] Decode(byte[] file)
    {
        if (file.Length < 12 || Encoding.ASCII.GetString(file, 0, 4) != "RIFF" || Encoding.ASCII.GetString(file, 8, 4) != "WAVE")
        {
            throw new FormatException("Это не файл WAV");
        }

        int channels = 0, rate = 0, bits = 0;
        var offset = 12;
        while (offset + 8 <= file.Length)
        {
            var id = Encoding.ASCII.GetString(file, offset, 4);
            var size = BitConverter.ToInt32(file, offset + 4);
            var body = offset + 8;
            if (size < 0 || body + size > file.Length)
            {
                size = file.Length - body;
            }

            if (id == "fmt ")
            {
                if (BitConverter.ToUInt16(file, body) != 1)
                {
                    throw new FormatException("Поддерживается только PCM");
                }

                channels = BitConverter.ToUInt16(file, body + 2);
                rate = BitConverter.ToInt32(file, body + 4);
                bits = BitConverter.ToUInt16(file, body + 14);
            }
            else if (id == "data")
            {
                if (channels is < 1 or > 2 || rate <= 0 || bits is not (8 or 16))
                {
                    throw new FormatException($"Неподдерживаемый формат: {channels} кан., {rate} Гц, {bits} бит");
                }

                return Convert(file, body, size, channels, rate, bits);
            }

            offset = body + size + (size % 2);
        }

        throw new FormatException("В файле WAV нет данных");
    }

    private static short[] Convert(byte[] file, int start, int size, int channels, int rate, int bits)
    {
        var bytesPerSample = bits / 8;
        var frames = size / (bytesPerSample * channels);
        var source = new short[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var total = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var at = start + (((frame * channels) + channel) * bytesPerSample);
                total += bits == 8 ? (file[at] - 128) << 8 : BitConverter.ToInt16(file, at);
            }

            source[frame] = (short)(total / channels);
        }

        if (rate == SoundMixer.SampleRate)
        {
            return source;
        }

        // Ближайшая точка: звуки 8-битные, лишняя точность не нужна.
        var length = (int)((long)frames * SoundMixer.SampleRate / rate);
        var result = new short[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = source[Math.Min(frames - 1, (int)((long)i * rate / SoundMixer.SampleRate))];
        }

        return result;
    }
}
