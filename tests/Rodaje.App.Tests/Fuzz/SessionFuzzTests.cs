using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// El fuzz de la sesión del editor (<see cref="SessionFuzzer"/>): 200 semillas en la CI, con tope de tiempo, y 20 000 con
/// <c>RODAJE_PRUEBAS_LARGAS=1</c>. Un fallo dice su semilla; se reproduce con <see cref="Semilla_Reproduce"/> y, una vez
/// corregido, la semilla queda en <see cref="SemillasQueFallaron_YaNoFallan"/>.
/// </summary>
[Trait("Categoria", "Fuzz")]
public class SessionFuzzTests(ITestOutputHelper output)
{
    /// <summary>Acciones por semilla en la CI.</summary>
    private const int CiSteps = 120;

    /// <summary>Acciones por semilla en la corrida larga (cada una con 5 000 acciones sería un día de máquina).</summary>
    private const int LongSteps = 400;

    /// <summary>
    /// Tope de la corrida de la CI: si el runner va lento, se corta antes de las 200 semillas en vez de alargar la CI más
    /// de un par de minutos (las semillas que no corrieron se dicen en la salida).
    /// </summary>
    private static readonly TimeSpan CiBudget = TimeSpan.FromSeconds(90);

    [Fact]
    public void DoscientasSemillas_SinFallos() => RunSeeds(0, 200, CiSteps, CiBudget);

    [LongFuzzFact]
    public void VeinteMilSemillas_SinFallos() => RunSeeds(1_000_000, 20_000, LongSteps, TimeSpan.MaxValue);

    /// <summary>Unas pocas secuencias de 5 000 acciones, con el historial y los compuestos bien hondos.</summary>
    [LongFuzzFact]
    public void SecuenciasDeCincoMilAcciones_SinFallos() => RunSeeds(2_000_000, 10, 5_000, TimeSpan.MaxValue);

    /// <summary>Para reproducir una semilla del mensaje de un fallo: cámbiala aquí y quita el Skip.</summary>
    [Theory(Skip = "Solo para reproducir a mano una semilla que falló.")]
    [InlineData(0, CiSteps)]
    public void Semilla_Reproduce(int seed, int steps) => SessionFuzzer.Run(seed, steps);

    /// <summary>Las semillas que encontraron defectos, con las acciones que hacían falta para llegar a ellos.</summary>
    [Theory]
    [MemberData(nameof(FailedSeeds))]
    public void SemillasQueFallaron_YaNoFallan(int seed, int steps) => SessionFuzzer.Run(seed, steps);

    /// <summary>Semilla y acciones; el defecto de cada una, en <see cref="SessionFuzzRegressionTests"/>.</summary>
    public static TheoryData<int, int> FailedSeeds => new()
    {
        { 35, CiSteps },  // «Añadir efecto» aceptaba el tipo reservado rodaje.speed.
        { 920, 300 },     // Un gesto cancelado quitaba la barrera de fusión: el recorte siguiente se fundía con uno viejo.
        { 6, 300 },       // Un gesto cancelado vaciaba lo que se podía rehacer.
        { 651, 300 },     // Q con ripple por pista dejaba atrás al audio enlazado que empezaba en el cursor.
        { 1007, 300 },    // Q con ripple por pista desfasaba enlazados que empezaban en instantes distintos.
        { 9335, 300 },    // Aplicar un seguimiento cuyo tipo no coincidía con sus cuadros lanzaba.
    };

    private void RunSeeds(int first, int count, int steps, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        int ran = 0;
        for (int seed = first; seed < first + count; seed++)
        {
            if (clock.Elapsed > budget)
            {
                output.WriteLine($"Tope de tiempo: corrieron {ran} de {count} semillas en {clock.Elapsed.TotalSeconds:0} s.");
                return;
            }

            SessionFuzzer.Run(seed, steps);
            ran++;
        }

        output.WriteLine($"{ran} semillas de {steps} acciones en {clock.Elapsed.TotalSeconds:0.0} s.");
    }
}

/// <summary>Solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LongFuzzFactAttribute : FactAttribute
{
    public LongFuzzFactAttribute() => Skip = Rodaje.TestMedia.Skip.LongRun;
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
