using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using static Rodaje.Core.Tests.Model.ModelSamples;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>
/// Tarea 92: un proyecto guardado con la v1.0.0, cuando la posición, el ancla y el giro no tenían límite, se sigue
/// abriendo aunque traiga valores enormes; al leerlo quedan acotados al límite nuevo. Lo que entra por edición lo sigue
/// rechazando el validador.
/// </summary>
public sealed class FormerlyUnboundedParametersTests
{
    [Fact]
    public void Un_proyecto_con_valores_enormes_se_abre_y_queda_acotado()
    {
        Project saved = ValidProject();
        Sequence sequence = saved.Sequences[0];
        EffectInstance transform = sequence.VideoTracks[0].Clips[0].Effects[0];
        transform.Parameters[T.X] = new Parameter { Value = 2e6 };
        transform.Parameters[T.Y] = new Parameter { Value = -1e300 };
        transform.Parameters[T.AnchorX] = new Parameter { Value = 5e7 };
        transform.Parameters[T.Rotation] = new Parameter
        {
            Value = 0,
            Keyframes = [new Keyframe(Sec(0), -5e9), new Keyframe(Sec(1), 90), new Keyframe(Sec(2), 1e12, Interpolation.Elastic)],
        };
        var mask = new EffectInstance { Id = "mascara", Type = BuiltInEffects.Mask };
        mask.Parameters[M.X] = new Parameter { Value = 1e5 };
        mask.Parameters[M.Rotation] = new Parameter { Value = -3e6 };
        sequence.VideoTracks[1].Clips[0].Effects.Add(mask);

        Clip title = MakeTitle("titulo", 20, 0, 5);
        title.Generator!.Style!.In!.Curves[T.X] = [new StyleKey(0, 3e6), new StyleKey(0.4, 0)];
        title.Generator.Style.In.Curves[T.Rotation] = [new StyleKey(0, -2e6), new StyleKey(0.4, 0)];
        sequence.VideoTracks.Add(new Track { Id = "v3", Kind = TrackKind.Video, Name = "V3", Clips = [title] });

        // Así lo guardaba la v1.0.0: el validador de hoy lo rechaza, pero abrirlo no.
        Assert.NotEmpty(ModelValidator.Validate(saved));
        Project opened = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(saved));

        Assert.Empty(ModelValidator.Validate(opened));
        Sequence s = opened.Sequences[0];
        Dictionary<string, Parameter> p = s.VideoTracks[0].Clips[0].Effects[0].Parameters;
        Assert.Equal(EffectRegistry.MaxOffset, p[T.X].Value);
        Assert.Equal(-EffectRegistry.MaxOffset, p[T.Y].Value);
        Assert.Equal(EffectRegistry.MaxOffset, p[T.AnchorX].Value);
        Assert.Equal([-EffectRegistry.MaxRotation, 90, EffectRegistry.MaxRotation], p[T.Rotation].Keyframes.Select(k => k.Value));
        Assert.Equal(Interpolation.Elastic, p[T.Rotation].Keyframes[2].Interpolation);
        Assert.Equal(1, p[T.Scale].Value);

        Dictionary<string, Parameter> m = s.VideoTracks[1].Clips[0].Effects.Single(e => e.Id == "mascara").Parameters;
        Assert.Equal(EffectRegistry.MaxMaskOffset, m[M.X].Value);
        Assert.Equal(-EffectRegistry.MaxRotation, m[M.Rotation].Value);

        StyleAnimation animation = s.VideoTracks[2].Clips[0].Generator!.Style!.In!;
        Assert.Equal(EffectRegistry.MaxOffset, animation.Curves[T.X][0].Value);
        Assert.Equal(-EffectRegistry.MaxRotation, animation.Curves[T.Rotation][0].Value);
        Assert.Equal(40, animation.Curves[T.Y][0].Value);
    }

    [Fact]
    public void Lo_que_ya_estaba_en_el_rango_se_abre_igual()
    {
        Project saved = ValidProject();
        saved.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters[T.X] = new Parameter { Value = -1234.5 };
        string json = ProjectSerializer.Serialize(saved);
        Assert.Equal(json, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(json)));
        Assert.Equal(0, ParameterLimits.Normalize(saved));
    }

    [Fact]
    public void Otros_parametros_fuera_de_rango_se_siguen_rechazando_al_abrir()
    {
        // Solo se acota lo que la v1.0.0 admitía sin límite; una escala de 1000 nunca fue válida.
        Project saved = ValidProject();
        saved.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters[T.Scale] = new Parameter { Value = 1000 };
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(saved)));
    }
}
