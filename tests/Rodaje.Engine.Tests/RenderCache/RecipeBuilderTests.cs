using System.Diagnostics;
using System.Text.RegularExpressions;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.RenderCache;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.RenderCache;

public sealed partial class RecipeBuilderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(30, 1)]
    [InlineData(24000, 1001)]
    public void Huella_igual_si_y_solo_si_la_exportacion_compone_lo_mismo(long num, long den)
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(new Rational(num, den), backend, content);
        RecipeBuilder recipes = Create(b, content);
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Content = content });

        var byKey = new Dictionary<RenderCacheKey, string>();
        var byComposition = new Dictionary<string, RenderCacheKey>();
        for (long f = 0; f < offline.FrameCount; f++)
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            string composition = Normalize(OfflineProjects.Describe(frame));
            RenderCacheKey key = recipes.KeyAt(f) ?? throw new Xunit.Sdk.XunitException($"Sin receta en el cuadro {f}.");
            Assert.Equal(byKey.TryAdd(key, composition) ? composition : byKey[key], composition);
            Assert.Equal(byComposition.TryAdd(composition, key) ? key : byComposition[composition], key);
        }

        Assert.True(byKey.Count > offline.FrameCount / 2);
        output.WriteLine($"{offline.FrameCount} cuadros, {byKey.Count} recetas distintas.");
    }

    [Fact]
    public void Mover_todo_un_tramo_conserva_las_huellas_en_su_nueva_posicion()
    {
        RenderCacheKey?[] before = Keys(Stack(Time.Zero), 0, 90);
        RenderCacheKey?[] after = Keys(Stack(S(1)), 30, 90);

        Assert.Equal(before, after);
        Assert.All(before, k => Assert.NotNull(k));
    }

    [Fact]
    public void Cambiar_un_efecto_solo_invalida_los_cuadros_de_su_clip()
    {
        TimelineBuilder original = Stack(Time.Zero);
        TimelineBuilder edited = Stack(Time.Zero);
        Clip top = edited.Sequence.VideoTracks[1].Clips[0];
        top.Effects[0].Parameters[BuiltInEffects.ColorParameters.Exposure] = new Parameter { Value = 0.75 };

        RenderCacheKey?[] a = Keys(original, 0, 90);
        RenderCacheKey?[] b = Keys(edited, 0, 90);
        long topStart = top.Start.ToFrames(Rational.Fps30);
        long topEnd = top.End.ToFrames(Rational.Fps30);
        for (int f = 0; f < 90; f++)
        {
            Assert.True((a[f] != b[f]) == (f >= topStart && f < topEnd), $"cuadro {f}");
        }
    }

    [Fact]
    public void Deshacer_devuelve_las_mismas_huellas()
    {
        TimelineBuilder b = Stack(Time.Zero);
        RenderCacheKey?[] before = Keys(b, 0, 90);
        Clip top = b.Sequence.VideoTracks[1].Clips[0];
        Parameter old = top.Effects[0].Parameters[BuiltInEffects.ColorParameters.Exposure];
        top.Effects[0].Parameters[BuiltInEffects.ColorParameters.Exposure] = new Parameter { Value = -1 };
        Assert.NotEqual(before, Keys(b, 0, 90));
        top.Effects[0].Parameters[BuiltInEffects.ColorParameters.Exposure] = old;

        Assert.Equal(before, Keys(b, 0, 90));
    }

    [Fact]
    public void Sin_receta_donde_falta_contenido()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        content.Transitions.Clear();
        RecipeBuilder recipes = Create(b, content, missingResource: OfflineProjects.Lut, missingMedia: TimelineBuilder.PathOf("b"));

        (Track _, Clip ntsc) = b.Sequence.GetClip("ntsc");
        (Track _, Clip slow) = b.Sequence.GetClip("lento");
        TransitionInstance preset = b.Sequence.VideoTracks[2].Transitions.Single(t => t.Type == BuiltInTransitions.Preset);
        (Track _, Clip moving) = b.Sequence.GetClip("mueve");
        Time cut = moving.End;
        Assert.Null(recipes.KeyAt(Mid(ntsc)));
        Assert.Null(recipes.KeyAt(Mid(slow)));
        Assert.Null(recipes.KeyAt((cut - Time.FromTicks(preset.Duration.Ticks / 4)).ToFrames(Rational.Fps30)));

        TimelineBuilder clean = Stack(Time.Zero);
        Assert.All(Keys(clean, 0, 90), k => Assert.NotNull(k));
    }

    [Fact]
    public void Sin_receta_si_el_analisis_de_estabilizar_no_esta()
    {
        TimelineBuilder b = Stack(Time.Zero);
        Clip bottom = b.Sequence.VideoTracks[0].Clips[0];
        EffectInstance stabilize = TimelineEffectsTests.Effect(BuiltInEffects.Stabilize, (BuiltInEffects.StabilizeParameters.Intensity, 1));
        stabilize.Options[BuiltInEffects.StabilizeOptions.Analysis] = "analisis-1";
        bottom.Effects.Add(stabilize);
        var content = new FakeEffectContent();

        Assert.All(Keys(b, 0, 90, content), k => Assert.Null(k));
        content.Stabilizations["analisis-1"] = new StabilizationTrack(0, [new LayerStabilization(1, 0, 0, 0)]);
        Assert.All(Keys(b, 0, 90, content), k => Assert.NotNull(k));
    }

    [Fact]
    public void Ocho_capas_y_1800_cuadros_en_poco_tiempo()
    {
        var b = new TimelineBuilder(8, 0, Rational.Fps30);
        for (int t = 0; t < 8; t++)
        {
            b.AddMedia($"m{t}", Rational.Fps30, 3000);
            Clip clip = b.AddVideo(t, $"m{t}", Time.Zero, S(t), S(60), $"c{t}");
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Exposure, 0.1 * t)));
            clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Vignette, (BuiltInEffects.VignetteParameters.Amount, 0.5)));
        }

        RecipeBuilder recipes = Create(b, new FakeEffectContent());
        for (int f = 0; f < 60; f++)
        {
            _ = recipes.KeyAt(f);
        }

        var watch = Stopwatch.StartNew();
        for (int f = 0; f < 1800; f++)
        {
            Assert.NotNull(recipes.KeyAt(f));
        }

        watch.Stop();
        output.WriteLine($"1800 cuadros con 8 capas: {watch.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(Debugger.IsAttached ? 5000 : 600), $"{watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>Dos pistas: una base de 3 s con color y otra de 1 s encima, con color y mezcla, que empieza en el segundo 1.</summary>
    private static TimelineBuilder Stack(Time offset)
    {
        var b = new TimelineBuilder(2, 0, Rational.Fps30);
        b.AddMedia("base", Rational.Fps30, 3000);
        b.AddMedia("encima", Rational.Fps24, 3000);
        Clip bottom = b.AddVideo(0, "base", offset, S(10), S(3), "abajo");
        bottom.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Exposure, 0.2)));
        Clip top = b.AddVideo(1, "encima", offset + S(1), S(20), S(1), "arriba");
        top.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Exposure, -0.3)));
        EffectInstance blend = TimelineEffectsTests.Effect(BuiltInEffects.Blend);
        blend.Options[BuiltInEffects.BlendOptions.Mode] = "screen";
        top.Effects.Add(blend);
        return b;
    }

    private static RenderCacheKey?[] Keys(TimelineBuilder b, long from, int count, FakeEffectContent? content = null)
    {
        RecipeBuilder recipes = Create(b, content ?? new FakeEffectContent());
        return [.. Enumerable.Range(0, count).Select(i => recipes.KeyAt(from + i))];
    }

    private static RecipeBuilder Create(TimelineBuilder b, FakeEffectContent content, string? missingResource = null, string? missingMedia = null) =>
        RecipeBuilder.Create(b.Project, b.Sequence.Id, new RecipeBuilderOptions
        {
            ProjectDirectory = @"C:\proyecto",
            Content = content,
            Media = path => path == missingMedia ? null
                : new RecipeMedia(new RecipeSource("contenido:" + Path.GetFileNameWithoutExtension(path), "yuv420p"), new Rational(1, 1), 0),
            Resources = key => key.Reference == missingResource ? null : "v1:" + key.Reference,
            Fonts = _ => "fuentes",
            Chain = "prueba",
        });

    private static long Mid(Clip clip) => (clip.Start + Time.FromTicks(clip.Duration.Ticks / 2)).ToFrames(Rational.Fps30);

    /// <summary>La descripción sin el instante ni los ids de clip, que no cambian píxeles.</summary>
    private static string Normalize(string description) => ClipId().Replace(Instant().Replace(description, ""), "$1::");

    [GeneratedRegex(@"^t=\d+ ")]
    private static partial Regex Instant();

    [GeneratedRegex(@"(?<=(?:^|\| |<- |\{ ))(\d+):[^:\s]+:")]
    private static partial Regex ClipId();
}
