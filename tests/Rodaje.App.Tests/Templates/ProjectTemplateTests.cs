using Rodaje.App.Services;
using Rodaje.App.Services.Templates;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Templates;

public class ProjectTemplateTests
{
    [Theory]
    [InlineData(BuiltInTemplates.Landscape1080Id, 1920, 1080)]
    [InlineData(BuiltInTemplates.Vertical1080Id, 1080, 1920)]
    [InlineData(BuiltInTemplates.Square1080Id, 1080, 1080)]
    [InlineData(BuiltInTemplates.Landscape4KId, 3840, 2160)]
    public void DeFabrica_CreanUnProyectoVálidoConSuFormato(string id, int width, int height)
    {
        ProjectTemplate template = BuiltInTemplates.Find(id)!;
        Assert.True(template.IsBuiltIn);

        Project project = template.CreateProject("p", new SequentialIdGenerator());

        Assert.Empty(ModelValidator.Validate(project));
        Sequence sequence = Assert.Single(project.Sequences);
        Assert.Equal((width, height), (sequence.Width, sequence.Height));
        Assert.Equal(Rational.Fps30, sequence.FrameRate);
        Assert.Equal(48000, sequence.SampleRate);
        Assert.Equal(["Video 1", "Video 2"], sequence.VideoTracks.Select(t => t.Name));
        Assert.Equal(["Audio 1", "Audio 2"], sequence.AudioTracks.Select(t => t.Name));
        Assert.All(sequence.VideoTracks, t => Assert.Equal(TrackKind.Video, t.Kind));
        Assert.All(sequence.AudioTracks, t => Assert.Equal(TrackKind.Audio, t.Kind));
        Assert.Equal(4, sequence.AllTracks.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void DeFabrica_TienenIdentificadoresYNombresDistintos()
    {
        Assert.Equal(4, BuiltInTemplates.All.Count);
        Assert.Equal(4, BuiltInTemplates.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(4, BuiltInTemplates.All.Select(t => t.Name).Distinct().Count());
        Assert.All(BuiltInTemplates.All, t => Assert.Same(t, BuiltInTemplates.Find(t.Id)));
    }

    [Fact]
    public void Resumen_DiceFormatoTasaYPistas()
    {
        Assert.Equal("1080×1920 · 30 fps · 2 de video, 2 de audio", BuiltInTemplates.Find(BuiltInTemplates.Vertical1080Id)!.Summary);
        var ntsc = new ProjectTemplate { Id = "propia.x", Name = "x", FrameRate = Rational.Ntsc30, VideoTracks = [new()], AudioTracks = [] };
        Assert.Equal("1920×1080 · 29.97 fps · 1 de video, 0 de audio", ntsc.Summary);
    }

    [Fact]
    public void GuardarComoPlantilla_TomaFormatoPistasYNombresSinClips()
    {
        var ids = new SequentialIdGenerator();
        Project project = BuiltInTemplates.Find(BuiltInTemplates.Landscape1080Id)!.CreateProject("p", ids);
        Sequence sequence = project.Sequences[0];
        sequence.Width = 2560;
        sequence.Height = 1440;
        sequence.FrameRate = Rational.Ntsc24;
        sequence.SampleRate = 44100;
        sequence.VideoTracks[0].Name = "Principal";
        sequence.VideoTracks[1].Name = "Rótulos";
        sequence.VideoTracks[1].Role = TrackRoles.Subtitles;
        sequence.VideoTracks[1].Muted = true;
        sequence.AudioTracks.Add(new Track { Id = "t-extra", Kind = TrackKind.Audio, Name = "Música" });

        ProjectTemplate template = ProjectTemplate.FromSequence("  Mi canal  ", sequence);

        Assert.False(template.IsBuiltIn);
        Assert.StartsWith("propia.", template.Id, StringComparison.Ordinal);
        Assert.Equal("Mi canal", template.Name);
        Assert.Equal((2560, 1440), (template.Width, template.Height));
        Assert.Equal(Rational.Ntsc24, template.FrameRate);
        Assert.Equal(44100, template.SampleRate);
        Assert.Equal(["Principal", "Rótulos"], template.VideoTracks.Select(t => t.Name));
        Assert.Equal([null, TrackRoles.Subtitles], template.VideoTracks.Select(t => t.Role));
        Assert.Equal(["Audio 1", "Audio 2", "Música"], template.AudioTracks.Select(t => t.Name));

        Project again = template.CreateProject("q", ids);
        Sequence copy = again.Sequences[0];
        Assert.Empty(ModelValidator.Validate(again));
        Assert.Equal((2560, 1440, Rational.Ntsc24, 44100), (copy.Width, copy.Height, copy.FrameRate, copy.SampleRate));
        Assert.Equal(["Principal", "Rótulos"], copy.VideoTracks.Select(t => t.Name));
        Assert.Equal(TrackRoles.Subtitles, copy.VideoTracks[1].Role);
        Assert.False(copy.VideoTracks[1].Muted);
        Assert.Equal(3, copy.AudioTracks.Count);
        Assert.All(copy.AllTracks, t => Assert.Empty(t.Clips));
        Assert.Empty(copy.AllTracks.Select(t => t.Id).Intersect(sequence.AllTracks.Select(t => t.Id)));
    }

    [Fact]
    public void Normalizar_AcotaLadosPistasYNombres()
    {
        var template = new ProjectTemplate
        {
            Id = "propia.a",
            Name = "Nombre\u0007 " + new string('x', 200),
            Width = 1081,
            Height = 99_999,
            VideoTracks = [.. Enumerable.Range(0, 12).Select(i => new TemplateTrack { Name = i == 0 ? " \t " : $"V{i}" })],
            AudioTracks = [],
        };

        ProjectTemplate normalized = template.Normalized()!;

        Assert.Equal(1080, normalized.Width);
        Assert.Equal(ProjectTemplate.MaxSide, normalized.Height);
        Assert.Equal(ProjectTemplate.MaxNameLength, normalized.Name.Length);
        Assert.DoesNotContain(normalized.Name, char.IsControl);
        Assert.Equal(NewProjectTracks.Max, normalized.VideoTracks.Count);
        Assert.Equal("Video 1", normalized.VideoTracks[0].Name);
        Assert.Equal("V1", normalized.VideoTracks[1].Name);
        Assert.Equal(["Audio 1"], normalized.AudioTracks.Select(t => t.Name));
    }

    [Theory]
    [InlineData("", "nombre", 30, 48000)]
    [InlineData("propia.a", "  ", 30, 48000)]
    [InlineData("propia.a", "nombre", 1000, 48000)]
    [InlineData("propia.a", "nombre", 30, 100)]
    public void Normalizar_SinArreglo_DevuelveNull(string id, string name, int fps, int sampleRate)
    {
        var template = new ProjectTemplate { Id = id, Name = name, FrameRate = new Rational(fps, 1), SampleRate = sampleRate };
        Assert.Null(template.Normalized());
        Assert.Throws<InvalidOperationException>(() => template.CreateProject("p", new SequentialIdGenerator()));
    }

    [Fact]
    public void Normalizar_NoPartePorLaMitadUnEmoji()
    {
        string name = new string('a', ProjectTemplate.MaxNameLength - 1) + "🎬";
        ProjectTemplate normalized = new ProjectTemplate { Id = "propia.a", Name = name }.Normalized()!;
        Assert.Equal(new string('a', ProjectTemplate.MaxNameLength - 1), normalized.Name);
    }
}
