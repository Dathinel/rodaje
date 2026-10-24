using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Commands;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Inspector;

/// <summary>Con varios clips seleccionados, el inspector cambia a todos en un paso y muestra «—» si difieren.</summary>
public class MultiClipPropertyTests
{
    private const string Transform = BuiltInEffects.Transform;

    private static async Task<(EditorSession Session, ClipPropertyEditor Editor, Clip A, Clip B)> TwoClips()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        Clip a = session.MainTrack!.Clips[0];
        Clip b = session.MainTrack!.Clips[1];
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(2), Duration = session.Sequence.Duration });
        return (session, new ClipPropertyEditor(session), a, b);
    }

    private static double Opacity(ClipPropertyEditor editor, EditorSession session, Clip clip) =>
        editor.Read(session.Sequence.GetClip(clip.Id).Clip, Transform, P.Opacity, P.DefaultValue(P.Opacity)).Value;

    [Fact]
    public void CambiarLaOpacidad_VaATodosEnUnPaso_YElValorMixtoSeVe() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, Clip b) = await TwoClips();
        session.Select([a.Id]);
        Assert.True(editor.SetValue(a, Transform, P.Opacity, 0.5, "Cambiar opacidad"));
        session.Select([a.Id, b.Id]);
        (IReadOnlyList<Clip> videos, _) = editor.Targets();
        Assert.Equal(2, videos.Count);
        Assert.True(editor.IsMixed(videos, Transform, P.Opacity, P.DefaultValue(P.Opacity)));
        Assert.False(editor.IsMixed(videos, Transform, P.Scale, P.DefaultValue(P.Scale)));
        int steps = session.History.Count;

        editor.BeginChange(videos, Transform, P.Opacity, "Cambiar opacidad");
        Assert.True(editor.Change(0.7));
        Assert.True(editor.Change(0.8));
        editor.EndChange();

        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(0.8, Opacity(editor, session, a), 6);
        Assert.Equal(0.8, Opacity(editor, session, b), 6);
        (videos, _) = editor.Targets();
        Assert.False(editor.IsMixed(videos, Transform, P.Opacity, P.DefaultValue(P.Opacity)));

        session.Undo();
        Assert.Equal(0.5, Opacity(editor, session, a), 6);
        Assert.Equal(1, Opacity(editor, session, b), 6);
    });

    [Fact]
    public void ConTiposMezclados_SoloQuedaLoComun() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, _) = await TwoClips();
        Clip audioB = session.Sequence.AudioTracks[0].Clips[1];
        session.Select([a.Id, audioB.Id]);

        (IReadOnlyList<Clip> videos, IReadOnlyList<Clip> audios) = editor.Targets();

        Assert.Empty(videos);
        Assert.Equal(2, audios.Count);
        Assert.Contains(audioB, audios);
        Assert.True(editor.SetValue(audios, BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, -6, "Cambiar volumen"));
        Assert.All(audios, c => Assert.Equal(-6,
            editor.Read(session.Sequence.GetClip(c.Id).Clip, BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, 0).Value, 6));
    });

    [Fact]
    public void ResetAsync_VariosClips_RestableceEnUnSoloPaso() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, Clip b) = await TwoClips();
        session.Select([a.Id, b.Id]);
        (IReadOnlyList<Clip> videos, _) = editor.Targets();
        editor.SetValue(videos, Transform, P.Opacity, 0.4, "Cambiar opacidad");
        int steps = session.History.Count;

        bool reset = await editor.ResetAsync(videos, Transform, [(P.Opacity, 1.0)], "Restablecer opacidad", _ => Task.FromResult(true));
        Assert.True(reset);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(1.0, Opacity(editor, session, a), 6);
        Assert.Equal(1.0, Opacity(editor, session, b), 6);

        session.Undo();
        Assert.Equal(0.4, Opacity(editor, session, a), 6);
        Assert.Equal(0.4, Opacity(editor, session, b), 6);
    });

    [Fact]
    public void VoltearYModoDeMezcla_VariosClips_AplicaEnUnSoloPaso() => UiThread.Run(async () =>
    {
        (EditorSession session, _, Clip a, Clip b) = await TwoClips();
        int steps = session.History.Count;

        // Voltear horizontal
        Assert.True(session.Execute(EffectCommands.SetOption(session.SequenceId, [a.Id, b.Id], Transform, null, BuiltInEffects.TransformOptions.FlipX, "on", "Voltear horizontal")));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("on", session.Sequence.GetClip(a.Id).Clip.Effects.First(e => e.Type == Transform).Options[BuiltInEffects.TransformOptions.FlipX]);
        Assert.Equal("on", session.Sequence.GetClip(b.Id).Clip.Effects.First(e => e.Type == Transform).Options[BuiltInEffects.TransformOptions.FlipX]);

        session.Undo();
        Assert.DoesNotContain(session.Sequence.GetClip(a.Id).Clip.Effects, e => e.Type == Transform && e.Options.ContainsKey(BuiltInEffects.TransformOptions.FlipX));

        // Blend mode
        Assert.True(session.Execute(EffectCommands.SetBlendMode(session.SequenceId, [a.Id, b.Id], "multiply")));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("multiply", session.Sequence.GetClip(a.Id).Clip.Effects.First(e => e.Type == BuiltInEffects.Blend).Options[BuiltInEffects.BlendOptions.Mode]);
        Assert.Equal("multiply", session.Sequence.GetClip(b.Id).Clip.Effects.First(e => e.Type == BuiltInEffects.Blend).Options[BuiltInEffects.BlendOptions.Mode]);

        session.Undo();
        Assert.DoesNotContain(session.Sequence.GetClip(a.Id).Clip.Effects, e => e.Type == BuiltInEffects.Blend);
    });

    [Fact]
    public void Velocidad_VariosClips_AplicaEnUnSoloPaso_YDetectaMixto() => UiThread.Run(async () =>
    {
        (EditorSession session, _, Clip a, Clip b) = await TwoClips();
        var speedEditor = new SpeedEditor(session, SpeedCommands.Default);
        session.Select([a.Id]);
        speedEditor.SetRate(new Rational(2, 1));

        session.Select([a.Id, b.Id]);
        IReadOnlyList<Clip> clips = speedEditor.Targets();
        Assert.True(SpeedEditor.IsMixed(clips));

        int steps = session.History.Count;
        speedEditor.SetRate(new Rational(3, 1));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.False(SpeedEditor.IsMixed(speedEditor.Targets()));

        Assert.Equal(new Rational(3, 1), session.Sequence.GetClip(a.Id).Clip.Speed!.Rate);
        Assert.Equal(new Rational(3, 1), session.Sequence.GetClip(b.Id).Clip.Speed!.Rate);

        session.Undo();
        Assert.Equal(new Rational(2, 1), session.Sequence.GetClip(a.Id).Clip.Speed!.Rate);
        Assert.Null(session.Sequence.GetClip(b.Id).Clip.Speed);
    });

    [Fact]
    public void Filtros_AnadirQuitarYEditar_VariosClipsEnUnSoloPaso() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, Clip b) = await TwoClips();
        session.Select([a.Id, b.Id]);
        int steps = session.History.Count;

        // Añadir filtro a ambos
        var addCmd = Edits.AddEffectOnClips(session.SequenceId, [a.Id, b.Id], BuiltInEffects.Color);
        Assert.True(session.Execute(addCmd));
        Assert.Equal(steps + 1, session.History.Count);

        Clip curA = session.Sequence.GetClip(a.Id).Clip;
        Clip curB = session.Sequence.GetClip(b.Id).Clip;
        EffectInstance fxA = curA.Effects.First(e => e.Type == BuiltInEffects.Color);
        EffectInstance fxB = curB.Effects.First(e => e.Type == BuiltInEffects.Color);

        // Cambiar parámetro común en uno solo primero para generar estado mixto
        editor.SetValue(curA, BuiltInEffects.Color, "saturation", 0.4, "Cambiar saturación", fxA.Id);
        curA = session.Sequence.GetClip(a.Id).Clip;
        var targets = new List<(Clip Clip, string? EffectId)> { (curA, fxA.Id), (curB, fxB.Id) };
        Assert.True(editor.IsMixed(targets, BuiltInEffects.Color, "saturation"));

        // Editar común en ambos en un solo paso
        steps = session.History.Count;
        editor.SetValue(targets, BuiltInEffects.Color, "saturation", 0.8, "Cambiar saturación");
        Assert.Equal(steps + 1, session.History.Count);

        curA = session.Sequence.GetClip(a.Id).Clip;
        curB = session.Sequence.GetClip(b.Id).Clip;
        Assert.Equal(0.8, editor.Read(curA, BuiltInEffects.Color, "saturation", 0, fxA.Id).Value, 6);
        Assert.Equal(0.8, editor.Read(curB, BuiltInEffects.Color, "saturation", 0, fxB.Id).Value, 6);

        // Quitar filtro de ambos en un solo paso
        steps = session.History.Count;
        var removeCmd = Edits.RemoveEffectFromClips(session.SequenceId, [(a.Id, fxA.Id), (b.Id, fxB.Id)]);
        Assert.True(session.Execute(removeCmd));
        Assert.Equal(steps + 1, session.History.Count);
        Assert.DoesNotContain(session.Sequence.GetClip(a.Id).Clip.Effects, e => e.Type == BuiltInEffects.Color);
        Assert.DoesNotContain(session.Sequence.GetClip(b.Id).Clip.Effects, e => e.Type == BuiltInEffects.Color);

        session.Undo();
        Assert.Contains(session.Sequence.GetClip(a.Id).Clip.Effects, e => e.Type == BuiltInEffects.Color);
        Assert.Contains(session.Sequence.GetClip(b.Id).Clip.Effects, e => e.Type == BuiltInEffects.Color);
    });

    [Fact]
    public void Mascaras_EditarParametroPorIndice_VariosClipsEnUnSoloPaso() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, Clip b) = await TwoClips();
        var addA = Edits.AddEffect(session.SequenceId, a.Id, BuiltInEffects.Mask);
        var addB = Edits.AddEffect(session.SequenceId, b.Id, BuiltInEffects.Mask);
        session.Execute(addA);
        session.Execute(addB);

        Clip curA = session.Sequence.GetClip(a.Id).Clip;
        Clip curB = session.Sequence.GetClip(b.Id).Clip;
        string maskA = addA.CreatedIds[0];
        string maskB = addB.CreatedIds[0];

        // Feather mixto
        editor.SetValue(curA, BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Feather, 0.15, "Borde", maskA);
        curA = session.Sequence.GetClip(a.Id).Clip;
        var targets = new List<(Clip Clip, string? EffectId)> { (curA, maskA), (curB, maskB) };
        Assert.True(editor.IsMixed(targets, BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Feather));

        int steps = session.History.Count;
        editor.SetValue(targets, BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Feather, 0.30, "Borde común");
        Assert.Equal(steps + 1, session.History.Count);

        curA = session.Sequence.GetClip(a.Id).Clip;
        curB = session.Sequence.GetClip(b.Id).Clip;
        Assert.Equal(0.30, editor.Read(curA, BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Feather, 0, maskA).Value, 6);
        Assert.Equal(0.30, editor.Read(curB, BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Feather, 0, maskB).Value, 6);
    });

    [Fact]
    public void EstiloTexto_VariosClips_CambiaEstiloEnUnPaso_YConservaContenido() => UiThread.Run(async () =>
    {
        (EditorSession session, _, _, _) = await TwoClips();
        var genA = new ClipGenerator { Text = "Texto Clip A", Style = TitleStyles.Default() };
        var genB = new ClipGenerator { Text = "Texto Clip B", Style = TitleStyles.Default() };
        var cmdA = Edits.InsertTitle(session.SequenceId, Time.Zero, Time.FromSeconds(3), genA);
        var cmdB = Edits.InsertTitle(session.SequenceId, Time.FromSeconds(4), Time.FromSeconds(3), genB);
        session.Execute(cmdA);
        session.Execute(cmdB);

        string titleA = cmdA.CreatedIds[0];
        string titleB = cmdB.CreatedIds[0];

        session.Select([titleA, titleB]);
        var titleEditor = new TitleEditor(session);
        Assert.Equal(2, titleEditor.Targets().Count);

        int steps = session.History.Count;
        Assert.True(titleEditor.ChangeStyle("font-size", s => s.Size = 88));
        Assert.Equal(steps + 1, session.History.Count);

        Clip curA = session.Sequence.GetClip(titleA).Clip;
        Clip curB = session.Sequence.GetClip(titleB).Clip;
        Assert.Equal(88, curA.Generator!.Style!.Size);
        Assert.Equal(88, curB.Generator!.Style!.Size);
        // El contenido del texto no cambia
        Assert.Equal("Texto Clip A", curA.Generator!.Text);
        Assert.Equal("Texto Clip B", curB.Generator!.Text);

        session.Undo();
        curA = session.Sequence.GetClip(titleA).Clip;
        curB = session.Sequence.GetClip(titleB).Clip;
        Assert.Equal(TitleStyles.Default().Size, curA.Generator!.Style!.Size);
        Assert.Equal(TitleStyles.Default().Size, curB.Generator!.Style!.Size);
    });
}
