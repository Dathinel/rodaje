using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

public class EditorSessionTests
{
    private sealed class Harness
    {
        public Harness()
        {
            Session = new EditorSession(Playback, Prober, Dialogs, new SequentialIdGenerator());
            Session.Notified += (_, n) => Notifications.Add(n);
        }

        public FakePlayback Playback { get; } = new();

        public FakeProber Prober { get; } = new();

        public FakeDialogs Dialogs { get; } = new();

        public EditorSession Session { get; }

        public List<EditorNotification> Notifications { get; } = [];

        public string MainTrack => Session.Sequence.VideoTracks[0].Id;

        /// <summary>El reproductor "llegó" a esa posición.</summary>
        public void At(double seconds) =>
            Session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(seconds), Duration = Session.Sequence.Duration });

        public async Task<string> ImportAndAppend(string path, double seconds = 10)
        {
            Prober.Video(path, seconds);
            ImportResult result = await Session.ImportMediaAsync([path]);
            Assert.True(Session.AppendToTimeline(result.MediaIds[0]));
            return result.MediaIds[0];
        }
    }

    [Fact]
    public void Nueva_ProyectoVacioLimpioYEnElReproductor() => UiThread.Run(() =>
    {
        var h = new Harness();

        Assert.Equal(EditorSession.UntitledName, h.Session.ProjectName);
        Assert.False(h.Session.IsDirty);
        Assert.Null(h.Session.FilePath);
        Assert.Equal("Nuevo", h.Session.SaveStateText);
        (Project loaded, string sequenceId) = Assert.Single(h.Playback.Loads);
        Assert.NotSame(h.Session.Project, loaded);
        Assert.Equal(h.Session.SequenceId, sequenceId);
        HistoryEntry origin = Assert.Single(h.Session.HistoryEntries);
        Assert.True(origin.IsCurrent);
        Assert.True(origin.IsSaved);
    });

    [Fact]
    public void FueraDelHiloDeLaInterfaz_Falla()
    {
        EditorSession? session = null;
        UiThread.Run(() => session = new Harness().Session);
        Assert.Throws<InvalidOperationException>(() => session!.Undo());
    }

    [Fact]
    public void Execute_LlevaUnaCopiaAlReproductorYEnsuciaElProyecto() => UiThread.Run(() =>
    {
        var h = new Harness();
        var edits = new List<ProjectEditedEventArgs>();
        h.Session.ProjectEdited += (_, e) => edits.Add(e);

        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));

        Assert.Equal("Corto", h.Session.ProjectName);
        Assert.True(h.Session.IsDirty);
        Assert.Equal("Sin guardar", h.Session.SaveStateText);
        Project copy = Assert.Single(h.Playback.Reloads);
        Assert.NotSame(h.Session.Project, copy);
        Assert.Equal("Corto", copy.Name);
        Assert.Equal(EditKind.Execute, Assert.Single(edits).Kind);
    });

    [Fact]
    public void Execute_ConEdicionImposible_AvisaYNoCambiaNada() => UiThread.Run(() =>
    {
        var h = new Harness();

        Assert.False(h.Session.Execute(Edits.RemoveTrack(h.Session.SequenceId, "no-existe")));

        EditorNotification notice = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.False(h.Session.IsDirty);
        Assert.Empty(h.Playback.Reloads);
    });

    [Fact]
    public void DeshacerRehacerYSaltarEnElHistorial() => UiThread.Run(() =>
    {
        var h = new Harness();
        h.Session.Execute(new RenameProjectCommand("Uno"));
        h.Session.Execute(new RenameProjectCommand("Dos"));
        h.Session.Execute(new RenameProjectCommand("Tres"));

        Assert.True(h.Session.Undo());
        Assert.Equal("Dos", h.Session.ProjectName);
        Assert.True(h.Session.CanRedo);
        IReadOnlyList<HistoryEntry> entries = h.Session.HistoryEntries;
        Assert.Equal([0, 1, 2, 3], entries.Select(e => e.Position));
        Assert.Equal([true, true, true, false], entries.Select(e => e.IsApplied));
        Assert.True(entries[2].IsCurrent);

        Assert.True(h.Session.JumpToHistory(0));
        Assert.Equal(EditorSession.UntitledName, h.Session.ProjectName);
        Assert.False(h.Session.IsDirty);
        Assert.True(h.Session.JumpToHistory(3));
        Assert.Equal("Tres", h.Session.ProjectName);
        Assert.False(h.Session.JumpToHistory(3));
        Assert.True(h.Session.Redo() is false);
        Assert.True(h.Session.Undo());
        Assert.True(h.Session.Redo());
        Assert.Equal("Tres", h.Session.ProjectName);
    });

    [Fact]
    public void UnGesto_EsUnSoloPasoYCancelarloLoRevierte() => UiThread.Run(() =>
    {
        var h = new Harness();
        h.Session.BeginGesture("Renombrar dos veces");
        h.Session.Execute(new RenameProjectCommand("A"));
        h.Session.Execute(new RenameProjectCommand("B"));
        Assert.True(h.Session.IsInGesture);
        Assert.False(h.Session.Undo());
        h.Session.EndGesture();

        Assert.Equal(1, h.Session.History.Count);
        Assert.Equal("Renombrar dos veces", h.Session.UndoDescription);

        h.Session.BeginGesture("Arrastre");
        h.Session.Execute(new RenameProjectCommand("C"));
        h.Session.CancelGesture();
        Assert.Equal("B", h.Session.ProjectName);
        Assert.Equal(1, h.Session.History.Count);
        Assert.Equal("B", h.Playback.Reloads[^1].Name);
    });

    [Fact]
    public void Importar_AñadeALaBibliotecaEnUnPasoYTomaElFormato() => UiThread.Run(async () =>
    {
        var h = new Harness();
        h.Prober.Video("a.mp4", 10, 3840, 2160, new Rational(25, 1)).Video("b.mp4", 5).Audio("musica.mp3");

        ImportResult result = await h.Session.ImportMediaAsync(["a.mp4", "b.mp4", "musica.mp3", "roto.mp4"]);

        Assert.Equal(3, result.MediaIds.Count);
        SkippedMedia skipped = Assert.Single(result.Skipped);
        Assert.EndsWith("roto.mp4", skipped.Path, StringComparison.Ordinal);
        Assert.Equal(3, h.Session.Project.Media.Count);
        Assert.All(h.Session.Project.Media, m => Assert.True(Path.IsPathFullyQualified(m.Path)));
        Assert.Equal(3840, h.Session.Sequence.Width);
        Assert.Equal(new Rational(25, 1), h.Session.Sequence.FrameRate);
        Assert.Equal(1, h.Session.History.Count);
        Assert.Equal("Importar medios", h.Session.UndoDescription);
        Assert.Contains(h.Notifications, n => n.Severity == NotificationSeverity.Warning && n.Message.Contains("roto.mp4", StringComparison.Ordinal));

        h.Session.Undo();
        Assert.Empty(h.Session.Project.Media);
        Assert.Equal(1920, h.Session.Sequence.Width);
    });

    [Fact]
    public void Importar_UnArchivoQueYaEsta_DevuelveElMismoMedio() => UiThread.Run(async () =>
    {
        var h = new Harness();
        h.Prober.Video("a.mp4");
        ImportResult first = await h.Session.ImportMediaAsync(["a.mp4"]);
        ImportResult again = await h.Session.ImportMediaAsync([Path.GetFullPath("A.MP4"), "a.mp4"]);

        Assert.Equal(first.MediaIds, again.MediaIds);
        Assert.Single(h.Session.Project.Media);
        Assert.Equal(1, h.Session.History.Count);
    });

    [Fact]
    public void AñadirALaLinea_VideoConAudioVaEnlazadoYAudioSoloALaMusica() => UiThread.Run(async () =>
    {
        var h = new Harness();
        h.Prober.Video("a.mp4", 10).Audio("musica.mp3", 30);
        ImportResult imported = await h.Session.ImportMediaAsync(["a.mp4", "musica.mp3"]);

        Assert.True(h.Session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(h.Session.AppendToTimeline(imported.MediaIds[0]));
        Assert.True(h.Session.AppendToTimeline(imported.MediaIds[1]));

        Sequence s = h.Session.Sequence;
        Assert.Equal(2, s.VideoTracks[0].Clips.Count);
        Assert.Equal(Time.FromSeconds(10), s.VideoTracks[0].Clips[1].Start);
        Assert.Equal(2, s.AudioTracks[0].Clips.Count);
        Assert.NotNull(s.VideoTracks[0].Clips[0].LinkId);
        Assert.Equal(s.VideoTracks[0].Clips[0].LinkId, s.AudioTracks[0].Clips[0].LinkId);
        Clip music = Assert.Single(s.AudioTracks[^1].Clips);
        Assert.Equal(imported.MediaIds[1], music.MediaId);
        Assert.False(h.Session.AppendToTimeline("no-existe"));
    });

    [Fact]
    public void AbrirArchivosDeMedios_LosPoneSeguidosEnUnPaso() => UiThread.Run(async () =>
    {
        var h = new Harness();
        h.Prober.Video("a.mp4", 4).Video("b.mp4", 6);

        await h.Session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);

        Assert.Equal(2, h.Session.MainTrack!.Clips.Count);
        Assert.Equal(Time.FromSeconds(10), h.Session.Sequence.Duration);
        Assert.Equal(2, h.Session.History.Count);
        Assert.Equal("Añadir medios a la línea de tiempo", h.Session.UndoDescription);
    });

    [Fact]
    public void GuardarComo_EscribeRutasRelativasYAbrirLasVuelveAbsolutas() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string video = dir.File("toma 1.mp4");
        var h = new Harness();
        await h.ImportAndAppend(video);
        h.Dialogs.SavePath = dir.File("Mi corto");

        Assert.True(await h.Session.SaveAsync());

        string saved = dir.File("Mi corto.rodaje");
        Assert.Equal(saved, h.Session.FilePath);
        Assert.Equal("Mi corto", h.Session.ProjectName);
        Assert.False(h.Session.IsDirty);
        Assert.Equal("Guardado", h.Session.SaveStateText);
        Assert.Contains("\"toma 1.mp4\"", File.ReadAllText(saved), StringComparison.Ordinal);
        Assert.True(h.Session.HistoryEntries[^1].IsSaved);

        var other = new Harness();
        Assert.True(await other.Session.OpenProjectAsync(saved));
        Assert.Equal(video, other.Session.Project.Media[0].Path);
        Assert.Equal("Mi corto", other.Session.ProjectName);
        Assert.False(other.Session.IsDirty);
        Assert.Equal(2, other.Playback.Loads.Count);
        Assert.Equal("Proyecto abierto", other.Session.HistoryEntries[0].Description);

        h.Session.Execute(new RenameProjectCommand("Otro nombre"));
        Assert.True(await h.Session.SaveAsync());
        Assert.Equal(1, h.Dialogs.SavePicks);
    });

    [Fact]
    public void Guardar_SinElegirRuta_NoGuarda() => UiThread.Run(async () =>
    {
        var h = new Harness();
        h.Session.Execute(new RenameProjectCommand("X"));
        Assert.False(await h.Session.SaveAsync());
        Assert.True(h.Session.IsDirty);
    });

    [Fact]
    public void Abrir_UnArchivoDañado_AvisaYSigueConElProyecto() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string bad = dir.File("malo.rodaje");
        File.WriteAllText(bad, "{ no }");
        var h = new Harness();

        Assert.False(await h.Session.OpenProjectAsync(bad));

        Assert.Equal(NotificationSeverity.Error, Assert.Single(h.Notifications).Severity);
        Assert.Single(h.Playback.Loads);
    });

    [Theory]
    [InlineData(UnsavedChangesChoice.Cancel, false, false)]
    [InlineData(UnsavedChangesChoice.Discard, true, false)]
    [InlineData(UnsavedChangesChoice.Save, true, true)]
    public void ConCambios_PreguntaAntesDeDescartarlos(UnsavedChangesChoice choice, bool proceeds, bool saves) => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var h = new Harness();
        h.Session.Execute(new RenameProjectCommand("Cambio"));
        h.Dialogs.Choice = choice;
        h.Dialogs.SavePath = dir.File("p.rodaje");

        Assert.Equal(proceeds, await h.Session.NewProjectAsync());

        Assert.Equal(1, h.Dialogs.Asked);
        Assert.Equal(saves, File.Exists(dir.File("p.rodaje")));
        Assert.Equal(proceeds ? EditorSession.UntitledName : "Cambio", h.Session.ProjectName);
    });

    [Fact]
    public void SinCambios_NoPregunta() => UiThread.Run(async () =>
    {
        var h = new Harness();
        Assert.True(await h.Session.ConfirmDiscardChangesAsync());
        Assert.Equal(0, h.Dialogs.Asked);
    });

    [Fact]
    public void Transporte_SeCopiaYAvisaSoloSiCambia() => UiThread.Run(() =>
    {
        var h = new Harness();
        int changes = 0;
        var properties = new List<string?>();
        h.Session.TransportChanged += (_, _) => changes++;
        h.Session.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        var snapshot = new PlaybackSnapshot(Time.FromSeconds(1), Time.FromSeconds(9), true, 30, 270, "Reproduciendo");

        h.Session.UpdateTransport(snapshot);
        h.Session.UpdateTransport(snapshot with { });

        Assert.Equal(1, changes);
        Assert.True(h.Session.IsPlaying);
        Assert.Equal("0:01.000", h.Session.PositionText);
        Assert.Contains(nameof(EditorSession.PositionText), properties);
        Assert.Contains(nameof(EditorSession.IsPlaying), properties);
    });

    [Fact]
    public void Transporte_LasOrdenesVanAlReproductor() => UiThread.Run(async () =>
    {
        var h = new Harness();
        await h.ImportAndAppend("a.mp4", 10);
        h.Session.TogglePlayPause();
        h.Session.StepFrames(-1);
        h.Session.Seek(Time.FromSeconds(99));
        h.Session.Seek(Time.FromSeconds(-1));

        Assert.Equal(["toggle", "step:-1", "seek", "seek"], h.Playback.Commands);
        Assert.Equal([Time.FromSeconds(10), Time.Zero], h.Playback.Seeks);
    });

    [Fact]
    public void Seleccion_SeLimpiaDeClipsQueYaNoExisten() => UiThread.Run(async () =>
    {
        var h = new Harness();
        await h.ImportAndAppend("a.mp4");
        string clip = h.Session.MainTrack!.Clips[0].Id;
        int changes = 0;
        h.Session.SelectionChanged += (_, _) => changes++;

        h.Session.Select([clip, clip]);
        Assert.Equal([clip], h.Session.SelectedClipIds);
        h.Session.Undo();

        Assert.Empty(h.Session.SelectedClipIds);
        Assert.Equal(2, changes);
        h.Session.ToggleSelection("x");
        h.Session.ToggleSelection("x");
        Assert.Empty(h.Session.SelectedClipIds);
    });

    [Fact]
    public void Abrir_UnaRutaConCaracteresInvalidos_AvisaSinCaerse() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var h = new Harness();
        await h.ImportAndAppend(dir.File("toma 1.mp4"));
        h.Dialogs.SavePath = dir.File("p");
        Assert.True(await h.Session.SaveAsync());
        string saved = dir.File("p.rodaje");
        File.WriteAllText(saved, File.ReadAllText(saved).Replace("toma 1.mp4", "toma\\u00001.mp4", StringComparison.Ordinal));

        var other = new Harness();
        Assert.False(await other.Session.OpenProjectAsync(saved));

        EditorNotification notice = Assert.Single(other.Notifications);
        Assert.Equal(NotificationSeverity.Error, notice.Severity);
        Assert.Contains("ruta", notice.Message, StringComparison.Ordinal);
        Assert.Single(other.Playback.Loads);
    });

    [Fact]
    public void MientrasImporta_NoAbreNiCreaOtroProyecto() => UiThread.Run(async () =>
    {
        var prober = new GatedProber(new FakeProber().Video("a.mp4"));
        var dialogs = new FakeDialogs();
        var session = new EditorSession(new FakePlayback(), prober, dialogs, new SequentialIdGenerator());
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        Task<ImportResult> importing = session.ImportMediaAsync([Path.GetFullPath("a.mp4")]);
        // La importación ya espera la lectura y el fin de su espera vuelve a este hilo, que no se suelta hasta el próximo
        // await: se puede soltar la lectura ya y no dejar un hilo del pool bloqueado mientras se intenta abrir o crear.
        prober.Release();
        Assert.True(session.IsBusy);
        Assert.False(await session.OpenProjectAsync("otro.rodaje"));
        Assert.False(await session.NewProjectAsync());
        Assert.True(session.IsBusy);
        Assert.Single((await importing).MediaIds);

        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Equal(NotificationSeverity.Informational, n.Severity));
        Assert.Single(session.Project.Media);
        Assert.False(session.IsBusy);
    });

    [Fact]
    public void DosGuardadosSeguidos_VanEnFilaYQuedaElUltimo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var h = new Harness();
        await h.ImportAndAppend(dir.File("a.mp4"));
        h.Dialogs.SavePath = dir.File("p");
        Assert.True(await h.Session.SaveAsync());
        string path = dir.File("p.rodaje");

        h.Session.Execute(new RenameProjectCommand("Dos"));
        Task<bool> first = h.Session.SaveAsync();
        h.Session.Execute(new RenameProjectCommand("Tres"));
        Task<bool> second = h.Session.SaveAsync();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal("Tres", Rodaje.Core.Serialization.ProjectSerializer.Load(path).Name);
        Assert.False(h.Session.IsDirty);
        Assert.False(File.Exists(path + ".tmp"));
    });

    [Fact]
    public void AñadirALaLinea_VaPegadoALaPrincipalAunqueElAudioLlegueMasLejos() => UiThread.Run(async () =>
    {
        var h = new Harness();
        await h.ImportAndAppend("a.mp4", 4);
        h.Prober.Audio("musica.mp3", 30);
        ImportResult music = await h.Session.ImportMediaAsync([Path.GetFullPath("musica.mp3")]);
        string a1 = h.Session.Sequence.AudioTracks[0].Id;
        Assert.True(h.Session.Execute(Edits.InsertMedia(h.Session.SequenceId, music.MediaIds[0], Time.Zero, Time.FromSeconds(30), a1,
            Time.FromSeconds(4), PlacementMode.Strict)));
        h.Prober.Video("b.mp4", 6);
        ImportResult b = await h.Session.ImportMediaAsync([Path.GetFullPath("b.mp4")]);

        Assert.True(h.Session.AppendToTimeline(b.MediaIds[0]));

        Sequence s = h.Session.Sequence;
        Clip video = s.VideoTracks[0].Clips[1];
        Assert.Equal(Time.FromSeconds(4), video.Start);
        Clip sound = Assert.Single(s.AudioTracks[1].Clips);
        Assert.Equal(Time.FromSeconds(4), sound.Start);
        Assert.Equal(video.LinkId, sound.LinkId);
        Assert.Equal("Añadir al final", h.Session.UndoDescription);
    });

    /// <summary>
    /// Retiene la lectura hasta <see cref="Release"/>, para que la importación no termine antes de su primer await. Lo
    /// llama la sesión en un hilo del pool: la prueba lo suelta enseguida, sin esperar a lo que comprueba.
    /// </summary>
    private sealed class GatedProber(IMediaProber inner) : IMediaProber
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public MediaProbeResult Probe(string path)
        {
            _gate.Task.Wait(TimeSpan.FromSeconds(10));
            return inner.Probe(path);
        }
    }

    [Fact]
    public void OcultarYBloquear_ConDeshacerYRehacer_ElVeloDeCadaClipSigueALaPista() => UiThread.Run(async () =>
    {
        var h = new Harness();
        await h.ImportAndAppend("a.mp4");
        await h.ImportAndAppend("b.mp4");
        int edits = 0;
        h.Session.ProjectEdited += (_, _) => edits++;
        string id = h.MainTrack;
        double[] Veils() => [.. h.Session.Sequence.FindTrack(id)!.Clips.Select(c => ClipDecorations.Veil(c, h.Session.Sequence.FindTrack(id)!))];

        Assert.True(h.Session.Execute(Edits.SetTrackState(h.Session.SequenceId, id, hidden: true)));
        Assert.Equal([ClipDecorations.MutedVeil, ClipDecorations.MutedVeil], Veils());
        Assert.True(h.Session.Execute(Edits.SetTrackState(h.Session.SequenceId, id, hidden: false)));
        Assert.Equal([0d, 0d], Veils());
        Assert.True(h.Session.Execute(Edits.SetTrackState(h.Session.SequenceId, id, locked: true)));
        Assert.Equal([ClipDecorations.LockedVeil, ClipDecorations.LockedVeil], Veils());

        Assert.True(h.Session.Undo());
        Assert.Equal([0d, 0d], Veils());
        Assert.True(h.Session.Undo());
        Assert.Equal([ClipDecorations.MutedVeil, ClipDecorations.MutedVeil], Veils());
        Assert.True(h.Session.Redo());
        Assert.True(h.Session.Redo());
        Assert.Equal([ClipDecorations.LockedVeil, ClipDecorations.LockedVeil], Veils());
        Assert.Equal(7, edits);
    });
}
