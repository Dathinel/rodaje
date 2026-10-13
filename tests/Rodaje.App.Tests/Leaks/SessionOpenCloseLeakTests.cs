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
/// la sesión: ningún proyecto anterior queda vivo, las suscripciones a la sesión no crecen y la memoria tampoco.
/// </summary>
[Collection(AppLeakGroup.Name)]
public sealed class SessionOpenCloseLeakTests(ITestOutputHelper output) : IDisposable
{
    private static readonly int Scale = Rodaje.TestMedia.Skip.LongRuns ? 10 : 1;

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
        }

        long grown = ManagedMemory() - memoryBefore;
        int alive = opened.Count(w => w.TryGetTarget(out _));
        output.WriteLine($"{cycles} aperturas: {alive} proyectos vivos de {opened.Count}, {subscriptionsBefore} suscripciones, memoria {grown / 1024.0:F0} KB.");

        // El proyecto abierto y, como mucho, el anterior (lo que la ventana retiene hasta el próximo cuadro).
        Assert.InRange(alive, 0, 2);
        Assert.Equal(subscriptionsBefore, Subscriptions(session));
        Assert.True(grown < 8L << 20, $"La memoria administrada creció {grown / 1024.0:F0} KB en {cycles} aperturas.");
    });

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
