using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Pila de efectos, parámetros y keyframes: cada comando se deshace y rehace exacto.</summary>
public class EffectEditsTests
{
    private const string Clip2 = "clip-2";
    private const string Fx = "fx-1";

    private static (Project Project, UndoStack Stack) NewStack()
    {
        Project project = Create();
        return (project, new UndoStack(project));
    }

    /// <summary>Ejecuta, comprueba que cambió algo, y que deshacer y rehacer dan exactamente los dos estados.</summary>
    private static string RoundTrip(UndoStack stack, ICommand command)
    {
        string before = ProjectDump.Of(stack.Project);
        stack.Execute(command);
        string after = ProjectDump.Of(stack.Project);
        Assert.NotEqual(before, after);
        Assert.Empty(ModelValidator.Validate(stack.Project));
        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(stack.Project));
        Assert.True(stack.Redo());
        Assert.Equal(after, ProjectDump.Of(stack.Project));
        return after;
    }

    private static Clip ClipOf(Project project, string id) => Seq(project).GetClip(id).Clip;

    private static Parameter Param(Project project, string name, string clipId = Clip2, string effectId = Fx) =>
        ClipOf(project, clipId).Effects.Single(e => e.Id == effectId).Parameters[name];

    [Fact]
    public void EnsureEffect_CreaLaTransformacionUnaSolaVez()
    {
        (Project project, UndoStack stack) = NewStack();
        SequenceEditCommand create = Edits.EnsureEffect(SequenceId, "clip-1", BuiltInEffects.Transform, new SequentialIdGenerator());
        RoundTrip(stack, create);
        Assert.Equal(["effect-1"], create.CreatedIds);
        Assert.Equal(BuiltInEffects.Transform, ClipOf(project, "clip-1").Effects.Single().Type);

        SequenceEditCommand again = Edits.EnsureEffect(SequenceId, "clip-1", BuiltInEffects.Transform);
        stack.Execute(again);
        Assert.Empty(again.CreatedIds);
        Assert.Single(ClipOf(project, "clip-1").Effects);
    }

    [Fact]
    public void AddEffect_EnPosicion_YRedoConservaElIdentificador()
    {
        (Project project, UndoStack stack) = NewStack();
        SequenceEditCommand add = Edits.AddEffect(SequenceId, Clip2, BuiltInEffects.Blur, 0);
        RoundTrip(stack, add);
        Assert.Equal([add.CreatedIds[0], Fx], ClipOf(project, Clip2).Effects.Select(e => e.Id));
        Assert.Equal("Añadir filtro", add.Description);
    }

    [Fact]
    public void AddEffect_PosicionInvalida_NoCambiaNada()
    {
        (Project project, UndoStack stack) = NewStack();
        string before = ProjectDump.Of(project);
        Assert.Throws<EditException>(() => stack.Execute(Edits.AddEffect(SequenceId, Clip2, BuiltInEffects.Blur, 5)));
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void MoverActivarYQuitar_SeDeshacenExactos()
    {
        (Project project, UndoStack stack) = NewStack();
        stack.Execute(Edits.AddEffect(SequenceId, Clip2, BuiltInEffects.Vignette, ids: new SequentialIdGenerator()));
        RoundTrip(stack, Edits.MoveEffect(SequenceId, Clip2, "effect-1", 0));
        Assert.Equal(["effect-1", Fx], ClipOf(project, Clip2).Effects.Select(e => e.Id));

        RoundTrip(stack, Edits.SetEffectEnabled(SequenceId, Clip2, Fx, false));
        Assert.False(ClipOf(project, Clip2).Effects[1].Enabled);

        RoundTrip(stack, Edits.RemoveEffect(SequenceId, Clip2, "effect-1"));
        Assert.Equal([Fx], ClipOf(project, Clip2).Effects.Select(e => e.Id));
        Assert.Throws<EditException>(() => stack.Execute(Edits.MoveEffect(SequenceId, Clip2, Fx, 1)));
    }

    [Fact]
    public void SetParameter_SinKeyframes_CambiaElValorFijo()
    {
        (Project project, UndoStack stack) = NewStack();
        RoundTrip(stack, Edits.SetParameter(SequenceId, Clip2, Fx, "x", 120, S(11)));
        Assert.Equal(120, Param(project, "x").Value);
        Assert.Empty(Param(project, "x").Keyframes);

        RoundTrip(stack, Edits.SetParameter(SequenceId, Clip2, Fx, "scale", 2, S(11)));
        Assert.Equal(2, Param(project, "scale").Value);
    }

    [Fact]
    public void SetParameter_Animado_CreaKeyframeConLaCurvaDelAnterior()
    {
        (Project project, UndoStack stack) = NewStack();
        RoundTrip(stack, Edits.SetParameter(SequenceId, Clip2, Fx, "opacity", 0.25, S(13)));
        Assert.Equal(
            [new Keyframe(S(10), 0), new Keyframe(S(12), 1, Interpolation.EaseIn), new Keyframe(S(13), 0.25, Interpolation.EaseIn)],
            Param(project, "opacity").Keyframes);

        RoundTrip(stack, Edits.SetParameter(SequenceId, Clip2, Fx, "opacity", 0.5, S(12)));
        Assert.Equal(new Keyframe(S(12), 0.5, Interpolation.EaseIn), Param(project, "opacity").Keyframes[1]);
    }

    [Fact]
    public void SetParameter_Animado_FueraDelClip_Falla()
    {
        (Project project, UndoStack stack) = NewStack();
        string before = ProjectDump.Of(project);
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "opacity", 0.5, S(16))));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "x", double.NaN, S(11))));
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void SetParameter_MismoInstante_SeFunde_YOtroInstanteNo()
    {
        (Project project, UndoStack stack) = NewStack();
        string before = ProjectDump.Of(project);
        for (int i = 1; i <= 20; i++)
        {
            stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "opacity", i / 20.0, S(11)));
        }

        Assert.Equal(1, stack.Count);
        Assert.Equal(1, Param(project, "opacity").Evaluate(S(11)));
        stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "opacity", 0.3, S(11.5)));
        Assert.Equal(2, stack.Count);
        stack.Undo();
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void Gesto_ConEfectoNuevoYArrastre_EsUnPaso()
    {
        (Project project, UndoStack stack) = NewStack();
        string before = ProjectDump.Of(project);
        stack.BeginGroup("Cambiar escala");
        var ensure = Edits.EnsureEffect(SequenceId, "clip-1", BuiltInEffects.Transform, new SequentialIdGenerator());
        stack.Execute(ensure);
        string effect = ensure.CreatedIds[0];
        for (int i = 0; i < 30; i++)
        {
            stack.Execute(Edits.SetParameter(SequenceId, "clip-1", effect, "scale", 1 + (i / 100.0), S(2)));
        }

        stack.EndGroup();
        string after = ProjectDump.Of(project);

        Assert.Equal(1, stack.Count);
        Assert.Equal("Cambiar escala", stack.UndoDescription);
        Assert.Equal(1.29, Param(project, "scale", "clip-1", effect).Value);
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
        stack.Redo();
        Assert.Equal(after, ProjectDump.Of(project));
    }

    [Fact]
    public void Arrastre_TrasBreakMerge_EsOtroPaso()
    {
        (_, UndoStack stack) = NewStack();
        stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "x", 1, S(11)));
        stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "x", 2, S(11)));
        stack.BreakMerge();
        stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "x", 3, S(11)));
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void SetKeyframe_EnParametroFijo_EmpiezaLaAnimacion()
    {
        (Project project, UndoStack stack) = NewStack();
        RoundTrip(stack, Edits.SetKeyframe(SequenceId, Clip2, Fx, "x", S(11), 0.5));
        RoundTrip(stack, Edits.SetKeyframe(SequenceId, Clip2, Fx, "x", S(14), 300, Interpolation.EaseOut));
        Assert.Equal([new Keyframe(S(11), 0.5), new Keyframe(S(14), 300, Interpolation.EaseOut)], Param(project, "x").Keyframes);

        RoundTrip(stack, Edits.SetKeyframe(SequenceId, Clip2, Fx, "rotation", S(10), 45));
        Assert.Equal(45, Param(project, "rotation").Value);
        Assert.Single(Param(project, "rotation").Keyframes);
    }

    [Fact]
    public void SetKeyframe_Existente_ConservaSuCurvaSiNoSePide()
    {
        (Project project, UndoStack stack) = NewStack();
        stack.Execute(Edits.SetKeyframe(SequenceId, Clip2, Fx, "opacity", S(12), 0.8));
        Assert.Equal(new Keyframe(S(12), 0.8, Interpolation.EaseIn), Param(project, "opacity").Keyframes[1]);
    }

    [Fact]
    public void RemoveKeyframe_ElUltimo_DejaElValorFijo()
    {
        (Project project, UndoStack stack) = NewStack();
        RoundTrip(stack, Edits.RemoveKeyframe(SequenceId, Clip2, Fx, "opacity", S(10)));
        Assert.Equal([new Keyframe(S(12), 1, Interpolation.EaseIn)], Param(project, "opacity").Keyframes);

        stack.Execute(Edits.SetParameter(SequenceId, Clip2, Fx, "opacity", 0.4, S(12)));
        RoundTrip(stack, Edits.RemoveKeyframe(SequenceId, Clip2, Fx, "opacity", S(12)));
        Assert.Empty(Param(project, "opacity").Keyframes);
        Assert.Equal(0.4, Param(project, "opacity").Value);
        Assert.Throws<EditException>(() => stack.Execute(Edits.RemoveKeyframe(SequenceId, Clip2, Fx, "opacity", S(12))));
    }

    [Fact]
    public void SetKeyframeInterpolation_CambiaSoloEseTramo()
    {
        (Project project, UndoStack stack) = NewStack();
        RoundTrip(stack, Edits.SetKeyframeInterpolation(SequenceId, Clip2, Fx, "opacity", S(10), Interpolation.Hold));
        Assert.Equal(0, Param(project, "opacity").Evaluate(S(11.9)));
        Assert.Equal(Interpolation.EaseIn, Param(project, "opacity").Keyframes[1].Interpolation);
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetKeyframeInterpolation(SequenceId, Clip2, Fx, "opacity", S(11), Interpolation.Linear)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetKeyframeInterpolation(SequenceId, Clip2, Fx, "opacity", S(10), (Interpolation)42)));
    }

    [Fact]
    public void PistaBloqueada_RechazaTodo()
    {
        (Project project, UndoStack stack) = NewStack();
        string before = ProjectDump.Of(project);
        Assert.Throws<EditException>(() => stack.Execute(Edits.EnsureEffect(SequenceId, "clip-3", BuiltInEffects.Transform)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.AddEffect(SequenceId, "clip-3", BuiltInEffects.Blur)));
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void ClipOEfectoInexistente_EsEditException()
    {
        (_, UndoStack stack) = NewStack();
        Assert.Throws<EditException>(() => stack.Execute(Edits.RemoveEffect(SequenceId, "nada", Fx)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.RemoveEffect(SequenceId, Clip2, "nada")));
        Assert.Throws<EditException>(() => stack.Execute(Edits.RemoveKeyframe(SequenceId, Clip2, Fx, "nada", S(10))));
    }

    [Fact]
    public void ClavesDeFusion_IncluyenParametroEInstante()
    {
        Assert.Equal("param:c:e:x:" + S(1).Ticks, Edits.SetParameter("s", "c", "e", "x", 0, S(1)).MergeKey);
        Assert.Equal("keyframe:c:e:x:" + S(1).Ticks, Edits.SetKeyframe("s", "c", "e", "x", S(1), 0).MergeKey);
        Assert.Null(Edits.RemoveKeyframe("s", "c", "e", "x", S(1)).MergeKey);
        Assert.Equal("Cambiar escala", Edits.SetParameter("s", "c", "e", "scale", 0, S(1), "Cambiar escala").Description);
    }

    [Fact]
    public void ValoresPorDefectoDeLaTransformacion()
    {
        Assert.Equal(1, BuiltInEffects.TransformParameters.DefaultValue(BuiltInEffects.TransformParameters.Scale));
        Assert.Equal(1, BuiltInEffects.TransformParameters.DefaultValue(BuiltInEffects.TransformParameters.Opacity));
        Assert.Equal(0, BuiltInEffects.TransformParameters.DefaultValue(BuiltInEffects.TransformParameters.X));
    }
}
