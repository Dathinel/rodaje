using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;

namespace Rodaje.Engine.Tests;

/// <summary>
/// El audio adelantado bajo presión: el hilo de audio lee y busca mientras otro hilo rehace la mezcla
/// (<see cref="BufferedAudioSource.Rewind"/>) con anillos chicos, y lo que suena siempre es la muestra que toca.
/// </summary>
[Trait("Categoria", "Estres")]
public sealed class BufferedAudioSourceStressTests
{
    private const int Chunk = 1024;

    [Theory]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(37)]
    public void Reads_seeks_and_rewinds_from_several_threads_never_desynchronize(int seed)
    {
        int capacity = new Random(seed).Next(2 * Chunk, 6 * Chunk);
        var pcm = new FakePcm();
        var source = new BufferedAudioSource(pcm, capacity);
        int reads = Stress.Iterations(4000);
        int done = 0;
        try
        {
            Stress.Run(
                seed,
                ("audio", (random, stop) =>
                {
                    long origin = 0;
                    long readFrame = 0;
                    float[] buffer = new float[3000 * 2];
                    foreach (int i in Stress.Steps(reads, stop))
                    {
                        if (random.Next(40) == 0)
                        {
                            origin = random.Next(0, 480_000);
                            readFrame = 0;
                            source.Seek(Time.FromSamples(origin, 48_000));
                        }

                        int wanted = random.Next(1, 3000);
                        int count = source.Read(buffer.AsSpan(0, wanted * 2));
                        Assert.InRange(count, 0, wanted);
                        for (int f = 0; f < count; f++)
                        {
                            float expected = origin + readFrame + f;
                            if (buffer[f * 2] != expected || buffer[(f * 2) + 1] != -expected)
                            {
                                Assert.Fail($"Lectura {i}, cuadro {f} de {count}: sonó {buffer[f * 2]} y tocaba {expected} (anillo de {capacity}).");
                            }
                        }

                        readFrame += wanted;
                        if (random.Next(4) == 0)
                        {
                            Thread.Yield();
                        }
                    }

                    Volatile.Write(ref done, 1);
                }),
                ("edición", (random, stop) =>
                {
                    while (Volatile.Read(ref done) == 0 && !stop.IsCancellationRequested)
                    {
                        source.Rewind(random.Next(0, capacity + Chunk), alignToChunk: random.Next(2) == 0);
                        Assert.InRange(source.BufferedFrames, 0, capacity);
                        Thread.SpinWait(random.Next(10, 2000));
                    }
                }));
        }
        finally
        {
            Stress.Within(seed, "Dispose", TimeSpan.FromSeconds(10), source.Dispose);
        }

        Assert.Equal(0, source.Errors);
    }

    /// <summary>
    /// Regresión: con el anillo lleno, un <see cref="BufferedAudioSource.Rewind"/> que conservaba casi todo dejaba pasar al
    /// hilo de decodificación sin mirar si había lugar y el bloque nuevo pisaba lo que aún no había sonado.
    /// </summary>
    /// <remarks>
    /// El decodificador falso anota cuánto había en el anillo al empezar cada lectura: ninguna puede empezar sin lugar para
    /// un bloque. Esa anotación se toma fuera del candado y lo único que puede hacer el hilo de la prueba mientras tanto es
    /// vaciar el anillo, así que un valor alto siempre es una lectura sin lugar. Sin espera fija: el defecto se ve cuando el
    /// hilo de decodificación toma el candado antes que la lectura de la prueba (el <c>Thread.Yield</c> le cede el turno), y
    /// eso no se puede forzar desde fuera, así que se repite muchas vueltas; sin el arreglo, falla en las primeras cientos.
    /// Con el arreglo no hay carrera que pueda hacerla fallar.
    /// </remarks>
    [Fact]
    public void A_rewind_with_a_full_ring_waits_for_room_instead_of_overwriting()
    {
        const int capacity = 2 * Chunk;
        int rounds = Stress.Iterations(1000);
        float[] buffer = new float[capacity * 2];
        for (int round = 0; round < rounds; round++)
        {
            var pcm = new ProbingPcm();
            using var source = new BufferedAudioSource(pcm, capacity);
            pcm.Probe = () => source.BufferedFrames;
            Stress.WaitUntil(0, $"llenar el anillo (vuelta {round})", () => source.BufferedFrames == capacity);
            Assert.Equal(2, pcm.Reads);

            // Conserva todo: el bloque que se vuelva a pedir no cabe hasta que suene algo.
            source.Rewind(capacity);
            Thread.Yield();
            Assert.Equal(capacity, source.Read(buffer));
            for (int f = 0; f < capacity; f++)
            {
                Assert.True(buffer[f * 2] == f, $"Vuelta {round}: el cuadro {f} sonó {buffer[f * 2]} (lo pisó un bloque leído sin lugar).");
            }

            // Con lugar, sigue desde donde quedó lo conservado.
            Stress.WaitUntil(0, $"el bloque siguiente (vuelta {round})", () => source.BufferedFrames >= Chunk);
            Assert.Equal(Chunk, source.Read(buffer.AsSpan(0, Chunk * 2)));
            Assert.Equal(capacity, buffer[0]);
            Assert.Equal(capacity + Chunk - 1, buffer[(Chunk - 1) * 2]);

            int[] seen = pcm.BufferedAtRead;
            Assert.True(seen.Length >= 3, $"Vuelta {round}: el decodificador se leyó {seen.Length} veces.");
            for (int read = 0; read < seen.Length; read++)
            {
                Assert.True(seen[read] + Chunk <= capacity,
                    $"Vuelta {round}: la lectura {read + 1} del decodificador empezó con {seen[read]} cuadros en un anillo de {capacity}.");
            }
        }
    }

    /// <summary>
    /// Como <see cref="FakePcm"/> (la muestra <c>n</c> vale <c>n</c>), pero anota lo que devuelve <see cref="Probe"/> al empezar
    /// cada lectura, en el hilo de decodificación.
    /// </summary>
    private sealed class ProbingPcm : IPcmSource
    {
        private readonly List<int> _bufferedAtRead = [];
        private long _position;

        public Func<int>? Probe { get; set; }

        public int Reads
        {
            get
            {
                lock (_bufferedAtRead)
                {
                    return _bufferedAtRead.Count;
                }
            }
        }

        public int[] BufferedAtRead
        {
            get
            {
                lock (_bufferedAtRead)
                {
                    return [.. _bufferedAtRead];
                }
            }
        }

        public int Read(Span<float> interleaved)
        {
            int buffered = Probe?.Invoke() ?? 0;
            lock (_bufferedAtRead)
            {
                _bufferedAtRead.Add(buffered);
            }

            int frames = interleaved.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                interleaved[i * 2] = _position + i;
                interleaved[(i * 2) + 1] = -(_position + i);
            }

            _position += frames;
            return frames;
        }

        public void Seek(Time position) => _position = position.ToSamples(48_000);

        public void Dispose()
        {
        }
    }
}
