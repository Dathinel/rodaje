using System.Diagnostics;

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

    /// <summary>
    /// Con capturas en paralelo, la que vence solo mata el <c>createdump</c> que nació durante ella, no el de otra que ya
    /// estaba en curso. Los dos hijos son copias de <c>sleep</c> llamadas <c>createdump</c> (su <c>comm</c> en
    /// <c>/proc</c>): uno se lanza antes de la captura y el otro dentro de ella, que después no vuelve hasta que la prueba
    /// la suelta. El plazo solo tiene que cubrir el arranque del segundo hijo, y la prueba comprueba que lo cubrió.
    /// </summary>
    [LinuxOnlyFact]
    public void Al_vencer_el_plazo_solo_muere_el_createdump_de_esta_captura()
    {
        string dir = Directory.CreateTempSubdirectory("rodaje-createdump-").FullName;
        using var release = new ManualResetEventSlim();
        Process? other = null;
        Process? mine = null;
        try
        {
            string fake = Path.Combine(dir, "createdump");
            File.Copy("/bin/sleep", fake); // Copia también el permiso de ejecución.
            other = Process.Start(fake, "300");
            Assert.NotNull(other);

            string text = ThreadDump.Capture(
                () =>
                {
                    mine = Process.Start(fake, "300");
                    release.Wait(CancellationToken.None);
                    return "no debió llegar";
                },
                TimeSpan.FromSeconds(2));

            Assert.StartsWith(ThreadDump.TimedOutPrefix, text, StringComparison.Ordinal);
            Assert.True(mine is not null, "El createdump de la captura no arrancó dentro del plazo.");
            Assert.True(mine.WaitForExit(TimeSpan.FromSeconds(10)), "El createdump de esta captura siguió vivo.");
            Assert.False(other.HasExited, "Se mató el createdump de otra captura.");
        }
        finally
        {
            release.Set();
            foreach (Process? child in new[] { other, mine })
            {
                if (child is not null)
                {
                    try
                    {
                        child.Kill();
                        child.WaitForExit(TimeSpan.FromSeconds(10));
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    child.Dispose();
                }
            }

            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Una prueba que solo tiene sentido en Linux (lee <c>/proc</c>): fuera de Linux se omite y lo dice.</summary>
file sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Solo Linux: lee los procesos hijos en /proc.";
        }
    }
}
