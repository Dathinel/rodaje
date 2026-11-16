using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Rodaje.Modules.Protocol;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// El lado del módulo: se conecta al socket de dominio Unix que nombra <c>--rodaje-pipe</c>, se protege
/// contra la muerte del padre (en Linux <c>prctl(PR_SET_PDEATHSIG, SIGKILL)</c>; en macOS un hilo con
/// <c>kqueue</c> <c>EVFILT_PROC</c>/<c>NOTE_EXIT</c> sobre el padre, con el sondeo de <c>getppid()</c> cada
/// 200 ms de respaldo) y además sale al ver el fin de flujo. Habla el protocolo v1 real: saludo
/// <c>hello</c>/<c>welcome</c>, <c>ping</c>→<c>pong</c>, <c>invoke</c> de la función <c>eco</c> (copia el
/// primer búfer en el segundo dentro de la región de memoria compartida) y <c>shutdown</c>.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class Modulo
{
    public static async Task<int> RunAsync(string socketPath)
    {
        // La carpeta del socket es la carpeta 0700 de la ejecución: ahí viven también los archivos de las regiones.
        string runDir = Path.GetDirectoryName(Path.GetFullPath(socketPath)) ?? ".";
        ArmarVigilancia();

        Socket socket;
        try
        {
            using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(10));
            socket = await UnixTransport.ConnectAsync(socketPath, plazo.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("No se pudo conectar con el editor: " + ex.Message).ConfigureAwait(false);
            return 2;
        }

        using MessageChannel channel = new(new NetworkStream(socket, ownsSocket: true));
        try
        {
            ModuleMessage? first;
            try
            {
                // 30 s de plazo del saludo, el DefaultHandshakeTimeout que comparten ModuleClient y ModuleHost.
                using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(30));
                first = await channel.ReceiveAsync(plazo.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 2; // el editor no saludó a tiempo: error de conexión, no un cierre limpio.
            }
            if (first is not HelloMessage hello)
            {
                await channel.SendAsync(new ErrorMessage(ErrorCodes.Malformed, "Lo primero tiene que ser «hello».")).ConfigureAwait(false);
                return 2;
            }
            int chosen = Math.Min(ProtocolVersion.Current, hello.MaxProtocol);
            if (chosen < Math.Max(ProtocolVersion.Current, hello.MinProtocol))
            {
                await channel.SendAsync(new ErrorMessage(ErrorCodes.VersionIncompatible,
                    $"El módulo habla el protocolo {ProtocolVersion.Current}–{ProtocolVersion.Current} y el editor {hello.MinProtocol}–{hello.MaxProtocol}.")).ConfigureAwait(false);
                return 2;
            }
            await channel.SendAsync(new WelcomeMessage(chosen, EcoInfo.Modulo)).ConfigureAwait(false);

            Dictionary<string, UnixRegion> regiones = new(StringComparer.Ordinal);
            while (true)
            {
                ModuleMessage? m = await channel.ReceiveAsync().ConfigureAwait(false);
                switch (m)
                {
                    case null:
                    case ShutdownMessage:
                        return 0; // shutdown o fin de flujo: las dos salidas limpias.
                    case PingMessage ping:
                        await channel.SendAsync(new PongMessage(ping.Id)).ConfigureAwait(false);
                        break;
                    case InvokeMessage invoke:
                        await channel.SendAsync(Ejecuta(runDir, regiones, invoke)).ConfigureAwait(false);
                        break;
                    default:
                        await channel.SendAsync(new ErrorMessage(ErrorCodes.Malformed, $"El editor no manda «{m.GetType().Name}».")).ConfigureAwait(false);
                        return 2;
                }
            }
        }
        catch (ModuleProtocolException ex)
        {
            try
            {
                await channel.SendAsync(new ErrorMessage(ErrorCodes.Malformed, ex.Message)).ConfigureAwait(false);
            }
            catch (Exception send) when (send is IOException or ObjectDisposedException or ModuleException)
            {
                // El editor ya no escucha.
            }
            return 2;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // El editor cortó la conexión (EOF): salida limpia, como en ModuleHost.
            return 0;
        }
    }

    /// <summary>La función <c>eco</c>: copia el primer búfer de la llamada en el segundo y lo devuelve.</summary>
    private static ModuleMessage Ejecuta(string runDir, Dictionary<string, UnixRegion> regiones, InvokeMessage invoke)
    {
        if (invoke.Function != "eco")
            return new ErrorMessage(ErrorCodes.UnknownFunction, $"El módulo no tiene la función «{invoke.Function}».", invoke.Id);
        if (invoke.Buffers is not [SharedBufferRef entrada, SharedBufferRef salida])
            return new ErrorMessage(ErrorCodes.InvalidParameters, "«eco» espera dos búferes: entrada y salida.", invoke.Id);
        try
        {
            Region(runDir, regiones, entrada).GetSpan(entrada).CopyTo(Region(runDir, regiones, salida).GetSpan(salida));
            return new ResultMessage(invoke.Id, null, [salida]);
        }
        catch (ModuleException ex)
        {
            return new ErrorMessage(ex.Code, ex.Message, invoke.Id);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return new ErrorMessage(ErrorCodes.Internal, ex.Message, invoke.Id);
        }
    }

    /// <summary>Abre la región por nombre dentro de la carpeta de la ejecución (una por nombre, como ModuleHost).</summary>
    private static UnixRegion Region(string runDir, Dictionary<string, UnixRegion> regiones, SharedBufferRef b) =>
        regiones.TryGetValue(b.Region, out UnixRegion? r)
            ? r
            : regiones[b.Region] = UnixRegion.Open(runDir, b.Region, b.Offset + b.Length);

    /// <summary>
    /// La protección contra la muerte del padre, lo antes posible al arrancar. Además del mecanismo de cada
    /// sistema, el módulo sale siempre al ver el fin de flujo en el socket (el kernel cierra los descriptores
    /// del padre muerto). La carrera que describen las dos llamadas: si el padre ya murió antes de armar,
    /// <c>getppid()</c> ya devuelve otro proceso y el módulo sale ahora mismo.
    /// </summary>
    private static void ArmarVigilancia()
    {
        int ppid = Posix.GetPPid();
        if (OperatingSystem.IsLinux())
        {
            if (Posix.PrCtl(Posix.PR_SET_PDEATHSIG, Posix.SIGKILL, 0, 0, 0) == 0)
                Console.Error.WriteLine("vigilancia: prctl(PR_SET_PDEATHSIG, SIGKILL)");
            else
            {
                Console.Error.WriteLine($"vigilancia: prctl falló (errno {Posix.Errno()}); sondeo de getppid cada 200 ms");
                Sondeo(ppid);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (ArmarKqueue(ppid))
                Console.Error.WriteLine("vigilancia: kqueue EVFILT_PROC/NOTE_EXIT sobre el padre");
            else
            {
                Console.Error.WriteLine("vigilancia: kqueue no se pudo; sondeo de getppid cada 200 ms");
                Sondeo(ppid);
            }
        }
        if (Posix.GetPPid() != ppid)
            Environment.Exit(0);
    }

    /// <summary>Respaldo de la vigilancia: un hilo que cada 200 ms mira si el padre sigue siendo el mismo.</summary>
    private static void Sondeo(int ppid)
    {
        Thread vigilante = new(() =>
        {
            while (true)
            {
                Thread.Sleep(200);
                if (Posix.GetPPid() != ppid)
                    Environment.Exit(0);
            }
        })
        { IsBackground = true, Name = "vigilancia-getppid" };
        vigilante.Start();
    }

    /// <summary>Registra <c>NOTE_EXIT</c> sobre el padre en un kqueue propio y lo espera en un hilo.</summary>
    [SupportedOSPlatform("macos")]
    private static unsafe bool ArmarKqueue(int ppid)
    {
        int kq = Posix.KQueue();
        if (kq < 0)
            return false;
        KEvent alta = new()
        {
            Ident = (nuint)ppid,
            Filter = Posix.EVFILT_PROC,
            Flags = Posix.EV_ADD,
            Fflags = Posix.NOTE_EXIT,
        };
        if (Posix.KEvent(kq, &alta, 1, null, 0, null) < 0)
        {
            Posix.Close(kq);
            return false;
        }
        Thread vigilante = new(() => EsperaKqueue(kq, ppid)) { IsBackground = true, Name = "vigilancia-kqueue" };
        vigilante.Start();
        return true;
    }

    [SupportedOSPlatform("macos")]
    private static unsafe void EsperaKqueue(int kq, int ppid)
    {
        while (true)
        {
            KEvent ev;
            int n = Posix.KEvent(kq, null, 0, &ev, 1, null);
            if (n > 0)
            {
                if ((ev.Fflags & Posix.NOTE_EXIT) != 0)
                    Environment.Exit(0);
            }
            else if (n < 0 && Posix.Errno() != Posix.EINTR)
            {
                Posix.Close(kq);
                Sondeo(ppid);
                return;
            }
        }
    }
}

/// <summary>
/// Los otros dos papeles que hacen falta para las comprobaciones: <c>--intruso</c> se conecta sin ser el
/// módulo que el editor lanzó (lo rechazan las credenciales del socket) y <c>--editor-intermedio</c> hace
/// de editor para la prueba de muerte del padre: escucha, lanza el módulo, anuncia su pid y duerme hasta
/// que lo maten.
/// </summary>
internal static class Auxiliar
{
    public static async Task<int> IntrusoAsync(string socketPath)
    {
        try
        {
            using Socket socket = await UnixTransport.ConnectAsync(socketPath).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            await Console.Error.WriteLineAsync("intruso: " + ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    public static async Task<int> EditorIntermedioAsync(string socketPath)
    {
        using Socket listener = UnixTransport.Listen(socketPath);
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Sin ProcessPath.");
        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--rodaje-pipe");
        start.ArgumentList.Add(socketPath);
        using Process hijo = Process.Start(start) ?? throw new InvalidOperationException("No se pudo abrir el módulo.");
        _ = hijo.StandardOutput.ReadToEndAsync();
        Console.Out.WriteLine($"hijo={hijo.Id}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        // Reenvía la salida de errores del módulo: su primera línea dice qué vigilancia armó.
        _ = Task.Run(async () =>
        {
            while (await hijo.StandardError.ReadLineAsync().ConfigureAwait(false) is { } linea)
                Console.Out.WriteLine("modulo: " + linea);
        });
        await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
        return 0;
    }
}
