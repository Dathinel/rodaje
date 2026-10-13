using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Fuzz;

/// <summary>
/// Los defectos que encontró el fuzz de la sesión, reducidos a lo mínimo (las semillas, en
/// <see cref="SessionFuzzTests.SemillasQueFallaron_YaNoFallan"/>, dependen de cómo sortea el fuzz y pueden dejar de llegar
/// al defecto si el fuzz cambia; estas no).
/// </summary>
[Trait("Categoria", "Fuzz")]
public class SessionFuzzRegressionTests
{
    /// <summary>
    /// Semilla 35: «Añadir efecto» aceptaba el tipo reservado <c>rodaje.speed</c> y dejaba un proyecto que el validador
    /// rechaza (y que no se podía volver a abrir).
    /// </summary>
    [Fact]
    public void AnadirUnEfectoReservado_SeRechazaSinTocarElProyecto() => UiThread.Run(() =>
    {
        var session = new EditorSession(new FuzzPlayback(), new FakeProber(), new FakeDialogs(), new FuzzIds());
        string track = session.Sequence.VideoTracks[0].Id;
        string before = Rodaje.Core.Serialization.ProjectSerializer.Serialize(session.Project);

        Assert.False(session.Execute(Edits.AddEffect(session.SequenceId, track, BuiltInEffects.Speed)));
        Assert.False(session.Execute(Edits.EnsureEffect(session.SequenceId, track, BuiltInEffects.Speed)));

        Assert.Equal(before, Rodaje.Core.Serialization.ProjectSerializer.Serialize(session.Project));
        Assert.Empty(ModelValidator.Validate(session.Project));
    });

    /// <summary>
    /// Semilla 651: borrar a la izquierda del cursor (Q, con ripple por pista) cuando el audio enlazado empieza justo en el
    /// cursor. El video se recortaba y se corría, pero su audio no pasaba por el cursor, así que no cerraba su tramo y
    /// quedaba desfasado del video.
    /// </summary>
    [Fact]
    public void BorrarALaIzquierda_ConElAudioEnlazadoEmpezandoEnElCursor_ConservaLaSincronia()
    {
        using var directory = new TempDirectory();
        UiThread.Run(async () =>
        {
            EditorSession session = await OpenAsync(directory.Path, audioFrom: 1);
            session.Timeline.MagneticMainTrack = true;
            session.Timeline.RippleMode = RippleMode.AffectedTracks;
            session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(1), Duration = Time.FromSeconds(2) });

            Assert.True(session.DeleteLeftOfPlayhead());

            Assert.Empty(LinkSync.Find(session.Sequence));
            Assert.Equal(Time.Zero, session.Sequence.GetClip("v").Clip.Start);
            Assert.Equal(Time.Zero, session.Sequence.GetClip("a").Clip.Start);
        });
    }

    /// <summary>
    /// Semillas 625 y 920: un gesto cancelado (Esc mientras se arrastra) quitaba la barrera de fusión del historial, así que
    /// el siguiente empujón o recorte se fundía con el último paso de antes del gesto y deshacerlo deshacía los dos.
    /// </summary>
    [Fact]
    public void TrasCancelarUnGesto_ElSiguienteEmpujonEsOtroPaso()
    {
        using var directory = new TempDirectory();
        UiThread.Run(async () =>
        {
            EditorSession session = await OpenAsync(directory.Path, audioFrom: 0);
            session.Select(["v"]);
            Assert.True(session.NudgeSelection(5));
            string afterFirst = ProjectSerializer.Serialize(session.Project);

            session.BeginGesture("Arrastrar");
            Assert.True(session.TryExecute(Edits.SetClipColor(session.SequenceId, ["v"], "#FF0000")));
            session.CancelGesture();
            Assert.True(session.NudgeSelection(5));

            Assert.Equal(2, session.History.Count);
            Assert.True(session.Undo());
            Assert.Equal(afterFirst, ProjectSerializer.Serialize(session.Project));
        });
    }

    /// <summary>Un video de 0 a 2 s en v1 y su audio enlazado y sincronizado en a1, desde <paramref name="audioFrom"/> s.</summary>
    private static async Task<EditorSession> OpenAsync(string directory, int audioFrom)
    {
        var session = new EditorSession(new FuzzPlayback(), new FakeProber(), new FakeDialogs(), new FuzzIds());
        Rational rate = Rational.Fps25;
        var project = new Project { Id = "p", Name = "fuzz" };
        project.Media.Add(new MediaRef
        {
            Id = "m", Name = "m", Path = Path.Combine(directory, "m.mp4"), Duration = Time.FromSeconds(10), HasVideo = true, HasAudio = true,
            FrameRate = rate, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2,
        });
        var sequence = new Sequence { Id = "seq", Name = "Principal", FrameRate = rate };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 1" });
        sequence.AudioTracks.Add(new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1" });
        sequence.VideoTracks[0].Clips.Add(new Clip { Id = "v", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(2), LinkId = "l" });
        sequence.AudioTracks[0].Clips.Add(new Clip
        {
            Id = "a", MediaId = "m", Start = Time.FromSeconds(audioFrom), SourceIn = Time.FromSeconds(audioFrom), SourceOut = Time.FromSeconds(2), LinkId = "l",
        });
        project.Sequences.Add(sequence);
        string path = Path.Combine(directory, "p" + ProjectSerializer.FileExtension);
        ProjectSerializer.Save(project, path);
        Assert.True(await session.OpenProjectAsync(path));
        return session;
    }
}
