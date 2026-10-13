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
                    for (int i = 0; i < reads && !stop.IsCancellationRequested; i++)
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
    [Fact]
    public void A_rewind_with_a_full_ring_waits_for_room_instead_of_overwriting()
    {
        const int capacity = 2 * Chunk;
        var pcm = new FakePcm { Gate = new SemaphoreSlim(2) };
        using var source = new BufferedAudioSource(pcm, capacity);
        Stress.WaitUntil(0, "llenar el anillo", () => source.BufferedFrames == capacity);

        // Conserva todo: el bloque que se vuelva a pedir no cabe hasta que suene algo.
        source.Rewind(capacity);
        pcm.Gate.Release();
        Thread.Sleep(200);
        Assert.InRange(source.BufferedFrames, 0, capacity);

        float[] buffer = new float[capacity * 2];
        Assert.Equal(capacity, source.Read(buffer));
        for (int f = 0; f < capacity; f++)
        {
            Assert.Equal(f, buffer[f * 2]);
        }

        // Con lugar, sigue desde donde quedó lo conservado.
        Stress.WaitUntil(0, "el bloque siguiente", () => source.BufferedFrames >= Chunk);
        Assert.Equal(Chunk, source.Read(buffer.AsSpan(0, Chunk * 2)));
        Assert.Equal(capacity, buffer[0]);
        Assert.Equal(capacity + Chunk - 1, buffer[(Chunk - 1) * 2]);

        // Que el hilo no quede esperando al decodificador falso al desecharse.
        pcm.Gate.Release(1000);
    }
}
