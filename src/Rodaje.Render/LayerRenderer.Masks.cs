using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

internal sealed unsafe partial class LayerRenderer
{
    /// <summary>Lado mayor de la textura de máscaras: con un borde suave no hace falta más.</summary>
    internal const int MaxMaskSide = 2048;

    private readonly List<LayerEffect> _masks = [];

    /// <summary>
    /// Dibuja las máscaras de la capa en una textura del tamaño en pantalla (empieza en 1 y cada una la multiplica) y la deja
    /// lista para <c>PsGraded</c> con <see cref="EffectShaders.Mask"/>. Devuelve la textura, que quien llama devuelve al
    /// pool, o <c>null</c> si no hay máscaras que aplicar (sin shader o sin memoria se saltan y se cuentan).
    /// </summary>
    private RenderTarget? PrepareMasks(in LayerPlacement placement, in Destination destination, ref LayerPlan plan,
        ref EffectConstants effects, ref FrameStats stats)
    {
        if (DrawMasks(_masks, placement, destination, ref stats, out int applied) is not { } target)
        {
            return null;
        }

        plan = new LayerPlan(plan.Flags | EffectShaders.Mask, plan.Lut, plan.PerPixelCount + applied, plan.Curves);
        effects.MapB = new ImageView(target, MaskWidth, MaskHeight).Map;
        return target;
    }

    /// <summary>
    /// Las máscaras que limitan efectos, en una textura como la de las de recorte (en el espacio de la capa) para
    /// <c>RegionMix</c>. <c>null</c> si no hay ninguna: los efectos con zona se aplican entonces en toda la imagen.
    /// </summary>
    private RenderTarget? PrepareRegionMask(in LayerPlacement placement, in Destination destination, ref FrameStats stats)
    {
        RenderTarget? target = DrawMasks(_regionMasks, placement, destination, ref stats, out int applied);
        if (target is not null)
        {
            _regionMap = new ImageView(target, MaskWidth, MaskHeight).Map;
            stats.EffectsApplied += applied;
        }

        return target;
    }

    private int MaskWidth { get; set; }

    private int MaskHeight { get; set; }

    private RenderTarget? DrawMasks(List<LayerEffect> masks, in LayerPlacement placement, in Destination destination, ref FrameStats stats,
        out int applied)
    {
        applied = 0;
        if (masks.Count == 0)
        {
            return null;
        }

        float imageWidth = placement.LocalWidth;
        float imageHeight = placement.LocalHeight;
        float onScreen = placement.Scale * destination.PixelScale;
        float wanted = Math.Max(imageWidth, imageHeight) * onScreen;
        float fit = Math.Min(1f, Math.Min(Math.Max(destination.Width, destination.Height), MaxMaskSide) / Math.Max(wanted, 1e-3f));
        int width = Math.Clamp((int)MathF.Ceiling(imageWidth * onScreen * fit), 1, MaxMaskSide);
        int height = Math.Clamp((int)MathF.Ceiling(imageHeight * onScreen * fit), 1, MaxMaskSide);
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Mask, 0);
        RenderTarget? target = shader == null ? null : _pool.Rent(width, height, IntermediateFormat);
        if (target is null)
        {
            stats.EffectsSkipped += masks.Count;
            return null;
        }

        float* one = stackalloc float[4] { 1, 1, 1, 1 };
        _context->ClearRenderTargetView(target.Rtv, one);
        float pixel = imageWidth / width;
        MaskWidth = width;
        MaskHeight = height;
        ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2];
        foreach (LayerEffect mask in masks)
        {
            EffectDefinition definition = EffectRegistry.Find(mask.Code)!;
            EffectValues values = mask.Values;
            var shape = (MaskShape)Math.Clamp(float.IsNaN(values[7]) ? 0 : (int)values[7], 0, 3);
            int points = 0;
            ID3D11ShaderResourceView* path = null;
            ID3D11ShaderResourceView* matte = null;
            if (shape == MaskShape.Path)
            {
                Vector2[] stroke = mask.Resource is { Reference: { } text } ? _maskTextures.Path(text) : [];
                if (stroke.Length < 3)
                {
                    stats.EffectsSkipped++;
                    continue;
                }

                points = stroke.Length;
                path = _maskTextures.UploadPath(stroke);
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

            float radians = Value(values, definition, 4) * MathF.PI / 180;
            EffectConstants constants = default;
            constants.Mix0 = new((float)shape, values[8] >= 0.5f ? 1 : 0, Value(values, definition, 6),
                Math.Max(Value(values, definition, 5) * Math.Min(imageWidth, imageHeight), pixel));
            constants.Mix1 = new(Value(values, definition, 0) * imageWidth, Value(values, definition, 1) * imageHeight,
                Value(values, definition, 2) * imageWidth / 2, Value(values, definition, 3) * imageHeight / 2);
            constants.Kernel = new(MathF.Cos(radians), MathF.Sin(radians), points, 0);
            constants.Canvas = new(imageWidth, imageHeight, 0, 0);
            _quad.Bind(target.Rtv, width, height, shader, _effects.MaskBlend);
            inputs[0] = path;
            inputs[1] = matte;
            _context->PSSetShaderResources(8, 2, inputs);
            _effects.Set(constants);
            _quad.Draw(QuadConstants.FullTarget());
            inputs[0] = null;
            inputs[1] = null;
            _context->PSSetShaderResources(8, 2, inputs);
            applied++;
        }

        if (applied == 0)
        {
            _pool.Return(target);
            return null;
        }

        return target;
    }
}
