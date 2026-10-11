using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Settings;

/// <summary>De qué pistas de video sale el audio de un medio al importarlo o soltarlo (Ajustes).</summary>
public class LinkedAudioTracksTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    // ---- La regla y su guardado ----

    [Fact]
    public void PorDefecto_TodasLasPistas_ComoHastaAhora()
    {
        var settings = new AppSettings();
        Assert.Null(settings.LinkedAudioVideoTracks);
        Assert.True(settings.LinkedAudioTracks.IsAll);
        Assert.Empty(settings.LinkedAudioTracks.Numbers);
        Assert.All(Enumerable.Range(1, 20), n => Assert.True(settings.LinkedAudioTracks.AllowsNumber(n)));
        Assert.False(LinkedAudioTracks.All.AllowsNumber(0));
    }

    [Fact]
    public void UnaLista_SoloEsasPistas()
    {
        LinkedAudioTracks rule = LinkedAudioTracks.Only([2, 1, 2]);
        Assert.False(rule.IsAll);
        Assert.Equal([1, 2], rule.Numbers);
        Assert.True(rule.AllowsNumber(1));
        Assert.True(rule.AllowsNumber(2));
        Assert.False(rule.AllowsNumber(3));
        Assert.Equal([1, 2], rule.ToSetting());

        LinkedAudioTracks none = LinkedAudioTracks.Only([]);
        Assert.False(none.IsAll);
        Assert.False(none.AllowsNumber(1));
        Assert.Equal([], none.ToSetting());
    }

    [Fact]
    public void Alternar_DesdeTodasPasaAUnaListaSinEsaPista()
    {
        LinkedAudioTracks rule = LinkedAudioTracks.All.Toggle(3, knownTracks: 8);
        Assert.Equal([1, 2, 4, 5, 6, 7, 8], rule.Numbers);

        rule = rule.Toggle(3, 8);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], rule.Numbers);
        Assert.False(rule.IsAll);

        // Una pista más alta que las conocidas también se puede quitar.
        Assert.Equal(Enumerable.Range(1, 9), LinkedAudioTracks.All.Toggle(10, 8).Numbers);
        Assert.Equal([5], LinkedAudioTracks.Only([]).Toggle(5, 8).Numbers);
    }

    [Fact]
    public void Permite_CuentaLasPistasDeVideoPorSuLugar()
    {
        Sequence sequence = new NewProjectTracks(3, 2).CreateProject("p", new SequentialIdGenerator()).Sequences[0];
        LinkedAudioTracks rule = LinkedAudioTracks.Only([1, 2]);
        Assert.True(rule.Allows(sequence, sequence.VideoTracks[0].Id));
        Assert.True(rule.Allows(sequence, sequence.VideoTracks[1].Id));
        Assert.False(rule.Allows(sequence, sequence.VideoTracks[2].Id));

        // Las pistas de audio no las limita esta regla.
        Assert.True(LinkedAudioTracks.Only([]).Allows(sequence, sequence.AudioTracks[0].Id));
    }

    [Fact]
    public void Normalize_CorrigeUnaListaEditadaAMano()
    {
        var settings = new AppSettings { LinkedAudioVideoTracks = [3, 0, -1, 3, 99, 2] };
        settings.Normalize();
        Assert.Equal([2, 3], settings.LinkedAudioVideoTracks);

        settings = new AppSettings();
        settings.Normalize();
        Assert.Null(settings.LinkedAudioVideoTracks);
    }

    [Fact]
    public void GuardarYLeer_ConservaLaEleccion()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        store.Current.LinkedAudioVideoTracks = LinkedAudioTracks.Only([1, 2]).ToSetting();
        store.Save();
        Assert.Contains("\"linkedAudioVideoTracks\"", File.ReadAllText(path), StringComparison.Ordinal);

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());
        Assert.Equal([1, 2], loaded.Current.LinkedAudioTracks.Numbers);

        // «Todas» se guarda como null y vuelve como «todas»; una lista vacía, como ninguna.
        loaded.Current.LinkedAudioVideoTracks = null;
        loaded.Save();
        var all = new SettingsStore(path);
        Assert.Null(all.Load());
        Assert.True(all.Current.LinkedAudioTracks.IsAll);

        all.Current.LinkedAudioVideoTracks = [];
        all.Save();
        var none = new SettingsStore(path);
        Assert.Null(none.Load());
        Assert.False(none.Current.LinkedAudioTracks.IsAll);
        Assert.Empty(none.Current.LinkedAudioTracks.Numbers);
    }

    [Fact]
    public void UnAjustesSinElCampo_SigueConTodas()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        File.WriteAllText(path, """{ "version": 2, "defaultVideoTracks": 3 }""");
        var store = new SettingsStore(path);
        Assert.Null(store.Load());
        Assert.True(store.Current.LinkedAudioTracks.IsAll);
    }

    // ---- La regla con la sesión real ----

    /// <summary>Proyecto con 3 pistas de video y 2 de audio; en la biblioteca «n» (video con sonido), «m» (solo audio).</summary>
    private static async Task<EditorSession> ThreeVideoTracks(LinkedAudioTracks rule)
    {
        var prober = new FakeProber().Video("n.mp4", 6).Audio("m.mp3", 30).Video("muda.mp4", 4, audio: false);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator(), new NewProjectTracks(3, 2))
        {
            LinkedAudioTracks = rule,
        };
        await session.ImportMediaAsync([Path.GetFullPath("n.mp4"), Path.GetFullPath("m.mp3"), Path.GetFullPath("muda.mp4")]);
        return session;
    }

    private static MediaRef Media(EditorSession session, string name) => session.Project.Media.First(m => m.Name == name);

    private static DragOptions Options(EditorSession session) => new()
    {
        MagneticTrackId = session.MainTrack!.Id,
        RippleScope = RippleScope.Track,
        PixelsPerSecond = 100,
        LinkedAudio = session.LinkedAudioTracks,
    };

    private static DropPlan Drop(EditorSession session, string media, Track track, double at) =>
        TimelinePlanning.PlanDrop(session.Project, session.Sequence, Media(session, media),
            TrackLayout.Build(session.Sequence, session.MainTrack).Find(track.Id)!, S(at), false, Options(session), session.Position)!;

    private static int AudioClips(EditorSession session) => session.Sequence.AudioTracks.Sum(t => t.Clips.Count);

    [Fact]
    public void Soltar_ConVideo1y2_SiYVideo3_No() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeVideoTracks(LinkedAudioTracks.Only([1, 2]));
        Sequence sequence = session.Sequence;
        Track v1 = sequence.VideoTracks[0];
        Track v2 = sequence.VideoTracks[1];
        Track v3 = sequence.VideoTracks[2];

        // Video 1 (la principal, magnética): con su audio en la primera pista de audio, enlazado.
        DropPlan plan = Drop(session, "n", v1, 0);
        Assert.Equal(sequence.AudioTracks[0].Id, plan.LinkedTrackId);
        Assert.True(session.Execute(plan.Build()));
        Clip main = Assert.Single(v1.Clips);
        Clip sound = Assert.Single(sequence.AudioTracks[0].Clips);
        Assert.NotNull(main.LinkId);
        Assert.Equal(main.LinkId, sound.LinkId);

        // Video 2: con su audio en una pista de audio libre en ese tramo.
        plan = Drop(session, "n", v2, 1);
        Assert.Equal(sequence.AudioTracks[1].Id, plan.LinkedTrackId);
        Assert.True(session.Execute(plan.Build()));
        Assert.Equal(2, AudioClips(session));

        // Video 3: el medio entra sin su clip de audio.
        plan = Drop(session, "n", v3, 20);
        Assert.Null(plan.LinkedTrackId);
        Assert.True(session.Execute(plan.Build()));
        Clip silent = Assert.Single(v3.Clips);
        Assert.Null(silent.LinkId);
        Assert.Equal(2, AudioClips(session));

        // Deshacer la última deja todo como estaba.
        Assert.True(session.Undo());
        Assert.Empty(v3.Clips);
        Assert.Equal(2, AudioClips(session));
    });

    [Fact]
    public void Soltar_ConTodas_EsLoDeSiempre() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeVideoTracks(LinkedAudioTracks.All);
        Track v3 = session.Sequence.VideoTracks[2];
        DropPlan plan = Drop(session, "n", v3, 0);
        Assert.Equal(session.Sequence.AudioTracks[0].Id, plan.LinkedTrackId);
        Assert.True(session.Execute(plan.Build()));
        Assert.Equal(1, AudioClips(session));
    });

    [Fact]
    public void Soltar_EnLaPrincipalSinAudioPermitido_EntraSinClipDeAudio() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeVideoTracks(LinkedAudioTracks.Only([2, 3]));
        DropPlan plan = Drop(session, "n", session.MainTrack!, 0);
        Assert.Null(plan.LinkedTrackId);
        Assert.True(session.Execute(plan.Build()));
        Assert.Single(session.MainTrack!.Clips);
        Assert.Equal(0, AudioClips(session));
    });

    [Fact]
    public void Soltar_MusicaYPistasDeAudio_NoLasLimitaLaRegla() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeVideoTracks(LinkedAudioTracks.Only([]));

        // Un medio sin imagen sobre una pista de video va a la última de audio, como siempre.
        DropPlan music = Drop(session, "m", session.Sequence.VideoTracks[2], 0);
        Assert.Equal(session.Sequence.AudioTracks[^1].Id, music.TrackId);

        // Un video con sonido soltado sobre una pista de audio pone su sonido ahí: la persona eligió esa pista.
        DropPlan onAudio = Drop(session, "n", session.Sequence.AudioTracks[0], 0);
        Assert.Equal(session.Sequence.AudioTracks[0].Id, onAudio.TrackId);
    });

    [Fact]
    public void AnadirAlFinal_RespetaLaRegla() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeVideoTracks(LinkedAudioTracks.Only([2]));
        Assert.True(session.AppendToTimeline(Media(session, "n").Id));
        Clip clip = Assert.Single(session.MainTrack!.Clips);
        Assert.Null(clip.LinkId);
        Assert.Equal(0, AudioClips(session));

        // La música sigue yendo a la última pista de audio.
        Assert.True(session.AppendToTimeline(Media(session, "m").Id));
        Assert.Single(session.Sequence.AudioTracks[^1].Clips);

        // Con «todas», el siguiente trae su audio.
        session.LinkedAudioTracks = LinkedAudioTracks.All;
        Assert.True(session.AppendToTimeline(Media(session, "n").Id));
        Assert.Equal(2, session.MainTrack!.Clips.Count);
        Assert.NotNull(session.MainTrack!.Clips[1].LinkId);
        Assert.Single(session.Sequence.AudioTracks[0].Clips);
    });

    [Fact]
    public void ArrastrarArchivosALaVentana_RespetaLaRegla() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 5).Video("b.mp4", 5);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator())
        {
            LinkedAudioTracks = LinkedAudioTracks.Only([2]),
        };
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        Assert.Equal(2, session.MainTrack!.Clips.Count);
        Assert.Equal(0, AudioClips(session));

        session.LinkedAudioTracks = LinkedAudioTracks.Only([1]);
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.Equal(3, session.MainTrack!.Clips.Count);
        Assert.Equal(1, AudioClips(session));
    });

    [Fact]
    public void Sesion_NoAceptaUnaReglaNula()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(session.LinkedAudioTracks.IsAll);
        Assert.Throws<ArgumentNullException>(() => session.LinkedAudioTracks = null!);
    }
}
