namespace Rodaje.App.Tests.Fuzz;

/// <summary>El mensaje de cuelgue del fuzz de proyectos extremos (tarea 290) dice los tramos, el GC y el estado del hilo.</summary>
public class PhaseLogTests
{
    [Fact]
    public void Describe_DiceLosTramosJuntaLosPasosYCuentaElGc()
    {
        var log = new PhaseLog();
        log.StartCase("preparar");
        log.Mark("abrir");
        log.Mark("paso 0: acción 1");
        log.Mark("paso 1: acción 2");
        log.Mark("guardar como");
        GC.Collect();
        log.Mark("volver a leer");

        string text = log.Describe();

        Assert.Equal("volver a leer", log.Current);
        Assert.Contains("Iba por «volver a leer» desde hace", text, StringComparison.Ordinal);
        Assert.Contains("«preparar» ", text, StringComparison.Ordinal);
        Assert.Contains("«abrir» ", text, StringComparison.Ordinal);
        Assert.Contains("2 pasos ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("«paso 0: acción 1» 0", text, StringComparison.Ordinal);
        Assert.Contains("«guardar como» ", text, StringComparison.Ordinal);
        // La colección forzada entre «guardar como» y «volver a leer» cuenta en el caso, no en el tramo en curso.
        Assert.Matches(@"colecciones gen0/1/2 \+[1-9]\d*/\+[1-9]\d*/\+[1-9]\d*", text);
        Assert.Contains("ms de pausa (total del proceso", text, StringComparison.Ordinal);
        Assert.Contains("Hilo del tramo", text, StringComparison.Ordinal);
        Assert.Contains("vivo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SinTramos_LoDice() => Assert.Equal("Sin tramos marcados.", new PhaseLog().Describe());
}
