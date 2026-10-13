namespace Rodaje.Audio.Tests.Boundaries;

/// <summary>
/// Tarea 225. Defecto: si el destino del volumen estaba a menos de lo que el float puede sumar a la ganancia (de 1 a
/// 0,99999 con una rampa de 480 cuadros, paso ≈ 2·10⁻⁸), <c>gain + step</c> redondeaba a <c>gain</c> y la rampa no
/// terminaba nunca. Ahora, cuando el paso ya no acerca, la ganancia salta al destino.
/// </summary>
public sealed class VolumeRampStallTests
{
    private const int RampFrames = 480;

    [Theory]
    [InlineData(1f, 0.99999f, 1)]
    [InlineData(1f, 0.99999f, 7)]
    [InlineData(1f, 0.99999f, 480)]
    [InlineData(0.99999f, 1f, 7)]
    [InlineData(0.5f, 0.500001f, 7)]
    [InlineData(1f, 0.9999f, 7)]
    public void LaRampaLlegaExactaAUnDestinoMuyCercano(float from, float to, int block)
    {
        var renderer = new FrameRenderer(new OnesSource(), historyFrames: 4_096, rampFrames: RampFrames, volume: from);
        renderer.Volume = to;
        float[] device = new float[2 * RampFrames * 2];
        for (int done = 0; done < 2 * RampFrames; done += block)
        {
            int n = Math.Min(block, (2 * RampFrames) - done);
            renderer.Render(device.AsSpan(done * 2), n, 2);
        }

        // Sin pasarse por el camino y exacta al final. (Cerca de 1 el paso se redondea a unidades enteras del float y
        // la rampa puede durar algo más de RampFrames: de 1 a 0,9999 tarda unos 560 cuadros; por eso no se mira el 480.)
        float low = MathF.Min(from, to);
        float high = MathF.Max(from, to);
        Assert.True(Array.TrueForAll(device, s => s >= low && s <= high), $"de {from} a {to}, bloque {block}: se pasa");
        Assert.Equal(to, device[^1]);

        // Ya fuera de la rampa: lo que sigue sale con la ganancia fija del destino.
        float[] after = new float[RampFrames * 2];
        renderer.Render(after, RampFrames, 2);
        Assert.True(Array.TrueForAll(after, s => s == to), $"de {from} a {to}, bloque {block}: después de la rampa");
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
