using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using Rodaje.Render.Text;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

internal sealed unsafe partial class LayerRenderer
{
    /// <summary>
    /// Dibuja un título ya rasterizado: a su tamaño nativo y centrado, con la transformación, la opacidad y la mezcla de la
    /// capa por <c>PsImage</c>. Desenfoque y nitidez pasan por un intermedio con margen del radio; color, LUT y viñeta no
    /// se aplican a los títulos en la v1 y se cuentan como saltados.
    /// </summary>
    private bool DrawTitleImage(LayerFrame layer, TitleImage title, in Destination destination, ref FrameStats stats)
    {
        _effects.BindConstants();
        LayerTransform transform = TransformOf(layer);
        if (!(transform.Opacity > 0)
            || LayerLayout.PlaceNative(destination.SequenceWidth, destination.SequenceHeight, title.LocalWidth, title.LocalHeight, transform)
                is not { } placement)
        {
            return false;
        }

        _spatial.Clear();
        IReadOnlyList<LayerEffect> list = layer.Effects;
        for (int i = 0; i < list.Count; i++)
        {
            GpuEffect? gpu = GpuEffectRegistry.Find(list[i].Code);
            EffectDefinition? definition = EffectRegistry.Find(list[i].Code);
            switch (gpu?.Kind)
            {
                case GpuEffectKind.Transform or GpuEffectKind.Stabilize or GpuEffectKind.Blend:
                    break;
                case GpuEffectKind.Blur or GpuEffectKind.Sharpen or GpuEffectKind.Grain or GpuEffectKind.Glow
                    or GpuEffectKind.ChromaticAberration or GpuEffectKind.Mirror or GpuEffectKind.LensDistortion or GpuEffectKind.Pixelate:
                    _spatial.Add(new SpatialStep(gpu.Kind, list[i].Values, definition!, i, 0));
                    break;
                default:
                    stats.EffectsSkipped++;
                    break;
            }
        }

        var view = new ImageView(title.Target, title.Width, title.Height);
        if (_spatial.Count > 0)
        {
            if (TitleThroughIntermediate(layer, title, placement, transform, destination, ref stats) is { } spatial)
            {
                (view, placement) = spatial;
            }
            else
            {
                stats.EffectsSkipped += _spatial.Count;
            }
        }

        BlendMode blend = BlendOf(layer);
        bool shaderBlend = EffectPipeline.IsShaderBlend(blend);
        ID3D11PixelShader* image = _effects.TryGetShader(EffectShader.Image, shaderBlend ? EffectShaders.Blend : 0);
        EffectConstants effects = default;
        QuadConstants quad = PlacedQuad(placement, VideoLayout.Corners(0, 1, 1), Opacity(layer), destination.SequenceWidth,
            destination.SequenceHeight, destination.PixelScale);
        RenderTarget? background = null;
        if (shaderBlend && image != null)
        {
            background = CopyBackground(destination, Bounds(quad, destination), ref effects);
            if (background is null)
            {
                image = _effects.TryGetShader(EffectShader.Image, 0);
                blend = BlendMode.Normal;
                stats.EffectsSkipped++;
            }
        }

        if (image == null)
        {
            // Hasta que compilan los shaders de efectos, un título no tiene con qué dibujarse.
            ReturnUnlessSource(view, new ImageView(title.Target, 0, 0));
            stats.LayersSkipped++;
            return false;
        }

        effects.BlendInfo.X = (float)blend;
        effects.MapA = view.Map;
        _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, image, StateFor(blend));
        BindInputs(background, null, view.Target, null);
        _effects.Set(effects);
        _quad.Draw(quad);
        UnbindInputs();
        _pool.Return(background);
        ReturnUnlessSource(view, new ImageView(title.Target, 0, 0));
        stats.EffectsApplied += blend == BlendMode.Normal ? 0 : 1;
        return true;
    }

    /// <summary>
    /// El título en un intermedio del tamaño en pantalla con un margen de tres sigmas, desenfocado o enfocado ahí. Devuelve
    /// la vista y su colocación (el mismo centro, más grande por el margen), o nulo si falta un shader o memoria.
    /// </summary>
    private (ImageView View, LayerPlacement Placement)? TitleThroughIntermediate(LayerFrame layer, TitleImage title, LayerPlacement placement,
        LayerTransform transform, in Destination destination, ref FrameStats stats)
    {
        ID3D11PixelShader* copy = _effects.TryGetShader(EffectShader.Image, 0);
        if (copy == null)
        {
            return null;
        }

        // El margen en píxeles locales (los de la secuencia antes de escalar la capa) para que el desenfoque no se corte.
        float reach = 0;
        foreach (SpatialStep step in _spatial)
        {
            reach += Reach(step);
        }

        float margin = (reach / placement.Scale) + 1;
        float localWidth = (float)title.LocalWidth + (2 * margin);
        float localHeight = (float)title.LocalHeight + (2 * margin);
        float onScreen = placement.Scale * destination.PixelScale;
        float fit = Math.Min(1f, Math.Max(destination.Width, destination.Height) / (Math.Max(localWidth, localHeight) * onScreen));
        float s = onScreen * fit;
        int width = Math.Max(1, (int)MathF.Ceiling(localWidth * s));
        int height = Math.Max(1, (int)MathF.Ceiling(localHeight * s));
        RenderTarget? first = _pool.Rent(width, height, IntermediateFormat);
        if (first is null)
        {
            return null;
        }

        // Lo que mide el intermedio en píxeles locales, con el título justo en el medio.
        localWidth = width / s;
        localHeight = height / s;
        float marginX = (localWidth - (float)title.LocalWidth) / 2;
        float marginY = (localHeight - (float)title.LocalHeight) / 2;
        float* clear = stackalloc float[4];
        _context->ClearRenderTargetView(first.Rtv, clear);
        var inside = new LayerPlacement(Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(marginX * s, marginY * s),
            (float)title.LocalWidth, (float)title.LocalHeight, s);
        EffectConstants constants = default;
        constants.MapA = new ImageView(title.Target, title.Width, title.Height).Map;
        _quad.Bind(first.Rtv, width, height, copy, _effects.BlendState(BlendMode.Normal));
        BindInputs(null, null, title.Target, null);
        _effects.Set(constants);
        _quad.Draw(PlacedQuad(inside, VideoLayout.Corners(0, 1, 1), 1, width, height, 1));
        UnbindInputs();

        float perSequencePixel = s / placement.Scale;
        var view = new ImageView(first, width, height);
        foreach (SpatialStep step in _spatial)
        {
            ImageView? next = ApplySpatial(view, step, perSequencePixel, layer, source: true);
            if (next is not { } result)
            {
                stats.EffectsSkipped++;
                continue;
            }

            if (result.Target != view.Target)
            {
                _pool.Return(view.Target);
            }

            view = result;
            stats.EffectsApplied++;
        }

        // El intermedio cubre el título más el margen y tiene el mismo centro: se coloca como un título más grande.
        LayerPlacement? grown = LayerLayout.PlaceNative(destination.SequenceWidth, destination.SequenceHeight,
            localWidth, localHeight, transform);
        if (grown is not { } enlarged)
        {
            _pool.Return(view.Target);
            return null;
        }

        return (view, enlarged);
    }
}
