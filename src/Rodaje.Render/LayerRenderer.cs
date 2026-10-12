using System.Numerics;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>Dónde se dibuja: la composición o un lienzo de transición, con su relación con la secuencia.</summary>
internal readonly unsafe struct Destination(RenderTarget target, int width, int height, int sequenceWidth, int sequenceHeight)
{
    public RenderTarget Target { get; } = target;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int SequenceWidth { get; } = sequenceWidth;

    public int SequenceHeight { get; } = sequenceHeight;

    /// <summary>Píxeles del destino por píxel de la secuencia.</summary>
    public float PixelScale => Math.Min((float)Width / SequenceWidth, (float)Height / SequenceHeight);
}

/// <summary>Contadores de un cuadro para <see cref="CompositeResult"/>.</summary>
internal struct FrameStats
{
    public int LayersSkipped;
    public int EffectsApplied;
    public int EffectsSkipped;
    public int MissingContent;
    public int TitlesPending;
}

/// <summary>
/// Dibuja una capa. Sin efectos (<see cref="LayerFrame.IsPlain"/>) es el camino rápido de la fase 3: un cuadrilátero con
/// <c>PsLayer</c> sobre el destino ya enlazado. Con efectos, color, LUT y viñeta van fusionados en el mismo shader que
/// convierte el color; desenfoque y nitidez, en intermedios del tamaño en pantalla; la mezcla, por estado fijo o por
/// shader con una copia del fondo; y una transición, en dos lienzos que mezcla un shader.
/// </summary>
/// <remarks>Lo que no se puede aplicar en este cuadro (shader sin compilar, pool lleno) se salta y se cuenta; nunca se espera.</remarks>
internal sealed unsafe partial class LayerRenderer
{
    private const DXGI_FORMAT IntermediateFormat = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;

    private readonly GpuDevice _gpu;
    private readonly ID3D11DeviceContext* _context;
    private readonly QuadPipeline _quad;
    private readonly EffectPipeline _effects;
    private readonly PlaneViewCache _views;
    private readonly RenderTargetPool _pool;
    private readonly LutTextures _luts;
    private readonly List<SpatialStep> _spatial = [];

    public LayerRenderer(GpuDevice gpu, QuadPipeline quad, EffectPipeline effects, PlaneViewCache views, RenderTargetPool pool, LutTextures luts)
    {
        _gpu = gpu;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        _quad = quad;
        _effects = effects;
        _views = views;
        _pool = pool;
        _luts = luts;
    }

    public IEffectResources? Resources { get; set; }

    /// <summary>La textura de la capa si se puede dibujar; <c>null</c> si hay que saltarla.</summary>
    /// <exception cref="ArgumentException">La textura es de otro dispositivo.</exception>
    public D3D11TexturePayload? Usable(LayerFrame layer)
    {
        if (layer.Frame.Payload is not D3D11TexturePayload payload || payload.IsReleased
            || payload.PixelFormat is not (FramePixelFormat.Nv12 or FramePixelFormat.P010))
        {
            return null;
        }

        if (payload.Device != _gpu)
        {
            throw new ArgumentException($"La capa de la pista {layer.TrackIndex} ({layer.ClipId}) es una textura de otro dispositivo.");
        }

        return payload;
    }

    /// <summary>El camino rápido: dibuja con el pipeline ya enlazado (<c>PsLayer</c> y "over").</summary>
    public bool DrawPlain(LayerFrame layer, D3D11TexturePayload payload, SourceGeometry geometry, int sequenceWidth, int sequenceHeight, float pixelScale)
    {
        if (Place(layer, payload, geometry, sequenceWidth, sequenceHeight) is not { } placement)
        {
            return false;
        }

        QuadConstants constants = LayerQuad(payload, placement, geometry, Opacity(layer), sequenceWidth, sequenceHeight, pixelScale);
        PlaneViews views = _views.Get(payload, out bool owned);
        try
        {
            ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[2] { views.Luma, views.Chroma };
            _context->PSSetShaderResources(0, 2, srvs);
            _quad.Draw(constants);
            if (owned)
            {
                srvs[0] = null;
                srvs[1] = null;
                _context->PSSetShaderResources(0, 2, srvs);
            }
        }
        finally
        {
            if (owned)
            {
                views.Release();
            }
        }

        return true;
    }

    /// <summary>Dibuja una capa con efectos, mezcla o transición. Deja el pipeline enlazado a otra cosa: quien llama lo vuelve a enlazar.</summary>
    public bool Draw(LayerFrame layer, D3D11TexturePayload payload, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination, ref FrameStats stats)
    {
        _effects.BindConstants();
        return layer.Transition is { } transition
            ? DrawTransition(layer, payload, transition, geometry, destination, ref stats)
            : DrawSingle(layer, payload, geometry?.Invoke(layer) ?? SourceGeometry.Square, destination, layer.Blend, ref stats);
    }

    private static float Opacity(LayerFrame layer) => (float)Math.Min(layer.Transform.Opacity, 1);

    private static LayerPlacement? Place(LayerFrame layer, D3D11TexturePayload payload, SourceGeometry geometry, int sequenceWidth, int sequenceHeight)
    {
        if (!(layer.Transform.Opacity > 0))
        {
            return null;
        }

        (int displayWidth, int displayHeight) = layer.DisplayWidth > 0 && layer.DisplayHeight > 0
            ? (layer.DisplayWidth, layer.DisplayHeight)
            : geometry.DisplaySize(payload.Width, payload.Height);
        return LayerLayout.Place(sequenceWidth, sequenceHeight, displayWidth, displayHeight, layer.Transform);
    }

    private bool DrawSingle(LayerFrame layer, D3D11TexturePayload payload, SourceGeometry geometry, in Destination destination,
        BlendMode blend, ref FrameStats stats)
    {
        if (Place(layer, payload, geometry, destination.SequenceWidth, destination.SequenceHeight) is not { } placement)
        {
            return false;
        }

        EffectConstants effects = default;
        LayerPlan plan = BuildPlan(layer, ref effects, ref stats);
        int blendCount = blend == BlendMode.Normal ? 0 : 1;
        if (_spatial.Count > 0)
        {
            if (DrawThroughIntermediate(payload, placement, geometry, Opacity(layer), plan, effects, destination, blend, ref stats,
                out bool blendDropped))
            {
                stats.EffectsApplied += plan.PerPixelCount + (blendDropped ? 0 : blendCount);
                stats.EffectsSkipped += blendDropped ? blendCount : 0;
                return true;
            }

            stats.EffectsSkipped += _spatial.Count;
        }

        bool shaderBlend = EffectPipeline.IsShaderBlend(blend);
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Graded, plan.Flags | (shaderBlend ? EffectShaders.Blend : 0));
        if (shader == null)
        {
            // Variante todavía sin compilar: la capa sale sin efectos y con mezcla normal.
            _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, QuadPipeline.Shader.Layer);
            stats.EffectsSkipped += plan.PerPixelCount + blendCount;
            return DrawPlain(layer, payload, geometry, destination.SequenceWidth, destination.SequenceHeight, destination.PixelScale);
        }

        QuadConstants quad = LayerQuad(payload, placement, geometry, Opacity(layer), destination.SequenceWidth, destination.SequenceHeight,
            destination.PixelScale);
        RenderTarget? background = null;
        if (shaderBlend)
        {
            background = CopyBackground(destination, Bounds(quad, destination), ref effects);
            if (background is null)
            {
                shader = _effects.TryGetShader(EffectShader.Graded, plan.Flags);
                blend = BlendMode.Normal;
                stats.EffectsSkipped += blendCount;
                blendCount = 0;
                if (shader == null)
                {
                    return false;
                }
            }
        }

        effects.BlendInfo.X = (float)blend;
        _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, shader, _effects.BlendState(blend));
        BindInputs(background, plan.Lut, null, null);
        _effects.Set(effects);
        DrawPlanes(payload, quad);
        UnbindInputs();
        _pool.Return(background);
        stats.EffectsApplied += plan.PerPixelCount + blendCount;
        return true;
    }

    private bool DrawThroughIntermediate(D3D11TexturePayload payload, LayerPlacement placement, SourceGeometry geometry, float opacity,
        in LayerPlan plan, EffectConstants effects, in Destination destination, BlendMode blend, ref FrameStats stats, out bool blendDropped)
    {
        blendDropped = false;
        bool shaderBlend = EffectPipeline.IsShaderBlend(blend);
        ID3D11PixelShader* graded = _effects.TryGetShader(EffectShader.Graded, plan.Flags);
        ID3D11PixelShader* image = _effects.TryGetShader(EffectShader.Image, shaderBlend ? EffectShaders.Blend : 0);
        if (graded == null || image == null)
        {
            return false;
        }

        // Del tamaño en pantalla, sin pasar del lado mayor del destino (una capa muy ampliada pierde algo de detalle).
        float onScreen = placement.Scale * destination.PixelScale;
        float wanted = Math.Max(placement.LocalWidth, placement.LocalHeight) * onScreen;
        float fit = Math.Min(1f, Math.Max(destination.Width, destination.Height) / wanted);
        int width = Math.Max(1, (int)MathF.Ceiling(placement.LocalWidth * onScreen * fit));
        int height = Math.Max(1, (int)MathF.Ceiling(placement.LocalHeight * onScreen * fit));
        RenderTarget? first = _pool.Rent(width, height, IntermediateFormat);
        if (first is null)
        {
            return false;
        }

        float scaleX = width / placement.LocalWidth;
        float scaleY = height / placement.LocalHeight;
        var local = new LayerPlacement(Matrix3x2.CreateScale(scaleX, scaleY), placement.LocalWidth, placement.LocalHeight, Math.Min(scaleX, scaleY));
        QuadConstants toIntermediate = LayerQuad(payload, local, geometry, 1, width, height, 1);
        _quad.Bind(first.Rtv, width, height, graded, null);
        BindInputs(null, plan.Lut, null, null);
        _effects.Set(effects);
        DrawPlanes(payload, toIntermediate);
        UnbindInputs();

        // Píxeles del intermedio por píxel de la secuencia: los radios se escalan igual en la vista previa reducida.
        float perSequencePixel = scaleX / placement.Scale;
        var view = new ImageView(first, width, height);
        foreach (SpatialStep step in _spatial)
        {
            ImageView? next = step.Kind == GpuEffectKind.Blur
                ? Blur(view, step.Sigma * perSequencePixel)
                : Sharpen(view, step.Amount, step.Sigma * perSequencePixel);
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

        QuadConstants quad = PlacedQuad(placement, VideoLayout.Corners(0, 1, 1), opacity, destination.SequenceWidth, destination.SequenceHeight,
            destination.PixelScale);
        RenderTarget? background = null;
        if (shaderBlend)
        {
            background = CopyBackground(destination, Bounds(quad, destination), ref effects);
            if (background is null)
            {
                image = _effects.TryGetShader(EffectShader.Image, 0);
                blend = BlendMode.Normal;
                blendDropped = true;
            }
        }

        if (image == null)
        {
            _pool.Return(view.Target);
            return false;
        }

        effects.BlendInfo.X = (float)blend;
        effects.MapA = view.Map;
        _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, image, _effects.BlendState(blend));
        BindInputs(background, null, view.Target, null);
        _effects.Set(effects);
        _quad.Draw(quad);
        UnbindInputs();
        _pool.Return(background);
        _pool.Return(view.Target);
        return true;
    }

    private void DrawPlanes(D3D11TexturePayload payload, in QuadConstants quad)
    {
        PlaneViews views = _views.Get(payload, out bool owned);
        try
        {
            ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[2] { views.Luma, views.Chroma };
            _context->PSSetShaderResources(0, 2, srvs);
            _quad.Draw(quad);
            srvs[0] = null;
            srvs[1] = null;
            _context->PSSetShaderResources(0, 2, srvs);
        }
        finally
        {
            if (owned)
            {
                views.Release();
            }
        }
    }

    /// <summary>Enlaza t3 (fondo), t4 (LUT), t5 y t6 (intermedios). Va después de enlazar el destino.</summary>
    private void BindInputs(RenderTarget? background, ID3D11ShaderResourceView* lut, RenderTarget? imageA, RenderTarget? imageB)
    {
        ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[4];
        srvs[0] = background is null ? null : background.Srv;
        srvs[1] = lut;
        srvs[2] = imageA is null ? null : imageA.Srv;
        srvs[3] = imageB is null ? null : imageB.Srv;
        _context->PSSetShaderResources(3, 4, srvs);
    }

    private void UnbindInputs()
    {
        ID3D11ShaderResourceView** none = stackalloc ID3D11ShaderResourceView*[4];
        _context->PSSetShaderResources(3, 4, none);
    }

    /// <summary>Copia del destino en <paramref name="rect"/> para las mezclas por shader; <c>null</c> si no cabe en el pool.</summary>
    private RenderTarget? CopyBackground(in Destination destination, PixelRect rect, ref EffectConstants effects)
    {
        if (rect.IsEmpty)
        {
            rect = new PixelRect(0, 0, 1, 1);
        }

        RenderTarget? copy = _pool.Rent(rect.Width, rect.Height, destination.Target.Format);
        if (copy is null)
        {
            return null;
        }

        var box = new D3D11_BOX
        {
            left = (uint)rect.X,
            top = (uint)rect.Y,
            right = (uint)(rect.X + rect.Width),
            bottom = (uint)(rect.Y + rect.Height),
            front = 0,
            back = 1,
        };
        _context->CopySubresourceRegion((ID3D11Resource*)copy.Texture, 0, 0, 0, 0, (ID3D11Resource*)destination.Target.Texture, 0, &box);
        effects.BlendInfo.Y = rect.X;
        effects.BlendInfo.Z = rect.Y;
        return copy;
    }

    /// <summary>Rectángulo en píxeles del destino que cubre el cuadrilátero (con su margen de suavizado).</summary>
    private static PixelRect Bounds(in QuadConstants quad, in Destination destination)
    {
        float minX = Math.Min(Math.Min(quad.TopLeft.X, quad.TopRight.X), Math.Min(quad.BottomLeft.X, quad.BottomRight.X));
        float maxX = Math.Max(Math.Max(quad.TopLeft.X, quad.TopRight.X), Math.Max(quad.BottomLeft.X, quad.BottomRight.X));
        float minY = Math.Min(Math.Min(quad.TopLeft.Y, quad.TopRight.Y), Math.Min(quad.BottomLeft.Y, quad.BottomRight.Y));
        float maxY = Math.Max(Math.Max(quad.TopLeft.Y, quad.TopRight.Y), Math.Max(quad.BottomLeft.Y, quad.BottomRight.Y));
        int left = Math.Clamp((int)MathF.Floor((minX + 1) * 0.5f * destination.Width), 0, destination.Width);
        int right = Math.Clamp((int)MathF.Ceiling((maxX + 1) * 0.5f * destination.Width), 0, destination.Width);
        int top = Math.Clamp((int)MathF.Floor((1 - maxY) * 0.5f * destination.Height), 0, destination.Height);
        int bottom = Math.Clamp((int)MathF.Ceiling((1 - minY) * 0.5f * destination.Height), 0, destination.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Constantes de una capa YUV: colocación, rango, matriz y recorte del relleno del decodificador.</summary>
    internal static QuadConstants LayerQuad(D3D11TexturePayload payload, in LayerPlacement placement, SourceGeometry geometry,
        float opacity, float sequenceWidth, float sequenceHeight, float pixelScale)
    {
        bool tenBit = payload.PixelFormat == FramePixelFormat.P010;
        YuvRange range = YuvRange.For(tenBit, payload.Color.FullRange);
        YuvToRgb toRgb = YuvToRgb.For(payload.Color.Matrix);
        float uMax = (float)payload.Width / payload.CodedWidth;
        float vMax = (float)payload.Height / payload.CodedHeight;
        QuadConstants constants = PlacedQuad(placement, VideoLayout.Corners(geometry.Rotation, uMax, vMax), opacity,
            sequenceWidth, sequenceHeight, pixelScale);
        constants.Range = new(range.LumaScale, range.LumaBias, range.ChromaScale, range.ChromaBias);
        constants.ToRgb = new(toRgb.RedFromCr, toRgb.GreenFromCb, toRgb.GreenFromCr, toRgb.BlueFromCb);
        constants.Limits = new(
            (payload.Width - 0.5f) / payload.CodedWidth,
            (payload.Height - 0.5f) / payload.CodedHeight,
            (payload.Width - 1f) / payload.CodedWidth,
            (payload.Height - 1f) / payload.CodedHeight);
        return constants;
    }

    /// <summary>Esquinas, coordenadas locales y forma de un rectángulo colocado, con un píxel de margen para suavizar el borde.</summary>
    internal static QuadConstants PlacedQuad(in LayerPlacement placement, in QuadTexCoords uv, float opacity,
        float sequenceWidth, float sequenceHeight, float pixelScale)
    {
        float screenScale = placement.Scale * pixelScale;
        // Un píxel de destino más allá del borde, para que la cobertura lo suavice también hacia afuera.
        float margin = 1f / screenScale;
        float w = placement.LocalWidth;
        float h = placement.LocalHeight;
        var constants = new QuadConstants { Shape = new(opacity, screenScale, w, h) };
        constants.TopLeft = Corner(placement, uv, -margin, -margin, sequenceWidth, sequenceHeight, out constants.LocalTopLeft);
        constants.TopRight = Corner(placement, uv, w + margin, -margin, sequenceWidth, sequenceHeight, out constants.LocalTopRight);
        constants.BottomLeft = Corner(placement, uv, -margin, h + margin, sequenceWidth, sequenceHeight, out constants.LocalBottomLeft);
        constants.BottomRight = Corner(placement, uv, w + margin, h + margin, sequenceWidth, sequenceHeight, out constants.LocalBottomRight);
        return constants;
    }

    private static Float4 Corner(in LayerPlacement placement, in QuadTexCoords uv, float x, float y, float sequenceWidth, float sequenceHeight,
        out Float4 local)
    {
        Vector2 ndc = LayerLayout.ToNdc(placement.Map(x, y), sequenceWidth, sequenceHeight);
        TexCoord t = LayerLayout.TexCoordAt(uv, x, y, placement.LocalWidth, placement.LocalHeight);
        local = new Float4(x, y, 0, 0);
        return new Float4(ndc.X, ndc.Y, t.U, t.V);
    }
}
