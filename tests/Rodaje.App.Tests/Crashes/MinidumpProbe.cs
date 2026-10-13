using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// El minivolcado de verdad de otro proceso, por pasos, para <c>Minivolcado_EsUnArchivoMdmp</c>. Cada paso (arranque,
/// señal, volcado, lectura del archivo) corre en un hilo propio, fuera del pool, con un plazo generoso que solo corta un
/// cuelgue; si falla o se agota, el mensaje dice en qué paso se quedó, cuánto llevaba cada uno y el código de Win32.
/// </summary>
/// <remarks>
/// Antes la prueba leía la salida de <c>cmd.exe</c> con <c>ReadLineAsync().WaitAsync(30 s)</c>: esa lectura (la tubería
/// es síncrona) y la continuación de la prueba necesitan hilos del pool, que en la CI de Windows otras pruebas de
/// <c>Rodaje.App.Tests</c> tienen bloqueados; se agotaba a los 49 s sin decir dónde (13-oct).
/// </remarks>
internal sealed class MinidumpProbe : IDisposable
{
    // Solo contra cuelgues: en una vuelta normal cada paso dura milisegundos.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DumpTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    // Reintentos del volcado si falla (ERROR_PARTIAL_COPY con el sistema cargado), dentro de su plazo.
    private static readonly TimeSpan DumpRetryWindow = TimeSpan.FromSeconds(20);

    private readonly List<string> _steps = [];
    private Process? _child;

    /// <summary>
    /// Hijo ligero: <c>ping.exe</c> de System32, sin <c>cmd.exe</c> en medio. Su primer byte de salida (antes del primer
    /// eco) dice que el cargador terminó y el programa ya corre; vive minutos y se mata al terminar la prueba.
    /// </summary>
    public void StartChild()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        _child = Step("arranque", StartTimeout, () =>
        {
            try
            {
                return Process.Start(new ProcessStartInfo(ping, "-n 600 127.0.0.1")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                }) ?? throw Failure("arranque", "Process.Start no devolvió proceso.");
            }
            catch (Win32Exception ex)
            {
                throw Failure("arranque", $"no se pudo iniciar {ping}: {Win32(ex.NativeErrorCode)} ({ex.Message}).");
            }
        });
    }

    /// <summary>Espera el primer byte de la salida del hijo; si la cierra sin escribir, dice con qué código salió.</summary>
    public void WaitUntilReady()
    {
        Process child = Child;
        int read = Step("señal", SignalTimeout, () =>
        {
            try
            {
                return child.StandardOutput.BaseStream.Read(new byte[1], 0, 1);
            }
            catch (IOException ex)
            {
                throw Failure("señal", $"no se pudo leer la salida del hijo: {Win32(ex.HResult)}.");
            }
        });
        if (read == 0)
        {
            string exit = child.WaitForExit(5000) ? Invariant($"salió con {child.ExitCode} (0x{child.ExitCode:X8})") : "sigue vivo";
            throw Failure("señal", $"el hijo cerró su salida sin escribir nada; {exit}.");
        }
    }

    /// <summary>Vuelca el hijo, con reintentos breves si <c>MiniDumpWriteDump</c> devuelve error.</summary>
    public void WriteDump(string path)
    {
        Process child = Child;
        (bool written, int error, int attempts, TimeSpan spent) = Step("volcado", DumpTimeout, () =>
        {
            var clock = Stopwatch.StartNew();
            int attempts = 0;
            int error;
            bool written;
            while (!(written = NativeCrashHandler.WriteMinidump(path, child, out error)) && clock.Elapsed < DumpRetryWindow)
            {
                attempts++;
                Thread.Sleep(200);
            }

            return (written, error, attempts + 1, clock.Elapsed);
        });
        if (!written)
        {
            string alive = child.HasExited ? Invariant($"el hijo salió con {child.ExitCode}") : "el hijo sigue vivo";
            throw Failure("volcado", Invariant($"MiniDumpWriteDump falló {attempts} veces en {spent.TotalSeconds:0.0} s; último error {Win32(error)}; {alive}."));
        }
    }

    /// <summary>Los cuatro primeros bytes del archivo escrito.</summary>
    public byte[] ReadHead(string path) => Step("lectura del archivo", ReadTimeout, () =>
    {
        try
        {
            byte[] head = new byte[4];
            using FileStream stream = File.OpenRead(path);
            if (stream.Length < head.Length)
            {
                throw Failure("lectura del archivo", Invariant($"el minivolcado tiene {stream.Length} bytes."));
            }

            stream.ReadExactly(head);
            return head;
        }
        catch (IOException ex)
        {
            throw Failure("lectura del archivo", $"{Win32(ex.HResult)} ({ex.Message}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Failure("lectura del archivo", $"{Win32(ex.HResult)} ({ex.Message}).");
        }
    });

    public void Dispose()
    {
        if (_child is null)
        {
            return;
        }

        try
        {
            _child.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Ya había salido: nada que matar.
        }

        _child.Dispose();
    }

    private Process Child => _child ?? throw new InvalidOperationException("Falta StartChild.");

    // Corre el paso en un hilo propio (no en el pool, que puede estar ocupado) y espera con plazo.
    private T Step<T>(string name, TimeSpan timeout, Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
#pragma warning disable CA1031 // Se vuelve a lanzar en el hilo de la prueba.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        })
        { IsBackground = true, Name = "Minivolcado: " + name };
        thread.Start();
        bool finished = thread.Join(timeout);
        _steps.Add(Invariant($"{name} {clock.Elapsed.TotalSeconds:0.000} s"));
        if (!finished)
        {
            throw Failure(name, Invariant($"no terminó en {timeout.TotalSeconds:0} s (¿colgado?)."));
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        return result;
    }

    // Con los pasos ya cerrados y lo que tardó cada uno.
    private MinidumpStepException Failure(string step, string detail) =>
        new($"Minivolcado: se quedó en «{step}»: {detail} Pasos terminados: {(_steps.Count == 0 ? "ninguno" : string.Join(", ", _steps))}.");

    private static string Win32(int code) => Invariant($"{code} (0x{code:X8})");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Fallo de un paso del minivolcado, con el paso y los tiempos en el mensaje.</summary>
internal sealed class MinidumpStepException : Exception
{
    public MinidumpStepException(string message)
        : base(message)
    {
    }
}
