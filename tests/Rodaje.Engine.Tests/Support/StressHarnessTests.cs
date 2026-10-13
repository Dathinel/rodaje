namespace Rodaje.Engine.Tests.Support;

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
    /// corrida termina por <see cref="Stress.Deadline"/> (24 vueltas) con otro mensaje y la prueba falla. El mensaje trae
    /// además las pilas de los hilos, con el trabado en <see cref="StuckWorker"/>.
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
                    StuckWorker(unblock);
                })));

            Assert.Contains("Semilla 5", error.Message, StringComparison.Ordinal);
            Assert.Contains("Posible interbloqueo: trabado no avanzó", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sano no avanzó", error.Message, StringComparison.Ordinal);

            // Las pilas dicen dónde estaba: el trabado, por su nombre, en su espera.
            // Si la instantánea no se pudo tomar (en Linux sin privilegios createdump puede no adjuntarse, o venció su
            // plazo), la prueba falla diciendo por qué, con el texto de ThreadDump.Capture, y no con un índice fuera de rango.
            int at = error.Message.IndexOf("Pilas de los hilos", StringComparison.Ordinal);
            Assert.True(at >= 0, $"El mensaje del arnés no trae las pilas de los hilos:{Environment.NewLine}{error.Message}");
            string stacks = error.Message[at..];
            int stuckAt = stacks.IndexOf("--- [trabado]", StringComparison.Ordinal);
            if (stuckAt < 0)
            {
                Assert.Fail($"La instantánea de pilas no trae el hilo trabado; ThreadDump.Capture devolvió:{Environment.NewLine}{stacks}");
            }

            string stuck = stacks[stuckAt..];
            stuck = stuck[..(stuck.IndexOf("\n---", StringComparison.Ordinal) is int next and > 0 ? next : stuck.Length)];
            Assert.Contains(nameof(StuckWorker), stuck, StringComparison.Ordinal);
        }
        finally
        {
            unblock.Set();
        }
    }

    /// <summary>El cuerpo del hilo trabado, con nombre propio para buscarlo en las pilas del mensaje.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void StuckWorker(ManualResetEventSlim unblock) =>
        // Sin mirar la señal de parada, como un interbloqueo.
        unblock.Wait(CancellationToken.None);

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
    /// Con el presupuesto vencido (cero), el arnés no enciende la parada mientras el hilo que recorre sus vueltas con
    /// <see cref="Stress.Steps"/> no hizo su mínimo: los demás hilos siguen pisándolo. Antes la parada llegaba en la primera
    /// vuelta de vigilancia (100 ms) y la prueba podía pasar en verde casi sin haber corrido. Sin reloj que medir: lo que
    /// se comprueba es que la parada no llegó, y la espera de 300 ms solo le da tiempo al arnés de equivocarse.
    /// </summary>
    [Fact]
    public void Con_el_presupuesto_vencido_la_parada_espera_al_minimo()
    {
        const int Count = 400;
        int done = 0;
        bool stoppedBeforeMinimum = false;
        int helper = 0;
        Stress.Run(
            8,
            Stress.Stall,
            () => TimeSpan.Zero,
            TimeSpan.Zero,
            ("conductor", (random, stop) =>
            {
                foreach (int i in Stress.Steps(Count, stop))
                {
                    _ = random.Next(10);
                    if (i == 0)
                    {
                        stoppedBeforeMinimum = stop.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
                    }

                    done = i + 1;
                }
            }),
            ("ayudante", (random, stop) =>
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = random.Next(10);
                    Interlocked.Increment(ref helper);
                    Thread.Yield();
                }
            }));

        Assert.False(stoppedBeforeMinimum, "El presupuesto encendió la parada antes de que el conductor hiciera su mínimo.");
        Assert.InRange(done, Stress.MinimumSteps(Count), Count);
        Assert.True(Volatile.Read(ref helper) > 0);
    }

    /// <summary>
    /// Un hilo que llega a sus vueltas con la parada del presupuesto ya encendida (empezó tarde) hace igual su mínimo, y
    /// ni una más. Determinista: espera a la parada antes de pedir las vueltas.
    /// </summary>
    [Fact]
    public void Un_hilo_que_empieza_con_el_presupuesto_vencido_hace_su_minimo()
    {
        const int Count = 400;
        int done = 0;
        Stress.Run(
            10,
            Stress.Stall,
            () => TimeSpan.Zero,
            TimeSpan.Zero,
            ("tardío", (random, stop) =>
            {
                _ = random.Next(10);
                stop.WaitHandle.WaitOne();
                foreach (int i in Stress.Steps(Count, stop))
                {
                    _ = random.Next(10);
                    done = i + 1;
                }
            }));

        Assert.Equal(Stress.MinimumSteps(Count), done);
    }

    /// <summary>Un hilo que sale de sus vueltas antes del mínimo sin que nadie fallara hace fallar la corrida.</summary>
    [Fact]
    public void Un_hilo_que_sale_antes_del_minimo_falla()
    {
        var error = Assert.ThrowsAny<Exception>(() => Stress.Run(
            9,
            ("conductor", (random, stop) =>
            {
                foreach (int i in Stress.Steps(Stress.Iterations(400), stop))
                {
                    _ = random.Next(10);
                    if (i == 9)
                    {
                        break;
                    }
                }
            })));

        Assert.Contains("Semilla 9", error.Message, StringComparison.Ordinal);
        Assert.Contains($"conductor hizo 9 de {Stress.Iterations(400)} vueltas; el mínimo es {Stress.MinimumSteps(Stress.Iterations(400))}.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void El_minimo_es_la_cuarta_parte_de_las_vueltas_de_la_ci()
    {
        Assert.Equal(1, Stress.MinimumSteps(1));
        Assert.Equal(3, Stress.MinimumSteps(Stress.Iterations(12)));
        Assert.Equal(1000, Stress.MinimumSteps(Stress.Iterations(4000)));
        Assert.Throws<InvalidOperationException>(() => Stress.Steps(10, CancellationToken.None).GetEnumerator().MoveNext());
    }
}
