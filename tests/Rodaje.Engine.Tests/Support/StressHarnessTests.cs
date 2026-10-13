namespace Rodaje.Engine.Tests.Support;

/// <summary>
/// El propio arnés de estrés: que su detector de «sin avance» falle de verdad con un hilo trabado y que una corrida de
/// cero vueltas no pase en verde. Sin reloj que medir: el tope de «sin avance» baja a unos cientos de milisegundos y lo
/// que se comprueba es que la corrida falla, no cuánto tarda.
/// </summary>
public sealed class StressHarnessTests
{
    [Fact]
    public void Un_hilo_trabado_que_no_saca_numeros_se_da_por_interbloqueo()
    {
        using var unblock = new ManualResetEventSlim();
        try
        {
            var error = Assert.ThrowsAny<Exception>(() => Stress.Run(
                5,
                TimeSpan.FromMilliseconds(300),
                ("sano", (random, stop) =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        _ = random.Next();
                        Thread.Sleep(1);
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
}
