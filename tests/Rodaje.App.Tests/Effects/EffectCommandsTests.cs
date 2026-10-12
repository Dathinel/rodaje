using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Tests.Effects;

public class EffectCommandsTests
{
    private const string Source = BuiltInEffects.CommonOptions.Source;

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Dos clips de 10 s seguidos en la pista principal (con su audio), el primero seleccionado.</summary>
    internal static async Task<EditorSession> TwoClips()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        session.Select([session.MainTrack!.Clips[0].Id]);
        return session;
    }

    private static Clip First(EditorSession session) => session.MainTrack!.Clips[0];

    [Fact]
    public void ModoDeMezcla_CreaElEfectoYLoCambiaEnUnPaso() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        int steps = session.History.Count;
        Assert.True(session.Execute(EffectCommands.SetBlendMode(session.SequenceId, First(session).Id, "overlay")));
        Assert.True(session.Execute(EffectCommands.SetBlendMode(session.SequenceId, First(session).Id, "screen")));

        EffectInstance blend = Assert.Single(First(session).Effects);
        Assert.Equal(BuiltInEffects.Blend, blend.Type);
        Assert.Equal("screen", blend.Options[BuiltInEffects.BlendOptions.Mode]);
        Assert.Equal(steps + 2, session.History.Count);

        Assert.False(session.Execute(EffectCommands.SetBlendMode(session.SequenceId, First(session).Id, "plasma")));
        session.Undo();
        session.Undo();
        Assert.Empty(First(session).Effects);
    });

    [Fact]
    public void Lut_EnElColorIndicado_YVaciaLaQuita() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        string clipId = First(session).Id;
        Assert.True(session.Execute(EffectCommands.SetLut(session.SequenceId, clipId, null, "p@1.0.0/lut")));
        EffectInstance color = First(session).Effects.Single(e => e.Type == BuiltInEffects.Color);
        Assert.Equal("p@1.0.0/lut", color.Options[BuiltInEffects.ColorOptions.Lut]);

        Assert.True(session.Execute(EffectCommands.SetLut(session.SequenceId, clipId, color.Id, "")));
        Assert.False(First(session).Effects.Single().Options.ContainsKey(BuiltInEffects.ColorOptions.Lut));
        Assert.False(session.Execute(EffectCommands.SetLut(session.SequenceId, clipId, "no-existe", "x")));
    });

    [Fact]
    public void Filtro_SeExpandeConSuOrigen_YReaplicarLoReemplaza() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        var filter = new FilterPreset("calido", "Cálido", [
            new PresetEffect(BuiltInEffects.Color, new Dictionary<string, double> { ["temperature"] = 0.4, ["exposure"] = 9 },
                new Dictionary<string, string> { ["lut"] = "p@1.0.0/calido" }),
            new PresetEffect(BuiltInEffects.Vignette, new Dictionary<string, double> { ["amount"] = 0.3 }, new Dictionary<string, string>()),
            new PresetEffect(BuiltInEffects.Blend, new Dictionary<string, double>(), new Dictionary<string, string> { ["blendMode"] = "plasma" }),
        ]);
        const string reference = "p@1.0.0/calido-filtro";

        Assert.True(session.Execute(EffectCommands.ApplyFilter(session.SequenceId, First(session).Id, filter, reference)));
        Assert.True(session.Execute(EffectCommands.ApplyFilter(session.SequenceId, First(session).Id, filter, reference)));

        List<EffectInstance> effects = First(session).Effects;
        Assert.Equal([BuiltInEffects.Color, BuiltInEffects.Vignette, BuiltInEffects.Blend], effects.Select(e => e.Type));
        Assert.All(effects, e => Assert.Equal(reference, e.Options[Source]));
        Assert.Equal(0.4, effects[0].Parameters["temperature"].Value);
        Assert.Equal(4, effects[0].Parameters["exposure"].Value);
        Assert.Equal("p@1.0.0/calido", effects[0].Options["lut"]);
        Assert.False(effects[2].Options.ContainsKey("blendMode"));
    });

    [Fact]
    public void Efecto_CurvasComoKeyframesAlFinal_EscaladasALaSecuencia() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Clip clip = First(session);
        var preset = new EffectPreset("salida", "Salida", EffectPlacement.Out, 2, 960, 540,
            [new PresetEffect(BuiltInEffects.Transform, new Dictionary<string, double>(), new Dictionary<string, string>())],
            [
                new PresetCurve(BuiltInEffects.Transform, "x", [new PresetKey(0, 0, PresetInterpolation.EaseOut), new PresetKey(2, 100, PresetInterpolation.Linear)]),
                new PresetCurve(BuiltInEffects.Vignette, "amount", [new PresetKey(1, 0.5, PresetInterpolation.Linear)]),
            ]);

        Assert.True(session.Execute(EffectCommands.ApplyEffect(session.SequenceId, clip.Id, preset, "p@1.0.0/salida")));

        clip = First(session);
        EffectInstance transform = clip.Effects.Single(e => e.Type == BuiltInEffects.Transform);
        List<Keyframe> keys = transform.Parameters["x"].Keyframes;
        Assert.Equal([clip.SourceOut - S(2), clip.SourceOut], keys.Select(k => k.Time));
        Assert.Equal([0.0, 200.0], keys.Select(k => k.Value));
        Assert.Equal(Interpolation.EaseOut, keys[0].Interpolation);
        EffectInstance vignette = clip.Effects.Single(e => e.Type == BuiltInEffects.Vignette);
        Assert.Equal("p@1.0.0/salida", vignette.Options[Source]);
        Assert.Equal(clip.SourceOut - S(1), Assert.Single(vignette.Parameters["amount"].Keyframes).Time);
    });

    [Theory]
    [InlineData(EffectPlacement.In, 2, 0, 1)]
    [InlineData(EffectPlacement.Out, 2, 8, 1)]
    [InlineData(EffectPlacement.Whole, 2, 0, 5)]
    [InlineData(EffectPlacement.In, 20, 0, 0.5)]
    public void Colocacion(EffectPlacement placement, double seconds, double start, double scale)
    {
        var clip = new Clip { SourceIn = Time.Zero, SourceOut = S(10) };
        var preset = new EffectPreset("e", "E", placement, seconds, 1920, 1080, [], []);
        (Time at, double factor) = EffectCommands.Placement(clip, preset);
        Assert.Equal(S(start), at);
        Assert.Equal(scale, factor, 9);
    }

    [Fact]
    public void Efecto_EnUnClipA2x_SeColocaEnLaLineaDeTiempoYGuardaTiempoDelOrigen() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Assert.True(session.Execute(Edits.SetSpeed(session.SequenceId, First(session).Id, new Rational(2, 1))));
        Clip clip = First(session);
        Assert.Equal(S(5), clip.Duration);
        var preset = new EffectPreset("salida", "Salida", EffectPlacement.Out, 2, 1920, 1080, [],
            [new PresetCurve(BuiltInEffects.Transform, "opacity", [new PresetKey(0, 1, PresetInterpolation.Linear), new PresetKey(2, 0, PresetInterpolation.Linear)])]);

        Assert.Equal((S(3), 1.0), EffectCommands.Placement(clip, preset));
        Assert.True(session.Execute(EffectCommands.ApplyEffect(session.SequenceId, clip.Id, preset, "p@1.0.0/salida")));

        // Los últimos 2 s en pantalla son los últimos 4 s del origen.
        List<Keyframe> keys = First(session).Effects.Single().Parameters["opacity"].Keyframes;
        Assert.Equal([S(6), S(10)], keys.Select(k => k.Time));
    });

    [Fact]
    public void TiempoDeKeyframes_SigueElMapeo_EnReversaYCongelado()
    {
        var plain = new Clip { Start = S(2), SourceIn = S(1), SourceOut = S(11) };
        Assert.Equal(S(1), EffectCommands.KeyframeTime(plain, S(0)));
        Assert.Equal(S(4), EffectCommands.KeyframeTime(plain, S(5)));
        Assert.Equal(S(11), EffectCommands.KeyframeTime(plain, S(12)));
        Assert.Equal(S(11), EffectCommands.KeyframeTime(plain, S(30)));
        Assert.Equal(S(5), EffectCommands.TimelineOfKeyframe(plain, S(4)));

        var reversed = new Clip { Start = S(0), SourceIn = S(0), SourceOut = S(10), Speed = new ClipSpeed { Reverse = true }, Duration = S(10) };
        Time tick = Time.FromTicks(1);
        Assert.Equal(S(10) - tick, EffectCommands.KeyframeTime(reversed, S(0)));
        Assert.Equal(S(6) - tick, EffectCommands.KeyframeTime(reversed, S(4)));
        Assert.Equal(S(0), EffectCommands.KeyframeTime(reversed, S(10)));
        Assert.Equal(S(4), EffectCommands.TimelineOfKeyframe(reversed, S(6) - tick));

        // Un congelado no avanza en el origen, pero sus keyframes usan un tiempo virtual que sí (Ken Burns sobre el cuadro).
        var frozen = new Clip { Start = S(4), SourceIn = S(1), SourceOut = S(1) + tick, Speed = new ClipSpeed { Freeze = true }, Duration = S(3) };
        Assert.Equal(S(1), EffectCommands.KeyframeTime(frozen, S(4)));
        Assert.Equal(S(2), EffectCommands.KeyframeTime(frozen, S(5)));
        Assert.Equal(S(4), EffectCommands.KeyframeTime(frozen, S(7)));
        Assert.Equal(S(5), EffectCommands.TimelineOfKeyframe(frozen, S(2)));
        Assert.Equal(S(7), EffectCommands.TimelineOfKeyframe(frozen, S(9)));
    }

    [Fact]
    public void Transicion_DelArrastre_PoneTipoYPreajusteEnUnPaso() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Assert.True(TransitionDrag.TryParse("rodaje.wipe", out TransitionDrag? wipe));
        Assert.Equal(("rodaje.wipe", ""), (wipe.Type, wipe.Preset));
        Assert.True(TransitionDrag.TryParse(" p@1.0.0/barrido ", out TransitionDrag? preset));
        Assert.Equal((BuiltInTransitions.Preset, "p@1.0.0/barrido"), (preset.Type, preset.Preset));
        Assert.Equal("p@1.0.0/barrido", preset.ToString());
        Assert.False(TransitionDrag.TryParse(BuiltInTransitions.Preset, out _));
        Assert.False(TransitionDrag.TryParse(BuiltInEffects.Blur, out _));
        Assert.False(TransitionDrag.TryParse("", out _));

        int steps = session.History.Count;
        SequenceEditCommand command = preset.CreateCommand(session.SequenceId, First(session).Id);
        Assert.True(session.Execute(command));
        TransitionInstance transition = Assert.Single(session.MainTrack!.Transitions);
        Assert.Equal(command.CreatedIds[0], transition.Id);
        Assert.Equal("p@1.0.0/barrido", transition.Options[BuiltInTransitions.PresetOptions.Preset]);
        Assert.Equal(Rodaje.Core.Effects.EffectRegistry.DefaultTransitionDuration, transition.Duration);
        Assert.Equal(steps + 1, session.History.Count);
    });

    [Fact]
    public void Biblioteca_AplicaEfectosLutFiltrosYTransiciones() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        var loader = new FakeContentLoader();
        loader.Filters["p@1.0.0/frio"] = new FilterPreset("frio", "Frío",
            [new PresetEffect(BuiltInEffects.Color, new Dictionary<string, double> { ["temperature"] = -0.3 }, new Dictionary<string, string>())]);
        var applier = new ElementApplier(session, loader);
        LibraryElement Element(string reference, ItemKind kind, PackageOrigin? origin = PackageOrigin.Installed) =>
            new(reference, "", "", reference, true, origin, kind);

        Assert.True(await applier.ApplyAsync(Element(BuiltInEffects.Blur, ItemKind.Effect, null)));
        Assert.True(await applier.ApplyAsync(Element("p@1.0.0/frio", ItemKind.Filter)));
        Assert.True(await applier.ApplyAsync(Element("p@1.0.0/look", ItemKind.Lut)));
        Assert.False(await applier.ApplyAsync(Element("p@1.0.0/falta", ItemKind.Filter)));
        Assert.True(await applier.ApplyAsync(Element(BuiltInTransitions.Push, ItemKind.Transition, null)));

        Clip clip = First(session);
        Assert.Equal([BuiltInEffects.Blur, BuiltInEffects.Color], clip.Effects.Select(e => e.Type));
        Assert.Equal("p@1.0.0/look", clip.Effects[1].Options[BuiltInEffects.ColorOptions.Lut]);
        Assert.Equal(BuiltInTransitions.Push, Assert.Single(session.MainTrack!.Transitions).Type);

        // El segundo clip no tiene corte libre después; usa el de su inicio, que ya está ocupado.
        session.Select([session.MainTrack.Clips[1].Id]);
        Assert.False(await applier.ApplyAsync(Element(BuiltInTransitions.Crossfade, ItemKind.Transition, null)));
        session.ClearSelection();
        Assert.False(await applier.ApplyAsync(Element(BuiltInEffects.Blur, ItemKind.Effect, null)));
    });
}
