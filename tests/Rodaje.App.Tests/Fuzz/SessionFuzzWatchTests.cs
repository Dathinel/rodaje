using System.Text.RegularExpressions;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// La vigilancia del fuzz de la sesión contra cuelgues (tarea 182). La semilla 9335 agotó una vez en la CI de Windows el
/// tope de 120 s «por el paso 240 de 300: Select», una acción síncrona que no espera nada y que en Linux tarda menos de un
/// milisegundo (la semilla entera, unos 0,4 s; ni con 1 o 2 procesadores ni con las pruebas de Core y Engine a la vez pasa
/// de 1 s). No se reprodujo: el tope medía la semilla entera, así que no distinguía un runner lento de un tramo colgado,
/// ni decía en qué parte del paso estaba. Ahora el tope es por tramo sin avanzar y el fallo dice paso, acción, tramo,
/// cuánto llevaba ahí y a qué ritmo iban los pasos anteriores. Estas pruebas sustituyen el reloj de la vigilancia por un
/// paso a paso con el fuzz, así que no dependen del tiempo ni de los hilos del pool.
/// </summary>
[Trait("Categoria", "Fuzz")]
public sealed class SessionFuzzWatchTests
{
    private const int Seed = 9335;

    [Fact]
    public Task Un_tramo_que_no_avanza_falla_con_paso_accion_y_tramo() => OwnThread.Run(() =>
    {
        // La acción del paso 3, del recorrido de la misma semilla.
        string act = Regex.Match(SessionFuzzer.Run(Seed, 5), @"^3\. (\w+)", RegexOptions.Multiline).Groups[1].Value;
        Assert.NotEmpty(act);

        using var stuck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TimeoutException error;
        try
        {
            error = Assert.Throws<TimeoutException>(() => SessionFuzzer.Run(Seed, 5, TimeSpan.FromSeconds(120),
                onMark: (step, phase) =>
                {
                    if (step == 3 && phase == "comprobar")
                    {
                        // Un tramo que no termina: el hilo del fuzz se queda aquí hasta que la prueba lo suelte.
                        stuck.Set();
                        release.Wait();
                    }
                },
                // Cada tramo de vigilancia acaba cuando el fuzz se trabó (o terminó): el primero ve avance, el segundo no.
                waitSlice: (task, _) =>
                {
                    WaitHandle.WaitAny([stuck.WaitHandle, ((IAsyncResult)task).AsyncWaitHandle]);
                    return task.IsCompleted;
                }));
        }
        finally
        {
            release.Set();
        }

        Assert.Contains($"Semilla {Seed}: La prueba no avanzó en 120 s.", error.Message, StringComparison.Ordinal);
        Assert.Contains($"Iba por el paso 3 de 5: {act} (comprobar), en ese tramo desde hace ", error.Message, StringComparison.Ordinal);
        Assert.Matches(@"; 2 pasos en \d+\.\d s, el más lento el [12] \(\d+ ms\)\.$", error.Message);
    });

    [Fact]
    public Task Una_semilla_lenta_que_avanza_no_es_un_cuelgue() => OwnThread.Run(() =>
    {
        // Paso a paso: cada tramo del fuzz espera a que la vigilancia lo vea, y cada tramo de vigilancia «dura» el tope
        // entero. La semilla dura así decenas de topes y pasa, porque ninguno se queda sin avance; con el tope total de
        // antes habría fallado en el primero.
        using var marks = new SemaphoreSlim(0);
        using var seen = new SemaphoreSlim(0);
        int slices = 0;
        bool bodyWaiting = false;
        string trace = SessionFuzzer.Run(Seed, 30, TimeSpan.FromSeconds(120),
            onMark: (_, _) =>
            {
                marks.Release();
                seen.Wait();
            },
            waitSlice: (task, _) =>
            {
                if (bodyWaiting)
                {
                    seen.Release();
                }

                WaitHandle.WaitAny([marks.AvailableWaitHandle, ((IAsyncResult)task).AsyncWaitHandle]);
                if (task.IsCompleted)
                {
                    return true;
                }

                Assert.True(marks.Wait(0));
                bodyWaiting = true;
                slices++;
                return false;
            });

        Assert.Equal(30, Regex.Count(trace, @"^\d+\. ", RegexOptions.Multiline));
        Assert.True(slices > 30 * 4, $"{slices} tramos de vigilancia");
    });
}
