namespace Rodaje.App.Tests.Support;

/// <summary>
/// La vigilancia del fuzz de proyectos extremos (tarea 321) con un reloj falso y pausas del GC simuladas: no corta un
/// tramo que se fue entero en el GC (la semilla 20261018 en la CI de Windows: «abrir» 9,2 s con 9,3 s de pausa) y sí uno
/// que no avanza sin GC, en el mismo plazo de siempre.
/// </summary>
public sealed class StallWatchTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private TimeSpan _clock;
    private TimeSpan _gc;

    private StallWatch NewWatch() => new(Limit, () => _clock, () => _gc);

    [Fact]
    public void UnTramoQueEsTodoGc_NoSeCorta()
    {
        StallWatch watch = NewWatch();
        Assert.False(watch.Stalled(1));

        _clock += TimeSpan.FromSeconds(60);
        _gc += TimeSpan.FromSeconds(59);

        Assert.Equal(TimeSpan.FromSeconds(1), watch.Idle(1));
        Assert.False(watch.Stalled(1));
    }

    [Fact]
    public void SinAvanceNiGc_SeCortaPasadoElPlazo()
    {
        StallWatch watch = NewWatch();
        Assert.False(watch.Stalled(1));

        _clock += Limit;
        Assert.False(watch.Stalled(1));
        _clock += TimeSpan.FromMilliseconds(1);
        _gc += TimeSpan.FromMilliseconds(0.5);

        Assert.True(watch.Stalled(1));
    }

    [Fact]
    public void ElAvance_EmpiezaLaCuentaDeNuevo()
    {
        StallWatch watch = NewWatch();
        Assert.False(watch.Stalled(1));
        _clock += TimeSpan.FromSeconds(14);
        Assert.False(watch.Stalled(2));

        _clock += TimeSpan.FromSeconds(14);

        Assert.False(watch.Stalled(2));
        _clock += TimeSpan.FromSeconds(2);
        Assert.True(watch.Stalled(2));
    }

    [Fact]
    public Task UnCasoParadoEnElGc_Termina_YUnoColgado_FallaConSuSemilla() => OwnThread.Run(() =>
    {
        // Con RODAJE_FUZZ_SEMILLA solo corre esa semilla: se usa ella.
        int seed = Rodaje.Tests.Fuzz.FuzzRunner.OnlySeed ?? 7;
        // Cada vistazo de la vigilancia avanza el reloj falso 10 s: sin avance, el segundo ya pasa del plazo.
        using var release = new ManualResetEventSlim();
        int looks = 0;
        var watch = new StallWatch(Limit, () => _clock += TimeSpan.FromSeconds(10), () =>
        {
            _gc = _clock;

            // El caso parado sigue hasta que la vigilancia lo miró de sobra.
            if (++looks > 20)
            {
                release.Set();
            }

            return _gc;
        });

        int done = StallWatch.RunCases("vigilancia", seed, cases: 1, budget: TimeSpan.FromSeconds(10), minimumCases: 1, () => 0, () => "", watch,
            (_, _) => release.Wait());
        Assert.Equal(1, done);

        using var hung = new ManualResetEventSlim();
        try
        {
            var error = Assert.Throws<Xunit.Sdk.XunitException>(() => StallWatch.RunCases("vigilancia", seed, cases: 1, budget: TimeSpan.FromSeconds(10), minimumCases: 1,
                () => 0, () => "Iba por «abrir».", new StallWatch(Limit, () => _clock += TimeSpan.FromSeconds(10), () => _gc), (_, _) => hung.Wait()));
            Assert.StartsWith($"vigilancia: el caso de la semilla {seed} no avanzó en 15 s sin contar el GC", error.Message, StringComparison.Ordinal);
            Assert.EndsWith("Iba por «abrir».", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            hung.Set();
        }
    });
}
