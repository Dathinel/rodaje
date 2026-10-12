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

/// <summary>
/// Lo que dibuja una capa: los planos Y'CbCr de un cuadro decodificado o el intermedio RGBA premultiplicado de una capa
/// compuesta, que ocupa el cuadro de la secuencia.
/// </summary>
internal readonly record struct LayerSource(D3D11TexturePayload? Planes, ImageView Image)
{
    public static LayerSource Of(D3D11TexturePayload payload) => new(payload, default);

    public static LayerSource Of(ImageView image) => new(null, image);

    public bool IsImage => Planes is null;
}

/// <summary>Efectos que la vista previa puede dejar fuera: para comparar antes y después y para el cuentagotas del croma.</summary>
[Flags]
public enum PreviewBypass
{
    None = 0,

    /// <summary>Color y LUT, viñeta, ruedas y curvas.</summary>
    Grading = 1,

    /// <summary>El croma.</summary>
    Key = 2,

    /// <summary>El recorte: se ve la imagen entera mientras se ajusta en el visor.</summary>
    Crop = 4,

    /// <summary>El calificador HSL (su cuentagotas mira el color que él recibe).</summary>
    Qualifier = 8,
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
internal sealed unsafe partial class LayerRenderer : IDisposable
{
    internal const DXGI_FORMAT IntermediateFormat = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;

    private readonly GpuDevice _gpu;
    private readonly ID3D11DeviceContext* _context;
    private readonly QuadPipeline _quad;
    private readonly EffectPipeline _effects;
    private readonly PlaneViewCache _views;
    private readonly RenderTargetPool _pool;
    private readonly LutTextures _luts;
    private readonly List<SpatialStep> _spatial = [];
    private readonly MaskTextures _maskTextures;
    private readonly CurveTextures _curves;
    private LayerTransform? _motionTransform;
    private float _motionWeight;

    public LayerRenderer(GpuDevice gpu, QuadPipeline quad, EffectPipeline effects, PlaneViewCache views, RenderTargetPool pool, LutTextures luts)
    {
        _gpu = gpu;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        _quad = quad;
        _effects = effects;
        _views = views;
        _pool = pool;
        _luts = luts;
        _maskTextures = new MaskTextures((ID3D11Device*)gpu.NativeDevice, _context);
        _curves = new CurveTextures((ID3D11Device*)gpu.NativeDevice, _context);
    }

    public void Dispose()
    {
        ReleaseHistory();
        _maskTextures.Dispose();
        _curves.Dispose();
    }

    /// <summary>Solo vista previa: qué efectos se dejan fuera al componer.</summary>
    public PreviewBypass Bypass { get; set; }

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
        geometry = geometry with { Crop = CropOf(layer) };
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

    /// <summary>
    /// Dibuja una capa con efectos, mezcla o transición, o una capa compuesta. Deja el pipeline enlazado a otra cosa: quien
    /// llama lo vuelve a enlazar.
    /// </summary>
    /// <param name="incoming">El origen del clip que entra si la capa está en una transición; nulo si no se puede dibujar.</param>
    public bool Draw(LayerFrame layer, LayerSource source, LayerSource? incoming, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination,
        ref FrameStats stats)
    {
        _effects.BindConstants();
        return layer.Transition is { } transition
            ? DrawTransition(layer, source, transition, incoming, geometry, destination, ref stats)
            : DrawSingle(layer, source, GeometryOf(layer, source, geometry), destination, BlendOf(layer), ref stats);
    }

    /// <summary>Una capa compuesta ya tiene la forma de la secuencia: no lleva relación de aspecto ni giro del origen.</summary>
    private static SourceGeometry GeometryOf(LayerFrame layer, LayerSource source, Func<LayerFrame, SourceGeometry>? geometry) =>
        source.IsImage ? SourceGeometry.Square : geometry?.Invoke(layer) ?? SourceGeometry.Square;

    private Rodaje.Core.Effects.CropRect? CropOf(LayerFrame layer) => (Bypass & PreviewBypass.Crop) != 0 ? null : TransformOf(layer).Crop;

    private float Opacity(LayerFrame layer) => (float)Math.Min(TransformOf(layer).Opacity, 1) * (_motionWeight > 0 ? _motionWeight : 1);

    /// <summary>
    /// Desenfoque de movimiento: mientras dura, cada capa se dibuja con <paramref name="transform"/>, con el peso
    /// <paramref name="weight"/> en la opacidad, mezcla normal y suma en el destino (un intermedio transparente que
    /// acumula el promedio de las muestras).
    /// </summary>
    public void BeginMotionSample(LayerTransform transform, float weight)
    {
        _motionTransform = transform;
        _motionWeight = weight;
    }

    public void EndMotion()
    {
        _motionTransform = null;
        _motionWeight = 0;
    }

    private LayerTransform TransformOf(LayerFrame layer) => _motionTransform ?? layer.Transform;

    private BlendMode BlendOf(LayerFrame layer) => _motionWeight > 0 ? BlendMode.Normal : layer.Blend;

    private ID3D11BlendState* StateFor(BlendMode blend) => _motionWeight > 0 ? _effects.Accumulate : _effects.BlendState(blend);

    private LayerPlacement? Place(LayerFrame layer, D3D11TexturePayload payload, SourceGeometry geometry, int sequenceWidth, int sequenceHeight) =>
        Place(layer, LayerSource.Of(payload), geometry, sequenceWidth, sequenceHeight);

    private LayerPlacement? Place(LayerFrame layer, LayerSource source, SourceGeometry geometry, int sequenceWidth, int sequenceHeight)
    {
        if (!(TransformOf(layer).Opacity > 0))
        {
            return null;
        }

        (int displayWidth, int displayHeight) = source.Planes is not { } payload ? (sequenceWidth, sequenceHeight)
            : layer.DisplayWidth > 0 && layer.DisplayHeight > 0 ? (layer.DisplayWidth, layer.DisplayHeight)
            : geometry.DisplaySize(payload.Width, payload.Height);
        return LayerLayout.Place(sequenceWidth, sequenceHeight, displayWidth, displayHeight, TransformOf(layer) with { Crop = geometry.Crop });
    }

    private bool DrawSingle(LayerFrame layer, LayerSource payload, SourceGeometry geometry, in Destination destination,
        BlendMode blend, ref FrameStats stats)
    {
        geometry = geometry with { Crop = CropOf(layer) };
        if (Place(layer, payload, geometry, destination.SequenceWidth, destination.SequenceHeight) is not { } placement)
        {
            return false;
        }

        EffectConstants effects = default;
        LayerPlan plan = BuildPlan(layer, ref effects, ref stats);
        RenderTarget? mask = PrepareMasks(placement, destination, ref plan, ref effects, ref stats);
        _regionMask = _spatial.Count > 0 ? PrepareRegionMask(placement, destination, ref stats) : null;
        try
        {
            return DrawPlanned(layer, payload, geometry, destination, blend, placement, plan, effects, mask, ref stats);
        }
        finally
        {
            _pool.Return(mask);
            _pool.Return(_regionMask);
            _regionMask = null;
        }
    }

    private bool DrawPlanned(LayerFrame layer, LayerSource payload, SourceGeometry geometry, in Destination destination,
        BlendMode blend, LayerPlacement placement, LayerPlan plan, EffectConstants effects, RenderTarget? mask, ref FrameStats stats)
    {
        int blendCount = blend == BlendMode.Normal ? 0 : 1;
        if (_spatial.Count > 0)
        {
            if (DrawThroughIntermediate(layer, payload, placement, geometry, Opacity(layer), plan, effects, mask, destination, blend, ref stats,
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
            if (payload.Planes is not { } planes)
            {
                stats.LayersSkipped++;
                return false;
            }

            return DrawPlain(layer, planes, geometry, destination.SequenceWidth, destination.SequenceHeight, destination.PixelScale);
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
        PrepareSource(payload, ref effects);
        _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, shader, StateFor(blend));
        BindInputs(background, plan.Lut, null, null, mask, plan.Curves);
        _effects.Set(effects);
        DrawPlanes(payload, quad);
        UnbindInputs();
        _pool.Return(background);
        stats.EffectsApplied += plan.PerPixelCount + blendCount;
        return true;
    }

    private bool DrawThroughIntermediate(LayerFrame layer, LayerSource payload, LayerPlacement placement, SourceGeometry geometry, float opacity,
        in LayerPlan plan, EffectConstants effects, RenderTarget? mask, in Destination destination, BlendMode blend, ref FrameStats stats,
        out bool blendDropped)
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
        float onScreen = placement.MaxAxisScale * destination.PixelScale;
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
        PrepareSource(payload, ref effects);
        _quad.Bind(first.Rtv, width, height, graded, null);
        BindInputs(null, plan.Lut, null, null, mask, plan.Curves);
        _effects.Set(effects);
        DrawPlanes(payload, toIntermediate);
        UnbindInputs();

        // Píxeles del intermedio por píxel de la secuencia: los radios se escalan igual en la vista previa reducida.
        float perSequencePixel = scaleX / placement.Scale;
        var view = new ImageView(first, width, height);
        foreach (SpatialStep step in _spatial)
        {
            ImageView? next = ApplyStep(view, step, perSequencePixel, layer, payload.IsImage);
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
        _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, image, StateFor(blend));
        BindInputs(background, null, view.Target, null);
        _effects.Set(effects);
        _quad.Draw(quad);
        UnbindInputs();
        _pool.Return(background);
        _pool.Return(view.Target);
        return true;
    }

    /// <summary>
    /// Dibuja un intermedio premultiplicado del tamaño del destino (el promedio de las muestras del desenfoque de
    /// movimiento) sobre el destino, con la mezcla de la capa.
    /// </summary>
    public bool DrawImage(ImageView view, BlendMode blend, in Destination destination, ref FrameStats stats)
    {
        _effects.BindConstants();
        bool shaderBlend = EffectPipeline.IsShaderBlend(blend);
        ID3D11PixelShader* image = _effects.TryGetShader(EffectShader.Image, shaderBlend ? EffectShaders.Blend : 0);
        if (image == null)
        {
            return false;
        }

        var placement = new LayerPlacement(Matrix3x2.Identity, destination.SequenceWidth, destination.SequenceHeight, 1);
        QuadConstants quad = PlacedQuad(placement, VideoLayout.Corners(0, 1, 1), 1, destination.SequenceWidth, destination.SequenceHeight,
            destination.PixelScale);
        EffectConstants effects = default;
        RenderTarget? background = null;
        if (shaderBlend)
        {
            background = CopyBackground(destination, Bounds(quad, destination), ref effects);
            if (background is null)
            {
                image = _effects.TryGetShader(EffectShader.Image, 0);
                blend = BlendMode.Normal;
                stats.EffectsSkipped++;
                if (image == null)
                {
                    return false;
                }
            }
        }

        effects.BlendInfo.X = (float)blend;
        effects.MapA = view.Map;
        _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, image, _effects.BlendState(blend));
        BindInputs(background, null, view.Target, null);
        _effects.Set(effects);
        _quad.Draw(quad);
        UnbindInputs();
        _pool.Return(background);
        return true;
    }

    /// <summary>Una capa compuesta se lee de t5 con su mapa (<c>extra.w</c> en <c>PsGraded</c>).</summary>
    private static void PrepareSource(LayerSource source, ref EffectConstants effects)
    {
        if (source.IsImage)
        {
            effects.Extra.W = 1;
            effects.MapA = source.Image.Map;
        }
    }

    private void DrawPlanes(LayerSource source, in QuadConstants quad)
    {
        if (source.Planes is not { } payload)
        {
            ID3D11ShaderResourceView* srv = source.Image.Target.Srv;
            _context->PSSetShaderResources(5, 1, &srv);
            _quad.Draw(quad);
            srv = null;
            _context->PSSetShaderResources(5, 1, &srv);
            return;
        }

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

    /// <summary>Enlaza t3 (fondo), t4 (LUT), t5 y t6 (intermedios), t7 (máscaras) y t10 (curvas). Va después de enlazar el destino.</summary>
    private void BindInputs(RenderTarget? background, ID3D11ShaderResourceView* lut, RenderTarget? imageA, RenderTarget? imageB,
        RenderTarget? mask = null, ID3D11ShaderResourceView* curves = null)
    {
        ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[8];
        srvs[0] = background is null ? null : background.Srv;
        srvs[1] = lut;
        srvs[2] = imageA is null ? null : imageA.Srv;
        srvs[3] = imageB is null ? null : imageB.Srv;
        srvs[4] = mask is null ? null : mask.Srv;
        srvs[7] = curves;
        _context->PSSetShaderResources(3, 8, srvs);
    }

    private void UnbindInputs()
    {
        ID3D11ShaderResourceView** none = stackalloc ID3D11ShaderResourceView*[8];
        _context->PSSetShaderResources(3, 8, none);
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

    private static QuadConstants LayerQuad(LayerSource source, in LayerPlacement placement, SourceGeometry geometry,
        float opacity, float sequenceWidth, float sequenceHeight, float pixelScale) =>
        source.Planes is { } payload
            ? LayerQuad(payload, placement, geometry, opacity, sequenceWidth, sequenceHeight, pixelScale)
            : PlacedQuad(placement, VideoLayout.Crop(VideoLayout.Corners(0, 1, 1), geometry.Crop), opacity, sequenceWidth, sequenceHeight, pixelScale);

    /// <summary>Constantes de una capa YUV: colocación, rango, matriz y recorte del relleno del decodificador.</summary>
    internal static QuadConstants LayerQuad(D3D11TexturePayload payload, in LayerPlacement placement, SourceGeometry geometry,
        float opacity, float sequenceWidth, float sequenceHeight, float pixelScale)
    {
        bool tenBit = payload.PixelFormat == FramePixelFormat.P010;
        YuvRange range = YuvRange.For(tenBit, payload.Color.FullRange);
        YuvToRgb toRgb = YuvToRgb.For(payload.Color.Matrix);
        float uMax = (float)payload.Width / payload.CodedWidth;
        float vMax = (float)payload.Height / payload.CodedHeight;
        QuadConstants constants = PlacedQuad(placement, VideoLayout.Crop(VideoLayout.Corners(geometry.Rotation, uMax, vMax), geometry.Crop), opacity,
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
        float edgeX = placement.EdgeScaleX;
        float edgeY = placement.EdgeScaleY;
        bool uniform = Math.Abs(edgeX - placement.Scale) <= 1e-4f * placement.Scale && Math.Abs(edgeY - placement.Scale) <= 1e-4f * placement.Scale;
        float screenX = uniform ? screenScale : edgeX * pixelScale;
        float screenY = uniform ? screenScale : edgeY * pixelScale;

        // Un píxel de destino más allá del borde, para que la cobertura lo suavice también hacia afuera.
        float marginX = 1f / screenX;
        float marginY = 1f / screenY;
        float w = placement.LocalWidth;
        float h = placement.LocalHeight;
        var constants = new QuadConstants { Shape = new(opacity, screenX, w, h), Present = new(0, uniform ? 0 : screenY, 0, 0) };
        constants.TopLeft = Corner(placement, uv, -marginX, -marginY, sequenceWidth, sequenceHeight, out constants.LocalTopLeft);
        constants.TopRight = Corner(placement, uv, w + marginX, -marginY, sequenceWidth, sequenceHeight, out constants.LocalTopRight);
        constants.BottomLeft = Corner(placement, uv, -marginX, h + marginY, sequenceWidth, sequenceHeight, out constants.LocalBottomLeft);
        constants.BottomRight = Corner(placement, uv, w + marginX, h + marginY, sequenceWidth, sequenceHeight, out constants.LocalBottomRight);
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
