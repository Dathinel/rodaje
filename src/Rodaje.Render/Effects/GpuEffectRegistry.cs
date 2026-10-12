using System.Collections.Frozen;
using System.Collections.Immutable;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.Render.Effects;

/// <summary>Cómo aplica la GPU cada tipo de efecto.</summary>
public enum GpuEffectKind
{
    /// <summary>Llega en <c>LayerFrame.Transform</c>.</summary>
    Transform,

    /// <summary>Llega en <c>LayerTransform.Stabilization</c>: se compone antes de la transformación al colocar la capa.</summary>
    Stabilize,
    Color,
    Vignette,
    Blur,
    Sharpen,

    /// <summary>Llega en <c>LayerFrame.Blend</c>.</summary>
    Blend,
    Crossfade,
    Dip,
    Wipe,
    Push,
    ZoomDissolve,
    BlurDissolve,

    /// <summary>Se dibuja en la textura de máscaras de la capa y multiplica su alfa (<c>PsMask</c>).</summary>
    Mask,

    /// <summary>Transición de paquete: el motor la entrega resuelta a su mezcla; si llega sin resolver, fundido cruzado.</summary>
    Preset,
}

/// <summary>Una fila de la tabla: código y tipo del registro del núcleo, etapa e implementación.</summary>
public sealed record GpuEffect(int Code, string Type, EffectStage Stage, GpuEffectKind Kind);

/// <summary>
/// Tabla explícita código → implementación en la GPU, sin reflexión. Una prueba exige que coincida con
/// <see cref="EffectRegistry"/> en códigos, tipos y etapas.
/// </summary>
public static class GpuEffectRegistry
{
    public static ImmutableArray<GpuEffect> All { get; } =
    [
        new(1, BuiltInEffects.Transform, EffectStage.Transform, GpuEffectKind.Transform),
        new(2, BuiltInEffects.Color, EffectStage.PerPixel, GpuEffectKind.Color),
        new(3, BuiltInEffects.Vignette, EffectStage.PerPixel, GpuEffectKind.Vignette),
        new(4, BuiltInEffects.Blur, EffectStage.Spatial, GpuEffectKind.Blur),
        new(5, BuiltInEffects.Sharpen, EffectStage.Spatial, GpuEffectKind.Sharpen),
        new(6, BuiltInEffects.Blend, EffectStage.Blend, GpuEffectKind.Blend),
        new(9, BuiltInEffects.Stabilize, EffectStage.Transform, GpuEffectKind.Stabilize),
        new(10, BuiltInEffects.Mask, EffectStage.PerPixel, GpuEffectKind.Mask),
        new(101, BuiltInTransitions.Crossfade, EffectStage.Transition, GpuEffectKind.Crossfade),
        new(102, BuiltInTransitions.Dip, EffectStage.Transition, GpuEffectKind.Dip),
        new(103, BuiltInTransitions.Wipe, EffectStage.Transition, GpuEffectKind.Wipe),
        new(104, BuiltInTransitions.Push, EffectStage.Transition, GpuEffectKind.Push),
        new(105, BuiltInTransitions.ZoomDissolve, EffectStage.Transition, GpuEffectKind.ZoomDissolve),
        new(106, BuiltInTransitions.BlurDissolve, EffectStage.Transition, GpuEffectKind.BlurDissolve),
        new(107, BuiltInTransitions.Preset, EffectStage.Transition, GpuEffectKind.Preset),
    ];

    private static readonly FrozenDictionary<int, GpuEffect> ByCode = All.ToFrozenDictionary(e => e.Code);

    public static GpuEffect? Find(int code) => ByCode.TryGetValue(code, out GpuEffect? effect) ? effect : null;

    /// <summary>Si el núcleo espera que la GPU aplique este tipo (video o transición, sin contar audio ni lo diferido).</summary>
    public static bool AppliesOnGpu(EffectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Stage is EffectStage.Transform or EffectStage.PerPixel or EffectStage.Spatial
            or EffectStage.Blend or EffectStage.Transition;
    }
}
