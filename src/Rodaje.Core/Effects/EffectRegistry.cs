using System.Collections.Frozen;
using System.Collections.Immutable;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Effects;

/// <summary>
/// Los tipos de efecto y de transición que entiende el motor, sin reflexión (apto para AOT). Un tipo que no está aquí
/// (de un paquete o de otro programa) se conserva en el proyecto, pero no se aplica.
/// </summary>
/// <remarks>Los códigos son estables: el render los usa como índice y una prueba exige que no se repitan.</remarks>
public static class EffectRegistry
{
    private const double Inf = double.PositiveInfinity;

    /// <summary>Los 13 modos de mezcla, en el orden de <c>Rodaje.Playback.Composition.BlendMode</c>.</summary>
    public static ImmutableArray<string> BlendModes { get; } =
    [
        "normal", "add", "multiply", "screen", "overlay", "softLight", "hardLight",
        "darken", "lighten", "difference", "exclusion", "colorDodge", "colorBurn",
    ];

    /// <summary>Hacia dónde sale el clip en <see cref="BuiltInTransitions.Push"/>.</summary>
    public static ImmutableArray<string> PushDirections { get; } = ["left", "right", "up", "down"];

    /// <summary>Recorte de <see cref="BuiltInEffects.Stabilize"/>: automático o ninguno.</summary>
    public static ImmutableArray<string> StabilizeCrops { get; } = ["auto", "off"];

    /// <summary>Formas de <see cref="BuiltInEffects.Mask"/>, en el orden de <see cref="MaskShape"/>.</summary>
    public static ImmutableArray<string> MaskShapes { get; } = ["rectangle", "ellipse", "path", "image"];

    /// <summary>Duración con la que se suelta una transición.</summary>
    public static Time DefaultTransitionDuration { get; } = Time.FromSeconds(0.5);

    /// <summary>Ganancia que se trata como silencio: el mínimo de <see cref="A.GainDb"/>.</summary>
    public const double SilenceDb = -60;

    public static ImmutableArray<EffectDefinition> All { get; } =
    [
        new(BuiltInEffects.Transform, 1, EffectTarget.Video, EffectStage.Transform,
        [
            new(T.X, 0, -Inf, Inf),
            new(T.Y, 0, -Inf, Inf),
            new(T.Scale, 1, 0, 100),
            new(T.Rotation, 0, -Inf, Inf),
            new(T.Opacity, 1, 0, 1),
        ]),
        new(BuiltInEffects.Color, 2, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(C.Exposure, 0, -4, 4),
            new(C.Contrast, 0, -1, 1),
            new(C.Saturation, 0, -1, 1),
            new(C.Temperature, 0, -1, 1),
            new(C.Tint, 0, -1, 1),
            new(C.Highlights, 0, -1, 1),
            new(C.Shadows, 0, -1, 1),
            new(C.LutIntensity, 1, 0, 1),
        ],
        [OptionDefinition.Reference(BuiltInEffects.ColorOptions.Lut, allowsFile: true)]),
        new(BuiltInEffects.Vignette, 3, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(BuiltInEffects.VignetteParameters.Amount, 0.5, 0, 1),
            new(BuiltInEffects.VignetteParameters.Size, 0.5, 0, 1),
            new(BuiltInEffects.VignetteParameters.Softness, 0.5, 0, 1),
        ]),
        new(BuiltInEffects.Blur, 4, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.BlurParameters.Radius, 10, 0, 500),
        ]),
        new(BuiltInEffects.Sharpen, 5, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.SharpenParameters.Amount, 0.5, 0, 4),
            new(BuiltInEffects.SharpenParameters.Radius, 1, 0.5, 10),
        ]),
        new(BuiltInEffects.Blend, 6, EffectTarget.Video, EffectStage.Blend, [],
        [OptionDefinition.Choice(BuiltInEffects.BlendOptions.Mode, "normal", [.. BlendModes])]),
        new(BuiltInEffects.Audio, 7, EffectTarget.Audio, EffectStage.Audio,
        [
            new(A.GainDb, 0, SilenceDb, 24),
            new(A.FadeIn, 0, 0, Inf, Animatable: false),
            new(A.FadeOut, 0, 0, Inf, Animatable: false),
        ]),
        new(BuiltInEffects.Speed, 8, EffectTarget.Video | EffectTarget.Audio, EffectStage.Deferred) { Hidden = true },
        new(BuiltInEffects.Stabilize, 9, EffectTarget.Video, EffectStage.Transform,
        [
            new(BuiltInEffects.StabilizeParameters.Intensity, 0.5, 0, 1, Animatable: false),
        ],
        [OptionDefinition.Choice(BuiltInEffects.StabilizeOptions.Crop, "auto", [.. StabilizeCrops])]),
        new(BuiltInEffects.Mask, 10, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(M.X, 0.5, -Inf, Inf),
            new(M.Y, 0.5, -Inf, Inf),
            new(M.Width, 0.5, 0, 100),
            new(M.Height, 0.5, 0, 100),
            new(M.Rotation, 0, -Inf, Inf),
            new(M.Feather, 0, 0, 1),
            new(M.Opacity, 1, 0, 1),
        ],
        [
            OptionDefinition.Choice(BuiltInEffects.MaskOptions.Shape, "rectangle", [.. MaskShapes]),
            OptionDefinition.Choice(BuiltInEffects.MaskOptions.Invert, "off", "off", "on"),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Path),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Matte),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.MatteStart),
        ]),

        new(BuiltInTransitions.Crossfade, 101, EffectTarget.Transition, EffectStage.Transition),
        new(BuiltInTransitions.Dip, 102, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.DipParameters.Red, 0, 0, 1, Animatable: false),
            new(BuiltInTransitions.DipParameters.Green, 0, 0, 1, Animatable: false),
            new(BuiltInTransitions.DipParameters.Blue, 0, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.Wipe, 103, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.WipeParameters.Angle, 0, -360, 360, Animatable: false),
            new(BuiltInTransitions.WipeParameters.Softness, 0.05, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.Push, 104, EffectTarget.Transition, EffectStage.Transition, [],
        [OptionDefinition.Choice(BuiltInTransitions.PushOptions.Direction, "left", [.. PushDirections])]),
        new(BuiltInTransitions.ZoomDissolve, 105, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.ZoomDissolveParameters.Zoom, 0.25, 0, 4, Animatable: false),
        ]),
        new(BuiltInTransitions.BlurDissolve, 106, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.BlurDissolveParameters.Radius, 40, 0, 500, Animatable: false),
        ]),
        new(BuiltInTransitions.Preset, 107, EffectTarget.Transition, EffectStage.Transition, [],
        [OptionDefinition.Reference(BuiltInTransitions.PresetOptions.Preset)]),
    ];

    private static readonly FrozenDictionary<string, EffectDefinition> ByType =
        All.ToFrozenDictionary(d => d.Type, StringComparer.Ordinal);

    private static readonly FrozenDictionary<int, EffectDefinition> ByCode = All.ToFrozenDictionary(d => d.Code);

    /// <summary>Las transiciones, en el orden en que se ofrecen.</summary>
    public static ImmutableArray<EffectDefinition> Transitions { get; } = [.. All.Where(d => d.IsTransition)];

    public static EffectDefinition? Find(string type) =>
        type is not null && ByType.TryGetValue(type, out EffectDefinition? definition) ? definition : null;

    public static EffectDefinition? Find(int code) => ByCode.TryGetValue(code, out EffectDefinition? definition) ? definition : null;

    public static bool IsKnown(string type) => Find(type) is not null;

    /// <summary>Valor de un parámetro que falta; 0 si el tipo o el parámetro no están registrados.</summary>
    public static double DefaultValue(string type, string parameter) =>
        Find(type)?.FindParameter(parameter)?.DefaultValue ?? 0;

    /// <summary>Valor de una opción que falta; vacío si el tipo o la opción no están registrados.</summary>
    public static string DefaultOption(string type, string option) =>
        Find(type)?.FindOption(option)?.DefaultValue ?? "";
}
