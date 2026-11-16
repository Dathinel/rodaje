using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Experimentos.Eco;

/// <summary>
/// La batería del lado del editor: crea la carpeta 0700 y el socket de dominio Unix, lanza al módulo (el
/// mismo ejecutable con <c>--rodaje-pipe</c>), comprueba sus credenciales, lo saluda con el protocolo v1,
/// le pasa un cuadro 1080p RGBA por una región de memoria compartida y mide el eco y el ping. Escribe la
/// tabla en consola y <c>resultado.json</c>; sale con 0 solo si todo pasa.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class Suite
{
    private const int FrameBytes = 1920 * 1080 * 4; // 8 294 400

    public static async Task<int> RunAsync()
    {
        Informe informe = new();
        Console.WriteLine("== Eco del protocolo de módulos en Unix (fase 0e) ==");
        Console.WriteLine($"Sistema: {RuntimeInformation.OSDescription} · {RuntimeInformation.RuntimeIdentifier} · .NET {Environment.Version}");
        informe.Sistema = OperatingSystem.IsLinux() ? "linux" : "macos";
        informe.Rid = RuntimeInformation.RuntimeIdentifier;
        informe.SunPath = UnixTransport.SunPathLimit;

        string baseDir = UnixTransport.BaseDir();
        informe.RutaBase = baseDir;
        informe.Comprueba("carpeta-base-0700", UnixTransport.EnsureDir0700(baseDir), baseDir);

        int barridos = UnixTransport.SweepOrphans(baseDir);
        if (barridos > 0)
            Console.WriteLine($"  info   (barridos {barridos} huérfanos al arrancar)");

        string runDir = UnixTransport.NewRunDir(baseDir);
        informe.RutaEjecucion = runDir;
        informe.Comprueba("carpeta-ejecucion-0700", UnixTransport.EnsureDir0700(runDir), runDir);

        string socketPath = UnixTransport.NewSocketPath(runDir);
        int socketBytes = Encoding.UTF8.GetByteCount(socketPath);
        informe.RutaSocket = socketPath;
        informe.Comprueba("ruta-socket-corta", socketBytes <= UnixTransport.SunPathLimit,
            $"{socketBytes} B ≤ {UnixTransport.SunPathLimit} (sun_path)");

        RiesgoTmpDir(informe);
        RegionHostiles(informe, runDir, baseDir);

        Socket? listener = null;
        Process? modulo = null;
        string paso = "socket";
        Task<string>? errModulo = null;
        try
        {
            listener = UnixTransport.Listen(socketPath);
            modulo = Lanzar("--rodaje-pipe", socketPath);
            errModulo = modulo.StandardError.ReadToEndAsync();

            // Aceptar hasta dar con el hijo: las credenciales del socket dicen quién conectó.
            paso = "credenciales-extremo";
            Socket? conn = null;
            (int pid, uint uid) creds = (0, 0);
            using (CancellationTokenSource plazo = new(TimeSpan.FromSeconds(30)))
            {
                Task salio = modulo.WaitForExitAsync(plazo.Token);
                while (conn is null)
                {
                    Task<Socket> accept = listener.AcceptAsync(plazo.Token).AsTask();
                    if (await Task.WhenAny(accept, salio).ConfigureAwait(false) == salio && salio.IsCompletedSuccessfully)
                        throw new ModuleDisconnectedException($"El módulo terminó con el código {modulo.ExitCode} antes de conectarse.{Cola(errModulo)}");
                    Socket s = await accept.ConfigureAwait(false);
                    creds = UnixTransport.PeerCreds(s);
                    if (creds.pid == modulo.Id)
                        conn = s;
                    else
                        s.Dispose();
                }
            }
            informe.Comprueba("credenciales-extremo",
                creds.uid == Posix.GetEuid(),
                $"pid {creds.pid} = hijo {modulo.Id}, uid {creds.uid} = {Posix.GetEuid()}");

            // Negativo: otro proceso del mismo usuario conecta y el pid no es el del hijo → rechazado.
            paso = "intruso-rechazado";
            Process intruso = Lanzar("--intruso", socketPath);
            using (intruso)
            {
                Socket ic;
                using (CancellationTokenSource plazo = new(TimeSpan.FromSeconds(15)))
                    ic = await listener.AcceptAsync(plazo.Token).ConfigureAwait(false);
                (int ipid, uint iuid) = UnixTransport.PeerCreds(ic);
                ic.Dispose();
                informe.Comprueba("intruso-rechazado",
                    ipid == intruso.Id && ipid != modulo.Id,
                    $"pid {ipid} (intruso {intruso.Id}) ≠ módulo {modulo.Id}; conexión cerrada");
            }

            // Saludo v1 con el MessageChannel real del protocolo.
            paso = "saludo-v1";
            NetworkStream stream = new(conn, ownsSocket: true);
            using MessageChannel channel = new(stream);
            await channel.SendAsync(new HelloMessage(ProtocolVersion.Current, ProtocolVersion.Current, "rodaje", "1.0.3", "es-CO")).ConfigureAwait(false);
            ModuleMessage? first;
            using (CancellationTokenSource plazo = new(TimeSpan.FromSeconds(30)))
                first = await channel.ReceiveAsync(plazo.Token).ConfigureAwait(false);
            bool saludo = first is WelcomeMessage { Protocol: ProtocolVersion.Current } w
                && w.Module.Functions.Any(f => f.Id == "eco");
            informe.Comprueba("saludo-v1", saludo,
                first is WelcomeMessage w2 ? $"protocolo {w2.Protocol}, {w2.Module.Functions.Count} función(es)" : $"llegó {first?.GetType().Name ?? "fin de flujo"}");
            if (!saludo)
                throw new ModuleProtocolException("Sin welcome: no se puede seguir.");

            long id = 0;

            // El cuadro: una región con entrada y salida, como la función «copiar» del módulo de eco real.
            paso = "eco-cuadro-1080p";
            using UnixRegion region = UnixRegion.Create(runDir, 2L * FrameBytes);
            VideoLayout video = new(1920, 1080, 1920 * 4, PixelFormats.Rgba8);
            SharedBufferRef entrada = region.Describe(0, FrameBytes, video);
            SharedBufferRef salida = region.Describe(FrameBytes, FrameBytes, video);
            Patron(region.GetSpan(entrada));

            ModuleMessage res = await Invocar(channel, ++id, "eco", [entrada, salida]).ConfigureAwait(false);
            informe.Comprueba("eco-cuadro-1080p", EcoVerificado(res, region, entrada, salida),
                res is ResultMessage ? "byte a byte" : $"llegó {res.GetType().Name}");

            paso = "eco-200-cuadros";
            long t0 = Stopwatch.GetTimestamp();
            bool todos = true;
            for (int i = 0; i < 200; i++)
            {
                res = await Invocar(channel, ++id, "eco", [entrada, salida]).ConfigureAwait(false);
                todos &= EcoVerificado(res, region, entrada, salida);
            }
            double fps = 200 / Stopwatch.GetElapsedTime(t0).TotalSeconds;
            informe.Medida("eco_cuadros_s", fps);
            informe.Comprueba("eco-200-cuadros", todos, $"{fps:0.#} cuadros/s");

            paso = "ping-1000";
            double[] us = new double[1000];
            for (int i = 0; i < us.Length; i++)
            {
                long t = Stopwatch.GetTimestamp();
                await channel.SendAsync(new PingMessage(++id)).ConfigureAwait(false);
                await EsperaPong(channel, id).ConfigureAwait(false);
                us[i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            }
            Array.Sort(us);
            double p50 = us[us.Length / 2], p95 = us[(int)(us.Length * 0.95)];
            informe.Medida("ping_p50_us", p50);
            informe.Medida("ping_p95_us", p95);
            informe.Comprueba("ping-1000", true, $"p50 {p50:0.#} µs · p95 {p95:0.#} µs");

            paso = "cierre-limpio";
            await channel.SendAsync(new ShutdownMessage()).ConfigureAwait(false);
            await modulo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            informe.Comprueba("cierre-limpio", modulo.ExitCode == 0, $"código {modulo.ExitCode}");
        }
        catch (Exception ex) when (ex is ModuleException or IOException or SocketException or TimeoutException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            informe.Comprueba(paso, false, ex.GetType().Name + ": " + ex.Message + Cola(errModulo));
        }
        finally
        {
            listener?.Dispose();
            if (modulo is { HasExited: false })
            {
                try { modulo.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
            }
            modulo?.Dispose();
        }

        await MuertePadre(informe, runDir).ConfigureAwait(false);

        try { File.Delete(socketPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(runDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        Console.WriteLine();
        string json = Path.Combine(Directory.GetCurrentDirectory(), "resultado.json");
        informe.EscribeJson(json);
        Console.WriteLine($"Resultado: {informe.Pasadas}/{informe.Total} comprobaciones — {(informe.TodoPasa ? "todo pasa" : "FALLA")}");
        Console.WriteLine($"Escrito {json}");
        return informe.TodoPasa ? 0 : 2;
    }

    /// <summary>
    /// El riesgo de la ruta del socket: en macOS <c>$TMPDIR</c> ya casi llena <c>sun_path</c> (104), así que
    /// el nombre de tubería con forma Windows (<c>CoreFxPipe_Rodaje.Mod.pipe.&lt;32 hex&gt;</c>) no cabe y el
    /// bind tiene que fallar. Si cupiera, escucharía: también se informa.
    /// </summary>
    private static void RiesgoTmpDir(Informe informe)
    {
        string tmp = Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } t ? t : "/tmp";
        string nombre = "CoreFxPipe_" + UnixRegion.NamePrefix + "pipe." + Guid.NewGuid().ToString("N");
        string path = Path.Combine(tmp, nombre);
        int bytes = Encoding.UTF8.GetByteCount(path);
        string que;
        bool falloBind;
        try
        {
            using (UnixTransport.Listen(path)) { }
            falloBind = false;
            que = "escuchó (cabe)";
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            falloBind = true;
            que = ex.GetType().Name + ": " + ex.Message;
        }
        informe.Comprueba("riesgo-ruta-tmpdir", bytes <= UnixTransport.SunPathLimit || falloBind,
            $"{bytes} B frente a {UnixTransport.SunPathLimit} en «{path}»: {que}");
    }

    /// <summary>
    /// Las comprobaciones hostiles de la memoria compartida: la región es un archivo en la carpeta 0700 y
    /// solo el archivo exacto (regular, del usuario, con 600 y con el tamaño) se abre.
    /// </summary>
    private static void RegionHostiles(Informe informe, string runDir, string baseDir)
    {
        // Crear, escribir por la vista y borrar al cerrar.
        string nombre = UnixRegion.NamePrefix + Guid.NewGuid().ToString("N");
        string path = Path.Combine(runDir, nombre);
        using (UnixRegion region = UnixRegion.Create(runDir, nombre, 4096))
        {
            region.GetSpan(0, 16).Fill(0xAB);
            informe.Comprueba("region-crear", File.Exists(path) && File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                $"«{nombre}» de 4096 B, modo 600");
        }
        informe.Comprueba("region-borrada-al-cerrar", !File.Exists(path), "el archivo desapareció con Dispose");

        // Creación exclusiva: el mismo nombre dos veces falla.
        using (UnixRegion primera = UnixRegion.Create(runDir, "dup.fija", 1024))
        {
            bool rechazada;
            try
            {
                using UnixRegion segunda = UnixRegion.Create(runDir, "dup.fija", 1024);
                rechazada = false;
            }
            catch (IOException)
            {
                rechazada = true;
            }
            informe.Comprueba("region-nombre-duplicado", rechazada, "CreateNew (O_EXCL) rechazó la segunda");
        }

        // Un enlace simbólico con nombre de región no se abre (O_NOFOLLOW).
        string enlace = Path.Combine(runDir, "enlace.falso");
        try
        {
            File.CreateSymbolicLink(enlace, Path.Combine(runDir, "no-existe"));
            bool rechazado;
            try
            {
                using UnixRegion x = UnixRegion.Open(runDir, "enlace.falso", 1);
                rechazado = false;
            }
            catch (ModuleCallException)
            {
                rechazado = true;
            }
            informe.Comprueba("region-enlace-rechazado", rechazado, "O_NOFOLLOW rechazó el enlace");
            File.Delete(enlace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            informe.Comprueba("region-enlace-rechazado", false, "no se pudo crear el enlace: " + ex.Message);
        }

        // Un archivo regular con 600 pero de otro tamaño no se abre.
        string corto = Path.Combine(runDir, "corta.fija");
        File.WriteAllBytes(corto, new byte[100]);
        File.SetUnixFileMode(corto, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        RechazoAlAbrir(informe, runDir, "corta.fija", 4096, "region-tamano-distinto", "100 B frente a 4096 esperados");

        // Un archivo regular con permisos distintos de 600 tampoco.
        string ancho = Path.Combine(runDir, "ancha.fija");
        File.WriteAllBytes(ancho, new byte[4096]);
        File.SetUnixFileMode(ancho, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        RechazoAlAbrir(informe, runDir, "ancha.fija", 4096, "region-permisos-distintos", "modo 666 frente a 600");

        // Al arrancar se barren los e.<pid> de procesos que ya no existen.
        int muerto = UnixTransport.FindDeadPid();
        if (muerto > 0)
        {
            string huerfano = Path.Combine(baseDir, UnixTransport.RunPrefix + muerto);
            Directory.CreateDirectory(huerfano);
            File.WriteAllText(Path.Combine(huerfano, "m." + muerto), "");
            int n = UnixTransport.SweepOrphans(baseDir);
            informe.Comprueba("huerfanos-barridos", n >= 1 && !Directory.Exists(huerfano), $"barridos {n} (e.{muerto})");
        }
        else
        {
            informe.Comprueba("huerfanos-barridos", false, "no se encontró un pid muerto para fabricar el huérfano");
        }
    }

    private static void RechazoAlAbrir(Informe informe, string runDir, string nombre, long minCapacity, string id, string esperado)
    {
        string detalle;
        bool rechazado;
        try
        {
            using UnixRegion x = UnixRegion.Open(runDir, nombre, minCapacity);
            rechazado = false;
            detalle = "se abrió";
        }
        catch (ModuleCallException ex)
        {
            rechazado = true;
            detalle = ex.Message;
        }
        informe.Comprueba(id, rechazado, $"{esperado}: {detalle}");
    }

    /// <summary>Un invoke y su respuesta final: el primer result/error con ese id, con plazo.</summary>
    private static async Task<ModuleMessage> Invocar(MessageChannel channel, long id, string funcion, IReadOnlyList<SharedBufferRef> buffers)
    {
        await channel.SendAsync(new InvokeMessage(id, funcion, null, buffers)).ConfigureAwait(false);
        using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(15));
        while (true)
        {
            ModuleMessage? m = await channel.ReceiveAsync(plazo.Token).ConfigureAwait(false);
            switch (m)
            {
                case null:
                    throw new ModuleDisconnectedException("El módulo cerró a mitad de la llamada.");
                case ResultMessage r when r.Id == id:
                    return r;
                case ErrorMessage { Id: { } eid } e when eid == id:
                    throw new ModuleCallException(e.Code, e.Message);
            }
        }
    }

    private static async Task EsperaPong(MessageChannel channel, long id)
    {
        using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(5));
        while (await channel.ReceiveAsync(plazo.Token).ConfigureAwait(false) is { } m)
            if (m is PongMessage { Id: var pid } && pid == id)
                return;
        throw new ModuleDisconnectedException("El módulo cerró sin responder al ping.");
    }

    /// <summary>La respuesta es un result con el búfer de salida dentro de la región y la copia es exacta.</summary>
    private static bool EcoVerificado(ModuleMessage res, UnixRegion region, SharedBufferRef entrada, SharedBufferRef salida) =>
        res is ResultMessage { Buffers: [SharedBufferRef b] }
        && b.Region == region.Name && b.Offset == salida.Offset && b.Length == salida.Length
        && region.GetSpan(salida).SequenceEqual(region.GetSpan(entrada));

    /// <summary>Patrón determinista del cuadro (xorshift32): el mismo en todos los sistemas.</summary>
    private static void Patron(Span<byte> span)
    {
        uint x = 0x9E3779B9;
        for (int i = 0; i < span.Length; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            span[i] = (byte)(x + i);
        }
    }

    /// <summary>
    /// La muerte del padre: un proceso intermedio hace de editor (escucha y lanza el módulo), se mata con
    /// SIGKILL y el módulo tiene que desaparecer en menos de 2 s, por la vigilancia que armó o por el EOF.
    /// </summary>
    private static async Task MuertePadre(Informe informe, string runDir)
    {
        string socketPath = Path.Combine(runDir, "p." + Guid.NewGuid().ToString("N")[..8]);
        Process? intermedio = null;
        try
        {
            intermedio = Lanzar("--editor-intermedio", socketPath, salida: true);
            int pidModulo;
            string mecanismo = "sin anunciar";
            using (CancellationTokenSource plazo = new(TimeSpan.FromSeconds(15)))
            {
                string? linea = await intermedio.StandardOutput.ReadLineAsync(plazo.Token).ConfigureAwait(false);
                if (linea is null || !linea.StartsWith("hijo=", StringComparison.Ordinal) || !int.TryParse(linea["hijo=".Length..], out pidModulo))
                {
                    informe.Comprueba("muerte-padre-2s", false, $"el editor intermedio no anunció el pid del módulo (llegó «{linea}»)");
                    return;
                }
            }
            try
            {
                // La línea «modulo: vigilancia: …» puede tardar un poco: es informativa, con 3 s basta.
                using CancellationTokenSource plazo2 = new(TimeSpan.FromSeconds(3));
                while (await intermedio.StandardOutput.ReadLineAsync(plazo2.Token).ConfigureAwait(false) is { } l)
                    if (l.StartsWith("modulo: vigilancia:", StringComparison.Ordinal))
                    {
                        mecanismo = l["modulo: ".Length..];
                        break;
                    }
            }
            catch (OperationCanceledException)
            {
            }

            intermedio.Kill(entireProcessTree: false); // en Unix, SIGKILL.
            long t0 = Stopwatch.GetTimestamp();
            bool desaparecido = false;
            while (Stopwatch.GetElapsedTime(t0) < TimeSpan.FromSeconds(2))
            {
                if (!UnixTransport.PidAlive(pidModulo) || (OperatingSystem.IsLinux() && UnixTransport.PidIsZombie(pidModulo)))
                {
                    desaparecido = true;
                    break;
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            informe.Comprueba("muerte-padre-2s", desaparecido,
                desaparecido
                    ? $"módulo {pidModulo} desapareció en {ms:0} ms ({mecanismo})"
                    : $"módulo {pidModulo} sigue vivo tras 2 s ({mecanismo})");
            if (!desaparecido)
                Posix.Kill(pidModulo, Posix.SIGKILL);
        }
        catch (Exception ex) when (ex is ModuleException or IOException or SocketException or TimeoutException or OperationCanceledException or InvalidOperationException or FormatException)
        {
            informe.Comprueba("muerte-padre-2s", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (intermedio is { HasExited: false })
            {
                try { intermedio.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
            }
            intermedio?.Dispose();
            try { File.Delete(socketPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Lanza el propio ejecutable con un argumento y su valor, sin ventana y con la salida de errores capturada.</summary>
    private static Process Lanzar(string arg, string valor, bool salida = false)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Sin ProcessPath.");
        ProcessStartInfo start = new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = salida,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        start.ArgumentList.Add(arg);
        start.ArgumentList.Add(valor);
        return Process.Start(start) ?? throw new ModuleExitedException("No se pudo abrir el proceso.");
    }

    private static string Cola(Task<string>? stderr) =>
        stderr is { IsCompletedSuccessfully: true } t && t.Result.Trim() is { Length: > 0 } s
            ? " stderr: " + (s.Length > 300 ? s[^300..] : s)
            : "";
}
