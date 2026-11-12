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

        var ui = Path.Combine(sounds, "ui");
        Directory.CreateDirectory(ui);
        foreach (var (name, samples) in Interface())
        {
            Write(Path.Combine(ui, name + ".wav"), samples);
        }

        var music = Path.Combine(sounds, "music");
        Directory.CreateDirectory(music);
        Write(Path.Combine(music, "theme.wav"), Theme());
        Console.WriteLine($"Звуки записаны в {sounds}");
        return 0;
    }

    /// <summary>
    /// Короткие звуки событий игры: прямоугольная волна с быстрым затуханием, как в старых приставках.
    /// Ноты заданы номерами MIDI: 60 — до первой октавы.
    /// </summary>
    internal static IEnumerable<(string Name, double[] Samples)> Interface()
    {
        yield return ("purchase", Notes([(76, 0.06), (84, 0.12)], 0.25));
        yield return ("denied", Notes([(52, 0.09), (47, 0.16)], 0.5));
        yield return ("quest", Notes([(67, 0.07), (72, 0.07), (76, 0.07), (79, 0.18)], 0.25));
        yield return ("level", Notes([(60, 0.08), (64, 0.08), (67, 0.08), (72, 0.08), (76, 0.08), (84, 0.3)], 0.25));
        yield return ("achievement", Notes([(72, 0.08), (79, 0.08), (84, 0.08), (88, 0.26)], 0.125));
        yield return ("chest", Notes([(69, 0.05), (73, 0.05), (76, 0.05), (81, 0.05), (85, 0.22)], 0.25));
        yield return ("event", Notes([(81, 0.06), (0, 0.04), (81, 0.06), (88, 0.14)], 0.5));
        yield return ("warning", Notes([(70, 0.12), (0, 0.06), (70, 0.12), (0, 0.06), (70, 0.12)], 0.5));
        yield return ("survived", Notes([(64, 0.07), (67, 0.07), (72, 0.2)], 0.25));
        yield return ("feed", Chomp());
        yield return ("pet", Notes([(84, 0.05), (88, 0.08)], 0.125));
    }

    /// <summary>Мелодия по кругу примерно на 15 секунд: мелодия прямоугольной волной и бас треугольной.</summary>
    internal static double[] Theme()
    {
        const double beat = 0.24;
        int[] melody =
        [
            72, 0, 76, 79, 76, 0, 72, 74, 76, 0, 74, 72, 69, 0, 67, 0,
            69, 0, 72, 76, 74, 0, 72, 69, 67, 0, 69, 72, 74, 0, 0, 0,
            72, 0, 76, 79, 81, 0, 79, 76, 79, 0, 76, 74, 72, 0, 74, 0,
            76, 0, 74, 72, 69, 0, 67, 69, 72, 0, 0, 0, 72, 0, 0, 0,
        ];
        int[] bass = [48, 48, 45, 45, 41, 41, 43, 43, 48, 48, 45, 45, 41, 43, 48, 48];
        var samples = new double[Seconds(beat * melody.Length)];
        for (var step = 0; step < melody.Length; step++)
        {
            var offset = Seconds(step * beat);
            AddNote(samples, offset, Seconds(beat * 0.9), melody[step], 0.25, 0.32, square: true);
            if (step % 4 == 0)
            {
                AddNote(samples, offset, Seconds(beat * 3.8), bass[step / 4], 0.5, 0.45, square: false);
            }
        }

        return samples;
    }

    /// <summary>Хруст: два коротких шумовых удара.</summary>
    private static double[] Chomp()
    {
        var random = new Random(4);
        var samples = new double[Seconds(0.3)];
        foreach (var start in new[] { 0.0, 0.14 })
        {
            var offset = Seconds(start);
            for (var i = 0; i < Seconds(0.08) && offset + i < samples.Length; i++)
            {
                var t = i / (double)SampleRate;
                var noise = i % 4 == 0 ? (random.NextDouble() * 2) - 1 : samples[offset + i - 1] / 0.6;
                samples[offset + i] = noise * Math.Exp(-t * 40) * 0.6;
            }
        }

        return samples;
    }

    /// <summary>Ноты подряд. 0 — пауза. <paramref name="duty"/> — доля верхней половины прямоугольной волны.</summary>
    private static double[] Notes(IReadOnlyList<(int Note, double Seconds)> notes, double duty)
    {
        var samples = new double[Seconds(notes.Sum(note => note.Seconds) + 0.05)];
        var offset = 0;
        foreach (var (note, length) in notes)
        {
            AddNote(samples, offset, Seconds(length), note, duty, 0.55, square: true);
            offset += Seconds(length);
        }

        return samples;
    }

    private static void AddNote(double[] samples, int offset, int length, int note, double duty, double volume, bool square)
    {
        if (note <= 0)
        {
            return;
        }

        var frequency = 440 * Math.Pow(2, (note - 69) / 12.0);
        for (var i = 0; i < length && offset + i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            var phase = (t * frequency) % 1;
            var wave = square ? (phase < duty ? 1 : -1) : Triangle(t, frequency);

            // Короткая атака и спад убирают щелчки на границах нот.
            var envelope = Math.Min(1, i / (SampleRate * 0.004)) * Math.Min(1, (length - i) / (SampleRate * 0.01));
            samples[offset + i] += wave * volume * envelope;
        }
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
