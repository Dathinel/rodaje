using Rodaje.Core.Timing;

namespace Rodaje.Audio.Tests;

public class FrameRendererTests
{
    [Fact]
    public void EstereoPasaTalCual()
    {
        var source = new CountingSource();
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        float[] device = new float[3_000 * 2];

        int produced = renderer.Render(device, 3_000, 2);

        Assert.Equal(3_000, produced);
        Assert.Equal(3_000, renderer.Written);
        for (int f = 0; f < 3_000; f++)
        {
            Assert.Equal(CountingSource.Left(f), device[f * 2]);
            Assert.Equal(CountingSource.Right(f), device[(f * 2) + 1]);
        }

        Assert.Equal(0, renderer.Underruns);
    }

    [Fact]
    public void MonoEsElPromedioDeLosDosCanales()
    {
        float[] stereo = [1f, 0f, 0.5f, -0.5f, -1f, -1f];
        float[] mono = new float[3];
        ChannelMapper.FromStereo(stereo, mono, 3, 1);
        Assert.Equal([0.5f, 0f, -1f], mono);
    }

    [Fact]
    public void MasDeDosCanalesLlevaLyRALosFrontalesYSilencioAlResto()
    {
        float[] stereo = [0.1f, 0.2f, 0.3f, 0.4f];
        float[] surround = Enumerable.Repeat(9f, 12).ToArray();
        ChannelMapper.FromStereo(stereo, surround, 2, 6);
        Assert.Equal([0.1f, 0.2f, 0f, 0f, 0f, 0f, 0.3f, 0.4f, 0f, 0f, 0f, 0f], surround);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ElRenderAdaptaLosCanalesDelDispositivo(int channels)
    {
        var renderer = new FrameRenderer(new CountingSource(), historyFrames: 4_096, rampFrames: 480, volume: 1f);
        float[] device = new float[1_500 * channels];
        renderer.Render(device, 1_500, channels);
        for (int f = 0; f < 1_500; f += 149)
        {
            if (channels == 1)
            {
                Assert.Equal((CountingSource.Left(f) + CountingSource.Right(f)) * 0.5f, device[f]);
            }
            else
            {
                Assert.Equal(CountingSource.Left(f), device[f * channels]);
                Assert.Equal(CountingSource.Right(f), device[(f * channels) + 1]);
            }
        }
    }

    [Fact]
    public void LoQueFaltaEsSilencioYSeCuenta()
    {
        var source = new CountingSource { Limit = 700 };
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        float[] device = Enumerable.Repeat(7f, 1_000 * 2).ToArray();

        int produced = renderer.Render(device, 1_000, 2);

        Assert.Equal(1_000, produced);
        Assert.Equal(CountingSource.Left(100), device[100 * 2]);
        Assert.Equal(CountingSource.Left(699), device[699 * 2]);
        Assert.True(device[700 * 2] > 0f && device[700 * 2] < CountingSource.Left(699));
        Assert.All(device[((700 + FrameRenderer.GapFadeFrames) * 2)..], s => Assert.Equal(0f, s));
        Assert.Equal(1, renderer.Underruns);
        Assert.Equal(300, renderer.UnderrunFrames);

        renderer.Render(device, 500, 2);
        Assert.Equal(2, renderer.Underruns);
        Assert.Equal(800, renderer.UnderrunFrames);
        Assert.Equal(1_500, renderer.Written);
    }

    [Theory]
    [InlineData(256, 1024)]
    [InlineData(0, 1024)]
    [InlineData(256, 64)]
    [InlineData(0, 64)]
    [InlineData(0, 7)]
    public void UnVaciadoDeBufferNoEsUnEscalon(int framesBeforeGap, int block)
    {
        // Señal fuerte (0,5 constante) que se corta y vuelve, con bloques grandes o más chicos que el fundido: ningún salto
        // entre muestras seguidas pasa de lo que da el fundido.
        var source = new GappySource { Available = true };
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        var output = new List<float>();
        void Run(int frames)
        {
            for (int done = 0; done < frames; done += block)
            {
                int n = Math.Min(block, frames - done);
                float[] d = new float[n * 2];
                renderer.Render(d, n, 2);
                output.AddRange(d.Where((_, i) => i % 2 == 0));
            }
        }

        Run(256);
        source.Available = false;
        source.PartialFrames = framesBeforeGap;
        Run(400);
        source.Available = true;
        Run(512);

        float maxStep = 0;
        for (int f = 1; f < output.Count; f++)
        {
            maxStep = Math.Max(maxStep, Math.Abs(output[f] - output[f - 1]));
        }

        Assert.True(maxStep <= 0.5f / 100, $"salto de {maxStep}");
        Assert.Equal(0f, output[655]);
        Assert.Equal(0.5f, output[^1]);
    }

    [Fact]
    public void Tras_un_seek_el_fundido_no_arrastra_lo_de_la_posicion_anterior()
    {
        var source = new GappySource { Available = true };
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        float[] d = new float[256 * 2];
        renderer.Render(d, 256, 2);
        renderer.Seek(Time.FromSeconds(5));
        source.Available = false;
        renderer.Render(d, 256, 2);
        Assert.All(d, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void UnaFuenteQueFallaODevuelveBasuraDaSilencioYNoTumbaElHilo()
    {
        var source = new CountingSource { Throw = true };
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        float[] device = Enumerable.Repeat(7f, 100 * 2).ToArray();

        renderer.Render(device, 100, 2);
        Assert.All(device, s => Assert.Equal(0f, s));
        Assert.Equal(1, renderer.SourceErrors);
        Assert.Equal(100, renderer.UnderrunFrames);

        source.Throw = false;
        source.Lie = true;
        renderer.Render(device, 100, 2);
        Assert.Equal(2, renderer.SourceErrors);
        Assert.Equal(200, renderer.UnderrunFrames);

        source.Lie = false;
        source.Throw = true;
        renderer.Seek(Time.FromSeconds(1));
        Assert.Equal(3, renderer.SourceErrors);
    }

    [Fact]
    public void ElVolumenCambiaConRampaYLuegoQuedaFijo()
    {
        var renderer = new FrameRenderer(new ConstantSource(1f), historyFrames: 4_096, rampFrames: 100, volume: 1f);
        renderer.Volume = 0.5f;
        Assert.Equal(0.5f, renderer.Volume);
        float[] device = new float[300 * 2];

        renderer.Render(device, 300, 2);

        for (int f = 1; f < 300; f++)
        {
            Assert.True(device[f * 2] <= device[(f - 1) * 2]);
            Assert.Equal(device[f * 2], device[(f * 2) + 1]);
        }

        Assert.True(device[0] < 1f && device[0] > 0.99f);
        Assert.Equal(0.5f, device[100 * 2], 5);
        Assert.Equal(0.5f, device[299 * 2]);

        renderer.Render(device, 10, 2);
        Assert.Equal(0.5f, device[0]);

        renderer.Volume = 7f;
        Assert.Equal(1f, renderer.Volume);
        renderer.Volume = -1f;
        Assert.Equal(0f, renderer.Volume);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Volume = float.NaN);
    }

    [Fact]
    public void RepiteLoPendienteSinVolverALeerLaFuente()
    {
        var source = new CountingSource();
        var renderer = new FrameRenderer(source, historyFrames: 1_000, rampFrames: 480, volume: 1f);
        float[] device = new float[2_500 * 2];
        renderer.Render(device, 2_500, 2);

        Assert.Equal(2_200, renderer.BeginReplay(2_200));
        Assert.True(renderer.IsReplaying);
        int produced = renderer.Render(device, 500, 2);

        Assert.Equal(200, produced);
        Assert.False(renderer.IsReplaying);
        for (int f = 0; f < 500; f++)
        {
            Assert.Equal(CountingSource.Left(2_200 + f), device[f * 2]);
        }

        Assert.Equal(2_700, renderer.Written);
        Assert.Equal(2_700, source.Position);
    }

    [Fact]
    public void LaRepeticionSeLimitaAlHistorial()
    {
        var renderer = new FrameRenderer(new CountingSource(), historyFrames: 1_000, rampFrames: 480, volume: 1f);
        float[] device = new float[3_000 * 2];
        renderer.Render(device, 3_000, 2);

        Assert.Equal(2_000, renderer.BeginReplay(0));
        Assert.Equal(3_000, renderer.BeginReplay(5_000));
        Assert.False(renderer.IsReplaying);

        renderer.BeginReplay(2_500);
        renderer.CancelReplay();
        Assert.False(renderer.IsReplaying);
        Assert.Equal(10, renderer.Render(device, 10, 2));
    }

    [Fact]
    public void DescartarConsumeLaFuenteYAlimentaElHistorial()
    {
        var source = new CountingSource();
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        renderer.Discard(2_500);
        Assert.Equal(2_500, renderer.Written);
        Assert.Equal(2_500, source.Position);

        renderer.BeginReplay(2_400);
        float[] device = new float[100 * 2];
        Assert.Equal(0, renderer.Render(device, 100, 2));
        Assert.Equal(CountingSource.Left(2_450), device[50 * 2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Discard(-1));
    }

    [Fact]
    public void SeekAvisaALaFuenteYCancelaLaRepeticion()
    {
        var source = new CountingSource();
        var renderer = new FrameRenderer(source, historyFrames: 4_096, rampFrames: 480, volume: 1f);
        renderer.Discard(100);
        renderer.BeginReplay(50);
        renderer.Seek(Time.FromSeconds(2));
        Assert.False(renderer.IsReplaying);
        Assert.Equal(Time.FromSeconds(2), source.SeekedTo);
    }

    [Fact]
    public void ValidaSusArgumentos()
    {
        Assert.Throws<ArgumentNullException>(() => new FrameRenderer(null!, 1, 1, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRenderer(new CountingSource(), 0, 1, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRenderer(new CountingSource(), 1, 0, 1f));
        var renderer = new FrameRenderer(new CountingSource(), 10, 1, 1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(new float[4], 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.Render(new float[4], 1, 0));
    }

    [Fact]
    public void LaFuentePorDefectoIgnoraElSeek()
    {
        IAudioSampleSource source = new ConstantSource(0.25f);
        source.Seek(Time.FromSeconds(1));
        float[] buffer = new float[4];
        Assert.Equal(2, source.Read(buffer));
    }

    private sealed class CountingSource : IAudioSampleSource
    {
        public long Position { get; private set; }

        public long Limit { get; set; } = long.MaxValue;

        public bool Throw { get; set; }

        public bool Lie { get; set; }

        public Time? SeekedTo { get; private set; }

        public static float Left(long frame) => (frame % 1_000) / 1_000f;

        public static float Right(long frame) => -(frame % 777) / 777f;

        public int Read(Span<float> interleaved)
        {
            if (Throw)
            {
                throw new InvalidOperationException("Fuente rota a propósito.");
            }

            int frames = interleaved.Length / 2;
            if (Lie)
            {
                return frames + 1;
            }

            int n = (int)Math.Clamp(Limit - Position, 0, frames);
            for (int f = 0; f < n; f++)
            {
                interleaved[f * 2] = Left(Position + f);
                interleaved[(f * 2) + 1] = Right(Position + f);
            }

            Position += n;
            return n;
        }

        public void Seek(Time position)
        {
            if (Throw)
            {
                throw new InvalidOperationException("Fuente rota a propósito.");
            }

            SeekedTo = position;
        }
    }

    private sealed class GappySource : IAudioSampleSource
    {
        public bool Available { get; set; }

        public int PartialFrames { get; set; }

        public int Read(Span<float> interleaved)
        {
            int n = Available ? interleaved.Length / 2 : Math.Min(PartialFrames, interleaved.Length / 2);
            interleaved[..(n * 2)].Fill(0.5f);
            PartialFrames = Available ? 0 : PartialFrames - n;
            return n;
        }

        public void Seek(Time position)
        {
        }
    }

    private sealed class ConstantSource(float value) : IAudioSampleSource
    {
        public int Read(Span<float> interleaved)
        {
            interleaved.Fill(value);
            return interleaved.Length / 2;
        }
    }
}
