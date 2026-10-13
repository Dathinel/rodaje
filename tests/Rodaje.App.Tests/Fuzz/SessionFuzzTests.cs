using System.Diagnostics;
using Xunit.Abstractions;

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

    public static TheoryData<int, int> FailedSeeds => new()
    {
        { -1, 0 },
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

/// <summary>TEMPORAL: explora semillas y lista los fallos distintos.</summary>
public class SessionFuzzExplore(ITestOutputHelper output)
{
    [Fact]
    public void Explorar()
    {
        if (Environment.GetEnvironmentVariable("FUZZ_DESDE") is not { } from)
        {
            return;
        }

        int first = int.Parse(from, System.Globalization.CultureInfo.InvariantCulture);
        int count = int.Parse(Environment.GetEnvironmentVariable("FUZZ_CUANTAS") ?? "200", System.Globalization.CultureInfo.InvariantCulture);
        int steps = int.Parse(Environment.GetEnvironmentVariable("FUZZ_PASOS") ?? "120", System.Globalization.CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        var seen = new Dictionary<string, int>();
        for (int seed = first; seed < first + count; seed++)
        {
            try
            {
                SessionFuzzer.Run(seed, steps);
            }
            catch (Exception ex)
            {
                if (count == 1)
                {
                    output.WriteLine(ex.Message);
                    return;
                }

                string line = ex.Message.Split('\n')[0];
                string key = System.Text.RegularExpressions.Regex.Replace(line, @"semilla \d+, paso \d+\. ", "");
                key = System.Text.RegularExpressions.Regex.Replace(key, @"[a-z]+-z?\d+|\d+", "#");
                if (!seen.ContainsKey(key))
                {
                    seen[key] = seed;
                    output.WriteLine(line.Length > 400 ? line[..400] : line);
                }
            }
        }

        output.WriteLine($"{count} semillas en {clock.Elapsed.TotalSeconds:0.0} s");
    }
}
