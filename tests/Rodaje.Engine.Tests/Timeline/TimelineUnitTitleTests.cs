using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Títulos por unidades en el motor: la fase y el avance de cada cuadro (sin delta del bloque), el karaoke cuadro a cuadro,
/// la escala de la textura, que no reserva más por Tick y que la exportación da las mismas capas que la vista previa.
/// </summary>
public sealed class TimelineUnitTitleTests(ITestOutputHelper output)
{
    private static TextStyle PerLetter() => new()
    {
        Size = 96,
        In = TextAnimationPresets.Find("subir")!.In(TextUnit.Letter, 0.5, seconds: 1),
        Out = TextAnimationPresets.Find("desenfoque")!.Out(TextUnit.Word, 0.3, seconds: 0.5),
    };

    private static GeneratedClipPlan Single(TimelineBuilder b) => Assert.Single(b.Plan().Layers[0].Generated);

    [Fact]
    public void Per_unit_phases_move_the_units_not_the_block()
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, S(10), S(4), "Letra por letra", PerLetter());
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Y, 380)));
        GeneratedClipPlan title = Single(b);
        Assert.True(title.Content.UsesUnits);

        // La transformación es la del clip en todo momento: la animación va por unidades.
        foreach (double t in (double[])[10, 10.5, 12, 13.8])
        {
            Assert.Equal(380, title.TransformAt(S(t)).Y, 9);
            Assert.Equal(1, title.TransformAt(S(t)).Opacity, 9);
        }

        Assert.Equal(new TextMotion(TextPhase.In, 0, -1, 0), title.MotionAt(S(10)));
        Assert.Equal(TextPhase.In, title.MotionAt(S(10.25)).Phase);
        Assert.Equal(0.25, title.MotionAt(S(10.25)).Progress, 9);
        Assert.Equal(TextMotion.Rest, title.MotionAt(S(12)));
        Assert.Equal(TextPhase.Out, title.MotionAt(S(13.75)).Phase);
        Assert.Equal(0.5, title.MotionAt(S(13.75)).Progress, 9);
        Assert.Equal(title.MotionAt(S(10.25)), title.Layer(0, S(10.25), new EffectListRing(), null, 0).TextMotion);
    }

    [Fact]
    public void A_short_clip_shrinks_both_phases_and_a_block_entrance_still_moves_the_block()
    {
        var b = new TimelineBuilder(1, 0);
        TextStyle style = PerLetter();
        style.In = new StyleAnimation { DurationSeconds = 1, Curves = new() { [T.Y] = [new(0, 50), new(1, 0)] } };
        b.AddTitle(0, S(10), S(1), "Corto", style);
        GeneratedClipPlan title = Single(b);

        // Entrada (1 s) y salida (0,5 s) no caben en 1 s: se reparten 2/3 y 1/3.
        Assert.True(title.TransformAt(S(10)).Y > 0, "La entrada por bloque sigue siendo un delta de la transformación.");
        Assert.Equal(TextMotion.Rest, title.MotionAt(S(10.3)));
        Assert.Equal(TextPhase.Out, title.MotionAt(S(10.7)).Phase);
        Assert.Equal(0.1, title.MotionAt(S(10.7)).Progress, 6);
    }

    [Fact]
    public void Karaoke_by_units_reports_the_word_and_how_much_of_it_was_said()
    {
        var b = new TimelineBuilder(1, 0);
        var style = new TextStyle { Size = 96, Highlight = new StyleHighlight { Color = "#FFD400", Mode = HighlightMode.Sweep } };
        Clip clip = b.AddTitle(0, S(10), S(3), "Hola a\ntodos", style);
        clip.SourceIn = S(1);
        clip.SourceOut = S(4);
        clip.Generator!.Words =
        [
            new GeneratorWord { Start = S(1.2), End = S(1.6), Text = "Hola" },
            new GeneratorWord { Start = S(1.6), End = S(1.8), Text = "a" },
            new GeneratorWord { Start = S(2), End = S(2.5), Text = "todos" },
        ];

        GeneratedClipPlan title = Single(b);
        Assert.Single(title.Contents);
        Assert.True(title.Content.UsesUnits);
        Assert.Equal([(0, 4), (5, 1), (7, 5)], title.Content.Words);
        Assert.Equal(-1, title.MotionAt(S(10.1)).Word);
        Assert.Equal((0, 0.5), (title.MotionAt(S(10.4)).Word, title.MotionAt(S(10.4)).WordProgress));
        Assert.Equal((1, 1.0), (title.MotionAt(S(10.9)).Word, title.MotionAt(S(10.9)).WordProgress));
        TextMotion last = title.MotionAt(S(11.25));
        Assert.Equal(2, last.Word);
        Assert.Equal(0.5, last.WordProgress, 9);
    }

    [Fact]
    public void Spans_and_box_height_go_to_the_descriptor_and_its_key()
    {
        var b = new TimelineBuilder(1, 0);
        Clip clip = b.AddTitle(0, S(0), S(3), "Hola mundo", new TextStyle());
        string plain = Single(b).Content.Key;
        clip.Generator!.Spans = [new TextSpan { Start = 5, Length = 5, Color = "#FF0000" }];
        clip.Generator.Box = new TextFrame { WidthFraction = 0.5, HeightFraction = 0.25 };
        GeneratedContent content = Single(b).Content;
        Assert.Equal(270, content.BoxHeight, 9);
        Assert.Single(content.Spans);
        Assert.NotEqual(plain, content.Key);
        Assert.False(content.UsesUnits);
    }

    [Fact]
    public void The_raster_scale_covers_the_largest_unit_and_disabled_edges_leave_the_key_alone()
    {
        var b = new TimelineBuilder(1, 0);
        TextStyle style = new() { Size = 96, In = TextAnimationPresets.Find("escala")!.In() };
        style.In.Curves[T.Scale] = [new(0, 3), new(0.7, 1)];
        Clip clip = b.AddTitle(0, S(0), S(3), "Grande", style);
        Assert.Equal(3, Single(b).Content.RasterScale, 9);

        clip.Generator!.Edges.In = false;
        GeneratedContent off = Single(b).Content;
        Assert.False(off.UsesUnits);
        Assert.Equal(1, off.RasterScale, 9);
    }

    [Fact]
    public void A_unit_title_tick_reserves_no_more_than_a_block_title_tick()
    {
        long Bytes(TextStyle style)
        {
            var backend = new FakeTimelineBackend();
            var builder = new TimelineBuilder(1, 0);
            Clip clip = builder.AddTitle(0, Time.Zero, S(10), "Título animado por letra", style);
            clip.Generator!.Words = [new GeneratorWord { Start = S(0.1), End = S(9), Text = "Título" }];
            using TimelineSession session = TimelineSessionTests.Open(builder, backend,
                new TimelineSessionOptions { Titles = new TimelineTitleTests.FakeTitles { AlwaysReady = true }, EnableAudio = false });
            session.Seek(S(0.2));
            for (int i = 0; i < 50; i++)
            {
                backend.RunDecoders();
                session.Tick().Dispose();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                session.Tick().Dispose();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / 200;
        }

        long block = Bytes(TimelineTitleTests.Animated());
        TextStyle units = PerLetter();
        units.Highlight = new StyleHighlight { Mode = HighlightMode.Sweep };
        long perUnit = Bytes(units);
        output.WriteLine($"Por Tick: título por bloque {block} B, por unidades con karaoke {perUnit} B");
        Assert.True(perUnit <= block, $"Por unidades reserva {perUnit} B por Tick y por bloque {block} B.");
    }

    [Fact]
    public void Export_renders_the_same_unit_title_layers_as_the_settled_preview()
    {
        var backend = new FakeTimelineBackend();
        var titles = new TimelineTitleTests.FakeTitles { AlwaysReady = true };
        var b = new TimelineBuilder(1, 0);
        TextStyle style = PerLetter();
        style.Highlight = new StyleHighlight { Mode = HighlightMode.Sweep };
        Clip clip = b.AddTitle(0, S(0), S(2), "Uno dos tres", style, id: "unidades");
        clip.Generator!.Words =
        [
            new GeneratorWord { Start = S(0.2), End = S(0.6), Text = "Uno" },
            new GeneratorWord { Start = S(0.7), End = S(1.1), Text = "dos" },
            new GeneratorWord { Start = S(1.2), End = S(1.9), Text = "tres" },
        ];
        using TimelineSession preview = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Titles = titles });
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Titles = titles });

        int moving = 0;
        for (long f = 0; f < offline.FrameCount; f++)
        {
            preview.SeekToFrame(f);
            backend.RunDecoders();
            using CompositionFrame expected = preview.Tick();
            using CompositionFrame actual = offline.Render(f, CancellationToken.None);
            Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(actual));
            moving += actual.Layers.Count(l => l.TextMotion != TextMotion.Rest);
        }

        Assert.Equal(offline.FrameCount, moving);
    }
}
