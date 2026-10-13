using System.Reflection;
using System.Runtime.CompilerServices;
using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Relink;
using Rodaje.App.Tests.Media;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Packages;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Leaks;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLeakGroup
{
    /// <summary>Miden la memoria administrada del proceso: corren solas.</summary>
    public const string Name = "Fugas de la app";
}

/// <summary>
/// Abrir y cerrar 100 veces un proyecto con video, título, máscara y mezclador, con los servicios que la ventana une a
/// la sesión: ningún proyecto anterior queda vivo, las suscripciones a la sesión no crecen y la memoria y los
/// identificadores del proceso tampoco.
/// </summary>
[Collection(AppLeakGroup.Name)]
public sealed class SessionOpenCloseLeakTests(ITestOutputHelper output) : IDisposable
{
    private static readonly int Scale = Rodaje.TestMedia.Skip.LongRuns ? 10 : 1;

    private const int HandleSampleCycles = HandleLeakCriterion.SampleCycles;

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Opening_a_project_a_hundred_times_keeps_no_old_project_alive() => UiThread.Run(async () =>
    {
        var dialogs = new FakeDialogs { Choice = UnsavedChangesChoice.Discard };
        var prober = new FakeProber();
        var session = new EditorSession(new ForgetfulPlayback(), prober, dialogs, new SequentialIdGenerator());
        using Services services = Attach(session, dialogs);
        string path = await SaveRichProjectAsync(session, prober);

        var opened = new List<WeakReference<Project>>();
        long memoryBefore = 0;
        var handles = new List<ProcessHandles.Snapshot>();
        int subscriptionsBefore = 0;
        int cycles = 100 * Scale;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            Assert.True(await session.OpenProjectAsync(path));
            Remember(opened, session.Project);

            // Un poco de trabajo antes de cerrar: seleccionar, mover y deshacer, como al revisar el proyecto.
            Clip clip = session.Sequence.VideoTracks[0].Clips[0];
            session.Select([clip.Id]);
            Assert.True(session.Execute(Edits.SetClipEnabled(session.SequenceId, clip.Id, enabled: false)));
            session.Undo();
            services.Autosave.Tick();
            services.Visuals.GetWaveform(clip.MediaId!, Time.Zero, Time.FromSeconds(1), 8);
            Assert.True(await session.NewProjectAsync());
            Remember(opened, session.Project);
            if (cycle == 9)
            {
                memoryBefore = ManagedMemory();
                subscriptionsBefore = Subscriptions(session);
            }

            if (cycle >= 9 && (cycle + 1) % HandleSampleCycles == 0)
            {
                handles.Add(ProcessHandles.Take());
            }
        }

        long grown = ManagedMemory() - memoryBefore;
        int alive = opened.Count(w => w.TryGetTarget(out _));
        int[] owned = [.. handles.Select(h => h.Owned)];
        int[] ownThreads = [.. handles.Select(h => h.OwnThreads)];
        string changes = ProcessHandles.Changes(handles[0], handles[^1]);

        // Los Event que no vienen con un hilo (tarea 308): Event − k·Thread, con k medido aquí mismo después de las
        // aperturas, para no mover las muestras. Así el grupo de hilos que crece y para no cuenta, y un evento retenido
        // por apertura sí.
        double eventsPerThread = ProcessHandles.MeasureEventsPerThread();
        int[] events = HandleLeakCriterion.UnthreadedEvents(
            [.. handles.Select(h => h.Of(ProcessHandles.EventType))], [.. handles.Select(h => h.Of(ProcessHandles.ThreadType))], eventsPerThread);
        output.WriteLine($"{cycles} aperturas: {alive} proyectos vivos de {opened.Count}, {subscriptionsBefore} suscripciones, memoria {grown / 1024.0:F0} KB.");
        output.WriteLine($"Cada {HandleSampleCycles}: identificadores {string.Join(" ", owned)} (sin los de los hilos; total {string.Join(" ", handles.Select(h => h.Total))}); hilos propios {string.Join(" ", ownThreads)} (del grupo {string.Join(" ", handles.Select(h => h.PoolThreads))}). Por tipo: {changes}.");
        output.WriteLine($"Eventos sin los de los hilos ({eventsPerThread:F2} por hilo): {string.Join(" ", events)}.");

        // El proyecto abierto y, como mucho, el anterior (lo que la ventana retiene hasta el próximo cuadro).
        Assert.InRange(alive, 0, 2);
        Assert.Equal(subscriptionsBefore, Subscriptions(session));
        Assert.True(grown < 8L << 20, $"La memoria administrada creció {grown / 1024.0:F0} KB en {cycles} aperturas.");

        string? leak = HandleLeakCriterion.Find(owned);
        Assert.True(leak is null, $"{leak} Por tipo: {changes}.");
        string? eventLeak = HandleLeakCriterion.Find(events, $"Los identificadores Event que no son de un hilo ({eventsPerThread:F2} por hilo)");
        Assert.True(eventLeak is null, $"{eventLeak} Por tipo: {changes}.");
        string? threadLeak = HandleLeakCriterion.Find(ownThreads, "Los hilos que no son del grupo de hilos");
        Assert.True(threadLeak is null, threadLeak);
    });

    /// <summary>
    /// El criterio de fuga de identificadores sobre muestras de fabricación. Regresión: el anterior solo miraba el total y
    /// toleraba la mitad de las aperturas medidas (45 de 90), así que un identificador sin cerrar cada tres o cuatro
    /// aperturas pasaba en verde.
    /// </summary>
    [Fact]
    public void A_small_steady_handle_leak_is_detected_and_pool_noise_is_not()
    {
        // Uno cada cuatro aperturas (2 o 3 por tramo de 10), con el ruido de algún hilo que entra y sale.
        int[] slowLeak = [400, 402, 405, 406, 410, 412, 415, 417, 420, 422];
        Assert.NotNull(HandleLeakCriterion.Find(slowLeak));
        Assert.True(slowLeak[^1] - slowLeak[0] < (slowLeak.Length - 1) * HandleSampleCycles / 2, "El criterio anterior la dejaba pasar.");

        // Uno cada diez aperturas, aunque un tramo suelte hilos del grupo.
        Assert.NotNull(HandleLeakCriterion.Find([400, 401, 402, 403, 401, 402, 403, 404, 405, 406]));

        // Una ráfaga suelta (el grupo de hilos crece una vez) es grande pero no sostenida: en la mayoría de los tramos no
        // crece nada.
        Assert.Null(HandleLeakCriterion.Find([400, 400, 406, 406, 405, 405, 406, 405, 405, 405]));

        // Una ráfaga que no para tampoco pasa, aunque no sea constante.
        Assert.NotNull(HandleLeakCriterion.Find([400, 400, 400, 400, 400, 440, 480, 480, 480, 480]));

        // Lo que falló dos veces en la CI de Windows (tarea 299): un identificador que unas muestras están abiertos y otras
        // no (una escritura del autoguardado o un hilo del grupo en vuelo al contar). Sube 5 tramos de 9 y baja 3, así que
        // la mediana de lo que crece cada tramo era 1, pero en 90 aperturas solo sumó 2: no hay nada que no se cierre.
        Assert.Null(HandleLeakCriterion.Find([746, 747, 746, 747, 746, 747, 747, 748, 747, 748]));

        // La misma oscilación encima de una fuga de uno cada diez aperturas sí se ve: lo que no se cierra nunca vuelve a bajar.
        Assert.NotNull(HandleLeakCriterion.Find([400, 402, 402, 404, 404, 406, 406, 408, 408, 410]));
        Assert.NotNull(HandleLeakCriterion.Find([400, 402, 401, 403, 402, 404, 403, 405, 404, 406]));
    }

    /// <summary>
    /// Los hilos no cuentan como identificadores de la sesión, y los archivos abiertos sí. En la CI de Windows el grupo de
    /// hilos sumó 108 identificadores en 40 aperturas (unos tres por hilo) y paró: el total los mezclaba con los de la
    /// sesión.
    /// </summary>
    [Fact]
    public void Threads_are_counted_apart_from_session_handles_and_open_files_are_not()
    {
        const int Count = 24;
        ProcessHandles.Snapshot before = ProcessHandles.Take();
        using var release = new ManualResetEventSlim();
        Thread[] threads = [.. Enumerable.Range(0, Count).Select(_ => new Thread(() => release.Wait()) { IsBackground = true })];
        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        ProcessHandles.Snapshot withThreads;
        try
        {
            withThreads = ProcessHandles.Take();
        }
        finally
        {
            release.Set();
            foreach (Thread thread in threads)
            {
                thread.Join();
            }
        }

        var files = new List<FileStream>();
        ProcessHandles.Snapshot withFiles;
        try
        {
            for (int i = 0; i < Count; i++)
            {
                files.Add(new FileStream(Path.Combine(_folder.Path, $"abierto-{i}.bin"), FileMode.Create, FileAccess.Write, FileShare.None));
            }

            withFiles = ProcessHandles.Take();
        }
        finally
        {
            files.ForEach(f => f.Dispose());
        }

        output.WriteLine($"Con {Count} hilos: {ProcessHandles.Changes(before, withThreads)}; con {Count} archivos: {ProcessHandles.Changes(before, withFiles)}.");
        Assert.True(withThreads.OwnThreads - before.OwnThreads >= Count, $"Hilos propios {before.OwnThreads} → {withThreads.OwnThreads}.");
        Assert.True(Math.Abs(withThreads.Owned - before.Owned) < Count / 3, $"Los hilos movieron los identificadores sin los de los hilos: {ProcessHandles.Changes(before, withThreads)}.");
        Assert.True(withFiles.Owned - before.Owned >= Count, $"Los archivos abiertos no se contaron: {ProcessHandles.Changes(before, withFiles)}.");
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(before.ByType);
            Assert.True(withThreads.Total - before.Total >= Count, $"En Windows cada hilo es al menos un identificador: total {before.Total} → {withThreads.Total}.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Remember(List<WeakReference<Project>> opened, Project project) => opened.Add(new WeakReference<Project>(project));

    /// <summary>Los servicios que <c>MainWindow</c> une a la sesión, sin ventana.</summary>
    private Services Attach(EditorSession session, FakeDialogs dialogs)
    {
        var packages = new PackageService(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog());
        var content = AppEffectContent.Create(packages);
        AppEffectContent.AttachSession(content, session);
        MissingContent.Attach(session, packages);
        new MediaRelinkService(new FakeProber()).Attach(session, _ => Task.CompletedTask);
        var visuals = new ClipVisuals(id => session.Project.Media.Find(m => m.Id == id)?.Path, new FakeSources(), new ClipVisualsOptions { ThumbnailWorkers = 1 });
        session.ProjectOpened += (_, _) => visuals.ForgetRemovedMedia();
        session.ProjectEdited += (_, _) => visuals.ForgetRemovedMedia();
        var autosave = new AutosaveService(session, dialogs, Path.Combine(_folder.Path, "autoguardado"));
        Assert.True(autosave.Start());
        return new Services(
            visuals,
            autosave,
            new ClipPropertyEditor(session),
            new SpeedEditor(session, SpeedCommands.Default),
            new ModuleProposals(session),
            new LongActionService(session));
    }

    private async Task<string> SaveRichProjectAsync(EditorSession session, FakeProber prober)
    {
        string video = Path.Combine(_folder.Path, "toma.mp4");
        prober.Video(video, 10);
        ImportResult imported = await session.ImportMediaAsync([video]);
        Assert.True(session.AppendToTimeline(imported.MediaIds[0]));
        string clipId = session.Sequence.VideoTracks[0].Clips[0].Id;
        Assert.True(session.Execute(Edits.AddMask(session.SequenceId, clipId, MaskShape.Ellipse)));
        Assert.True(session.AddTitleAt(null, Time.FromSeconds(1)));
        Assert.True(session.Execute(Edits.AddBus(session.SequenceId, "Voces")));
        string path = Path.Combine(_folder.Path, "rico.rodaje");
        Assert.True(await session.SaveAsAsync(path));
        return path;
    }

    /// <summary>Suscriptores de todos los eventos de la sesión y de su historial.</summary>
    private static int Subscriptions(EditorSession session)
    {
        int count = 0;
        for (Type? type = typeof(EditorSession); type is not null; type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (typeof(Delegate).IsAssignableFrom(field.FieldType) && field.GetValue(session) is Delegate handler)
                {
                    count += handler.GetInvocationList().Length;
                }
            }
        }

        return count;
    }

    private static long ManagedMemory()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private sealed record Services(
        ClipVisuals Visuals,
        AutosaveService Autosave,
        ClipPropertyEditor Properties,
        SpeedEditor Speed,
        ModuleProposals Proposals,
        LongActionService LongActions) : IDisposable
    {
        public void Dispose()
        {
            Visuals.Dispose();
            Autosave.Dispose();
        }
    }

    /// <summary>Reproductor que no guarda lo que recibe (el de las otras pruebas anota cada proyecto y los retendría).</summary>
    private sealed class ForgetfulPlayback : IPlaybackEngine
    {
        public PlaybackSnapshot Snapshot => PlaybackSnapshot.Empty;

        public void Load(Project project, string sequenceId)
        {
        }

        public void Reload(Project project)
        {
        }

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void TogglePlayPause()
        {
        }

        public void Seek(Time position)
        {
        }

        public void StepFrames(int frames)
        {
        }

        public void SetProxyUsage(ProxyUsage usage)
        {
        }
    }
}
