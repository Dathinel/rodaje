namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// La vigilancia de los identificadores <c>Event</c> sobre series de fabricación (tarea 308). Desde #278 la cuenta de la
/// apertura de proyectos apartaba todo <c>Event</c> junto con los hilos, porque el grupo de hilos los crea y los suelta con
/// cada hilo; así un evento retenido por apertura no lo veía nadie. Ahora se vigilan aparte, sin los de los hilos.
/// </summary>
public sealed class HandleLeakCriterionTests
{
    /// <summary>Tres identificadores Event por cada identificador Thread, como un hilo del runtime en Windows.</summary>
    private const int PerThread = 3;

    /// <summary>
    /// Los identificadores Thread del grupo de hilos en la serie de #264: crece en cuatro tramos (unos 36 hilos) y para.
    /// </summary>
    private static readonly int[] PoolGrowth = [40, 50, 60, 70, 76, 76, 77, 76, 76, 76];

    [Fact]
    public void Pool_threads_that_come_and_go_with_their_events_are_not_a_leak()
    {
        int[] events = Events(PoolGrowth, perOpening: 0);

        // Sin restar los de los hilos, el criterio fallaría: por eso #278 los apartó todos.
        Assert.NotNull(HandleLeakCriterion.Find(events));
        Assert.Null(HandleLeakCriterion.Find(HandleLeakCriterion.UnthreadedEvents(events, PoolGrowth, PerThread)));

        // Hilos que entran y salen en cada muestra.
        int[] oscillating = [40, 52, 41, 55, 40, 60, 42, 51, 40, 58];
        Assert.Null(HandleLeakCriterion.Find(HandleLeakCriterion.UnthreadedEvents(Events(oscillating, 0), oscillating, PerThread)));
    }

    [Fact]
    public void An_event_kept_per_opening_is_a_leak_even_while_pool_threads_change()
    {
        int[] perOpening = Events(PoolGrowth, perOpening: 1);
        Assert.NotNull(HandleLeakCriterion.Find(HandleLeakCriterion.UnthreadedEvents(perOpening, PoolGrowth, PerThread)));

        // Uno cada diez aperturas también, con hilos que entran y salen.
        int[] oscillating = [40, 52, 41, 55, 40, 60, 42, 51, 40, 58];
        int[] perTen = [.. Events(oscillating, 0).Select((e, i) => e + i)];
        Assert.NotNull(HandleLeakCriterion.Find(HandleLeakCriterion.UnthreadedEvents(perTen, oscillating, PerThread)));
    }

    /// <summary>
    /// <c>k</c> se mide en el proceso y puede no ser exacto (un hilo del grupo que nace mientras se mide): un error de un
    /// 10 % no convierte el crecimiento del grupo en fuga ni esconde un evento por apertura.
    /// </summary>
    [Fact]
    public void A_slightly_wrong_events_per_thread_keeps_both_verdicts()
    {
        foreach (double measured in new[] { PerThread * 0.9, PerThread * 1.1 })
        {
            Assert.Null(HandleLeakCriterion.Find(HandleLeakCriterion.UnthreadedEvents(Events(PoolGrowth, 0), PoolGrowth, measured)));
            Assert.NotNull(HandleLeakCriterion.Find(HandleLeakCriterion.UnthreadedEvents(Events(PoolGrowth, 1), PoolGrowth, measured)));
        }
    }

    [Fact]
    public void Events_per_thread_is_measured_from_the_growth_of_both()
    {
        Assert.Equal(2.0, HandleLeakCriterion.EventsPerThread(100, 10, 148, 34));
        Assert.Equal(0.0, HandleLeakCriterion.EventsPerThread(0, 0, 0, 0));
        Assert.Equal(0.0, HandleLeakCriterion.EventsPerThread(100, 10, 90, 34));
        Assert.Equal([10, 8, -1], HandleLeakCriterion.UnthreadedEvents([30, 30, 9], [10, 11, 5], 2));
        Assert.Throws<ArgumentException>(() => HandleLeakCriterion.UnthreadedEvents([1, 2], [1], 1));
    }

    /// <summary>Los Event de cada muestra: los de los hilos, unos fijos y los que se retienen por apertura.</summary>
    private static int[] Events(int[] threads, int perOpening) =>
        [.. threads.Select((t, i) => 200 + (PerThread * t) + (perOpening * HandleLeakCriterion.SampleCycles * i))];
}
