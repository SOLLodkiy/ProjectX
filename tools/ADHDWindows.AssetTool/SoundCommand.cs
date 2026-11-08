using System.IO;
using System.Text;

namespace ADHDWindows.AssetTool;

/// <summary>
/// Собирает 8-битные звуки программы: выстрел снайпера и свои звуки для «Случайных звуков» (стук в дверь, шаги,
/// капающая вода). Звуки синтезируются кодом с постоянным зерном случайности, поэтому каждый запуск даёт те же файлы.
/// </summary>
internal static class SoundCommand
{
    public const int SampleRate = 22050;

    public static int Run(string assets)
    {
        var sounds = Path.Combine(assets, "sounds");
        var random = Path.Combine(sounds, "random");
        Directory.CreateDirectory(random);
        Write(Path.Combine(sounds, "sniper-shot.wav"), Shot());
        Write(Path.Combine(random, "knock.wav"), Knock());
        Write(Path.Combine(random, "steps.wav"), Steps());
        Write(Path.Combine(random, "drip.wav"), Drip());
        Console.WriteLine($"Звуки записаны в {sounds}");
        return 0;
    }

    /// <summary>Выстрел: короткий щелчок, шумовой взрыв с быстрым затуханием и низкий «бум» прямоугольной волной.</summary>
    internal static double[] Shot()
    {
        var random = new Random(1);
        var samples = new double[Seconds(0.45)];
        var noise = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;

            // Шум обновляется не в каждой точке — так он звучит «зернисто», как в старых приставках.
            if (i % 3 == 0)
            {
                noise = (random.NextDouble() * 2) - 1;
            }

            var burst = noise * Math.Exp(-t * 18);
            var boom = Square(t, 90 - (t * 120)) * Math.Exp(-t * 9) * 0.6;
            samples[i] = (burst * 0.8) + boom;
        }

        return samples;
    }

    /// <summary>Три стука в дверь: глухой низкий тон и короткий шум на каждый удар.</summary>
    internal static double[] Knock()
    {
        var random = new Random(2);
        var samples = new double[Seconds(1.0)];
        foreach (var start in new[] { 0.05, 0.26, 0.47 })
        {
            var offset = Seconds(start);
            for (var i = 0; i < Seconds(0.12) && offset + i < samples.Length; i++)
            {
                var t = i / (double)SampleRate;
                var body = Triangle(t, 140 - (t * 200)) * Math.Exp(-t * 35);
                var click = ((random.NextDouble() * 2) - 1) * Math.Exp(-t * 120) * 0.5;
                samples[offset + i] += body + click;
            }
        }

        return samples;
    }

    /// <summary>Четыре шага: шум, сглаженный до глухого шороха, с мягким нарастанием.</summary>
    internal static double[] Steps()
    {
        var random = new Random(3);
        var samples = new double[Seconds(2.0)];
        foreach (var start in new[] { 0.05, 0.52, 0.98, 1.47 })
        {
            var offset = Seconds(start);
            var smooth = 0.0;
            for (var i = 0; i < Seconds(0.2) && offset + i < samples.Length; i++)
            {
                var t = i / (double)SampleRate;
                smooth += (((random.NextDouble() * 2) - 1) - smooth) * 0.08;
                var envelope = Math.Min(1, t * 120) * Math.Exp(-t * 22);
                samples[offset + i] += smooth * envelope * 3.2;
            }
        }

        return samples;
    }

    /// <summary>Капли воды: короткий тон, который быстро уходит вверх, с неровными паузами.</summary>
    internal static double[] Drip()
    {
        var samples = new double[Seconds(2.4)];
        foreach (var (start, pitch) in new[] { (0.05, 900.0), (0.7, 1050.0), (1.25, 950.0), (2.0, 1150.0) })
        {
            var offset = Seconds(start);
            var phase = 0.0;
            for (var i = 0; i < Seconds(0.09) && offset + i < samples.Length; i++)
            {
                var t = i / (double)SampleRate;
                phase += (pitch + (t * 9000)) / SampleRate;
                samples[offset + i] += Math.Sin(phase * Math.PI * 2) * Math.Exp(-t * 45) * 0.8;
            }
        }

        return samples;
    }

    /// <summary>WAV: 8 бит без знака, моно, 22 050 Гц.</summary>
    internal static byte[] Encode(double[] samples)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate);
            writer.Write((short)1);
            writer.Write((short)8);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(samples.Length);
            foreach (var sample in samples)
            {
                // Громкость с запасом: звук не громче системных.
                writer.Write((byte)Math.Clamp(128 + Math.Round(Math.Clamp(sample, -1, 1) * 100), 0, 255));
            }
        }

        return stream.ToArray();
    }

    private static void Write(string path, double[] samples) => File.WriteAllBytes(path, Encode(samples));

    private static int Seconds(double seconds) => (int)(seconds * SampleRate);

    private static double Square(double t, double frequency) => (t * frequency) % 1 < 0.5 ? 1 : -1;

    private static double Triangle(double t, double frequency)
    {
        var phase = (t * frequency) % 1;
        return phase < 0.5 ? (phase * 4) - 1 : 3 - (phase * 4);
    }
}
