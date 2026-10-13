using Rodaje.Audio.Processing;
using Rodaje.Engine.Tests.Timeline;

namespace Rodaje.Audio.Tests.Boundaries;

/// <summary>
/// Tarea 215: la salida al dispositivo con 1, 2 y 6 canales, volumen y paneo en los bordes, y el reloj del dispositivo a
/// 44,1/48/96 kHz y frecuencias raras, sin desfase acumulado. Semillas de <see cref="StressSeeds"/>.
/// </summary>
public sealed class RendererBoundaryTests
{
    /// <summary>
    /// Defecto: NaN y ±∞ de la fuente llegaban tal cual al dispositivo (y al historial que se repite al cambiar de
    /// dispositivo), y en mono la suma de dos muestras enormes se desbordaba a ∞. Ahora entran como silencio, igual que en
    /// la cadena de inserciones.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void LoQueNoEsUnNumeroNoLlegaAlDispositivo(int channels)
    {
        var renderer = new FrameRenderer(new PoisonSource(), historyFrames: 4_096, rampFrames: 480, volume: 0.5f);
        float[] device = new float[1_000 * channels];
        renderer.Render(device, 1_000, channels);
        int bad = Array.FindIndex(device, s => !float.IsFinite(s));
        Assert.True(bad < 0, $"{channels} canales: muestra {bad} = {(bad < 0 ? 0 : device[bad])}");

        // Lo que se repite del historial (cambio de dispositivo) tampoco.
        renderer.BeginReplay(0);
        renderer.Render(device, 1_000, channels);
        Assert.True(Array.TrueForAll(device, float.IsFinite), $"{channels} canales al repetir el historial");

        // Lo que sí es un número pasa con su volumen (en mono, promediado con el NaN que entró como silencio).
        float expected = channels == 1 ? 0.075f : 0.15f;
        Assert.Contains(device, s => s == expected);
    }

    /// <summary>Por bloques de cualquier tamaño (también 0), con la rampa de volumen a medias, sale lo mismo que de una vez.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void PorBloquesSaleLoMismoQueDeUnaVez(int channels)
    {
        int seed = StressSeeds.Seed(215, 3, channels);
        var random = new Random(seed);
        const int frames = 5_000;
        float[] whole = RenderInBlocks(channels, frames, _ => frames);
        float[] pieces = RenderInBlocks(channels, frames, left => random.Next(0, Math.Min(left, 700) + 1));
        int diff = whole.AsSpan().CommonPrefixLength(pieces);
        Assert.True(whole.AsSpan().SequenceEqual(pieces), $"semilla {seed}: la primera diferencia es la muestra {diff}");
    }

    /// <summary>
    /// Defecto: el paso de la rampa de volumen se recalculaba en cada llamada con la ganancia del momento, así que con
    /// bloques pequeños la rampa se volvía exponencial y no llegaba nunca al destino; ahora dura los mismos cuadros siempre.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    public void LaRampaDeVolumenDuraLoMismoConCualquierBloque(int block)
    {
        var renderer = new FrameRenderer(new ConstantSource(), historyFrames: 4_096, rampFrames: 480, volume: 0f);
        renderer.Volume = 1f;
        float[] device = new float[960 * 2];
        for (int done = 0; done < 960; done += block)
        {
            int n = Math.Min(block, 960 - done);
            renderer.Render(device.AsSpan(done * 2), n, 2);
        }

        Assert.InRange(device[478 * 2], 0.99f, 1f - 1e-6f);
        Assert.Equal(1f, device[479 * 2]);
        Assert.Equal(1f, device[959 * 2]);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(1e30f)]
    [InlineData(0f)]
    public void ElVolumenEnLosBordes_DejaLaSalidaEntre0Y1(float volume)
    {
        var renderer = new FrameRenderer(new CountingSource(), historyFrames: 4_096, rampFrames: 480, volume: 1f);
        if (float.IsNaN(volume))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Volume = volume);
            return;
        }

        renderer.Volume = volume;
        float[] device = new float[2_000 * 2];
        renderer.Render(device, 2_000, 2);
        Assert.InRange(renderer.Volume, 0f, 1f);
        Assert.True(Array.TrueForAll(device, s => float.IsFinite(s) && MathF.Abs(s) <= 1f));
    }

    /// <summary>Paneo y balance con NaN, ±∞, 0 y enormes: matriz finita, sin ganancia de más de √2 y la ley de −3 dB.</summary>
    [Fact]
    public void ElPaneoEnLosBordesDaUnaMatrizFinita()
    {
        int seed = StressSeeds.Seed(215, 4);
        var random = new Random(seed);
        double[] edges = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -0.0, 1e300, -1e300, 1, -1, 1e-300];
        for (int i = 0; i < 2_000; i++)
        {
            double pan = i < 100 ? edges[i % 10] : random.Next(3) == 0 ? edges[random.Next(10)] : (random.NextDouble() * 4) - 2;
            double balance = i < 100 ? edges[i / 10] : random.Next(3) == 0 ? edges[random.Next(10)] : (random.NextDouble() * 4) - 2;
            PanMatrix m = PanLaw.Matrix(pan, balance);
            float[] values = [m.LeftToLeft, m.RightToLeft, m.LeftToRight, m.RightToRight];
            Assert.True(Array.TrueForAll(values, v => float.IsFinite(v) && MathF.Abs(v) <= 1.4143f), $"semilla {seed}, paneo {pan}, balance {balance}: {m}");

            (double left, double right) = PanLaw.MonoGains(pan);
            Assert.Equal(2, (left * left) + (right * right), 4);
        }
    }

    /// <summary>
    /// El reloj sin dispositivo lleva la cuenta desde la base, no sumando trozos: a cualquier frecuencia, leída a saltos
    /// irregulares, da siempre ⌊ticks · frecuencia / f_QPC⌋ (nada que se pierda o se duplique entre lecturas).
    /// </summary>
    [Theory]
    [InlineData(44_100, 10_000_000)]
    [InlineData(48_000, 10_000_000)]
    [InlineData(96_000, 10_000_000)]
    [InlineData(22_050, 3_579_545)]
    [InlineData(37_800, 2_533_330)]
    [InlineData(8_000, 1_000_000_007)]
    public void ElRelojSinDispositivoNoAcumulaDesfase(int rate, long frequency)
    {
        int seed = StressSeeds.Seed(215, 5, rate);
        var random = new Random(seed);
        var model = new DevicePositionModel(rate, frequency, maxExtrapolationFrames: rate / 20);
        model.EnterFreeRun(timestamp: 1_000);
        model.Start(timestamp: 1_000);
        long ticks = 0;
        for (int i = 0; i < 5_000; i++)
        {
            ticks += random.Next(1, (int)Math.Min(int.MaxValue, frequency / 10));
            long expected = (long)((Int128)ticks * rate / frequency);
            Assert.Equal(expected, model.Read(1_000 + ticks));
        }
    }

    private static float[] RenderInBlocks(int channels, int frames, Func<int, int> next)
    {
        var renderer = new FrameRenderer(new CountingSource(), historyFrames: 4_096, rampFrames: 480, volume: 0.25f);
        renderer.Volume = 0.9f;
        float[] device = new float[frames * channels];
        int done = 0;
        while (done < frames)
        {
            int n = next(frames - done);
            renderer.Render(device.AsSpan(done * channels), n, channels);
            done += n;
        }

        return device;
    }

    private sealed class CountingSource : IAudioSampleSource
    {
        private long _frame;

        public int Read(Span<float> interleaved)
        {
            for (int i = 0; i + 1 < interleaved.Length; i += 2, _frame++)
            {
                interleaved[i] = (_frame % 200) / 200f;
                interleaved[i + 1] = -(_frame % 150) / 150f;
            }

            return interleaved.Length / 2;
        }
    }

    private sealed class ConstantSource : IAudioSampleSource
    {
        public int Read(Span<float> interleaved)
        {
            interleaved.Fill(1f);
            return interleaved.Length / 2;
        }
    }

    /// <summary>Repite NaN, +∞, el mayor float, −∞ y 0,3.</summary>
    private sealed class PoisonSource : IAudioSampleSource
    {
        private long _sample;

        public int Read(Span<float> interleaved)
        {
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] = (_sample++ % 5) switch
                {
                    0 => float.NaN,
                    1 => float.PositiveInfinity,
                    2 => float.MaxValue,
                    3 => float.NegativeInfinity,
                    _ => 0.3f,
                };
            }

            return interleaved.Length / 2;
        }
    }
}
