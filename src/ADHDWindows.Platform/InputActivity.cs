using System.Runtime.InteropServices;

namespace ADHDWindows.Platform;

/// <summary>
/// Сколько времени человек ничего не делал: клавиатура, мышь и геймпады XInput.
/// Не потокобезопасен: вызывается из одного потока.
/// </summary>
public sealed class InputActivity
{
    private const int MaxPads = 4;
    private const uint Success = 0;

    private readonly uint[] _packets = new uint[MaxPads];
    private readonly bool[] _known = new bool[MaxPads];
    private long _lastPadTick = Environment.TickCount64;
    private bool _xinputMissing;

    /// <summary>Время с последнего действия человека. Геймпад система сама не учитывает, поэтому он опрашивается здесь.</summary>
    public TimeSpan Idle()
    {
        var now = Environment.TickCount64;
        PollPads(now);

        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var systemIdle = GetLastInputInfo(ref info)
            ? (uint)Environment.TickCount - info.Time // Обе величины 32-битные и переполняются одинаково.
            : 0u;
        var padIdle = now - _lastPadTick;
        return TimeSpan.FromMilliseconds(Math.Min(systemIdle, padIdle));
    }

    private void PollPads(long now)
    {
        if (_xinputMissing)
        {
            return;
        }

        try
        {
            for (var pad = 0; pad < MaxPads; pad++)
            {
                if (XInputGetState((uint)pad, out var state) != Success)
                {
                    _known[pad] = false;
                    continue;
                }

                // Номер пакета меняется при любом изменении кнопок и стиков.
                if (_known[pad] && state.PacketNumber != _packets[pad])
                {
                    _lastPadTick = now;
                }

                _packets[pad] = state.PacketNumber;
                _known[pad] = true;
            }
        }
        catch (DllNotFoundException)
        {
            _xinputMissing = true;
        }
        catch (EntryPointNotFoundException)
        {
            _xinputMissing = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
