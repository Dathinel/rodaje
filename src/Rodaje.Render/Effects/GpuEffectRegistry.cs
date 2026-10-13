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

    /// <summary>Llega en <c>LayerTransform.Crop</c>: cambia el tamaño colocado y las esquinas de la textura.</summary>
    Crop,

    /// <summary>En <c>PsGraded</c> con <c>ADVANCED</c>, antes del color, sobre la imagen original.</summary>
    ChromaKey,

    /// <summary>En <c>PsGraded</c> con <c>ADVANCED</c>, después del color y antes de la LUT.</summary>
    ColorWheels,

    /// <summary>En <c>PsGraded</c> con <c>ADVANCED</c>, después de las ruedas: tabla en <c>t10</c>.</summary>
    Curves,

    /// <summary>Transición de paquete: el motor la entrega resuelta a su mezcla; si llega sin resolver, fundido cruzado.</summary>
    Preset,

    /// <summary>En un intermedio, como el desenfoque: bloques del promedio de su zona (<c>PsPixelate</c>).</summary>
    Pixelate,
    /// <summary>En el intermedio, con <c>PsFx</c>: ruido de valor por cuadro.</summary>
    Grain,

    /// <summary>En el intermedio: luces en luz lineal, desenfocadas y sumadas.</summary>
    Glow,

    /// <summary>En el intermedio: rojo y azul muestreados con otra escala o desplazamiento.</summary>
    ChromaticAberration,

    /// <summary>En el intermedio: bilateral de 5 × 5 y mezcla con el cuadro anterior de la misma capa.</summary>
    Denoise,

    /// <summary>En el intermedio: otra lectura de uv (espejo o mosaico).</summary>
    Mirror,

    /// <summary>En el intermedio: lectura radial de barril o cojín.</summary>
    LensDistortion,

    /// <summary>En <c>PsGraded</c> con <c>ADVANCED</c>, después de las curvas y antes de la LUT.</summary>
    HslQualifier,

    /// <summary>Llega en <c>LayerTransform.Pin</c>: la capa se coloca con perspectiva (w por esquina en <c>VsQuad</c>).</summary>
    CornerPin,
    /// <summary>Antes que los demás del intermedio: del equirectangular entero a la vista plana de la cámara 360.</summary>
    Reframe360,
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
        new(11, BuiltInEffects.Crop, EffectStage.Transform, GpuEffectKind.Crop),
        new(24, BuiltInEffects.Pixelate, EffectStage.Spatial, GpuEffectKind.Pixelate),
        new(60, BuiltInEffects.CornerPin, EffectStage.Transform, GpuEffectKind.CornerPin),
        new(12, BuiltInEffects.ChromaKey, EffectStage.PerPixel, GpuEffectKind.ChromaKey),
        new(13, BuiltInEffects.ColorWheels, EffectStage.PerPixel, GpuEffectKind.ColorWheels),
        new(14, BuiltInEffects.Curves, EffectStage.PerPixel, GpuEffectKind.Curves),
        new(15, BuiltInEffects.Grain, EffectStage.Spatial, GpuEffectKind.Grain),
        new(16, BuiltInEffects.Glow, EffectStage.Spatial, GpuEffectKind.Glow),
        new(17, BuiltInEffects.ChromaticAberration, EffectStage.Spatial, GpuEffectKind.ChromaticAberration),
        new(18, BuiltInEffects.Denoise, EffectStage.Spatial, GpuEffectKind.Denoise),
        new(19, BuiltInEffects.Mirror, EffectStage.Spatial, GpuEffectKind.Mirror),
        new(22, BuiltInEffects.LensDistortion, EffectStage.Spatial, GpuEffectKind.LensDistortion),
        new(23, BuiltInEffects.HslQualifier, EffectStage.PerPixel, GpuEffectKind.HslQualifier),
        new(30, BuiltInEffects.Reframe360, EffectStage.Spatial, GpuEffectKind.Reframe360),
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
