namespace Rodaje.Engine.Tests.Support;

/// <summary>
/// El plazo de corte de <see cref="ThreadDump"/>: una instantánea que no termina (un <c>createdump</c> colgado) no congela
/// la prueba que intenta diagnosticar un interbloqueo. Sin reloj real que decida: la captura falsa no vuelve nunca hasta
/// que la prueba la suelta, así que con cualquier plazo el resultado es el de plazo vencido.
/// </summary>
public sealed class ThreadDumpTests
{
    [Fact]
    public void Una_captura_que_no_termina_devuelve_el_texto_de_plazo()
    {
        using var release = new ManualResetEventSlim();
        try
        {
            string text = ThreadDump.Capture(
                () =>
                {
                    release.Wait(CancellationToken.None);
                    return "no debió llegar";
                },
                TimeSpan.FromMilliseconds(50));

            Assert.StartsWith(ThreadDump.TimedOutPrefix, text, StringComparison.Ordinal);
            Assert.Equal("(pilas no disponibles: la instantánea no terminó en 0.05 s)", text);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Una_captura_que_termina_devuelve_su_texto()
    {
        Assert.Equal("pilas", ThreadDump.Capture(() => "pilas", Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public void Una_captura_que_lanza_lo_dice_en_el_texto()
    {
        string text = ThreadDump.Capture(() => throw new InvalidOperationException("sin permiso"), Timeout.InfiniteTimeSpan);

        Assert.Equal("(no se pudieron leer las pilas: InvalidOperationException: sin permiso)", text);
    }
}
