using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace Rodaje.Engine.Tests.Support;

/// <summary>
/// Las pilas administradas de todos los hilos del proceso, en texto: lo que el arnés de estrés agrega al mensaje cuando da
/// con un interbloqueo, para ver en el registro de la CI dónde estaba cada hilo (y no solo cuál no avanzó).
/// </summary>
/// <remarks>
/// Toma una instantánea del propio proceso con ClrMD (en Windows, <c>PssCaptureSnapshot</c>; en Linux, un volcado que
/// escribe <c>createdump</c>) y la recorre; los hilos siguen corriendo. Si no puede, lo dice en el texto en vez de lanzar:
/// el fallo que se informa es el interbloqueo. La instantánea corre en un hilo propio con un plazo de corte
/// (<see cref="DefaultTimeout"/>), para que un <c>createdump</c> colgado no congele la prueba.
/// </remarks>
internal static class ThreadDump
{
    /// <summary>Cuadros por hilo, como mucho: el fondo de la pila es el arranque del hilo o del pool.</summary>
    private const int MaxFrames = 80;

    /// <summary>
    /// Plazo de corte de la instantánea: solo evita que la prueba se congele si <c>createdump</c> se cuelga o el volcado es
    /// enorme justo cuando se intenta diagnosticar un interbloqueo. No decide el resultado de ninguna prueba.
    /// </summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Comienzo del texto que devuelve <see cref="Capture(IReadOnlyDictionary{int, string})"/> si venció el plazo.</summary>
    internal const string TimedOutPrefix = "(pilas no disponibles: la instantánea no terminó en";

    /// <summary>
    /// Las pilas administradas de los hilos de <paramref name="names"/> (por su <see cref="Thread.ManagedThreadId"/>, con
    /// ese nombre) y después las de los demás que pasan por código de Rodaje (decodificadores, mezcla, el pool), salvo la
    /// del hilo que llama. Si la instantánea no termina en <see cref="DefaultTimeout"/>, lo dice en el texto.
    /// </summary>
    public static string Capture(IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        int caller = Environment.CurrentManagedThreadId;
        return Capture(() => CaptureSnapshot(names, caller), DefaultTimeout);
    }

    /// <summary>
    /// Corre <paramref name="capture"/> en un hilo propio (de fondo, para no retener el proceso si se cuelga) y devuelve su
    /// texto, o el de plazo vencido si no vuelve antes de <paramref name="timeout"/>; en ese caso intenta matar el
    /// <c>createdump</c> hijo que pudo dejar esta captura (no los que ya estaban antes de empezarla, que son de otra). La
    /// costura de las pruebas: la captura real es <see cref="CaptureSnapshot"/>.
    /// </summary>
    internal static string Capture(Func<string> capture, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(capture);

        // Los createdump hijos que ya había: si vence el plazo, esos no son de esta captura y no se tocan.
        HashSet<(int Pid, ulong StartTime)> before = CreateDumpChildren();
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = capture();
            }
#pragma warning disable CA1031 // Sin pilas, el interbloqueo se informa igual.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                result = $"(no se pudieron leer las pilas: {ex.GetType().Name}: {ex.Message})";
            }
        })
        { IsBackground = true, Name = "Instantánea de pilas" };
        thread.Start();
        if (!thread.Join(timeout))
        {
            KillCreateDumpChildren(before);
            return string.Create(CultureInfo.InvariantCulture, $"{TimedOutPrefix} {timeout.TotalSeconds:0.###} s)");
        }

        return result ?? "(no se pudieron leer las pilas: la captura no devolvió texto)";
    }

    /// <summary>
    /// En Linux, ClrMD escribe el volcado con un <c>createdump</c> hijo de este proceso: si la instantánea venció, se mata
    /// para que no siga escribiendo un volcado enorme ni quede huérfano. Solo los que nacieron después de empezar la
    /// captura: los de <paramref name="before"/> (pid y hora de inicio, para no confundir un pid reciclado) son de otra
    /// captura en curso. En Windows (<c>PssCaptureSnapshot</c>) no hay hijo.
    /// </summary>
    private static void KillCreateDumpChildren(HashSet<(int Pid, ulong StartTime)> before)
    {
        foreach ((int pid, ulong startTime) in CreateDumpChildren())
        {
            if (before.Contains((pid, startTime)))
            {
                continue;
            }

            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(pid);
                child.Kill();
            }
#pragma warning disable CA1031 // El proceso pudo terminar entre leerlo y matarlo: matarlo es un intento.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>
    /// Los procesos hijos de este cuyo nombre empieza por <c>createdump</c>, con su hora de inicio (en tics desde el
    /// arranque del sistema), leídos de <c>/proc</c>. Fuera de Linux, o si <c>/proc</c> no se puede leer, ninguno.
    /// </summary>
    private static HashSet<(int Pid, ulong StartTime)> CreateDumpChildren()
    {
        var children = new HashSet<(int Pid, ulong StartTime)>();
        if (!OperatingSystem.IsLinux())
        {
            return children;
        }

        try
        {
            string self = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            foreach (string dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                {
                    continue;
                }

                try
                {
                    // /proc/<pid>/stat: «pid (comm) estado ppid …», con la hora de inicio en el campo 22; el comm puede
                    // llevar espacios y paréntesis, así que los campos se cuentan desde el último «)».
                    string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    int close = stat.LastIndexOf(')');
                    int open = stat.IndexOf('(', StringComparison.Ordinal);
                    if (open < 0 || close < open)
                    {
                        continue;
                    }

                    string comm = stat[(open + 1)..close];
                    string[] rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (rest.Length > 19
                        && rest[1] == self
                        && comm.StartsWith("createdump", StringComparison.Ordinal)
                        && ulong.TryParse(rest[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong startTime))
                    {
                        children.Add((pid, startTime));
                    }
                }
#pragma warning disable CA1031 // El proceso pudo terminar entre listarlo y leerlo.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
#pragma warning disable CA1031 // Buscar el hijo es un intento: el plazo vencido se informa igual.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return children;
    }

    /// <summary>La captura real: la instantánea con ClrMD, sin los hilos <paramref name="caller"/> ni el que la toma.</summary>
    private static string CaptureSnapshot(IReadOnlyDictionary<int, string> names, int caller)
    {
        try
        {
            using DataTarget target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            var text = new StringBuilder();
            foreach (ClrInfo info in target.ClrVersions)
            {
                using ClrRuntime runtime = info.CreateRuntime();
                var threads = runtime.Threads
                    // El que toma la instantánea solo mostraría la propia instantánea, y el que llama, su espera.
                    .Where(t => t.IsAlive && t.ManagedThreadId != Environment.CurrentManagedThreadId && t.ManagedThreadId != caller)
                    .Select(t => (Thread: t, Frames: Frames(t)))
                    // Los del ejecutor de pruebas no dicen nada: solo los nombrados y los que pasan por el código de Rodaje.
                    .Where(t => t.Frames.Count > 0
                        && (names.ContainsKey(t.Thread.ManagedThreadId) || t.Frames.Exists(f => f.StartsWith("Rodaje.", StringComparison.Ordinal))))
                    .OrderBy(t => names.ContainsKey(t.Thread.ManagedThreadId) ? 0 : 1)
                    .ThenBy(t => t.Thread.ManagedThreadId);
                foreach ((ClrThread thread, List<string> frames) in threads)
                {
                    string name = names.TryGetValue(thread.ManagedThreadId, out string? known) ? known : "hilo";
                    text.AppendLine(CultureInfo.InvariantCulture, $"--- {name} (administrado {thread.ManagedThreadId}, SO {thread.OSThreadId})");
                    foreach (string frame in frames)
                    {
                        text.Append("    ").AppendLine(frame);
                    }
                }
            }

            return text.Length == 0 ? "(la instantánea no tenía hilos administrados)" : text.ToString();
        }
#pragma warning disable CA1031 // Sin pilas, el interbloqueo se informa igual.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return $"(no se pudieron leer las pilas: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static List<string> Frames(ClrThread thread)
    {
        var frames = new List<string>();
        foreach (ClrStackFrame frame in thread.EnumerateStackTrace().Take(MaxFrames))
        {
            // Debajo de la llamada a la prueba solo queda el ejecutor de pruebas.
            if (frame.Method?.Type?.Name is { } type && (type.StartsWith("Xunit.", StringComparison.Ordinal) || type.StartsWith("System.Reflection.", StringComparison.Ordinal)))
            {
                break;
            }

            // Los marcos del runtime (transiciones a código nativo) no dicen nada.
            if (frame.Kind == ClrStackFrameKind.ManagedMethod && frame.Method?.Signature is { } signature)
            {
                frames.Add(signature);
            }
        }

        return frames;
    }
}
