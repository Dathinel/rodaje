using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Engine.Timeline.Retime;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.AudioSignals;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Audio en reversa con un medio de verdad (AAC decodificado por FFmpeg, con seek exacto y bloques de 0,5 s hacia atrÃ¡s):
/// tono, chasquidos en las uniones de bloque y deriva frente a la decodificaciÃ³n seguida del mismo archivo.
/// </summary>
public sealed class AudioReverseMediaTests(ITestOutputHelper output)
{
    private const int Seconds = 8;
    private static readonly Lazy<string> Tone = new(() => Generate("reversa-tono-440.m4a", "sine=frequency=440:sample_rate=48000:duration=8"));

    [FfmpegFact]
    public void At_1x_the_reverse_is_the_decoded_file_read_backwards_sample_by_sample()
    {
        float[] forward = DecodeAll(Tone.Value);
        long sourceOut = 7L * Rate;
        float[] left = Left(ReadReverse(Tone.Value, new Rational(1, 1), preservePitch: false, Time.FromSeconds(6), Time.FromSeconds(7)));

        // Sin velocidad la salida i es el origen SourceOut âˆ’ 1 âˆ’ i: cada muestra, no solo el tono, y sin deriva al final.
        double worst = 0;
        double worstLate = 0;
        for (int i = 0; i < left.Length; i++)
        {
            double error = Math.Abs(left[i] - forward[(sourceOut - 1 - i) * 2]);
            worst = Math.Max(worst, error);
            worstLate = i >= left.Length - Rate ? Math.Max(worstLate, error) : worstLate;
        }

        double steps = MaxStep(left.AsSpan(Rate / 10)) / (2 * Amplitude(left.AsSpan(Rate / 4, Rate / 2), 440) * Math.Sin(Math.PI * 440 / Rate));
        output.WriteLine(Invariant($"MEDIDA reversa_1x_medio: error mÃ¡ximo {worst:E2} (Ãºltimo segundo {worstLate:E2}), tono {Frequency(left.AsSpan(Rate / 2)):F3} Hz, salto mÃ¡ximo {steps:F3} Ã— el ideal"));
        Assert.True(worst < 1e-3, Invariant($"La reversa se aparta {worst:E2} de la decodificaciÃ³n seguida."));
        Assert.InRange(Frequency(left.AsSpan(Rate / 2)), 439.5, 440.5);
        Assert.True(steps <= 1.1, Invariant($"Hay un chasquido: el mayor salto es {steps:F3} veces el del seno."));
    }

    [FfmpegTheory]
    [InlineData(2, 1, true, 440)]
    [InlineData(1, 2, true, 440)]
    [InlineData(1, 2, false, 220)]
    [InlineData(3, 2, false, 660)]
    public void With_speed_the_reverse_keeps_or_moves_the_tone_without_clicks_or_drift(int num, int den, bool preservePitch, double hz)
    {
        var rate = new Rational(num, den);
        Time sourceIn = Time.FromSeconds(0.5);
        Time sourceOut = Time.FromSeconds(0.5 + (6.0 * Math.Min(1, rate.ToDouble())));
        ClipTimeMap map = ClipTimeMap.Fit(Time.Zero, sourceIn, sourceOut, new ClipSpeed { Rate = rate, Reverse = true, PreservePitch = preservePitch });
        float[] left = Left(Read(Tone.Value, map, preservePitch));

        double measured = Frequency(left.AsSpan(Rate / 2, left.Length - Rate));
        double steps = MaxStep(left.AsSpan(Rate / 4, left.Length - (Rate / 2))) / (2 * Amplitude(left.AsSpan(Rate / 2, Rate), hz) * Math.Sin(Math.PI * hz / Rate));

        // Deriva: el tono de la Ãºltima medio segundo sigue en fase con el del principio (un corrimiento de muestras lo romperÃ­a en los empalmes).
        double late = Frequency(left.AsSpan(left.Length - (Rate / 2) - (Rate / 4), Rate / 2));
        output.WriteLine(Invariant($"MEDIDA reversa_{num}/{den}_{(preservePitch ? "tono" : "cinta")}: {measured:F3} Hz (final {late:F3}), salto mÃ¡ximo {steps:F3} Ã— el ideal, {map.Duration.ToSamples(Rate) / (double)Rate:F2} s"));
        Assert.InRange(measured, hz - 0.5, hz + 0.5);
        Assert.InRange(late, hz - 0.5, hz + 0.5);
        Assert.True(steps <= 1.1, Invariant($"Hay un chasquido: el mayor salto es {steps:F3} veces el del seno."));
    }

    private static float[] ReadReverse(string path, Rational rate, bool preservePitch, Time sourceIn, Time sourceOut) =>
        Read(path, ClipTimeMap.Fit(Time.Zero, sourceIn, sourceOut, new ClipSpeed { Rate = rate, Reverse = true, PreservePitch = preservePitch }), preservePitch);

    private static float[] Read(string path, ClipTimeMap map, bool preservePitch)
    {
        using IPcmSource pcm = MediaClipBackend.Instance.OpenAudio(path);
        var reader = new RetimedReader(new AudioRetime(map, preservePitch), pcm, -1);
        int frames = (int)map.Duration.ToSamples(Rate);
        var all = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(1024, frames - done);
            reader.Read(done, all.AsSpan(done * 2, n * 2), reader.DecoderNext);
            done += n;
        }

        return all;
    }

    private static float[] DecodeAll(string path)
    {
        using IPcmSource pcm = MediaClipBackend.Instance.OpenAudio(path);
        var all = new float[(Seconds + 1) * Rate * 2];
        int done = 0;
        while (done < all.Length)
        {
            int n = pcm.Read(all.AsSpan(done, Math.Min(4096, all.Length - done)));
            if (n <= 0)
            {
                break;
            }

            done += n * 2;
        }

        return all;
    }

    private static string Generate(string name, string source)
    {
        string path = Path.Combine(Path.GetTempPath(), "rodaje-pruebas", name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp.m4a";
            FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", source,
                "-ac", "2", "-c:a", "aac", "-b:a", "256k", temp]);
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
