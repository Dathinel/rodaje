using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Rodaje.App.Tests.Support;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// El fuzz de la sesión del editor (<see cref="SessionFuzzer"/>): 200 semillas en la CI, siempre todas, y 20 000 con
/// <c>RODAJE_PRUEBAS_LARGAS=1</c>. Un fallo dice su semilla; se reproduce con <see cref="Semilla_Reproduce"/> y, una vez
/// corregido, la semilla queda en <see cref="SemillasQueFallaron_YaNoFallan"/>.
/// </summary>
[Trait("Categoria", "Fuzz")]
public class SessionFuzzTests(ITestOutputHelper output)
{
    /// <summary>Acciones por semilla en la CI.</summary>
    private const int CiSteps = 120;

    /// <summary>Acciones por semilla en la corrida larga (cada una con 5 000 acciones sería un día de máquina).</summary>
    internal const int LongSteps = 400;

    /// <summary>Acciones de las secuencias hondas.</summary>
    internal const int DeepSteps = 5_000;

    internal const int LongFirstSeed = 1_000_000;

    internal const int LongSeeds = 20_000;

    /// <summary>Semillas por caso largo: unos 80 s en un solo hilo en Debug en un PC de escritorio.</summary>
    internal const int LongBatchSeeds = 200;

    public static TheoryData<int> LongBatches
    {
        get
        {
            var data = new TheoryData<int>();
            for (int first = LongFirstSeed; first < LongFirstSeed + LongSeeds; first += LongBatchSeeds)
            {
                data.Add(first);
            }

            return data;
        }
    }

    public static TheoryData<int> DeepSeeds
    {
        get
        {
            var data = new TheoryData<int>();
            for (int seed = 2_000_000; seed < 2_000_010; seed++)
            {
                data.Add(seed);
            }

            return data;
        }
    }

    /// <summary>
    /// Corren siempre las 200, sin tope de tiempo: con un tope, un runner lento pasaba la prueba con menos semillas sin que
    /// nadie lo notara. En Linux tardan unos 18 s; si un día tardan demasiado, se bajan aquí, no en silencio. Como todas las
    /// de esta clase, en un hilo propio (<see cref="OwnThread"/>): la espera del fuzz no ocupa un hilo del pool.
    /// </summary>
    [Fact]
    public Task DoscientasSemillas_SinFallos() => OwnThread.Run(() => RunSeeds(0, 200, CiSteps));

    /// <summary>
    /// Las 20 000 semillas largas, en lotes de <see cref="LongBatchSeeds"/> y varias a la vez. Un solo caso de 20 000 tardaba
    /// más de dos horas y el <c>--blame-hang-timeout</c> de la CI (10 min en ci.yml, 15 en la nocturna), que cuenta por
    /// prueba y no por avance, mataba el proceso a los 15 min: vstest lo informaba como «Test host process crashed».
    /// </summary>
    [LongFuzzTheory]
    [MemberData(nameof(LongBatches))]
    public Task VeinteMilSemillas_SinFallos(int first) => OwnThread.Run(() => RunSeedsInParallel(first, LongBatchSeeds, LongSteps));

    /// <summary>Unas pocas secuencias de 5 000 acciones, con el historial y los compuestos bien hondos; una por caso.</summary>
    [LongFuzzTheory]
    [MemberData(nameof(DeepSeeds))]
    public Task SecuenciasDeCincoMilAcciones_SinFallos(int seed) => OwnThread.Run(() => RunSeeds(seed, 1, DeepSteps));

    /// <summary>Para reproducir una semilla del mensaje de un fallo: cámbiala aquí y quita el Skip.</summary>
    [Theory(Skip = "Solo para reproducir a mano una semilla que falló.")]
    [InlineData(0, CiSteps)]
    public Task Semilla_Reproduce(int seed, int steps) => OwnThread.Run(() => SessionFuzzer.Run(seed, steps));

    /// <summary>
    /// Las semillas que encontraron defectos, con las acciones que hacían falta para llegar a ellos. Si el generador
    /// cambia, una semilla puede dejar de llegar a la acción que probaba y pasar en vacío: por eso se comprueba que esa
    /// acción sigue en el recorrido. Si falla esto (y no el fuzz), la semilla ya no sirve: búscale otra con
    /// <see cref="SessionFuzzExplore"/> o quítala (el defecto sigue cubierto en <see cref="SessionFuzzRegressionTests"/>).
    /// </summary>
    [Theory]
    [MemberData(nameof(FailedSeeds))]
    public Task SemillasQueFallaron_YaNoFallan(int seed, int steps, string action) => OwnThread.Run(() =>
    {
        string trace = SessionFuzzer.Run(seed, steps);
        Assert.True(Regex.IsMatch(trace, action, RegexOptions.Multiline),
            $"La semilla {seed} ya no hace «{action}»: el generador cambió y la semilla prueba otra cosa.");
    });

    /// <summary>
    /// Semilla, acciones y la acción defectuosa como expresión regular sobre el recorrido (una línea por acción, ver
    /// <see cref="SessionFuzzer.Run"/>); el defecto de cada una, en <see cref="SessionFuzzRegressionTests"/>.
    /// </summary>
    public static TheoryData<int, int, string> FailedSeeds => new()
    {
        // «Añadir efecto» aceptaba el tipo reservado rodaje.speed.
        { 35, CiSteps, @"^\d+\. Effect añadir rodaje\.speed " },
        // Un gesto cancelado quitaba la barrera de fusión: el recorte siguiente se fundía con uno viejo.
        { 920, 300, @"^\d+\. Gesture .* cancelado$" },
        // Un gesto cancelado vaciaba lo que se podía rehacer.
        { 6, 300, @"^\d+\. Gesture .* cancelado$" },
        // Q con ripple por pista dejaba atrás al audio enlazado que empezaba en el cursor.
        { 651, 300, @"^\d+\. DeleteSide izquierda Track (magnética=True|.*auto=True)" },
        // Q con ripple por pista desfasaba enlazados que empezaban en instantes distintos.
        { 1007, 300, @"^\d+\. DeleteSide izquierda Track (magnética=True|.*auto=True)" },
        // Aplicar un seguimiento cuyo tipo no coincidía con sus cuadros lanzaba. (En una vuelta de la CI de Windows agotó el
        // tope de 120 s «por el paso 240 de 300: Select», que no espera nada; ver SessionFuzzWatchTests.)
        { 9335, 300, @"^\d+\. Tracking aplicar .*no son de este tipo de seguimiento" },
        // Deshacer el compuesto de fuera desde uno anidado dejaba en la ruta una secuencia borrada; volver a ella lanzaba.
        { 1006161, LongSteps, @"^\d+\. LeaveCompound" },
        { 1016412, LongSteps, @"^\d+\. LeaveCompound" },
    };

    private void RunSeeds(int first, int count, int steps)
    {
        var clock = Stopwatch.StartNew();
        for (int seed = first; seed < first + count; seed++)
        {
            SessionFuzzer.Run(seed, steps);
        }

        output.WriteLine($"{count} semillas de {steps} acciones en {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    /// <summary>Cada semilla es independiente (su hilo de interfaz y su carpeta); si fallan varias, sale la menor.</summary>
    private void RunSeedsInParallel(int first, int count, int steps)
    {
        var clock = Stopwatch.StartNew();
        var failures = new ConcurrentDictionary<int, ExceptionDispatchInfo>();
        Parallel.For(first, first + count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, seed =>
        {
            try
            {
                SessionFuzzer.Run(seed, steps);
            }
            catch (Exception ex)
            {
                failures[seed] = ExceptionDispatchInfo.Capture(ex);
            }
        });

        if (!failures.IsEmpty)
        {
            failures[failures.Keys.Min()].Throw();
        }

        output.WriteLine($"{count} semillas de {steps} acciones en {clock.Elapsed.TotalSeconds:0.0} s.");
    }
}

/// <summary>Solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LongFuzzTheoryAttribute : TheoryAttribute
{
    public LongFuzzTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.LongRun;
}

/// <summary>
/// Para buscar defectos a mano: con <c>RODAJE_FUZZ_DESDE</c> (y opcionalmente <c>RODAJE_FUZZ_CUANTAS</c>, 200, y
/// <c>RODAJE_FUZZ_PASOS</c>, 120) corre ese tramo de semillas sin parar en el primer fallo y lista un fallo de cada tipo; con
/// una sola semilla muestra el mensaje entero. Sin la variable no hace nada. Por ejemplo, en Linux:
/// <c>RODAJE_FUZZ_DESDE=0 RODAJE_FUZZ_CUANTAS=3000 RODAJE_FUZZ_PASOS=300 dotnet test --filter FullyQualifiedName~Explorar --logger "console;verbosity=detailed"</c>.
/// </summary>
public class SessionFuzzExplore(ITestOutputHelper output)
{
    [Fact]
    public void Explorar()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_FUZZ_DESDE") is not { } from)
        {
            return;
        }

        int first = int.Parse(from, CultureInfo.InvariantCulture);
        int count = int.Parse(Environment.GetEnvironmentVariable("RODAJE_FUZZ_CUANTAS") ?? "200", CultureInfo.InvariantCulture);
        int steps = int.Parse(Environment.GetEnvironmentVariable("RODAJE_FUZZ_PASOS") ?? "120", CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = first; seed < first + count; seed++)
        {
            try
            {
                SessionFuzzer.Run(seed, steps);
            }
            catch (Exception ex) when (ex is XunitException or TimeoutException)
            {
                if (count == 1)
                {
                    output.WriteLine(ex.Message);
                    return;
                }

                // Un fallo de cada tipo: sin la semilla, el paso, los identificadores ni los números.
                string line = ex.Message.Split('\n')[0];
                string kind = Regex.Replace(Regex.Replace(line, @"semilla \d+, paso \d+\. ", ""), @"[a-z]+-z?\d+|-?\d+", "#");
                if (seen.Add(kind))
                {
                    output.WriteLine(line.Length > 400 ? line[..400] : line);
                }
            }
        }

        output.WriteLine($"{count} semillas de {steps} acciones en {clock.Elapsed.TotalSeconds:0.0} s.");
    }
}
