using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Las asas de fundido de los clips de audio: dónde están, qué agarran y qué comando dejan.</summary>
public class FadeHandlesTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Una pista de audio con un clip de 10 s que empieza a los 2 s; 100 px por segundo.</summary>
    private static (Sequence Sequence, Track Track, Clip Clip, TrackLayout Layout, TimelineViewport Viewport) Audio(double seconds = 10)
    {
        var clip = new Clip { Id = "a1", MediaId = "m", Start = S(2), SourceOut = S(seconds) };
        var track = new Track { Id = "audio", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [clip] };
        var video = new Track { Id = "video", Kind = TrackKind.Video, Name = "Video 1" };
        var sequence = new Sequence { Id = "s", Width = 1920, Height = 1080 };
        sequence.VideoTracks.Add(video);
        sequence.AudioTracks.Add(track);
        TrackLayout layout = TrackLayout.Build(sequence, video);
        var viewport = new TimelineViewport { PixelsPerSecond = 100, Width = 3000 };
        return (sequence, track, clip, layout, viewport);
    }

    private static void SetFades(Clip clip, double fadeIn, double fadeOut, string? shape = null)
    {
        var effect = new EffectInstance
        {
            Id = "vol",
            Type = BuiltInEffects.Audio,
            Parameters = { [A.FadeIn] = new Parameter { Value = fadeIn }, [A.FadeOut] = new Parameter { Value = fadeOut } },
        };
        if (shape is not null)
        {
            effect.Options[BuiltInEffects.AudioOptions.FadeShape] = shape;
        }

        clip.Effects.Add(effect);
    }

    [Fact]
    public void SinEfectoDeVolumen_LasAsasEstanEnLosBordesYNoHayFundidos()
    {
        (_, Track track, Clip clip, TrackLayout layout, TimelineViewport vp) = Audio();
        TrackRow row = layout.Find(track.Id)!;

        Assert.True(FadeHandles.HasHandles(clip, track));
        Assert.Equal((0.0, 0.0), FadeHandles.Seconds(clip));
        Assert.Equal(BuiltInEffects.FadeShapes.Power, FadeHandles.Shape(clip));
        Assert.Equal(200, FadeHandles.HandleX(clip, vp, FadeHandle.In));
        Assert.Equal(1200, FadeHandles.HandleX(clip, vp, FadeHandle.Out));
        Assert.Equal(FadeHandle.In, FadeHandles.Hit(clip, row, vp, 203, FadeHandles.HandleY(row) + 2));
        Assert.Equal(FadeHandle.Out, FadeHandles.Hit(clip, row, vp, 1195, FadeHandles.HandleY(row)));
    }

    [Fact]
    public void LasAsasSiguenALosFundidos_YSoloSeAgarranCerca()
    {
        (_, Track track, Clip clip, TrackLayout layout, TimelineViewport vp) = Audio();
        SetFades(clip, 1.5, 0.5, BuiltInEffects.FadeShapes.Linear);
        TrackRow row = layout.Find(track.Id)!;
        double y = FadeHandles.HandleY(row);

        Assert.Equal((1.5, 0.5), FadeHandles.Seconds(clip));
        Assert.Equal(BuiltInEffects.FadeShapes.Linear, FadeHandles.Shape(clip));
        Assert.Equal(350, FadeHandles.HandleX(clip, vp, FadeHandle.In));
        Assert.Equal(1150, FadeHandles.HandleX(clip, vp, FadeHandle.Out));
        Assert.Equal(FadeHandle.In, FadeHandles.Hit(clip, row, vp, 345, y));
        Assert.Equal(FadeHandle.In, FadeHandles.Hit(clip, row, vp, 340, y));
        Assert.Null(FadeHandles.Hit(clip, row, vp, 330, y));
        Assert.Null(FadeHandles.Hit(clip, row, vp, 350, y + FadeHandles.Grab + 1));
        Assert.Null(FadeHandles.Hit(clip, row, vp, 350, row.Bottom - 2));
    }

    [Fact]
    public void AreaDeAgarre_LlegaADiezPixeles()
    {
        (_, Track track, Clip clip, TrackLayout layout, TimelineViewport vp) = Audio();
        TrackRow row = layout.Find(track.Id)!;
        double y = FadeHandles.HandleY(row);
        double handleX = FadeHandles.HandleX(clip, vp, FadeHandle.In);

        Assert.Equal(FadeHandle.In, FadeHandles.Hit(clip, row, vp, handleX + 10, y));
        Assert.Equal(FadeHandle.In, FadeHandles.Hit(clip, row, vp, handleX - 10, y));
        Assert.Equal(FadeHandle.In, FadeHandles.Hit(clip, row, vp, handleX, y + 10));
        Assert.Null(FadeHandles.Hit(clip, row, vp, handleX + 11, y));
        Assert.Null(FadeHandles.Hit(clip, row, vp, handleX, y + 11));
    }

    [Fact]
    public void UnaPistaBloqueadaUnTituloOUnClipMuyAngosto_NoTienenAsas()
    {
        (_, Track track, Clip clip, TrackLayout layout, TimelineViewport vp) = Audio();
        TrackRow row = layout.Find(track.Id)!;
        track.Locked = true;
        Assert.False(FadeHandles.HasHandles(clip, track));
        Assert.Null(FadeHandles.Hit(clip, row, vp, 200, FadeHandles.HandleY(row)));
        track.Locked = false;

        var title = new Clip { Id = "t", Generator = new ClipGenerator { Text = "Título" }, SourceOut = S(5) };
        Assert.False(FadeHandles.HasHandles(title, track));

        var narrow = new TimelineViewport { PixelsPerSecond = 2, Width = 3000 };
        Assert.Null(FadeHandles.Hit(clip, row, narrow, 4, FadeHandles.HandleY(row)));
    }

    [Fact]
    public void LosSegundosArrastrados_SeMidenDesdeSuBordeYNoPasanDeLaMitadDelClip()
    {
        (_, _, Clip clip, _, TimelineViewport vp) = Audio();

        Assert.Equal(0.75, FadeHandles.SecondsAt(clip, vp, FadeHandle.In, 275));
        Assert.Equal(0.75, FadeHandles.SecondsAt(clip, vp, FadeHandle.Out, 1125));
        Assert.Equal(0, FadeHandles.SecondsAt(clip, vp, FadeHandle.In, 100));
        Assert.Equal(5, FadeHandles.SecondsAt(clip, vp, FadeHandle.In, 1500));
        Assert.Equal(5, FadeHandles.MaxSeconds(clip));
        Assert.Equal(0.33, FadeHandles.SecondsAt(clip, vp, FadeHandle.In, 233.4));
    }

    [Fact]
    public void LaCurvaDibujada_EsLaDelMotor()
    {
        Assert.Equal(0, FadeHandles.Factor(BuiltInEffects.FadeShapes.Power, 0));
        Assert.Equal(1, FadeHandles.Factor(BuiltInEffects.FadeShapes.Power, 1), 12);
        Assert.Equal(Math.Sin(Math.PI / 4), FadeHandles.Factor(BuiltInEffects.FadeShapes.Power, 0.5), 12);
        Assert.Equal(0.5, FadeHandles.Factor(BuiltInEffects.FadeShapes.Linear, 0.5));
        Assert.Equal((Math.Sqrt(1000) - 1) / 999, FadeHandles.Factor(BuiltInEffects.FadeShapes.Exponential, 0.5), 12);
        Assert.Equal(1, FadeHandles.Factor(BuiltInEffects.FadeShapes.Exponential, 1));
        Assert.Equal(1, FadeHandles.Factor("desconocida", 1), 12);
    }

    [Fact]
    public void ArrastrarUnAsa_CreaElEfectoYLosPasosSeguidosSeFundenEnUnoSolo()
    {
        (Sequence sequence, _, Clip clip, _, _) = Audio();
        var project = new Project { Id = "p", Name = "Fundidos", Sequences = [sequence] };
        var stack = new UndoStack(project);

        stack.Execute(FadeHandles.Set(sequence.Id, clip.Id, FadeHandle.In, 0.5));
        stack.Execute(FadeHandles.Set(sequence.Id, clip.Id, FadeHandle.In, 1.25));
        stack.Execute(FadeHandles.Set(sequence.Id, clip.Id, FadeHandle.Out, 2));

        Assert.Equal((1.25, 2.0), FadeHandles.Seconds(clip));
        Assert.Equal(2, stack.Count);
        Assert.Equal("Fundido de salida", stack.UndoDescription);
        // Deshacer restaura la secuencia desde una instantánea: el clip es otra instancia, se relee.
        Clip Current() => sequence.AudioTracks[0].Clips[0];
        Assert.True(stack.Undo());
        Assert.Equal((1.25, 0.0), FadeHandles.Seconds(Current()));
        Assert.True(stack.Undo());
        Assert.Equal((0.0, 0.0), FadeHandles.Seconds(Current()));
    }

    [Fact]
    public void FundidoHastaElCursor_PoneSegundosDistintosEnCadaClipEnUnPaso()
    {
        (Sequence sequence, Track track, Clip clip, _, _) = Audio();
        var other = new Clip { Id = "a2", MediaId = "m", Start = S(12), SourceOut = S(4) };
        track.Clips.Add(other);
        var project = new Project { Id = "p", Name = "Fundidos", Sequences = [sequence] };
        var stack = new UndoStack(project);

        stack.Execute(FadeHandles.SetMany(sequence.Id, [(clip.Id, 3), (other.Id, 1)], FadeHandle.Out));

        Assert.Equal((0.0, 3.0), FadeHandles.Seconds(clip));
        Assert.Equal((0.0, 1.0), FadeHandles.Seconds(other));
        Assert.Equal(1, stack.Count);
        Assert.Equal("Fundido de salida desde el cursor", stack.UndoDescription);
    }

    [Fact]
    public void ElGlobo_DiceElLadoYLosSegundosConComa() =>
        Assert.Equal(("Entrada 0,5 s", "Salida 1,25 s"), (FadeHandles.Balloon(FadeHandle.In, 0.5), FadeHandles.Balloon(FadeHandle.Out, 1.25)));
}
