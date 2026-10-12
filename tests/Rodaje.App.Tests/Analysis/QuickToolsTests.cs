using Rodaje.App.Views.Analysis;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Analysis;

public class QuickToolsTests
{
    /// <summary>Video con su audio enlazado (voz) y una música que se cruza con él en otra pista; y un título.</summary>
    private static Project Project()
    {
        static Clip Clip(string id, string media, double start, string? link = null) => new()
        {
            Id = id, MediaId = media, Start = Time.FromSeconds(start), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5), LinkId = link,
        };

        var video = new Track { Id = "v", Kind = TrackKind.Video, Name = "Video 1", Clips = [Clip("cam", "m", 0, "l1")] };
        var title = new Track { Id = "t", Kind = TrackKind.Video, Name = "Texto" };
        title.Clips.Add(new Clip { Id = "txt", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5), Generator = new ClipGenerator() });
        var voice = new Track { Id = "a", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [Clip("voz", "m", 0, "l1")] };
        var music = new Track { Id = "mu", Kind = TrackKind.Audio, Name = "Música", Clips = [Clip("musica", "song", 0)] };
        var sequence = new Sequence { Id = "sec", VideoTracks = [video, title], AudioTracks = [voice, music] };
        var project = new Project { Id = "p", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m", Path = @"C:\cam.mp4", HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080 });
        project.Media.Add(new MediaRef { Id = "song", Path = @"C:\song.mp3", HasAudio = true });
        return project;
    }

    private static QuickToolState State(string id, string[] selection, bool module = true)
    {
        Project p = Project();
        return QuickTools.State(id, p, p.Sequences[0], selection, module, module);
    }

    [Fact]
    public void SinSeleccion_LasDeAudioYVideoExplicanQueElegir_YTextoSiempreVa()
    {
        Assert.Equal("Selecciona un clip con sonido.", State("analysis.silences", []).Reason);
        Assert.Equal("Selecciona un clip de video.", State("analysis.stabilize", []).Reason);
        Assert.False(State("analysis.autocolor", []).Available);
        Assert.True(State(QuickTools.AddText, []).Available);
    }

    [Fact]
    public void ConLaMusicaElegida_AplicanRitmoYBajarLaMusica()
    {
        Assert.True(State("analysis.beats", ["musica"]).Available);
        Assert.True(State("analysis.ducking", ["musica"]).Available);
        Assert.True(State("analysis.silences", ["musica"]).Available);
        Assert.False(State("analysis.stabilize", ["musica"]).Available);
    }

    [Fact]
    public void ElVideo_AplicaEstabilizarYColor_YElRuidoUsaSuAudioEnlazado()
    {
        Assert.True(State("analysis.stabilize", ["cam"]).Available);
        Assert.True(State("analysis.whitebalance", ["cam"]).Available);
        Assert.True(State("analysis.noise", ["cam"]).Available);
        Assert.Contains("Audio con IA", State("analysis.noise", ["cam"], module: false).Reason);
    }

    [Fact]
    public void UnTitulo_NoEsVideoNiSonido()
    {
        Assert.False(State("analysis.stabilize", ["txt"]).Available);
        Assert.False(State("analysis.silences", ["txt"]).Available);
    }

    [Fact]
    public void SubtitulosAutomaticos_PidenUnClipConVozYElModulo()
    {
        Assert.True(State(QuickTools.AutoSubtitles, ["cam"]).Available);
        Assert.False(State(QuickTools.AutoSubtitles, []).Available);
        Assert.Contains("módulo", State(QuickTools.AutoSubtitles, ["cam"], module: false).Reason, StringComparison.Ordinal);
        Assert.True(State(QuickTools.TextToSpeech, []).Available);
        Assert.Equal(10, QuickTools.All.Count);
    }
}
