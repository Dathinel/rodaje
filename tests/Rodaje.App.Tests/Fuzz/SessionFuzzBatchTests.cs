namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// El <c>--blame-hang-timeout</c> de la CI mata el proceso de pruebas cuando un caso pasa de 10 min (15 en la nocturna), y
/// vstest lo informa como un cierre. Las 20 000 semillas en un solo caso tardaban más de dos horas: esta prueba, que corre
/// sin <c>RODAJE_PRUEBAS_LARGAS</c>, falla si un caso largo vuelve a cargar más acciones de las que caben o si los lotes dejan
/// de cubrir todas las semillas.
/// </summary>
[Trait("Categoria", "Fuzz")]
public class SessionFuzzBatchTests
{
    /// <summary>
    /// Acciones por caso, en un solo hilo: unos 100 s en Debug en un PC de escritorio (0,4 s por semilla de 400 acciones),
    /// así que un runner cinco veces más lento sigue lejos de los 10 min.
    /// </summary>
    private const int MaxActionsPerCase = 100_000;

    [Fact]
    public void LotesLargos_CubrenLasVeinteMilSemillas_YCadaUnoCabeEnElTopeDeLaCi()
    {
        int[] firsts = [.. ((IEnumerable<object[]>)SessionFuzzTests.LongBatches).Select(row => (int)row[0])];
        int[] expected = [.. Enumerable.Range(0, SessionFuzzTests.LongSeeds / SessionFuzzTests.LongBatchSeeds)
            .Select(i => SessionFuzzTests.LongFirstSeed + i * SessionFuzzTests.LongBatchSeeds)];

        Assert.Equal(0, SessionFuzzTests.LongSeeds % SessionFuzzTests.LongBatchSeeds);
        Assert.Equal(expected, firsts);
        Assert.True(SessionFuzzTests.LongBatchSeeds * SessionFuzzTests.LongSteps <= MaxActionsPerCase,
            $"Un caso largo carga {SessionFuzzTests.LongBatchSeeds} semillas de {SessionFuzzTests.LongSteps} acciones; el tope es {MaxActionsPerCase}.");
        Assert.True(SessionFuzzTests.DeepSteps <= MaxActionsPerCase);
        Assert.Equal(10, SessionFuzzTests.DeepSeeds.Count);
    }
}
