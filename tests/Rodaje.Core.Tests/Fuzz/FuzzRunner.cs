using System.Diagnostics;
using System.Globalization;

namespace Rodaje.Tests.Fuzz;

/// <summary>
/// Corre casos de fuzz reproducibles: el caso <c>i</c> usa <c>new Random(semillaBase + i)</c> y, si falla, el mensaje
/// dice la semilla y cómo repetir solo ese caso (<c>RODAJE_FUZZ_SEMILLA</c>). Los casos corren en un hilo aparte: si uno
/// tarda más de <see cref="CaseTimeout"/> se informa como cuelgue con su semilla en vez de dejar la CI esperando.
/// Con <c>RODAJE_PRUEBAS_LARGAS=1</c> corre veinte veces más casos y con más tiempo.
/// </summary>
/// <remarks>Se comparte entre <c>Rodaje.Core.Tests</c> y <c>Rodaje.Packages.Tests</c> (enlazado), sin dependencias.</remarks>
internal static class FuzzRunner
{
    /// <summary>Lo que puede tardar un solo caso antes de darlo por colgado.</summary>
    public static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(15);

    public static bool Long => Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") == "1";

    /// <summary>Si está, solo se corre el caso de esa semilla (para depurar un fallo).</summary>
    public static int? OnlySeed =>
        int.TryParse(Environment.GetEnvironmentVariable("RODAJE_FUZZ_SEMILLA"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed) ? seed : null;

    /// <summary>
    /// Corre hasta <paramref name="cases"/> casos (×20 con pruebas largas) con semillas consecutivas desde
    /// <paramref name="baseSeed"/>. Para al agotar <paramref name="budget"/> si ya corrió <paramref name="minimumCases"/>.
    /// </summary>
    /// <returns>Cuántos casos corrió.</returns>
    public static int Run(string name, int baseSeed, int cases, TimeSpan budget, int minimumCases, Action<int, Random> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        int total = Long ? cases * 20 : cases;
        TimeSpan limit = Long ? budget * 20 : budget;
        int? only = OnlySeed;

        int done = 0;
        int current = baseSeed;
        long caseStarted = Stopwatch.GetTimestamp();
        Exception? failure = null;
        bool stop = false;
        var watch = Stopwatch.StartNew();

        var thread = new Thread(() =>
        {
            for (int i = 0; i < total && !Volatile.Read(ref stop); i++)
            {
                int seed = baseSeed + i;
                if (only is { } wanted && seed != wanted)
                {
                    continue;
                }

                Volatile.Write(ref current, seed);
                Interlocked.Exchange(ref caseStarted, Stopwatch.GetTimestamp());
                try
                {
                    body(seed, new Random(seed));
                }
                catch (Exception ex)
                {
                    failure = new Xunit.Sdk.XunitException(string.Create(CultureInfo.InvariantCulture,
                        $"{name}: falló el caso de la semilla {seed} (repetir solo ese caso con RODAJE_FUZZ_SEMILLA={seed}).\n{ex.GetType().Name}: {ex.Message}\n{Truncate(ex.StackTrace ?? "", 2000)}"));
                    return;
                }

                Interlocked.Increment(ref done);
                if (watch.Elapsed > limit && done >= minimumCases)
                {
                    return;
                }
            }
        }, maxStackSize: 16 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "fuzz " + name,
        };

        thread.Start();
        while (!thread.Join(50))
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref caseStarted)) > CaseTimeout)
            {
                Volatile.Write(ref stop, true);
                throw new Xunit.Sdk.XunitException(string.Create(CultureInfo.InvariantCulture,
                    $"{name}: el caso de la semilla {Volatile.Read(ref current)} tardó más de {CaseTimeout.TotalSeconds:F0} s (cuelgue; repetir con RODAJE_FUZZ_SEMILLA={Volatile.Read(ref current)})."));
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        if (only is null)
        {
            Assert.True(done >= Math.Min(minimumCases, total), string.Create(CultureInfo.InvariantCulture,
                $"{name}: solo {done} casos en {watch.Elapsed.TotalSeconds:F1} s (mínimo {minimumCases})."));
        }

        return done;
    }

    public static string Truncate(string text, int max = 600) => text.Length <= max ? text : text[..max] + "…";
}
