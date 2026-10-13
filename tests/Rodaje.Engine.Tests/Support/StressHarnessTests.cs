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
}
