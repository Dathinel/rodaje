using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// Los tramos de un caso de fuzz (tarea 290), para el mensaje de un cuelgue. En la CI de Windows un caso se paró 5,4 s en
/// «volver a leer», que en local tarda 2 ms, con el pool libre: con solo el nombre del tramo no se distinguía una pausa
/// del GC que para el mundo (otras pruebas del proceso hacen <c>GC.Collect</c> en paralelo), un bloqueo de E/S (antivirus)
/// o un runner que no le dio CPU al proceso. Cada tramo guarda su duración, la CPU del proceso y la del hilo que lo marcó,
/// y la pausa del GC; el mensaje añade las colecciones por generación desde el principio del caso y el estado del hilo.
/// </summary>
internal sealed partial class PhaseLog
{
    private readonly Lock _gate = new();
    private readonly List<Phase> _phases = [];
    private Sample _caseStart;
    private int[] _caseCollections = [0, 0, 0];
    private long _progress;

    /// <summary>Cuántos tramos se marcaron: el avance que mira la vigilancia contra cuelgues (<c>StallWatch</c>).</summary>
    public long Progress => Interlocked.Read(ref _progress);

    /// <summary>Empieza un caso nuevo con su primer tramo.</summary>
    public void StartCase(string phase)
    {
        Sample now = Sample.Take();
        lock (_gate)
        {
            _phases.Clear();
            _caseStart = now;
            _caseCollections = Collections();
            _phases.Add(new Phase(phase, now));
        }

        Interlocked.Increment(ref _progress);
    }

    /// <summary>Cierra el tramo en curso y abre <paramref name="phase"/>, en el hilo que llama.</summary>
    public void Mark(string phase)
    {
        Sample now = Sample.Take();
        lock (_gate)
        {
            _phases.Add(new Phase(phase, now));
        }

        Interlocked.Increment(ref _progress);
    }

    /// <summary>El tramo en curso y desde cuándo (para las comprobaciones).</summary>
    public string Current
    {
        get
        {
            lock (_gate)
            {
                return _phases.Count == 0 ? "" : _phases[^1].Name;
            }
        }
    }

    /// <summary>
    /// Los tramos del caso hasta ahora (los pasos de edición, juntos: cuántos, cuánto y el más lento), lo que hizo el GC
    /// en el caso y en el tramo en curso, y el hilo de ese tramo: vivo o no, su estado administrado y el del sistema
    /// (con el motivo de la espera) y la CPU que gastó en el tramo.
    /// </summary>
    public string Describe()
    {
        Sample now = Sample.Take();
        int[] collections = Collections();
        Phase[] phases;
        Sample caseStart;
        int[] caseCollections;
        lock (_gate)
        {
            phases = [.. _phases];
            caseStart = _caseStart;
            caseCollections = _caseCollections;
        }

        if (phases.Length == 0)
        {
            return "Sin tramos marcados.";
        }

        var text = new StringBuilder();
        Phase current = phases[^1];
        text.Append(Invariant($"Iba por «{current.Name}» desde hace {Seconds(current.Start, now):F1} s. Tramos:"));
        int steps = 0;
        double stepsSeconds = 0;
        string slowest = "";
        double slowestSeconds = -1;
        for (int i = 0; i < phases.Length - 1; i++)
        {
            Sample end = phases[i + 1].Start;
            double seconds = Seconds(phases[i].Start, end);
            if (phases[i].Name.StartsWith("paso ", StringComparison.Ordinal))
            {
                steps++;
                stepsSeconds += seconds;
                if (seconds > slowestSeconds)
                {
                    slowestSeconds = seconds;
                    slowest = phases[i].Name;
                }

                continue;
            }

            AppendSteps(text, ref steps, stepsSeconds, slowest, slowestSeconds);
            stepsSeconds = 0;
            slowestSeconds = -1;
            text.Append(Invariant($" «{phases[i].Name}» {Milliseconds(phases[i].Start, end)} ({Megabytes(phases[i].Start, end)} asignados en el proceso);"));
        }

        AppendSteps(text, ref steps, stepsSeconds, slowest, slowestSeconds);
        text.Append(Invariant($" «{current.Name}» {Milliseconds(current.Start, now)} ({Megabytes(current.Start, now)} asignados) hasta ahora."));
        text.Append(Invariant($" GC en el caso ({Seconds(caseStart, now):F1} s): {(now.GcPause - caseStart.GcPause).TotalMilliseconds:0} ms de pausa (total del proceso {now.GcPause.TotalMilliseconds:0} ms), colecciones gen0/1/2 {Delta(collections, caseCollections)} (al empezar {string.Join('/', caseCollections)}, ahora {string.Join('/', collections)})"));
        text.Append(Invariant($"; en el tramo en curso, {(now.GcPause - current.Start.GcPause).TotalMilliseconds:0} ms de pausa y el proceso usó {(now.ProcessCpu - current.Start.ProcessCpu).TotalSeconds:0.00} s de CPU ({Environment.ProcessorCount} núcleos); GC {(System.Runtime.GCSettings.IsServerGC ? "de servidor" : "de estación")}, {System.Runtime.GCSettings.LatencyMode}."));
        text.Append(' ').Append(DescribeThread(current.Start));
        return text.ToString();
    }

    private static void AppendSteps(StringBuilder text, ref int steps, double seconds, string slowest, double slowestSeconds)
    {
        if (steps == 0)
        {
            return;
        }

        text.Append(Invariant($" {steps} pasos {seconds * 1000:0} ms (el más lento, «{slowest}», {slowestSeconds * 1000:0} ms);"));
        steps = 0;
    }

    private static string DescribeThread(Sample start)
    {
        Thread? thread = start.Thread;
        if (thread is null)
        {
            return "Hilo del tramo: desconocido.";
        }

        var text = new StringBuilder(Invariant($"Hilo del tramo «{thread.Name}»: {(thread.IsAlive ? "vivo" : "terminado")}, {thread.ThreadState}"));
        if (start.OsThreadId is { } id)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                ProcessThread? native = process.Threads.Cast<ProcessThread>().FirstOrDefault(t => t.Id == id);
                if (native is null)
                {
                    text.Append(Invariant($"; el hilo {id} ya no está en el sistema"));
                }
                else
                {
                    text.Append(Invariant($"; en el sistema (hilo {id}) {native.ThreadState}"));
                    if (native.ThreadState == System.Diagnostics.ThreadState.Wait)
                    {
                        text.Append(Invariant($" por {native.WaitReason}"));
                    }

                    if (start.ThreadCpu is { } before)
                    {
                        text.Append(Invariant($", {(native.TotalProcessorTime - before).TotalMilliseconds:0} ms de CPU en el tramo"));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                text.Append(Invariant($"; sin estado del sistema ({ex.GetType().Name})"));
            }
        }

        return text.Append('.').ToString();
    }

    private static int[] Collections() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static string Delta(int[] now, int[] before) => Invariant($"+{now[0] - before[0]}/+{now[1] - before[1]}/+{now[2] - before[2]}");

    private static double Seconds(Sample from, Sample to) => Stopwatch.GetElapsedTime(from.Ticks, to.Ticks).TotalSeconds;

    private static string Milliseconds(Sample from, Sample to) => Invariant($"{Seconds(from, to) * 1000:0} ms");

    private static string Megabytes(Sample from, Sample to) => Invariant($"{(to.Allocated - from.Allocated) / (1024.0 * 1024):0.0} MB");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private readonly record struct Phase(string Name, Sample Start);

    /// <summary>Un instante: reloj, CPU, memoria asignada y pausa del GC del proceso, y el hilo que lo tomó con su CPU (si se puede leer).</summary>
    private readonly record struct Sample(long Ticks, TimeSpan ProcessCpu, TimeSpan GcPause, long Allocated, Thread? Thread, int? OsThreadId, TimeSpan? ThreadCpu)
    {
        public static Sample Take() => new(Stopwatch.GetTimestamp(), Environment.CpuUsage.TotalTime, GC.GetTotalPauseDuration(), GC.GetTotalAllocatedBytes(), Thread.CurrentThread, NativeThread.Id(), NativeThread.Cpu());
    }

    /// <summary>El identificador del hilo en el sistema y su CPU, para compararlo con <see cref="ProcessThread"/>.</summary>
    private static partial class NativeThread
    {
        private const int ClockThreadCpuTime = 3;

        public static int? Id()
        {
            try
            {
                return OperatingSystem.IsWindows() ? (int)GetCurrentThreadId() : OperatingSystem.IsLinux() ? GetTid() : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public static TimeSpan? Cpu()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user) ? TimeSpan.FromTicks(kernel + user) : null;
                }

                if (OperatingSystem.IsLinux())
                {
                    return ClockGetTime(ClockThreadCpuTime, out TimeSpec time) == 0 ? TimeSpan.FromTicks((time.Seconds * TimeSpan.TicksPerSecond) + (time.Nanoseconds / 100)) : null;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }

            return null;
        }

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial uint GetCurrentThreadId();

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint GetCurrentThread();

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

        [LibraryImport("libc", EntryPoint = "gettid")]
        private static partial int GetTid();

        [LibraryImport("libc", EntryPoint = "clock_gettime")]
        private static partial int ClockGetTime(int clock, out TimeSpec time);

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            public long Seconds;
            public long Nanoseconds;
        }
    }
}
