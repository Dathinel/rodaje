using Rodaje.App.Services.Format;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Format;

public class ProjectFormatTests
{
    /// <summary>Secuencia 1920×1080 con un video 1920×1080 movido 100 px a la derecha y un título.</summary>
    private static Project Landscape()
    {
        var track = new Track { Id = "v", Kind = TrackKind.Video, Name = "Video 1" };
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5) };
        var transform = new EffectInstance { Id = "t", Type = BuiltInEffects.Transform };
        transform.Parameters[T.X] = new Parameter { Value = 100 };
        clip.Effects.Add(transform);
        track.Clips.Add(clip);
        var sequence = new Sequence { Id = "sec", Width = 1920, Height = 1080, FrameRate = Rational.Fps30 };
        sequence.VideoTracks.Add(track);
        var project = new Project { Id = "p", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m", Path = @"C:\clip.mp4", Width = 1920, Height = 1080, HasVideo = true });
        return project;
    }

    private static Parameter? Param(Project p, string name) =>
        p.Sequences[0].VideoTracks[0].Clips[0].Effects.Find(e => e.Type == BuiltInEffects.Transform)?.Parameters.GetValueOrDefault(name);

    [Theory]
    [InlineData("16:9", 1080, 1920, 1080)]
    [InlineData("9:16", 1080, 1080, 1920)]
    [InlineData("1:1", 1080, 1080, 1080)]
    [InlineData("4:5", 1080, 1080, 1350)]
    [InlineData("4:3", 1080, 1440, 1080)]
    [InlineData("21:9", 1080, 2520, 1080)]
    [InlineData("9:16", 2160, 2160, 3840)]
    public void LasProporciones_DanMedidasPares(string label, int shortSide, int width, int height)
    {
        AspectChoice aspect = ProjectFormats.Aspects.Single(a => a.Label == label);
        Assert.Equal((width, height), aspect.Size(shortSide));
        Assert.Same(aspect, ProjectFormats.Match(width, height));
    }

    [Fact]
    public void CambiarLaProporcion_ConservaLaResolucion()
    {
        AspectChoice vertical = ProjectFormats.Aspects.Single(a => a.Label == "9:16");
        Assert.Equal((1080, 1920), ProjectFormats.Reshape(vertical, 1920, 1080));
        Assert.Equal((720, 1280), ProjectFormats.Reshape(vertical, 1280, 720));
        Assert.Equal("9:16 · 1080×1920 · 29,97 fps", ProjectFormats.Describe(new Sequence { Width = 1080, Height = 1920, FrameRate = Rational.Ntsc30 }));
        Assert.True(ProjectFormats.Match(1000, 777).Custom);
    }

    [Fact]
    public void Encajar_NoTocaLaEscala_YLaPosicionSigueAlTamano_EnUnPasoDelHistorial()
    {
        Project project = Landscape();
        var undo = new UndoStack(project);
        SequenceSnapshot before = SequenceSnapshot.Capture(project.Sequences[0]);

        undo.Execute(SequenceFormatEdit.Command("sec", 1080, 1920, Rational.Fps25, ClipFit.Fit));

        Sequence s = project.Sequences[0];
        Assert.Equal((1080, 1920, Rational.Fps25), (s.Width, s.Height, s.FrameRate));
        Assert.Null(Param(project, T.Scale));
        Assert.Equal(100 * 1080 / 1920.0, Param(project, T.X)!.Value, 6);
        Assert.Equal(1, undo.Count);

        Assert.True(undo.Undo());
        Assert.True(before.Matches(s));
    }

    [Fact]
    public void Rellenar_LlenaElCuadroVertical()
    {
        Project project = Landscape();
        new UndoStack(project).Execute(SequenceFormatEdit.Command("sec", 1080, 1920, Rational.Fps30, ClipFit.Fill));

        // Encajado mide 1080×607,5; para cubrir 1920 de alto hace falta 1920/607,5 ≈ 3,16 veces.
        Assert.Equal(1920 / 607.5, Param(project, T.Scale)!.Value, 6);
        Assert.Equal(100 * 1920 / 1080.0, Param(project, T.X)!.Value, 6);
    }

    [Fact]
    public void Mantener_ConservaLosPixeles_YLosKeyframesSeEscalan()
    {
        Project project = Landscape();
        EffectInstance transform = project.Sequences[0].VideoTracks[0].Clips[0].Effects[0];
        transform.Parameters[T.Scale] = new Parameter { Value = 1, Keyframes = [new(Time.Zero, 1), new(Time.FromSeconds(1), 2)] };
        new UndoStack(project).Execute(SequenceFormatEdit.Command("sec", 1080, 1920, Rational.Fps30, ClipFit.Keep));

        Parameter scale = Param(project, T.Scale)!;
        Assert.Equal(1920 / 1080.0, scale.Keyframes[0].Value, 6);
        Assert.Equal(2 * 1920 / 1080.0, scale.Keyframes[1].Value, 6);
        Assert.Equal(100, Param(project, T.X)!.Value, 6);
    }

    [Fact]
    public void LasPistasBloqueadas_NoCambian()
    {
        Project project = Landscape();
        project.Sequences[0].VideoTracks[0].Locked = true;
        new UndoStack(project).Execute(SequenceFormatEdit.Command("sec", 1080, 1920, Rational.Fps30, ClipFit.Fill));
        Assert.Equal(100, Param(project, T.X)!.Value, 6);
        Assert.Null(Param(project, T.Scale));
    }

    [Fact]
    public void UnTamanoImpar_SeRechaza()
    {
        Assert.Throws<ArgumentException>(() => SequenceFormatEdit.Command("sec", 1081, 1920, Rational.Fps30, ClipFit.Fit));
        Assert.False(ProjectFormats.IsValidSize(8, 8));
    }
}
