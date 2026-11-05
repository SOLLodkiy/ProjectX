using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ADHDWindows.Core.Input;
using ADHDWindows.Platform;

namespace ADHDWindows.Tests;

public sealed class MouseHookTests
{
    private const uint LeftUpFlag = 0x0004;
    private const int InputMouse = 0;

    [Theory]
    [InlineData(MouseHook.LeftDown, MouseButton.Left, true)]
    [InlineData(MouseHook.LeftUp, MouseButton.Left, false)]
    [InlineData(MouseHook.RightDown, MouseButton.Right, true)]
    [InlineData(MouseHook.RightUp, MouseButton.Right, false)]
    [InlineData(MouseHook.MiddleDown, MouseButton.Middle, true)]
    [InlineData(MouseHook.MiddleUp, MouseButton.Middle, false)]
    public void Buttons_are_translated(int message, MouseButton button, bool pressed)
    {
        var change = MouseHook.Translate(message, 0, TimeSpan.FromSeconds(1));

        Assert.Equal(new MouseButtonChanged(button, pressed, false, TimeSpan.FromSeconds(1)), change);
    }

    [Theory]
    [InlineData(MouseHook.InjectedFlag)]
    [InlineData(MouseHook.LowerIntegrityInjectedFlag)]
    [InlineData(MouseHook.InjectedFlag | MouseHook.LowerIntegrityInjectedFlag)]
    public void Synthetic_flags_are_recognised(uint flags) =>
        Assert.True(MouseHook.Translate(MouseHook.LeftDown, flags, TimeSpan.Zero)!.Injected);

    [Theory]
    [InlineData(0x0200)] // движение
    [InlineData(0x020A)] // колёсико
    [InlineData(0x020B)] // боковые кнопки
    public void Other_messages_are_skipped(int message) =>
        Assert.Null(MouseHook.Translate(message, 0, TimeSpan.Zero));

    // Настоящее нажатие сработало бы там, где сейчас курсор. Отпускание без нажатия ничего не задевает,
    // а по пути через хук не отличается от него.
    [Fact]
    public void Real_hook_reports_a_synthetic_event_as_injected()
    {
        var received = new BlockingCollection<MouseButtonChanged>();
        using var hook = new MouseHook(received.Add, new RecordingLog());
        Assert.True(hook.Start());

        Send(LeftUpFlag);

        Assert.True(received.TryTake(out var change, TimeSpan.FromSeconds(5)));
        Assert.Equal(MouseButton.Left, change.Button);
        Assert.False(change.Pressed);
        Assert.True(change.Injected);
    }

    [Fact]
    public void Handler_error_does_not_stop_the_hook()
    {
        var log = new RecordingLog();
        var received = new BlockingCollection<MouseButtonChanged>();
        var first = true;
        using var hook = new MouseHook(
            change =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException();
                }

                received.Add(change);
            },
            log);
        Assert.True(hook.Start());

        Send(LeftUpFlag);
        Send(LeftUpFlag);

        Assert.True(received.TryTake(out _, TimeSpan.FromSeconds(5)));
        Assert.Single(log.Errors);
    }

    [Fact]
    public void Dispose_removes_the_hook_and_is_repeatable()
    {
        var count = 0;
        var hook = new MouseHook(_ => Interlocked.Increment(ref count), new RecordingLog());
        Assert.True(hook.Start());
        hook.Dispose();
        hook.Dispose();

        Send(LeftUpFlag);
        Thread.Sleep(50);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public void Slow_handler_does_not_delay_the_mouse()
    {
        // SendInput возвращается после обработки хуком, поэтому его время и есть добавленная задержка.
        using var release = new ManualResetEventSlim();
        using var hook = new MouseHook(_ => release.Wait(), new RecordingLog());
        Assert.True(hook.Start());

        var times = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Send(LeftUpFlag);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        release.Set();
        times.Sort();
        Assert.True(times[times.Count / 2] < 5, $"Медиана {times[times.Count / 2]} мс");
    }

    private static void Send(uint flags)
    {
        var input = new NativeInput { Type = InputMouse, Mouse = new NativeMouseInput { Flags = flags } };
        Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<NativeInput>()));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    // Размер как у INPUT: в объединении он задаётся MOUSEINPUT.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public int Type;
        public NativeMouseInput Mouse;
    }
}
