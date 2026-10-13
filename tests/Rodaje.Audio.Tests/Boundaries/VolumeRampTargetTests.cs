namespace Rodaje.Audio.Tests.Boundaries;

/// <summary>
/// Tarea 223: la rampa de volumen de <see cref="FrameRenderer"/> cuando el destino cambia más de una vez. El paso se fija
/// al cambiar el destino; si no se recalculara, tras subir 0→1 y bajar a 0 la ganancia seguiría subiendo sin tope.
/// La fuente da siempre 1, así que cada muestra de salida es la ganancia de ese cuadro.
/// </summary>
public sealed class VolumeRampTargetTests
{
    private const int RampFrames = 480;

    /// <summary>Sube 0→1, baja 1→0 y vuelve a subir, cada vez con la rampa anterior ya completa.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    public void SubirYBajarTrasCompletarLaRampa(int block)
    {
        (float Target, int Frames)[] phases = [(1f, 2 * RampFrames), (0f, 2 * RampFrames), (1f, 2 * RampFrames)];
        float[] pieces = RenderPhases(phases, block);
        float[] whole = RenderPhases(phases, int.MaxValue);

        AssertPhases(phases, pieces, start: 0f, block);
        AssertSame(whole, pieces, block);
    }

    /// <summary>
    /// Invierte el destino a mitad de rampa: 0→1, a mitad →0 y, a mitad de esa bajada, otra vez →1. Cada tramo va
    /// en línea recta hacia su destino, llega en <see cref="RampFrames"/> cuadros y no se pasa.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    public void InvertirElDestinoAMitadDeRampa(int block)
    {
        (float Target, int Frames)[] phases = [(1f, RampFrames / 2), (0f, RampFrames / 2), (1f, 2 * RampFrames)];
        float[] pieces = RenderPhases(phases, block);
        float[] whole = RenderPhases(phases, int.MaxValue);

        AssertPhases(phases, pieces, start: 0f, block);
        AssertSame(whole, pieces, block);

        // A mitad de la subida la ganancia va por 0,5; a mitad de la bajada (que parte de ahí), por 0,25.
        Assert.Equal(0.5f, pieces[(RampFrames / 2) - 1], 3);
        Assert.Equal(0.25f, pieces[RampFrames - 1], 3);
    }

    /// <summary>Cambios de destino en ambos sentidos sin dejar terminar ninguna rampa, y al final se deja llegar.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(480)]
    public void VariosCambiosSinTerminarNingunaRampa(int block)
    {
        (float Target, int Frames)[] phases =
        [
            (1f, 100), (0f, 100), (1f, 100), (0.2f, 300), (0.8f, 50), (0f, 2 * RampFrames), (0.6f, 2 * RampFrames),
        ];
        float[] pieces = RenderPhases(phases, block);
        float[] whole = RenderPhases(phases, int.MaxValue);

        AssertPhases(phases, pieces, start: 0f, block);
        AssertSame(whole, pieces, block);
    }

    /// <summary>Renderiza cada tramo con su destino, por bloques de <paramref name="block"/> cuadros (o de una vez).</summary>
    private static float[] RenderPhases((float Target, int Frames)[] phases, int block)
    {
        var renderer = new FrameRenderer(new OnesSource(), historyFrames: 4_096, rampFrames: RampFrames, volume: 0f);
        int total = phases.Sum(p => p.Frames);
        float[] device = new float[total * 2];
        int done = 0;
        foreach ((float target, int frames) in phases)
        {
            renderer.Volume = target;
            for (int end = done + frames; done < end;)
            {
                int n = Math.Min(block, end - done);
                renderer.Render(device.AsSpan(done * 2), n, 2);
                done += n;
            }
        }

        // Estéreo con la misma ganancia en los dos canales: basta el izquierdo.
        float[] gains = new float[total];
        for (int f = 0; f < total; f++)
        {
            Assert.Equal(device[f * 2], device[(f * 2) + 1]);
            gains[f] = device[f * 2];
        }

        return gains;
    }

    /// <summary>
    /// En cada tramo la ganancia se mueve solo hacia el destino, sin pasarse ni salir de [0, 1], y si el tramo dura una
    /// rampa entera lo alcanza exacto a más tardar en el cuadro <see cref="RampFrames"/> y ahí se queda.
    /// </summary>
    private static void AssertPhases((float Target, int Frames)[] phases, float[] gains, float start, int block)
    {
        float previous = start;
        int offset = 0;
        for (int p = 0; p < phases.Length; p++)
        {
            (float target, int frames) = phases[p];
            float from = previous;
            for (int i = 0; i < frames; i++)
            {
                float gain = gains[offset + i];
                string where = $"bloque {block}, tramo {p} ({from}→{target}), cuadro {i}: {gain}";
                Assert.True(gain is >= 0f and <= 1f, where);
                if (target >= from)
                {
                    Assert.True(gain >= previous && gain <= target, where);
                }
                else
                {
                    Assert.True(gain <= previous && gain >= target, where);
                }

                if (i >= RampFrames)
                {
                    Assert.True(gain == target, where);
                }

                previous = gain;
            }

            offset += frames;
        }
    }

    private static void AssertSame(float[] whole, float[] pieces, int block)
    {
        int diff = whole.AsSpan().CommonPrefixLength(pieces);
        Assert.True(diff == whole.Length, $"bloque {block}: la primera diferencia es el cuadro {diff}");
    }

    private sealed class OnesSource : IAudioSampleSource
    {
        public int Read(Span<float> interleaved)
        {
            interleaved.Fill(1f);
            return interleaved.Length / 2;
        }
    }
}
