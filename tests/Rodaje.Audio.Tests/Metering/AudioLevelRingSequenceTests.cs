using Rodaje.Audio.Metering;

namespace Rodaje.Audio.Tests.Metering;

/// <summary>
/// El lector del anillo de niveles descarta una entrada que el escritor pisó mientras la copiaba. El orden se fuerza sin
/// reloj: <see cref="AudioLevelRing.BeforeCopy"/> detiene al lector en una entrada, el hilo escritor da la vuelta entera al
/// anillo y solo entonces el lector sigue. Sin la comprobación de la secuencia, el lector mezclaría bloques de dos vueltas.
/// </summary>
public sealed class AudioLevelRingSequenceTests
{
    private const int Capacity = 16;
    private const int Frames = 16;
    private const float OldPeak = 0.1f;
    private const float NewPeak = 0.9f;

    // El lector va de lo más nuevo a lo más viejo: lee 15..10 de la primera vuelta y el escritor la pisa entera en la 9.
    private const long PassAt = 9;
    private const int UntouchedBlocks = Capacity - (int)PassAt - 1;

    [Fact]
    public void Measure_drops_an_entry_the_writer_overwrote_while_it_was_copied()
    {
        var ring = new AudioLevelRing(Capacity);
        AudioLevels levels;
        using (var writer = new ForcedWriter(ring, PassAt))
        {
            levels = ring.Measure(0, Capacity * Frames, 0);
            writer.AssertPassed();
        }

        // Solo cuenta lo que se leyó antes de que pasara el escritor: seis bloques de la primera vuelta, sin nada de la
        // segunda (que vuelve a empezar en la muestra 0, como tras un seek, con otro nivel).
        Assert.Equal(OldPeak, levels.Left.Peak);
        Assert.Equal(OldPeak, levels.Right.Peak);
        Assert.Equal(UntouchedBlocks * Frames, levels.Frames);
        Assert.Equal(OldPeak, levels.Left.Rms, 5);
    }

    [Fact]
    public void Weighted_power_drops_an_entry_the_writer_overwrote_while_it_was_copied()
    {
        var ring = new AudioLevelRing(Capacity);
        double power;
        using (var writer = new ForcedWriter(ring, PassAt))
        {
            power = ring.WeightedPower(0, Capacity * Frames);
            writer.AssertPassed();
        }

        Assert.Equal(OldPeak * OldPeak, power, 6);
    }

    private static LevelBlock Block(long start, float level)
    {
        double squares = (double)level * level * Frames;
        return new LevelBlock(start, Frames, level, level, level, level, squares, squares, Clipped: false, Weighted: squares);
    }

    /// <summary>
    /// El hilo escritor (el único que llama a <see cref="AudioLevelRing.Write"/>): escribe la primera vuelta al crearse y
    /// la segunda cuando el lector llega a la entrada <c>passAt</c>, mientras el lector espera.
    /// </summary>
    private sealed class ForcedWriter : IDisposable
    {
        // Solo contra cuelgues: el orden no depende de este plazo.
        private static readonly TimeSpan Hang = TimeSpan.FromSeconds(60);

        private readonly AudioLevelRing _ring;
        private readonly SemaphoreSlim _firstLap = new(0);
        private readonly SemaphoreSlim _go = new(0);
        private readonly SemaphoreSlim _done = new(0);
        private readonly Thread _thread;
        private int _passes;

        public ForcedWriter(AudioLevelRing ring, long passAt)
        {
            _ring = ring;
            _thread = new Thread(Run) { IsBackground = true, Name = "Escritor de niveles" };
            _thread.Start();
            Assert.True(_firstLap.Wait(Hang), "El escritor no escribió la primera vuelta.");
            ring.BeforeCopy = index =>
            {
                if (index != passAt || _passes++ > 0)
                {
                    return;
                }

                _go.Release();
                Assert.True(_done.Wait(Hang), "El escritor no dio la vuelta al anillo.");
            };
        }

        public void AssertPassed() => Assert.Equal(1, _passes);

        public void Dispose()
        {
            _ring.BeforeCopy = null;
            _go.Release();
            _thread.Join(Hang);
            _firstLap.Dispose();
            _go.Dispose();
            _done.Dispose();
        }

        private void Run()
        {
            for (int i = 0; i < Capacity; i++)
            {
                _ring.Write(Block(i * Frames, OldPeak));
            }

            _firstLap.Release();
            _go.Wait();
            if (_ring.BeforeCopy is null)
            {
                return;
            }

            // La segunda vuelta pisa todas las entradas: la que el lector está copiando y las más viejas que le quedan.
            for (int i = 0; i < Capacity; i++)
            {
                _ring.Write(Block(i * Frames, NewPeak));
            }

            _done.Release();
        }
    }
}
