using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// Los videos de las máscaras de imagen (Quitar fondo) se leen con un decodificador por archivo que guarda su último
/// cuadro. Antes de este arreglo se quedaban abiertos hasta cerrar la app: al abrir otro proyecto se cierran.
/// </summary>
public sealed class MatteLeakTests : IDisposable
{
    private readonly TempDirectory _folder = new();
    private readonly FakeMattes _mattes = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_matte_opens_on_the_first_request()
    {
        // La espera tras un fallo se calculaba con long.MinValue y se desbordaba: el primer intento ya parecía un fallo reciente.
        var reader = new MatteReader(_mattes.Open);

        Assert.True(reader.TryGet(Touch("mascara.mkv"), 0, out MatteFrame? frame));
        Assert.Equal(4, frame!.Height);
        Assert.Equal(1, _mattes.Opened);
    }

    [Fact]
    public void Clear_closes_every_decoder()
    {
        var reader = new MatteReader(_mattes.Open);
        string[] files = [.. Enumerable.Range(0, 20).Select(i => Touch($"mascara {i}.mkv"))];
        foreach (string file in files)
        {
            Assert.True(reader.TryGet(file, 0.5, out MatteFrame? frame));
            Assert.NotNull(frame);
        }

        Assert.Equal(20, _mattes.Live);
        reader.Clear();

        Assert.Equal(0, _mattes.Live);
        Assert.Equal(0, reader.OpenSources);

        // Después de cerrar, pedirlo de nuevo abre uno nuevo (y solo uno).
        Assert.True(reader.TryGet(files[0], 0.5, out _));
        Assert.Equal(1, _mattes.Live);
    }

    /// <summary>
    /// <see cref="MatteReader.Clear"/> desde otro hilo mientras un lector abre el decodificador: el cierre espera a que
    /// termine de abrir y lo cierra, en vez de pasar de largo y dejarlo abierto.
    /// </summary>
    [Fact]
    public void Clear_from_another_thread_waits_for_an_open_in_progress_and_closes_it()
    {
        var reader = new MatteReader(_mattes.Open);
        string file = Touch("mascara.mkv");
        FakeMattes.OpenGate gate = _mattes.BlockNextOpen();
        var opener = Start(() => reader.TryGet(file, 0.5, out _));
        Assert.True(gate.WaitOpening(TimeSpan.FromSeconds(10)), "El lector no empezó a abrir.");

        var clear = Start(reader.Clear);
        WaitUntilDoneOrBlocked(clear, "Clear");
        gate.Release();
        Join(opener, clear);
        Assert.False(gate.TimedOut);

        Assert.Equal(1, _mattes.Opened);
        Assert.Equal(0, _mattes.Live);
        Assert.Equal(0, reader.OpenSources);
    }

    /// <summary>
    /// Un lector que encontró el archivo antes de <see cref="MatteReader.Clear"/> y toma su candado después del cierre no
    /// lo vuelve a abrir: ese decodificador ya no está en la lista y nadie lo cerraría.
    /// </summary>
    [Fact]
    public void A_reader_that_found_the_file_before_Clear_does_not_reopen_it()
    {
        var reader = new MatteReader(_mattes.Open);
        string file = Touch("mascara.mkv");
        Assert.True(reader.TryGet(file, 0.5, out _));

        using var found = new ManualResetEventSlim();
        using var cleared = new ManualResetEventSlim();
        bool waited = false;
        reader.AfterLookup = _ =>
        {
            found.Set();
            waited = cleared.Wait(TimeSpan.FromSeconds(30));
        };
        bool read = true;
        var late = Start(() => read = reader.TryGet(file, 1.5, out _));
        Assert.True(found.Wait(TimeSpan.FromSeconds(10)), "El lector no llegó a buscar el archivo.");
        reader.AfterLookup = null;

        // Desde este hilo, con el lector ya en camino hacia el candado.
        reader.Clear();
        cleared.Set();
        Join(late);

        Assert.True(waited, "El lector no esperó al cierre: la prueba no ejerció la carrera.");
        Assert.False(read);
        Assert.Equal(1, _mattes.Opened);
        Assert.Equal(0, _mattes.Live);
        Assert.Equal(0, reader.OpenSources);
    }

    [Fact]
    public void Opening_projects_a_hundred_times_keeps_at_most_the_mattes_of_the_open_one() => UiThread.Run(async () =>
    {
        var content = new EffectContent(new FakeContentLoader(), new MatteReader(_mattes.Open));
        var dialogs = new FakeDialogs { Choice = UnsavedChangesChoice.Discard };
        var session = new EditorSession(new FakePlayback(), new FakeProber(), dialogs, new SequentialIdGenerator());
        AppEffectContent.AttachSession(content, session);

        // Dos proyectos en la misma carpeta, cada uno con sus máscaras: la carpeta no cambia al ir de uno a otro.
        string[] projects = [Path.Combine(_folder.Path, "uno.rodaje"), Path.Combine(_folder.Path, "dos.rodaje")];
        foreach (string project in projects)
        {
            Assert.True(await session.SaveAsAsync(project));
            for (int i = 0; i < 3; i++)
            {
                Touch($"{Path.GetFileNameWithoutExtension(project)}.medios/mascara {i}.mkv");
            }
        }

        int worst = 0;
        for (int cycle = 0; cycle < 100; cycle++)
        {
            string project = projects[cycle % 2];
            Assert.True(await session.OpenProjectAsync(project));
            for (int i = 0; i < 3; i++)
            {
                var key = new ResourceKey($"{LutReference.FilePrefix}{Path.GetFileNameWithoutExtension(project)}.medios/mascara {i}.mkv");
                Assert.True(content.TryGetMatte(key, cycle / 30.0, out _));
            }

            worst = Math.Max(worst, _mattes.Live);
        }

        // Sin cerrar al abrir, quedaban los seis abiertos (los de los dos proyectos) desde la segunda vuelta.
        Assert.Equal(3, worst);
        Assert.Equal(3 * 100, _mattes.Opened);
    });

    /// <summary>Corre en un hilo propio; lo que lance se relanza en <see cref="Join"/> (en el hilo, mataría el proceso).</summary>
    private static Worker Start(Action action)
    {
        var worker = new Worker();
        worker.Thread = new Thread(() =>
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // Se relanza en Join.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                worker.Error = ex;
            }
        })
        { IsBackground = true };
        worker.Thread.Start();
        return worker;
    }

    /// <summary>Espera, sin tope fijo de tiempo, a que el hilo termine o quede bloqueado (en el candado del lector).</summary>
    private static void WaitUntilDoneOrBlocked(Worker worker, string what)
    {
        Thread thread = worker.Thread!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (thread.IsAlive && (thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{what} no terminó ni quedó esperando.");
            Thread.Yield();
        }
    }

    private static void Join(params Worker[] workers)
    {
        foreach (Worker worker in workers)
        {
            Assert.True(worker.Thread!.Join(TimeSpan.FromSeconds(10)), "Un hilo de la prueba no terminó (posible interbloqueo).");
            Assert.Null(worker.Error);
        }
    }

    private sealed class Worker
    {
        public Thread? Thread { get; set; }

        public Exception? Error { get; set; }
    }

    private string Touch(string relative)
    {
        string path = Path.Combine(_folder.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>Decodificadores de mentira que cuentan cuántos siguen abiertos.</summary>
    private sealed class FakeMattes
    {
        private int _live;
        private int _opened;

        public int Live => Volatile.Read(ref _live);

        public int Opened => Volatile.Read(ref _opened);

        private OpenGate? _gate;

        /// <summary>La próxima apertura avisa a <see cref="OpenGate.WaitOpening"/> y espera a <see cref="OpenGate.Release"/>.</summary>
        public OpenGate BlockNextOpen() => _gate = new OpenGate();

        public IMatteDecoder Open(string path)
        {
            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                gate.Enter();
            }

            Interlocked.Increment(ref _live);
            Interlocked.Increment(ref _opened);
            return new Decoder(this);
        }

        public sealed class OpenGate
        {
            private readonly TaskCompletionSource _opening = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool WaitOpening(TimeSpan limit) => _opening.Task.Wait(limit);

            public bool TimedOut { get; private set; }

            public void Release() => _released.TrySetResult();

            /// <summary>En el hilo del lector: no lanza (mataría el proceso); si nadie la suelta, lo anota en <see cref="TimedOut"/>.</summary>
            internal void Enter()
            {
                _opening.TrySetResult();
                TimedOut = !_released.Task.Wait(TimeSpan.FromSeconds(30));
            }
        }

        private sealed class Decoder(FakeMattes owner) : IMatteDecoder
        {
            private int _disposed;

            public Rational FrameRate => Rational.Fps30;

            public int DisplayHeight => 4;

            public BgraImage? Decode(Time sourceTime, int height) => new(4, height, new byte[4 * height * 4]);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._live);
                }
            }
        }
    }
}
