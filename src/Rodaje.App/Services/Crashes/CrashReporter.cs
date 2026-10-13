using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Rodaje.App.Services.Crashes;

/// <summary>Por dónde llegó el error que se informa.</summary>
public enum CrashKind
{
    /// <summary><c>Application.UnhandledException</c> de WinUI (un manejador de XAML lanzó).</summary>
    XamlUnhandled,

    /// <summary><c>AppDomain.UnhandledException</c>: un hilo cualquiera lanzó y nadie lo atrapó.</summary>
    AppDomainUnhandled,

    /// <summary><c>TaskScheduler.UnobservedTaskException</c>: una tarea falló y nadie la esperó.</summary>
    UnobservedTask,

    /// <summary>Un callback del hilo de la interfaz (continuación de un <c>await</c>, <c>async void</c>) lanzó.</summary>
    DispatcherCallback,

    /// <summary>Excepción nativa que nadie atrapó (violación de acceso, error «stowed» de WinRT 0xC000027B).</summary>
    NativeFatal,

    /// <summary>La sesión anterior terminó sin cerrarse y sin dejar informe (lo detecta el siguiente arranque).</summary>
    UncleanExit,

    /// <summary>Error que el código atrapó y avisó en la ventana (<c>Program.WriteCrash</c>).</summary>
    Caught,

    /// <summary>Error al arrancar, antes de que haya ventana.</summary>
    Startup,
}

/// <summary>
/// Escribe los informes de fallo en <c>%LOCALAPPDATA%\Rodaje\fallos\</c>: la vía, la pila, la versión (<c>1.0.0+commit</c>),
/// la GPU y las últimas líneas del <see cref="SessionLog"/>. Sin WinUI, para poder probar cada vía. Los fatales se llaman
/// <c>&lt;fecha&gt;.txt</c> y los que la app sobrevivió <c>&lt;fecha&gt;-recuperado.txt</c> (uno por firma cada
/// <see cref="BurstWindow"/>); al siguiente arranque se
/// avisa de los fatales nuevos (<see cref="PendingReports"/>). Nunca lanza: si no puede escribir, devuelve <c>null</c>.
/// </summary>
public sealed class CrashReporter
{
    /// <summary>Carpeta de los informes dentro de la de datos.</summary>
    public const string FolderName = "fallos";

    /// <summary>Código de excepción de un error «stowed» de WinRT (STATUS_STOWED_EXCEPTION).</summary>
    public const uint StowedExceptionCode = 0xC000027B;

    /// <summary>Informes de texto que se conservan de cada clase (fatales y recuperados); los más viejos se borran.</summary>
    public const int KeepReports = 30;

    public const int KeepDumps = 3;

    /// <summary>
    /// Un error recuperado que se repite (la misma firma: tipo y primera línea de la pila) deja un solo informe en este
    /// intervalo; las repeticiones se cuentan y se anotan en ese informe («repetido N veces»).
    /// </summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);

    // Firmas recordadas como mucho; pasado el tope se olvidan las que ya salieron del intervalo.
    private const int MaxBursts = 64;

    private const string RecoveredSuffix = "-recuperado";
    private const string SessionsFolder = "sesiones";
    private const string SeenMarker = ".visto";

    private readonly SessionLog _log;
    private readonly Func<string?>? _gpu;
    private readonly Func<DateTime> _clock;
    private readonly Lock _gate = new();
    private readonly ConditionalWeakTable<Exception, Reported> _reported = [];
    private readonly Dictionary<string, Burst> _bursts = new(StringComparer.Ordinal);
    private string? _gpuCache;
    private string? _fatalReport;

    public CrashReporter(string folder, string version, SessionLog log, Func<string?>? gpu = null, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(log);
        Folder = Path.GetFullPath(folder);
        Version = string.IsNullOrWhiteSpace(version) ? "?" : version;
        _log = log;
        _gpu = gpu;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary><c>%LOCALAPPDATA%\Rodaje\fallos</c> (o la de <see cref="AppDataPaths.OverrideVariable"/>).</summary>
    public static string DefaultFolder => Path.Combine(AppDataPaths.Root, FolderName);

    public string Folder { get; }

    public string Version { get; }

    public SessionLog Log => _log;

    /// <summary>
    /// Quién escribe el minivolcado de un fallo nativo (ruta, <c>EXCEPTION_POINTERS*</c>); las pruebas lo cambian porque
    /// volcar un proceso sano desde dentro puede trabarse.
    /// </summary>
    public Func<string, nint, bool> MinidumpWriter { get; init; } = WriteOwnMinidump;

    /// <summary>Lee la GPU ahora (fuera de un fallo), para que un fallo nativo no tenga que tocar DXGI.</summary>
    public void WarmUp() => _ = DescribeGpu(probe: true);

    // ---- Vías de excepción ----

    /// <summary>Manejador de <c>AppDomain.CurrentDomain.UnhandledException</c>.</summary>
    public void OnAppDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ExceptionObject is Exception ex)
        {
            Write(CrashKind.AppDomainUnhandled, ex, e.IsTerminating);
        }
        else
        {
            Write(CrashKind.AppDomainUnhandled, e.ExceptionObject?.ToString() ?? "(sin detalles)", e.IsTerminating);
        }
    }

    /// <summary>Manejador de <c>TaskScheduler.UnobservedTaskException</c>: se informa y se da por observada (la app sigue).</summary>
    public void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Write(CrashKind.UnobservedTask, e.Exception, fatal: false);
        e.SetObserved();
    }

    /// <summary>Informa una excepción. La misma excepción por dos vías deja un solo informe (salvo que la segunda sea fatal).</summary>
    public string? Write(CrashKind kind, Exception exception, bool fatal) => Write(kind, exception, fatal, out _);

    /// <summary>
    /// Como <see cref="Write(CrashKind, Exception, bool)"/>; <paramref name="repeated"/> dice que no se escribió nada
    /// nuevo (la misma excepción ya informada, o un recuperado con la misma firma dentro de <see cref="BurstWindow"/>):
    /// quien llama no vuelve a avisar.
    /// </summary>
    public string? Write(CrashKind kind, Exception exception, bool fatal, out bool repeated)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            if (_reported.TryGetValue(exception, out Reported? before) && (before.Fatal || !fatal))
            {
                repeated = true;
                return before.Path;
            }

            string? path = WriteCore(kind, exception.ToString(), fatal, null, out repeated);
            _reported.AddOrUpdate(exception, new Reported(path, fatal));
            return path;
        }
    }

    /// <summary>Informa un error con su texto (pila o mensaje). Un fatal cierra la copia del registro: ya tiene informe.</summary>
    public string? Write(CrashKind kind, string? details, bool fatal, string? dumpPath = null)
    {
        lock (_gate)
        {
            return WriteCore(kind, details, fatal, dumpPath, out _);
        }
    }

    /// <summary>Como <see cref="Write(CrashKind, string, bool, string)"/>, y dice si era una repetición sin informe propio.</summary>
    public string? Write(CrashKind kind, string? details, bool fatal, out bool repeated)
    {
        lock (_gate)
        {
            return WriteCore(kind, details, fatal, null, out repeated);
        }
    }

    /// <summary>
    /// Firma de un error para agrupar las repeticiones: el tipo (lo que va antes de «: » en la primera línea) y la primera
    /// línea de la pila («at …»). Sin pila, la primera línea entera.
    /// </summary>
    public static string Signature(string? details)
    {
        string[] lines = (details ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        string first = lines[0].Trim();
        string? frame = lines.Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal));
        if (frame is null)
        {
            return first;
        }

        int colon = first.IndexOf(": ", StringComparison.Ordinal);
        return (colon > 0 ? first[..colon] : first) + "\n" + frame;
    }

    /// <summary>
    /// Fallo nativo fatal (lo llama el filtro de <see cref="NativeCrashHandler"/>): minivolcado e informe. Si ya hay un
    /// informe fatal de esta sesión (la excepción administrada que lo causó), solo agrega el minivolcado a su lado.
    /// </summary>
    public unsafe string? WriteNativeFatal(uint exceptionCode, void* exceptionPointers)
    {
        // Si otro hilo quedó a mitad de un informe (el fallo lo detuvo), no se espera para siempre: solo el minivolcado.
        if (!_gate.TryEnter(TimeSpan.FromSeconds(2)))
        {
            string? alone = NewPath(_clock(), ".dmp");
            return alone is not null && MinidumpWriter(alone, (nint)exceptionPointers) ? alone : null;
        }

        try
        {
            // Primero el informe de texto, que es rápido y casi nunca falla; después el minivolcado, que puede trabarse
            // (lo corta el vigilante de NativeCrashHandler) y entonces el texto ya está en disco.
            string? report = _fatalReport ?? WriteCore(CrashKind.NativeFatal, DescribeNativeCode(exceptionCode), fatal: true, null, out _);
            string? dump = report is not null ? Path.ChangeExtension(report, ".dmp") : NewPath(_clock(), ".dmp");
            if (dump is not null && MinidumpWriter(dump, (nint)exceptionPointers))
            {
                if (report is not null)
                {
                    TryAppend(report, $"{Environment.NewLine}Minivolcado: {CrashReportScrubber.Default.Scrub(dump)}{Environment.NewLine}");
                }

                Prune();
            }
            else
            {
                dump = null;
            }

            return report ?? dump;
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Texto del código de una excepción nativa, con nombre para los conocidos.</summary>
    public static string DescribeNativeCode(uint code)
    {
        string name = code switch
        {
            StowedExceptionCode => "error «stowed» de WinRT (STATUS_STOWED_EXCEPTION): una excepción de un manejador de XAML o de DispatcherQueue que nadie atrapó",
            0xC0000005 => "violación de acceso (STATUS_ACCESS_VIOLATION)",
            0xC00000FD => "desbordamiento de pila (STATUS_STACK_OVERFLOW)",
            0xC0000409 => "cierre inmediato (STATUS_STACK_BUFFER_OVERRUN / __fastfail)",
            0xE0434352 => "excepción de .NET (CLR)",
            0x80000003 => "punto de interrupción",
            _ => "excepción nativa",
        };
        return string.Create(CultureInfo.InvariantCulture, $"Código 0x{code:X8}: {name}.");
    }

    // ---- Sesiones ----

    /// <summary>
    /// Empieza la copia en disco del registro de esta sesión. Si el proceso termina sin <see cref="EndSession"/> ni un
    /// informe fatal, el siguiente arranque la encuentra (<see cref="RecoverUncleanSessions"/>).
    /// </summary>
    public void StartSession(int processId, long processStartTicks) =>
        _log.MirrorTo(Path.Combine(Folder, SessionsFolder, string.Create(CultureInfo.InvariantCulture, $"{processId}-{processStartTicks}.log")));

    /// <summary>La de este proceso.</summary>
    public void StartSession()
    {
        using var process = Process.GetCurrentProcess();
        StartSession(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    /// <summary>
    /// Empieza la sesión: convierte en informe las anteriores que terminaron sin cerrarse y abre la copia en disco de
    /// esta. Una corrida automática (medida, autoprueba, arranque; <c>AppOptions.IsAutomated</c>) no hace ni lo uno ni
    /// lo otro: las herramientas la matan cuando les conviene y eso no es un fallo, ni deben gastar el informe de una
    /// sesión del usuario que sí terminó mal. Devuelve los informes escritos.
    /// </summary>
    public IReadOnlyList<string> BeginSession(bool automated)
    {
        if (automated)
        {
            _log.Append("Corrida automática: sin copia en disco del registro ni recuperación de sesiones.");
            return [];
        }

        IReadOnlyList<string> recovered = RecoverUncleanSessions();
        StartSession();
        return recovered;
    }

    /// <summary>
    /// Cierre limpio: anota las repeticiones pendientes y borra la copia del registro. Se puede llamar más de una vez
    /// (al volver de <c>Application.Start</c>, con <c>WM_ENDSESSION</c> y al salir el proceso).
    /// </summary>
    public void EndSession()
    {
        lock (_gate)
        {
            FlushBursts();
        }

        _log.CloseMirror(delete: true);
    }

    /// <summary>
    /// Convierte en informe cada sesión anterior que terminó sin cerrarse (su copia del registro quedó y su proceso ya no
    /// existe) y borra la copia. Devuelve los informes escritos.
    /// </summary>
    public IReadOnlyList<string> RecoverUncleanSessions(Func<int, long, bool>? isAlive = null)
    {
        isAlive ??= IsProcessAlive;
        var written = new List<string>();
        string folder = Path.Combine(Folder, SessionsFolder);
        string[] files;
        try
        {
            files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.log") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return written;
        }

        foreach (string file in files)
        {
            if (file == _log.MirrorPath || !TryParseSession(Path.GetFileNameWithoutExtension(file), out int pid, out long ticks) || isAlive(pid, ticks))
            {
                continue;
            }

            try
            {
                IReadOnlyList<string> tail = SessionLog.ReadTail(file, _log.Capacity);
                DateTime ended = File.GetLastWriteTime(file);
                lock (_gate)
                {
                    string? path = NewPath(_clock(), ".txt");
                    var report = new CrashReport(CrashKind.UncleanExit, ended, Fatal: true, Version, DescribeGpu(probe: false),
                        string.Create(CultureInfo.InvariantCulture,
                            $"La sesión del proceso {pid} terminó sin cerrarse y sin dejar informe: un fallo nativo que no pasa por los filtros (error «stowed» de WinRT 0xC000027B, __fastfail, desbordamiento de pila), un cierre desde el Administrador de tareas o un apagón. Última escritura del registro: {ended:yyyy-MM-dd HH:mm:ss}."),
                        null, tail);
                    if (path is not null && TryWrite(path, report.Format()))
                    {
                        written.Add(path);
                    }
                }

                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (written.Count > 0)
        {
            Prune();
        }

        return written;
    }

    // ---- Aviso al siguiente arranque ----

    /// <summary>Informes fatales escritos desde el último <see cref="AcknowledgePending"/>, del más viejo al más nuevo.</summary>
    public IReadOnlyList<string> PendingReports()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }

            string seen = Path.Combine(Folder, SeenMarker);
            DateTime since = File.Exists(seen) ? File.GetLastWriteTimeUtc(seen) : DateTime.MinValue;
            return [.. Directory.GetFiles(Folder, "*.txt")
                .Where(f => !IsRecovered(f))
                .Where(f => File.GetLastWriteTimeUtc(f) > since)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Marca como vistos los informes pendientes (ya se avisó de ellos).</summary>
    public void AcknowledgePending()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string seen = Path.Combine(Folder, SeenMarker);
            File.WriteAllText(seen, string.Empty);
            File.SetLastWriteTimeUtc(seen, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>El texto del aviso para los informes pendientes (al menos uno).</summary>
    public static string DescribePending(IReadOnlyList<string> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        string latest = Path.Combine(FolderName, Path.GetFileName(pending[^1]));
        return pending.Count == 1
            ? $"La última vez Rodaje se cerró por un error. El informe quedó en {latest}, dentro de la carpeta de datos de Rodaje; cópialo y envíalo para que se pueda arreglar."
            : string.Create(CultureInfo.InvariantCulture,
                $"Rodaje se cerró por un error {pending.Count} veces desde la última vez. El informe más reciente quedó en {latest}, dentro de la carpeta de datos de Rodaje; cópialo y envíalo para que se pueda arreglar.");
    }

    // ---- Interno ----

    private static unsafe bool WriteOwnMinidump(string path, nint exceptionPointers) =>
        NativeCrashHandler.WriteMinidump(path, (void*)exceptionPointers);

    private string? DescribeGpu(bool probe)
    {
        if (_gpuCache is not null || !probe || _gpu is null)
        {
            return _gpuCache;
        }

        try
        {
            _gpuCache = _gpu();
        }
#pragma warning disable CA1031 // Leer la GPU no puede impedir el informe.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return _gpuCache;
    }

    private string? NewPath(DateTime now, string suffix)
    {
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string stem = now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        string path = Path.Combine(Folder, stem + suffix);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{suffix}"));
        }

        return path;
    }

    private static bool TryWrite(string path, string text)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryAppend(string path, string text)
    {
        try
        {
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string? WriteCore(CrashKind kind, string? details, bool fatal, string? dumpPath, out bool repeated)
    {
        DateTime now = _clock();
        repeated = false;
        string? signature = null;
        if (!fatal)
        {
            signature = Signature(details);
            if (_bursts.TryGetValue(signature, out Burst? burst))
            {
                if (now >= burst.Written && now - burst.Written < BurstWindow)
                {
                    burst.Repeated++;
                    burst.LastRepeat = now;
                    repeated = true;
                    return burst.Path;
                }

                FlushBurst(burst);
                _ = _bursts.Remove(signature);
            }
        }

        string? path = NewPath(now, fatal ? ".txt" : RecoveredSuffix + ".txt");
        if (path is not null)
        {
            var report = new CrashReport(kind, now, fatal, Version, DescribeGpu(probe: kind != CrashKind.NativeFatal), details ?? "(sin detalles)",
                dumpPath, _log.Tail());
            if (!TryWrite(path, report.Format()))
            {
                path = null;
            }
        }

        if (signature is not null)
        {
            // También si no se pudo escribir: con el disco lleno, una ráfaga no reintenta en cada repetición.
            RememberBurst(signature, new Burst(path, now));
        }

        if (path is null)
        {
            return null;
        }

        if (fatal)
        {
            _fatalReport = path;
            FlushBursts();
            _log.CloseMirror(delete: true);
        }

        Prune();
        return path;
    }

    private void RememberBurst(string signature, Burst burst)
    {
        if (_bursts.Count >= MaxBursts)
        {
            DateTime now = burst.Written;
            foreach ((string key, Burst old) in _bursts.Where(p => now - p.Value.Written >= BurstWindow || now < p.Value.Written).ToList())
            {
                FlushBurst(old);
                _ = _bursts.Remove(key);
            }

            if (_bursts.Count >= MaxBursts)
            {
                // Todas siguen dentro del intervalo (muchas firmas distintas a la vez): se olvida la más vieja.
                KeyValuePair<string, Burst> oldest = _bursts.MinBy(p => p.Value.Written);
                FlushBurst(oldest.Value);
                _ = _bursts.Remove(oldest.Key);
            }
        }

        _bursts[signature] = burst;
    }

    // Anota en su informe cuántas veces se repitió el error sin informe propio.
    private static void FlushBurst(Burst burst)
    {
        if (burst.Repeated == 0 || burst.Path is null)
        {
            return;
        }

        TryAppend(burst.Path, string.Create(CultureInfo.InvariantCulture,
            $"{Environment.NewLine}Repetido {burst.Repeated} veces más hasta las {burst.LastRepeat:HH:mm:ss.fff} (un informe por error cada {BurstWindow.TotalSeconds:0} s).{Environment.NewLine}"));
        burst.Repeated = 0;
    }

    private void FlushBursts()
    {
        foreach (Burst burst in _bursts.Values)
        {
            FlushBurst(burst);
        }
    }

    // Fatales y recuperados se guardan por separado: una ráfaga de recuperados no borra el fatal que espera su aviso.
    private void Prune()
    {
        try
        {
            string[] reports = Directory.GetFiles(Folder, "*.txt");
            IEnumerable<string>[] groups =
            [
                reports.Where(IsRecovered).Order(StringComparer.Ordinal).SkipLast(KeepReports),
                reports.Where(f => !IsRecovered(f)).Order(StringComparer.Ordinal).SkipLast(KeepReports),
                Directory.GetFiles(Folder, "*.dmp").Order(StringComparer.Ordinal).SkipLast(KeepDumps),
            ];
            foreach (string old in groups.SelectMany(g => g).ToList())
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryParseSession(string name, out int pid, out long ticks)
    {
        ticks = 0;
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        pid = 0;
        return dash > 0
            && int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
    }

    private static bool IsProcessAlive(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsRecovered(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith(RecoveredSuffix, StringComparison.Ordinal);

    private sealed record Reported(string? Path, bool Fatal);

    private sealed class Burst(string? path, DateTime written)
    {
        public string? Path { get; } = path;

        public DateTime Written { get; } = written;

        public int Repeated { get; set; }

        public DateTime LastRepeat { get; set; }
    }
}

/// <summary>Un informe de fallo y su texto.</summary>
public sealed record CrashReport(CrashKind Kind, DateTime Time, bool Fatal, string Version, string? Gpu, string Details, string? DumpPath,
    IReadOnlyList<string> LogTail)
{
    /// <summary>Nombre de la vía en el informe.</summary>
    public static string Describe(CrashKind kind) => kind switch
    {
        CrashKind.XamlUnhandled => "Application.UnhandledException (WinUI)",
        CrashKind.AppDomainUnhandled => "AppDomain.UnhandledException",
        CrashKind.UnobservedTask => "TaskScheduler.UnobservedTaskException",
        CrashKind.DispatcherCallback => "callback de DispatcherQueue (hilo de la interfaz)",
        CrashKind.NativeFatal => "excepción nativa no atrapada (SetUnhandledExceptionFilter)",
        CrashKind.UncleanExit => "la sesión anterior terminó sin cerrarse",
        CrashKind.Caught => "error atrapado y avisado en la ventana",
        CrashKind.Startup => "error al arrancar",
        _ => kind.ToString(),
    };

    public string Format()
    {
        var text = new StringBuilder();
        text.AppendLine("Informe de fallo de Rodaje");
        text.AppendLine(CultureInfo.InvariantCulture, $"Fecha: {Time:yyyy-MM-dd HH:mm:ss.fff}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Vía: {Describe(Kind)}");
        text.AppendLine(Fatal ? "La app se cerró: sí" : "La app se cerró: no (siguió abierta)");
        text.AppendLine(CultureInfo.InvariantCulture, $"Versión: {Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Windows: {Environment.OSVersion.VersionString} ({(Environment.Is64BitProcess ? "64" : "32")} bits)");
        text.AppendLine(CultureInfo.InvariantCulture, $"GPU: {Gpu ?? "(desconocida)"}");
        if (DumpPath is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Minivolcado: {DumpPath}");
        }

        text.AppendLine();
        text.AppendLine("== Error ==");
        text.AppendLine(Details.TrimEnd());
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"== Últimas {LogTail.Count} líneas del registro de la sesión ==");
        foreach (string line in LogTail)
        {
            text.AppendLine(line);
        }

        // Se invita a pegarlo en GitHub, que es público: sin rutas de medios ni carpetas del usuario.
        return CrashReportScrubber.Default.Scrub(text.ToString());
    }
}
