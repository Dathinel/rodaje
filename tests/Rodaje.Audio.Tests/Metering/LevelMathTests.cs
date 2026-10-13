using Rodaje.Audio.Metering;

namespace Rodaje.Audio.Tests.Metering;

public class LevelMathTests
{
    private const int Rate = 48_000;

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, -6.0206)]
    [InlineData(0.1, -20.0)]
    [InlineData(0.001, -60.0)]
    [InlineData(-0.5, -6.0206)]
    public void LosDecibeliosSonVeinteVecesElLogaritmoDeLaAmplitud(double amplitude, double expected)
    {
        Assert.Equal(expected, Decibels.FromAmplitude(amplitude), 3);
        Assert.Equal(Math.Abs(amplitude), Decibels.ToAmplitude(expected), 4);
    }

    [Fact]
    public void ElSilencioEsMenosInfinitoYVuelveACero()
    {
        Assert.Equal(double.NegativeInfinity, Decibels.FromAmplitude(0));
        Assert.Equal(double.NegativeInfinity, Decibels.FromAmplitude(1e-7));
        Assert.Equal(double.NegativeInfinity, Decibels.FromAmplitude(double.NaN));
        Assert.Equal(0, Decibels.ToAmplitude(double.NegativeInfinity));
    }

    [Fact]
    public void LaEscalaEsMonotonaConLasMarcasDeIec()
    {
        Assert.Equal(0, MeterScale.Deflection(double.NegativeInfinity));
        Assert.Equal(0, MeterScale.Deflection(-90));
        Assert.Equal(0.5, MeterScale.Deflection(-20), 9);
        Assert.Equal(0.3, MeterScale.Deflection(-30), 9);
        Assert.Equal(1, MeterScale.Deflection(0));
        Assert.Equal(1, MeterScale.Deflection(3));
        Assert.Equal(0.85, MeterScale.Deflection(-6), 9);

        double previous = -1;
        for (double db = -80; db <= 1; db += 0.25)
        {
            double value = MeterScale.Deflection(db);
            Assert.InRange(value, previous, 1);
            previous = value;
        }

        Assert.Equal([0, -3, -6, -12, -18, -24, -36, -48], MeterScale.Ticks);
    }

    [Fact]
    public void ElPicoYElRmsDeUnSenoSonLosDeLosLibros()
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        float[] mix = Sine(Rate, 997, 0.5, Rate, rightGain: 0.5);

        analyzer.Process(mix, 0);
        AudioLevels levels = ring.Measure(0, Rate, 0);

        Assert.Equal(0.5, levels.Left.Peak, 3);
        Assert.Equal(0.25, levels.Right.Peak, 3);
        Assert.Equal(0.5 / Math.Sqrt(2), levels.Left.Rms, 3);
        Assert.Equal(0.25 / Math.Sqrt(2), levels.Right.Rms, 3);
        Assert.Equal(-6.02 - 3.01, levels.Left.RmsDecibels, 1);
        Assert.Equal(Rate, levels.Frames);
        Assert.False(levels.Clipped);
        Assert.Equal(Rate / AudioLevelAnalyzer.DefaultBlockFrames, ring.Count);
    }

    [Fact]
    public void ElPicoRealVeLoQueHayEntreMuestras()
    {
        // Un seno a fs/4 con fase de 45°: todas las muestras valen ±0,707 pero la onda llega a 1.
        float[] mix = new float[4800 * 2];
        for (int i = 0; i < 4800; i++)
        {
            float value = (float)Math.Sin((Math.PI / 2 * i) + (Math.PI / 4));
            mix[i * 2] = value;
            mix[(i * 2) + 1] = value * 0.5f;
        }

        var ring = new AudioLevelRing();
        new AudioLevelAnalyzer(ring).Process(mix, 0);
        AudioLevels levels = ring.Measure(100, 4800, 4800);

        Assert.Equal(-3.01, levels.Left.PeakDecibels, 1);
        Assert.InRange(levels.Left.TruePeakDecibels, -0.3, 0.3);
        Assert.InRange(levels.Right.TruePeakDecibels, -6.3, -5.7);
    }

    [Fact]
    public void ElPicoRealDeUnaSenalLentaEsSuPicoDeMuestras()
    {
        var ring = new AudioLevelRing();
        new AudioLevelAnalyzer(ring).Process(Sine(Rate / 2, 100, 0.8, Rate), 0);

        AudioLevels levels = ring.Measure(1000, Rate / 2, 0);

        Assert.InRange(levels.Left.TruePeak, levels.Left.Peak, levels.Left.Peak * 1.002f);
    }

    [Fact]
    public void LaSaturacionSeMarcaConLaMezclaAntesDelLimitador()
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        float[] limited = new float[1024 * 2];
        float[] raw = new float[1024 * 2];
        Array.Fill(limited, 0.95f);
        Array.Fill(raw, 0.95f);
        raw[(700 * 2) + 1] = 1.4f;

        analyzer.Process(limited, 0, raw);

        Assert.False(ring.Measure(0, 640, 640).Clipped);
        Assert.True(ring.Measure(640, 768, 768).Clipped);
        Assert.False(ring.Measure(768, 1024, 1024).Clipped);
    }

    [Fact]
    public void CadaBloqueGuardaSuMuestraDeLaLineaDeTiempo()
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        float[] silence = new float[1000 * 2];
        float[] burst = new float[200 * 2];
        Array.Fill(burst, 0.5f);

        // Mezclado por delante: hasta la muestra 10 000 + 1200, con un golpe en [10 000 + 1000, +1200).
        analyzer.Process(new float[10_000 * 2], 0);
        analyzer.Process(silence, 10_000);
        analyzer.Process(burst, 11_000);

        // Lo que suena todavía está antes del golpe: los medidores no lo muestran aunque ya esté mezclado. El bloque
        // [10 880, 11 008) ya contiene el comienzo del golpe: la resolución es de un bloque (2,7 ms).
        Assert.Equal(0, ring.Measure(10_500, 10_870, 0).Left.Peak);
        Assert.Equal(0.5f, ring.Measure(10_990, 11_010, 0).Left.Peak);
        Assert.Equal(0, ring.Measure(11_300, 11_400, 0).Frames);
    }

    [Fact]
    public void UnBloqueNuevoTapaLoQueLaMezclaVolvioAEscribir()
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        float[] loud = new float[2048 * 2];
        Array.Fill(loud, 0.8f);
        float[] quiet = new float[1024 * 2];
        Array.Fill(quiet, 0.1f);

        analyzer.Process(loud, 0);

        // Una edición: la mezcla vuelve a escribir desde la muestra 1000 (a mitad de un bloque) con otra cosa.
        analyzer.Process(quiet, 1000);

        Assert.Equal(0.8f, ring.Measure(0, 900, 0).Left.Peak);
        Assert.Equal(0.1f, ring.Measure(1100, 2000, 2000).Left.Peak);
        Assert.Equal(0, ring.Measure(2030, 2040, 2040).Frames);

        // Seek hacia atrás: lo nuevo está antes de todo lo anterior y también manda.
        analyzer.Process(quiet, 0);
        Assert.Equal(0.1f, ring.Measure(0, 1000, 1000).Left.Peak);
    }

    [Fact]
    public void ElRmsSoloMiraSuVentana()
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        float[] loud = new float[4800 * 2];
        Array.Fill(loud, 0.5f);
        analyzer.Process(loud, 0);
        analyzer.Process(new float[4800 * 2], 4800);

        // La ventana empieza después del bloque [4736, 4864), el último con algo de la parte fuerte.
        AudioLevels half = ring.Measure(9500, 9600, 4864);
        AudioLevels mixed = ring.Measure(9500, 9600, 0);

        Assert.Equal(0, half.Left.Rms);
        Assert.Equal(0.5 * Math.Sqrt(4800.0 / 9600), mixed.Left.Rms, 3);
    }

    [Fact]
    public void LeerMientrasSeEscribeNuncaDaBloquesMezclados()
    {
        // Cada bloque lleva valores que dependen de su posición: un bloque leído a medias no cuadraría.
        // El lector va en un hilo propio, no en el pool: con pocos núcleos y otras pruebas en paralelo, una tarea del
        // pool podía no arrancar antes de que acabara el escritor. Se escribe al menos 400 ms y hasta tener 100 lecturas.
        // El escritor no se adelanta más de MaxLead bloques a la última vuelta del lector. Suelto, con el runner de Windows
        // saturado (13-oct, dos veces: «solo 0 lecturas en 31020 ms» y «solo 1 lecturas en 45102 ms», sin ningún bloque
        // mezclado), el escritor giraba 30 s sin pausa mientras el lector casi no corría, y lo poco que corría lo perdía: si
        // lo desalojan entre tomar Count y medir, el escritor da la vuelta al anillo y el lector descarta (bien) lo pisado.
        // Con el tope, el escritor espera al lector (y le deja el núcleo) en vez de dejarlo atrás. MaxLead pasa la capacidad
        // del anillo, así que a veces el escritor sigue pisando lo que se lee y el descarte se sigue ejercitando. Las 100
        // lecturas no tienen plazo; los 120 s son solo contra cuelgues.
        const int MinimumReads = 100;
        const int MaxLead = 96;
        var ring = new AudioLevelRing(64);
        var analyzer = new AudioLevelAnalyzer(ring, 16);
        var minimum = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        bool stop = false;
        long torn = 0;
        long reads = 0;
        long attempts = 0;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                long count = ring.Count;
                long end = Math.Max(16, (count - 2) * 16);
                AudioLevels levels = ring.Measure(end - 16, end, end - 16);
                if (levels.Frames > 0)
                {
                    float expected = Value(end - 16);
                    if (levels.Left.Peak != expected || levels.Right.Peak != expected * 0.5f)
                    {
                        Interlocked.Increment(ref torn);
                    }

                    Interlocked.Increment(ref reads);
                }

                Interlocked.Increment(ref attempts);
            }
        })
        { IsBackground = true, Name = "Lector de niveles" };
        reader.Start();

        try
        {
            float[] block = new float[16 * 2];
            long position = 0;
            long lead = 0;
            long seen = 0;
            while ((minimum.ElapsedMilliseconds < 400 || Interlocked.Read(ref reads) < MinimumReads) && !deadline.IsCancellationRequested)
            {
                float value = Value(position);
                for (int i = 0; i < 16; i++)
                {
                    block[i * 2] = value;
                    block[(i * 2) + 1] = value * 0.5f;
                }

                analyzer.Process(block, position);
                position += 16;
                if (++lead >= MaxLead)
                {
                    // Espera a que el lector termine otra vuelta; SpinWait acaba cediendo el núcleo con Sleep(1).
                    var spin = default(SpinWait);
                    while (Interlocked.Read(ref attempts) == seen && !deadline.IsCancellationRequested)
                    {
                        spin.SpinOnce();
                    }

                    seen = Interlocked.Read(ref attempts);
                    lead = 0;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            reader.Join();
        }

        Assert.Equal(0, torn);
        Assert.True(reads >= MinimumReads, $"solo {reads} lecturas de {attempts} vueltas en {minimum.ElapsedMilliseconds} ms");

        static float Value(long start) => ((start / 16) % 997 + 1) / 1000f;
    }

    private static float[] Sine(int frames, double hz, double amplitude, int rate, double rightGain = 1)
    {
        float[] mix = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double value = amplitude * Math.Sin(2 * Math.PI * hz * i / rate);
            mix[i * 2] = (float)value;
            mix[(i * 2) + 1] = (float)(value * rightGain);
        }

        return mix;
    }
}
