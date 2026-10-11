using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Settings;

public class NewProjectTracksTests
{
    [Fact]
    public void ValoresDeHoy_SonDosYDos()
    {
        var settings = new AppSettings();
        Assert.Equal(new NewProjectTracks(2, 2), settings.NewProjectTracks);
        Project project = settings.NewProjectTracks.CreateProject("p", new SequentialIdGenerator());
        Assert.Equal(["Video 1", "Video 2"], project.Sequences[0].VideoTracks.Select(t => t.Name));
        Assert.Equal(["Audio 1", "Audio 2"], project.Sequences[0].AudioTracks.Select(t => t.Name));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 8)]
    [InlineData(8, 8)]
    [InlineData(5, 4)]
    public void CreateProject_TrazaLaCantidadPedida(int video, int audio)
    {
        Project project = new NewProjectTracks(video, audio).CreateProject("p", new SequentialIdGenerator());
        Sequence sequence = project.Sequences[0];
        Assert.Equal(Enumerable.Range(1, video).Select(i => $"Video {i}"), sequence.VideoTracks.Select(t => t.Name));
        Assert.Equal(Enumerable.Range(1, audio).Select(i => $"Audio {i}"), sequence.AudioTracks.Select(t => t.Name));
        Assert.Equal(video + audio, sequence.VideoTracks.Concat(sequence.AudioTracks).Select(t => t.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(0, 99, 1, 8)]
    [InlineData(-5, 0, 1, 1)]
    public void Cantidades_FueraDeRango_SeAcotan(int video, int audio, int expectedVideo, int expectedAudio)
    {
        Sequence sequence = new NewProjectTracks(video, audio).CreateProject("p", new SequentialIdGenerator()).Sequences[0];
        Assert.Equal(expectedVideo, sequence.VideoTracks.Count);
        Assert.Equal(expectedAudio, sequence.AudioTracks.Count);
    }

    [Fact]
    public void Normalize_CorrigeAjustesEditadosAMano()
    {
        var settings = new AppSettings { DefaultVideoTracks = 40, DefaultAudioTracks = -2 };
        settings.Normalize();
        Assert.Equal(8, settings.DefaultVideoTracks);
        Assert.Equal(1, settings.DefaultAudioTracks);
    }

    [Fact]
    public void GuardarYLeer_ConservaLasPistas()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        store.Current.DefaultVideoTracks = 4;
        store.Current.DefaultAudioTracks = 3;
        store.Save();

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());
        Assert.Equal(new NewProjectTracks(4, 3), loaded.Current.NewProjectTracks);
    }

    [Fact]
    public async Task Sesion_UsaLasPistasAlCrearYAlHacerProyectoNuevo()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator(), new NewProjectTracks(3, 1));
        Assert.Equal(3, session.Sequence.VideoTracks.Count);
        Assert.Single(session.Sequence.AudioTracks);

        session.NewProjectTracks = new NewProjectTracks(1, 5);
        Assert.True(await session.NewProjectAsync());
        Assert.Single(session.Sequence.VideoTracks);
        Assert.Equal(5, session.Sequence.AudioTracks.Count);
        Assert.False(session.History.CanUndo);
    }
}
