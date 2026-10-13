using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Tests;

/// <summary>
/// Cerrar <see cref="ClipPlayer"/> con un cuadro colgado en el decodificador (un disco de red, un archivo dañado). Desde que
/// <see cref="DecodeThread"/> deja de esperar al vencer su plazo, el cierre liberaba el decodificador desde el hilo de la
/// ventana mientras su hilo seguía dentro de él: con FFmpeg, una violación de acceso. Determinista: el plazo es cero y el
/// cuadro solo vuelve cuando la prueba lo suelta; el tope solo corta la espera en rojo.
/// </summary>
public sealed class ClipPlayerHungCloseTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void Dispose_leaves_a_decoder_in_use_by_a_hung_thread_for_that_thread_to_release()
    {
        var fake = new FakeBackend(Samples.Info(Rational.Fps25, 100, audio: false));
        var backend = new HungBackend(fake);
        var player = new ClipPlayer("colgado.mp4", new ClipPlayerOptions(), backend);
        HangingVideo video = backend.Video!;
        Assert.True(video.Entered.Wait(Limit), "El hilo no empezó a decodificar.");

        try
        {
            player.Dispose();

            Assert.False(video.Disposed, "El cierre liberó el decodificador con su hilo todavía dentro de él.");
        }
        finally
        {
            video.Release.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => video.Disposed, Limit), "El hilo no liberó el decodificador al volver.");
        Assert.False(video.DisposedDuringDecode);
        Assert.Equal(0, fake.Video!.LiveFrames);
    }

    private sealed class HungBackend(FakeBackend inner) : IClipBackend
    {
        public HangingVideo? Video { get; private set; }

        public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => Video = new HangingVideo(inner.OpenVideo(path, options));

        public IPcmSource OpenAudio(string path) => inner.OpenAudio(path);

        public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => inner.OpenOutput(source, options);

        public IDecodeExecutor CreateExecutor() => new DecodeThread("Rodaje prueba cierre colgado", TimeSpan.Zero);
    }

    /// <summary>Cada cuadro espera a <see cref="Release"/>, como una llamada nativa que no vuelve.</summary>
    private sealed class HangingVideo(IClipVideo inner) : IClipVideo
    {
        private int _decoding;
        private bool _disposed;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public bool Disposed => Volatile.Read(ref _disposed);

        public bool DisposedDuringDecode { get; private set; }

        public MediaInfo Info => inner.Info;

        public VideoStreamInfo Stream => inner.Stream;

        public bool UsesHardwareDecoding => inner.UsesHardwareDecoding;

        public VideoDecoderStatistics Statistics => inner.Statistics;

        public ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _decoding);
            try
            {
                Entered.Set();
                // Una llamada nativa no atiende la cancelación.
                Release.Wait(Limit, CancellationToken.None);
                return inner.GetFrameAsync(frameIndex, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _decoding);
            }
        }

        public void Dispose()
        {
            DisposedDuringDecode = Volatile.Read(ref _decoding) > 0;
            inner.Dispose();
            Volatile.Write(ref _disposed, true);
        }
    }
}
