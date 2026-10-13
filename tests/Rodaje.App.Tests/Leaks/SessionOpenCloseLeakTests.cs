using System.Diagnostics;
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

    /// <summary>Cada cuántas aperturas se cuentan los identificadores del proceso.</summary>
    private const int HandleSampleCycles = 10;

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
        var handles = new List<int>();
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
                handles.Add(Handles());
            }
        }

        long grown = ManagedMemory() - memoryBefore;
        int alive = opened.Count(w => w.TryGetTarget(out _));
        output.WriteLine($"{cycles} aperturas: {alive} proyectos vivos de {opened.Count}, {subscriptionsBefore} suscripciones, memoria {grown / 1024.0:F0} KB, identificadores cada {HandleSampleCycles}: {string.Join(" ", handles)}.");

        // El proyecto abierto y, como mucho, el anterior (lo que la ventana retiene hasta el próximo cuadro).
        Assert.InRange(alive, 0, 2);
        Assert.Equal(subscriptionsBefore, Subscriptions(session));
        Assert.True(grown < 8L << 20, $"La memoria administrada creció {grown / 1024.0:F0} KB en {cycles} aperturas.");

        string? leak = HandleLeak(handles);
        Assert.True(leak is null, leak);
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
        Assert.NotNull(HandleLeak(slowLeak));
        Assert.True(slowLeak[^1] - slowLeak[0] < (slowLeak.Length - 1) * HandleSampleCycles / 2, "El criterio anterior la dejaba pasar.");

        // Uno cada diez aperturas, aunque un tramo suelte hilos del grupo.
        Assert.NotNull(HandleLeak([400, 401, 402, 403, 401, 402, 403, 404, 405, 406]));

        // Una ráfaga suelta (el grupo de hilos crece una vez) es grande pero no sostenida: en la mayoría de los tramos no
        // crece nada.
        Assert.Null(HandleLeak([400, 400, 406, 406, 405, 405, 406, 405, 405, 405]));

        // Una ráfaga que no para tampoco pasa, aunque no sea constante.
        Assert.NotNull(HandleLeak([400, 400, 400, 400, 400, 440, 480, 480, 480, 480]));
    }

    /// <summary>
    /// Por qué fallar con estas muestras de identificadores (una cada <see cref="HandleSampleCycles"/> aperturas), o nulo.
    /// Un archivo, evento o hilo sin cerrar crece en todos los tramos, aunque sea uno cada diez aperturas: la mediana de
    /// lo que crece cada tramo no llega a uno si no hay fuga, porque los hilos que el grupo de hilos crea o suelta por su
    /// cuenta lo hacen de vez en cuando. El total sigue con su tope, contra una fuga a ráfagas.
    /// </summary>
    private static string? HandleLeak(IReadOnlyList<int> samples)
    {
        int[] steps = [.. samples.Zip(samples.Skip(1), (a, b) => b - a).Order()];
        if (steps.Length == 0)
        {
            return "No hubo muestras de identificadores.";
        }

        int median = steps[steps.Length / 2];
        int total = samples[^1] - samples[0];
        int measured = steps.Length * HandleSampleCycles;
        if (median >= 1)
        {
            return $"Los identificadores del proceso crecen en casi cada tramo de {HandleSampleCycles} aperturas (mediana {median}; {string.Join(" ", samples)}).";
        }

        return total >= measured / 2
            ? $"Los identificadores del proceso crecieron {total} en {measured} aperturas ({string.Join(" ", samples)})."
            : null;
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

    /// <summary>Identificadores del proceso (archivos, eventos, hilos…), después de juntar la basura y correr los finalizadores.</summary>
    private static int Handles()
    {
        // Lo que solo espera a su finalizador (un SafeHandle abandonado) no es una fuga: se cierra antes de contar.
        _ = ManagedMemory();
        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
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
