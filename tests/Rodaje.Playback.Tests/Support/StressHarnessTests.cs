namespace Rodaje.Playback.Tests.Support;

/// <summary>
/// El propio arnés de estrés: que su detector de «sin avance» falle de verdad con un hilo trabado y que una corrida de
/// cero vueltas no pase en verde. Sin reloj que medir: la prueba del hilo trabado avanza a mano el reloj del arnés, en
/// paso cerrado con el hilo sano, y lo que se comprueba es que la corrida falla, no cuánto tarda.
/// </summary>
public sealed class StressHarnessTests
{
    /// <summary>
    /// El reloj inyectado avanza 10 s por cada vuelta de vigilancia del arnés, y solo después de que el hilo sano
    /// saque un número: entre dos lecturas del reloj el sano avanzó siempre, aunque la máquina lo deje sin CPU, y el
    /// trabado, que no avanza nunca, pasa del tope de 30 s en cuatro vueltas. Si se quita el detector de «sin avance», la
    /// corrida termina por <see cref="Stress.Deadline"/> (24 vueltas) con otro mensaje y la prueba falla.
    /// </summary>
    [Fact]
    public void Un_hilo_trabado_que_no_saca_numeros_se_da_por_interbloqueo()
    {
        using var unblock = new ManualResetEventSlim();
        using var tick = new SemaphoreSlim(0);
        using var drew = new SemaphoreSlim(0);
        TimeSpan now = TimeSpan.Zero;
        TimeSpan Clock()
        {
            // Una vuelta del sano por cada lectura: la suelta y espera a que haya sacado su número.
            tick.Release();
            Assert.True(drew.Wait(TimeSpan.FromMinutes(1)), "El hilo sano no sacó su número.");
            now += TimeSpan.FromSeconds(10);
            return now;
        }

        try
        {
            var error = Assert.ThrowsAny<Exception>(() => Stress.Run(
                5,
                TimeSpan.FromSeconds(30),
                Clock,
                ("sano", (random, stop) =>
                {
                    while (true)
                    {
                        try
                        {
                            tick.Wait(stop);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        _ = random.Next();
                        drew.Release();
                    }
                }),
                ("trabado", (random, stop) =>
                {
                    _ = random.Next();
                    // Sin mirar la señal de parada, como un interbloqueo.
                    unblock.Wait(CancellationToken.None);
                })));

            Assert.Contains("Semilla 5", error.Message, StringComparison.Ordinal);
            Assert.Contains("Posible interbloqueo: trabado no avanzó", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sano", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            unblock.Set();
        }
    }

    [Fact]
    public void Una_corrida_sin_ninguna_vuelta_falla()
    {
        var error = Assert.ThrowsAny<Exception>(() => Stress.Run(
            6,
            ("conductor", (_, stop) => stop.WaitHandle.WaitOne(0)),
            ("ayudante", (_, _) => { })));

        Assert.Contains("Semilla 6", error.Message, StringComparison.Ordinal);
        Assert.Contains("La corrida no avanzó", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Una_corrida_que_avanza_y_termina_pasa()
    {
        int done = 0;
        Stress.Run(
            7,
            ("conductor", (random, stop) =>
            {
                for (int i = 0; i < 200 && !stop.IsCancellationRequested; i++)
                {
                    _ = random.Next(10);
                }

                Volatile.Write(ref done, 1);
            }),
            // Un ayudante que no llega a dar ninguna vuelta porque el conductor ya terminó no es un fallo.
            ("ayudante", (random, stop) =>
            {
                while (Volatile.Read(ref done) == 0 && !stop.IsCancellationRequested)
                {
                    Thread.SpinWait(random.Next(1, 100));
                }
            }));
    }

    /// <summary>
    /// Regresión: entre «queda algún hilo vivo» y «tomar el primero vivo» podían terminar todos, y <c>First</c> lanzaba
    /// «Sequence contains no matching element» (BufferedAudioSourceStressTests, semilla 11, en Linux). Muchas corridas
    /// cortas, cuyos hilos terminan justo cuando el arnés los mira.
    /// </summary>
    [Fact]
    public void Hilos_que_terminan_mientras_el_arnes_los_mira_no_lo_rompen()
    {
        for (int run = 0; run < 300; run++)
        {
            Stress.Run(
                run,
                ("uno", (random, _) => random.Next()),
                ("otro", (random, _) => random.Next()),
                ("tercero", (random, _) => random.Next()));
        }
    }

    /// <summary>
    /// La «Semilla N» de un fallo repite la corrida en otro proceso: el <see cref="Random"/> de cada hilo depende solo de
    /// la semilla y del índice. Regresión: salía de <see cref="HashCode.Combine{T1, T2}"/>, cuya semilla es aleatoria por
    /// proceso, y estos valores fijados cambiaban en cada ejecución.
    /// </summary>
    [Fact]
    public void La_semilla_de_cada_hilo_es_la_misma_en_cualquier_proceso()
    {
        Assert.Equal(1_545_029_486, Stress.WorkerSeed(5, 0));
        Assert.Equal(1_325_234_912, Stress.WorkerSeed(5, 1));

        int[] first = new int[2];
        Stress.Run(
            5,
            ("primero", (random, _) => first[0] = random.Next()),
            ("segundo", (random, _) => first[1] = random.Next()));
        Assert.Equal([new Random(1_545_029_486).Next(), new Random(1_325_234_912).Next()], first);
    }
}
