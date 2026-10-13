namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// El criterio de fuga de identificadores sobre una serie de muestras (una cada <see cref="SampleCycles"/> aperturas),
/// sin nada del proceso: se prueba con series de fabricación (<c>HandleLeakCriterionTests</c>).
/// </summary>
internal static class HandleLeakCriterion
{
    /// <summary>Cada cuántas aperturas se cuentan los identificadores del proceso.</summary>
    public const int SampleCycles = 10;

    /// <summary>
    /// Por qué fallar con estas muestras, o nulo. Lo que no se cierra no vuelve a bajar: se mira el piso de cada muestra
    /// (el mínimo de ella y las que siguen), que no sube por un identificador que solo estaba abierto al contar, pero sí
    /// con cada uno que queda para siempre. Una fuga crece en todos los tramos, aunque sea una cada diez aperturas: la
    /// mediana de lo que sube el piso cada tramo llega a uno; si no hay fuga, no. El total sigue con su tope, contra una
    /// fuga a ráfagas.
    /// </summary>
    public static string? Find(int[] samples, string what = "Los identificadores del proceso")
    {
        int[] floor = new int[samples.Length];
        int lowest = int.MaxValue;
        for (int i = samples.Length - 1; i >= 0; i--)
        {
            lowest = Math.Min(lowest, samples[i]);
            floor[i] = lowest;
        }

        int[] steps = [.. floor.Zip(floor.Skip(1), (a, b) => b - a).Order()];
        if (steps.Length == 0)
        {
            return "No hubo muestras de identificadores.";
        }

        int median = steps[steps.Length / 2];
        int total = floor[^1] - floor[0];
        int measured = steps.Length * SampleCycles;
        if (median >= 1)
        {
            return $"{what} crecen en casi cada tramo de {SampleCycles} aperturas (mediana {median}; {string.Join(" ", samples)}).";
        }

        return total >= measured / 2
            ? $"{what} crecieron {total} en {measured} aperturas ({string.Join(" ", samples)})."
            : null;
    }

    /// <summary>
    /// Cuántos identificadores <c>Event</c> trae cada hilo, medido: lo que crecieron los eventos dividido por lo que
    /// crecieron los identificadores <c>Thread</c> al arrancar unos hilos que esperan. Cero si no hubo hilos nuevos (en
    /// Linux, donde ni los hilos ni los eventos del runtime son descriptores).
    /// </summary>
    public static double EventsPerThread(int eventsBefore, int threadsBefore, int eventsAfter, int threadsAfter)
    {
        int threads = threadsAfter - threadsBefore;
        return threads <= 0 ? 0 : Math.Max(0, eventsAfter - eventsBefore) / (double)threads;
    }

    /// <summary>
    /// Los eventos de cada muestra sin los que vienen con los hilos: <c>Event − k·Thread</c>, con <c>k</c> medido por
    /// <see cref="EventsPerThread"/>. El evento y el identificador de un hilo del runtime nacen y mueren juntos (se sueltan
    /// al finalizar el hilo), así que lo que el grupo de hilos sume o suelte se cancela; un evento que nadie cierra (un
    /// <c>ManualResetEvent</c>, el <c>AvailableWaitHandle</c> de un <c>SemaphoreSlim</c>, el <c>WaitHandle</c> de un
    /// <c>CancellationTokenSource</c>) queda.
    /// </summary>
    public static int[] UnthreadedEvents(int[] events, int[] threads, double perThread)
    {
        if (events.Length != threads.Length)
        {
            throw new ArgumentException("Hace falta una cuenta de hilos por muestra de eventos.", nameof(threads));
        }

        return [.. events.Zip(threads, (e, t) => (int)Math.Round(e - perThread * t, MidpointRounding.AwayFromZero))];
    }
}
