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
/// el fallo que se informa es el interbloqueo.
/// </remarks>
internal static class ThreadDump
{
    /// <summary>Cuadros por hilo, como mucho: el fondo de la pila es el arranque del hilo o del pool.</summary>
    private const int MaxFrames = 80;

    /// <summary>
    /// Las pilas administradas de los hilos de <paramref name="names"/> (por su <see cref="Thread.ManagedThreadId"/>, con
    /// ese nombre) y después las de los demás que pasan por código de Rodaje (decodificadores, mezcla, el pool), salvo la
    /// del hilo que llama.
    /// </summary>
    public static string Capture(IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        try
        {
            using DataTarget target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            var text = new StringBuilder();
            foreach (ClrInfo info in target.ClrVersions)
            {
                using ClrRuntime runtime = info.CreateRuntime();
                var threads = runtime.Threads
                    // El que toma la instantánea solo mostraría la propia instantánea.
                    .Where(t => t.IsAlive && t.ManagedThreadId != Environment.CurrentManagedThreadId)
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
