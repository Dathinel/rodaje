using Rodaje.Audio.Capture;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;
using Rodaje.Engine.Tests.Support;

namespace Rodaje.Engine.Tests.Recording;

/// <summary>
/// La voz en off con el micrófono en su propio hilo: terminar la toma en cualquier momento, incluso con un paquete a
/// medio escribir o con una captura que tarda en detenerse, no escribe en el WAV cerrado ni lanza en el hilo de captura.
/// </summary>
[Trait("Categoria", "Estres")]
public sealed class VoiceOverRecorderStressTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-voz-estres-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Finishing_at_a_random_moment_while_packets_arrive_never_races(int seed)
    {
        var random = new Random(seed);
        int takes = Stress.Iterations(15);
        for (int take = 0; take < takes; take++)
        {
            var capture = new ThreadCapture(stopsPromptly: random.Next(2) == 0);
            using var recorder = new VoiceOverRecorder(capture, Path.Combine(_folder, $"toma{take}.wav"), Time.Zero, qpc => Time.FromSeconds(qpc / 1e7));
            recorder.Begin();
            Thread.Sleep(random.Next(0, 20));
            VoiceOverTake result = recorder.Finish();
            capture.Join();
            Assert.True(capture.Error is null, $"Semilla {seed}, toma {take}: el hilo de captura lanzó {capture.Error}");
            Assert.True(DataBytes(result.Path) == result.Frames * 4, $"Semilla {seed}, toma {take}: el WAV no tiene las {result.Frames} muestras contadas.");
        }
    }

    /// <summary>
    /// Regresión: si la captura no se detenía a tiempo, el paquete que ya estaba dentro del grabador escribía después de
    /// que <see cref="VoiceOverRecorder.Finish"/> cerrara el WAV, y la excepción tumbaba el hilo de captura (y la app).
    /// </summary>
    [Fact]
    public void A_packet_in_progress_finishes_before_the_take_is_closed()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var capture = new ManualCapture();
        using var recorder = new VoiceOverRecorder(capture, Path.Combine(_folder, "toma.wav"), Time.Zero, _ =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Time.Zero;
        });
        recorder.Begin();
        Exception? error = null;
        var packet = new Thread(() =>
        {
            try
            {
                capture.Push(new float[480], 0);
            }
#pragma warning disable CA1031 // Se comprueba abajo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = ex;
            }
        });
        packet.Start();
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));

        // El paquete sigue dentro: el cierre no debe adelantarse a él.
        using var timer = new Timer(_ => release.Set(), null, 200, Timeout.Infinite);
        VoiceOverTake take = recorder.Finish();
        Assert.True(packet.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(error);
        Assert.Equal(480, take.Frames);
    }

    /// <summary>Tamaño del bloque <c>data</c> del WAV.</summary>
    private static long DataBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (System.Text.Encoding.ASCII.GetString(bytes, at, 4) == "data")
            {
                return size;
            }

            at += 8 + size + (size & 1);
        }

        return -1;
    }

    /// <summary>Un micrófono falso en su propio hilo, que puede tardar en detenerse (como uno trabado en el controlador).</summary>
    private sealed class ThreadCapture(bool stopsPromptly) : IAudioCapture
    {
        private Thread? _thread;
        private volatile bool _stop;

        public Exception? Error { get; private set; }

        public int SampleRate => Rate;

        public string DeviceName => "Micrófono de prueba";

        public void StartCapture(CapturePacketHandler handler)
        {
            _thread = new Thread(() =>
            {
                float[] samples = new float[480];
                long qpc = 0;
                try
                {
                    for (int p = 0; p < 2000 && !_stop; p++)
                    {
                        samples.AsSpan().Fill(p % 7 / 10f);
                        handler(samples, qpc, discontinuity: p % 50 == 49);
                        qpc += 100_000;
                        Thread.SpinWait(200);
                    }
                }
#pragma warning disable CA1031 // Lo mira la prueba.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Error = ex;
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void StopCapture()
        {
            if (stopsPromptly)
            {
                _stop = true;
                _thread?.Join();
            }
        }

        public void Join()
        {
            _stop = true;
            _thread?.Join();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ManualCapture : IAudioCapture
    {
        private CapturePacketHandler? _handler;

        public int SampleRate => Rate;

        public string DeviceName => "Micrófono de prueba";

        public void StartCapture(CapturePacketHandler handler) => _handler = handler;

        public void Push(float[] samples, long qpc) => _handler?.Invoke(samples, qpc, false);

        // Como una captura trabada: no se detiene a tiempo.
        public void StopCapture()
        {
        }

        public void Dispose()
        {
        }
    }
}
