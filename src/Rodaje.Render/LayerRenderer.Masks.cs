using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>Cómo se ve una máscara en la vista previa mientras se edita.</summary>
public enum MaskViewMode
{
    /// <summary>La imagen con sus máscaras, como se exporta.</summary>
    None,

    /// <summary>La máscara en gris: blanco lo que se ve, negro lo que tapa.</summary>
    Matte,

    /// <summary>La imagen entera con lo que tapa la máscara teñido de rojo.</summary>
    Overlay,
}

/// <summary>
/// Solo vista previa: muestra el grupo de máscaras de la máscara número <paramref name="MaskOrdinal"/> (contando las
/// máscaras activas de la capa en orden) del clip <paramref name="ClipId"/>, en lugar de aplicarlo.
/// </summary>
public readonly record struct MaskPreview(string ClipId, int MaskOrdinal, MaskViewMode Mode);

internal sealed unsafe partial class LayerRenderer
{
    /// <summary>Lado mayor de la textura de máscaras: con un borde suave no hace falta más.</summary>
    internal const int MaxMaskSide = 2048;

    /// <summary>Las máscaras de la capa (sin destino).</summary>
    private readonly List<LayerEffect> _masks = [];

    /// <summary>Las máscaras que limitan un efecto: índice del efecto en la lista de la capa y la máscara.</summary>
    private readonly List<(int Target, LayerEffect Mask)> _scopedMasks = [];
    private readonly List<LayerEffect> _group = [];

    /// <summary>Solo vista previa: la máscara que se ve en gris o superpuesta.</summary>
    public MaskPreview? MaskView { get; set; }

    /// <summary>El grupo que se muestra en esta capa (−1 la capa, o el índice del efecto) y cómo; nulo si no le toca.</summary>
    private (int Group, MaskViewMode Mode)? _maskView;

    /// <summary>
    /// Dibuja las máscaras que recortan la capa en una textura del tamaño en pantalla y la deja lista para <c>PsGraded</c>
    /// con <see cref="EffectShaders.Mask"/>. Devuelve la textura, que quien llama devuelve al pool, o <c>null</c> si no hay
    /// máscaras que aplicar (sin shader o sin memoria se saltan y se cuentan).
    /// </summary>
    private RenderTarget? PrepareMasks(in LayerPlacement placement, in Destination destination, ref LayerPlan plan,
        ref EffectConstants effects, ref FrameStats stats)
    {
        if (_masks.Count == 0)
        {
            return null;
        }

        (int width, int height) = MaskSize(placement, destination);
        if (DrawMaskGroup(_masks, ref width, ref height, placement.LocalWidth, placement.LocalHeight, ref stats, out int applied) is not { } target)
        {
            return null;
        }

        plan = new LayerPlan(plan.Flags | EffectShaders.Mask, plan.Lut, plan.PerPixelCount + applied, plan.Curves);
        effects.MapB = new ImageView(target, width, height).Map;
        if (_maskView is { Group: -1, Mode: var mode })
        {
            effects.BlendInfo.W = (float)mode;
        }

        return target;
    }

    /// <summary>
    /// Las máscaras con el uso «limitar efectos», en una textura como la de las de recorte (en el espacio de la capa) para
    /// <c>RegionMix</c>. <c>null</c> si no hay ninguna: los efectos con zona se aplican entonces en toda la imagen.
    /// </summary>
    private RenderTarget? PrepareRegionMask(in LayerPlacement placement, in Destination destination, ref FrameStats stats)
    {
        if (_regionMasks.Count == 0)
        {
            return null;
        }

        (int width, int height) = MaskSize(placement, destination);
        RenderTarget? target = DrawMaskGroup(_regionMasks, ref width, ref height, placement.LocalWidth, placement.LocalHeight, ref stats,
            out int applied);
        if (target is not null)
        {
            _regionMap = new ImageView(target, width, height).Map;
            stats.EffectsApplied += applied;
        }

        return target;
    }

    /// <summary>Tamaño de la textura de máscaras: el de la capa en pantalla, sin pasar del destino ni de <see cref="MaxMaskSide"/>.</summary>
    private static (int Width, int Height) MaskSize(in LayerPlacement placement, in Destination destination)
    {
        float imageWidth = placement.LocalWidth;
        float imageHeight = placement.LocalHeight;
        float onScreen = placement.Scale * destination.PixelScale;
        float wanted = Math.Max(imageWidth, imageHeight) * onScreen;
        float fit = Math.Min(1f, Math.Min(Math.Max(destination.Width, destination.Height), MaxMaskSide) / Math.Max(wanted, 1e-3f));
        int width = Math.Clamp((int)MathF.Ceiling(imageWidth * onScreen * fit), 1, MaxMaskSide);
        int height = Math.Clamp((int)MathF.Ceiling(imageHeight * onScreen * fit), 1, MaxMaskSide);
        return (width, height);
    }

    /// <summary>Las máscaras que limitan el efecto <paramref name="index"/> de la capa, en orden.</summary>
    private List<LayerEffect> ScopedGroup(int index)
    {
        _group.Clear();
        foreach ((int target, LayerEffect mask) in _scopedMasks)
        {
            if (target == index)
            {
                _group.Add(mask);
            }
        }

        return _group;
    }

    /// <summary>
    /// Combina <paramref name="masks"/> en orden en una textura de <paramref name="width"/> × <paramref name="height"/>
    /// que cubre la imagen de la capa (<paramref name="imageWidth"/> × <paramref name="imageHeight"/> píxeles): empieza en 0
    /// o en 1 según el modo de la primera que se dibuja y cada una se suma, resta, interseca o hace la diferencia con el
    /// estado de mezcla. Nula si ninguna se pudo dibujar.
    /// </summary>
    private RenderTarget? DrawMaskGroup(List<LayerEffect> masks, ref int width, ref int height, float imageWidth, float imageHeight,
        ref FrameStats stats, out int applied)
    {
        applied = 0;
        // Con todos los bordes anchos la textura puede ser más chica: la rampa es lineal y el filtro bilineal la rehace.
        int reduce = Math.Clamp((int)(SoftestEdge(masks, imageWidth, imageHeight) * width / imageWidth / 8), 1, 4);
        width = Math.Max(1, (width + reduce - 1) / reduce);
        height = Math.Max(1, (height + reduce - 1) / reduce);
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Mask, 0);
        RenderTarget? target = shader == null ? null : _pool.Rent(width, height, IntermediateFormat);
        if (target is null)
        {
            stats.EffectsSkipped += masks.Count;
            return null;
        }

        float pixel = imageWidth / width;
        float shortSide = Math.Min(imageWidth, imageHeight);
        ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2];
        float* clear = stackalloc float[4];
        foreach (LayerEffect mask in masks)
        {
            EffectDefinition definition = EffectRegistry.Find(mask.Code)!;
            EffectValues values = mask.Values;
            var shape = (MaskShape)Math.Clamp(float.IsNaN(values[MaskGeometry.ShapeSlot]) ? 0 : (int)values[MaskGeometry.ShapeSlot], 0,
                EffectRegistry.MaskShapes.Length - 1);
            var mode = (MaskMode)Math.Clamp(float.IsNaN(values[MaskGeometry.ModeSlot]) ? 2 : (int)values[MaskGeometry.ModeSlot], 0, 3);
            float halfWidth = Value(values, definition, 2) * imageWidth / 2;
            float halfHeight = Value(values, definition, 3) * imageHeight / 2;
            float feather = Value(values, definition, 5) * shortSide;
            float halfThickness = Value(values, definition, 9) * shortSide / 2;
            int points = 0;
            Float4 box = default;
            float margin = 0;
            float halfSoft = 0;
            ID3D11ShaderResourceView* path = null;
            ID3D11ShaderResourceView* matte = null;
            if (shape is MaskShape.Path or MaskShape.Bezier or MaskShape.Stroke)
            {
                ReadOnlySpan<Vector3> polyline = mask.Resource is { Reference: { } text }
                    ? _maskTextures.Polyline(text, values[MaskGeometry.MatteTimeSlot], shape, 2 * halfWidth, 2 * halfHeight, feather)
                    : [];
                if (polyline.Length < MaskGeometry.MinimumPoints(shape))
                {
                    stats.EffectsSkipped++;
                    continue;
                }

                points = polyline.Length;
                float maxWeight = 0;
                Vector2 min = new(float.MaxValue), max = new(float.MinValue);
                foreach (Vector3 p in polyline)
                {
                    var q = new Vector2(p.X * 2 * halfWidth, p.Y * 2 * halfHeight);
                    min = Vector2.Min(min, q);
                    max = Vector2.Max(max, q);
                    maxWeight = Math.Max(maxWeight, p.Z);
                }

                box = new Float4(min.X, min.Y, max.X, max.Y);
                float expansion = Value(values, definition, 7) * shortSide;
                float softest = Math.Max(feather * (shape == MaskShape.Bezier ? maxWeight : 1), pixel);
                halfSoft = 0.5f * softest;
                margin = Math.Max(0, expansion) + (0.5f * softest) + (shape == MaskShape.Stroke ? halfThickness * maxWeight : 0) + 1;
                path = _maskTextures.UploadPolyline(polyline);
            }
            else if (shape == MaskShape.Image)
            {
                if (mask.Resource is not { } key || Resources is not { } resources
                    || !resources.TryGetMatte(key, Math.Max(0, values[MaskGeometry.MatteTimeSlot]), out MatteFrame? frame)
                    || frame is null || frame.Width is < 1 or > 8192 || frame.Height is < 1 or > 8192
                    || frame.Gray.Length < (long)frame.Width * frame.Height)
                {
                    stats.MissingContent++;
                    continue;
                }

                matte = _maskTextures.UploadMatte(frame);
            }

            if (applied == 0)
            {
                float start = (float)MaskGeometry.StartValue(mode);
                clear[0] = clear[1] = clear[2] = clear[3] = start;
                _context->ClearRenderTargetView(target.Rtv, clear);
            }

            float radians = Value(values, definition, 4) * MathF.PI / 180;
            EffectConstants constants = default;
            constants.Mix0 = new((float)shape, values[MaskGeometry.InvertSlot] >= 0.5f ? 1 : 0, Value(values, definition, 6), feather);
            constants.Mix1 = new(Value(values, definition, 0) * imageWidth, Value(values, definition, 1) * imageHeight, halfWidth, halfHeight);
            constants.Kernel = new(MathF.Cos(radians), MathF.Sin(radians), points, Value(values, definition, 7) * shortSide);
            constants.Canvas = new(imageWidth, imageHeight, 0, 0);
            constants.Key0 = new(Value(values, definition, 8) * Math.Min(halfWidth, halfHeight), halfThickness, pixel, margin);
            constants.Key1 = box;
            inputs[0] = path;
            inputs[1] = matte;
            RenderTarget? coarse = points > CoarseMinPoints && width * height > CoarseMinPixels
                ? Coarse(shader, constants, inputs, width, height, imageWidth, imageHeight, halfSoft, out constants)
                : null;
            _quad.Bind(target.Rtv, width, height, shader, _effects.MaskBlend(mode));
            _context->PSSetShaderResources(8, 2, inputs);
            ID3D11ShaderResourceView* coarseView = coarse is null ? null : coarse.Srv;
            _context->PSSetShaderResources(11, 1, &coarseView);
            _effects.Set(constants);
            _quad.Draw(QuadConstants.FullTarget());
            inputs[0] = null;
            inputs[1] = null;
            _context->PSSetShaderResources(8, 2, inputs);
            coarseView = null;
            _context->PSSetShaderResources(11, 1, &coarseView);
            _pool.Return(coarse);
            applied++;
        }

        if (applied == 0)
        {
            _pool.Return(target);
            return null;
        }

        return target;
    }

    /// <summary>
    /// El borde suave más angosto del grupo, en píxeles de la imagen (0 si alguna tiene borde duro o es de imagen): decide
    /// cuánto se puede achicar la textura de máscaras.
    /// </summary>
    private float SoftestEdge(List<LayerEffect> masks, float imageWidth, float imageHeight)
    {
        float shortSide = Math.Min(imageWidth, imageHeight);
        float softest = float.MaxValue;
        foreach (LayerEffect mask in masks)
        {
            EffectDefinition definition = EffectRegistry.Find(mask.Code)!;
            EffectValues values = mask.Values;
            var shape = (MaskShape)Math.Clamp(float.IsNaN(values[MaskGeometry.ShapeSlot]) ? 0 : (int)values[MaskGeometry.ShapeSlot], 0,
                EffectRegistry.MaskShapes.Length - 1);
            float feather = Value(values, definition, 5) * shortSide;
            if (shape == MaskShape.Image || feather <= 0)
            {
                return 0;
            }

            if (shape == MaskShape.Bezier && mask.Resource is { Reference: { } text })
            {
                ReadOnlySpan<Vector3> polyline = _maskTextures.Polyline(text, values[MaskGeometry.MatteTimeSlot], shape,
                    Value(values, definition, 2) * imageWidth, Value(values, definition, 3) * imageHeight, feather);
                foreach (Vector3 p in polyline)
                {
                    softest = Math.Min(softest, feather * p.Z);
                }
            }
            else
            {
                softest = Math.Min(softest, feather);
            }
        }

        return softest == float.MaxValue ? 0 : softest;
    }

    /// <summary>Con más puntos que estos en la polilínea, primero se mide la distancia en una textura gruesa.</summary>
    private const int CoarseMinPoints = 24;

    private const int CoarseMinPixels = 256 * 256;

    /// <summary>Píxeles de la máscara por celda de la pasada gruesa.</summary>
    private const int CoarseCell = 8;

    /// <summary>
    /// Pasada gruesa de una máscara de vértices: la distancia al contorno en el centro de cada celda de
    /// <see cref="CoarseCell"/> píxeles. Como la distancia cambia a lo sumo lo que se mueve el punto, la pasada fina decide
    /// sin recorrer la polilínea toda celda que esté a más de media diagonal y medio borde del contorno; solo las celdas
    /// del contorno hacen la cuenta entera, con el mismo resultado. Devuelve la textura y las constantes de la pasada fina.
    /// </summary>
    private RenderTarget? Coarse(ID3D11PixelShader* shader, EffectConstants constants, ID3D11ShaderResourceView** inputs, int width,
        int height, float imageWidth, float imageHeight, float halfSoft, out EffectConstants fine)
    {
        fine = constants;
        int cw = (width + CoarseCell - 1) / CoarseCell;
        int ch = (height + CoarseCell - 1) / CoarseCell;
        RenderTarget? coarse = _pool.Rent(cw, ch, IntermediateFormat);
        if (coarse is null)
        {
            return null;
        }

        EffectConstants pass = constants;
        pass.Canvas.Z = 1;
        _quad.Bind(coarse.Rtv, cw, ch, shader, null);
        _context->PSSetShaderResources(8, 2, inputs);
        _effects.Set(pass);
        _quad.Draw(QuadConstants.FullTarget());

        // La distancia gruesa ya lleva la expansión y el grosor: se suma medio borde (el mayor), media diagonal de la celda
        // y un margen por la media precisión de la textura.
        float cellX = imageWidth / cw, cellY = imageHeight / ch;
        fine.Canvas.Z = 2;
        fine.Canvas.W = (0.5f * MathF.Sqrt((cellX * cellX) + (cellY * cellY))) + halfSoft + 2;
        fine.Tones = new(cw, ch, 0, 0);
        return coarse;
    }

    /// <summary>
    /// Lo de <paramref name="after"/> solo donde lo deja ver la textura de máscaras <paramref name="mask"/> y lo de
    /// <paramref name="before"/> en el resto, en un intermedio nuevo del tamaño de <paramref name="before"/>.
    /// </summary>
    private ImageView? MixMasked(ImageView before, ImageView after, ImageView mask, MaskViewMode view)
    {
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.MaskMix, 0);
        RenderTarget? output = shader == null ? null : _pool.Rent(before.Width, before.Height, IntermediateFormat);
        if (output is null)
        {
            return null;
        }

        var result = new ImageView(output, before.Width, before.Height);
        EffectConstants constants = default;
        constants.MapA = before.Map;
        constants.MapB = after.Map;
        constants.Mix0 = new((float)view, 0, 0, 0);
        constants.Mix1 = mask.Map;
        _quad.Bind(output.Rtv, output.Width, output.Height, shader, null);
        BindInputs(null, null, before.Target, after.Target, mask.Target);
        _effects.Set(constants);
        _quad.Draw(QuadConstants.FullTarget());
        UnbindInputs();
        return result;
    }

    /// <summary>Qué grupo de esta capa muestra la vista de máscaras, según el ordinal de la máscara elegida.</summary>
    private void ResolveMaskView(LayerFrame layer)
    {
        _maskView = null;
        if (MaskView is not { Mode: not MaskViewMode.None } view || layer.ClipId != view.ClipId)
        {
            return;
        }

        int ordinal = 0;
        foreach (LayerEffect effect in layer.Effects)
        {
            if (effect.Code != MaskCode)
            {
                continue;
            }

            if (ordinal++ == view.MaskOrdinal)
            {
                _maskView = (MaskTarget(effect.Values), view.Mode);
                return;
            }
        }
    }

    private static readonly int MaskCode = EffectRegistry.Find(Rodaje.Core.Model.BuiltInEffects.Mask)!.Code;
}
