using Rodaje.Core.Effects;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>Parte visible de un intermedio del pool, que puede ser más grande.</summary>
internal readonly record struct ImageView(RenderTarget Target, int Width, int Height)
{
    /// <summary>De uv de la imagen a uv de la textura y tope a medio texel del borde (<c>mapA</c>/<c>mapB</c>).</summary>
    public Float4 Map => new(
        (float)Width / Target.Width,
        (float)Height / Target.Height,
        (Width - 0.5f) / Target.Width,
        (Height - 0.5f) / Target.Height);
}

/// <summary>Desenfoque o nitidez pendiente, con sigma en píxeles de la secuencia.</summary>
internal readonly record struct SpatialStep(GpuEffectKind Kind, float Sigma, float Amount);

/// <summary>Efectos por píxel de una capa: banderas de la variante, LUT y cuántos se aplican.</summary>
internal readonly unsafe struct LayerPlan(int flags, ID3D11ShaderResourceView* lut, int perPixelCount, ID3D11ShaderResourceView* curves = null)
{
    public ID3D11ShaderResourceView* Curves { get; } = curves;

    public int Flags { get; } = flags;

    public ID3D11ShaderResourceView* Lut { get; } = lut;

    public int PerPixelCount { get; } = perPixelCount;
}

internal sealed unsafe partial class LayerRenderer
{
    /// <summary>Sigma máxima por nivel: por encima se reduce a la mitad (pirámide ½, ¼…) antes del gaussiano.</summary>
    internal const float MaxSigmaPerLevel = 2;

    /// <summary>Sigma del desenfoque a partir del radio: el radio es tres sigmas.</summary>
    internal static float BlurSigma(float radius) => radius / 3;

    private LayerPlan BuildPlan(LayerFrame layer, ref EffectConstants effects, ref FrameStats stats)
    {
        _spatial.Clear();
        _masks.Clear();
        int flags = 0;
        int perPixel = 0;
        ID3D11ShaderResourceView* lut = null;
        ID3D11ShaderResourceView* curves = null;
        int advanced = 0;
        IReadOnlyList<LayerEffect> list = layer.Effects;
        for (int i = 0; i < list.Count; i++)
        {
            LayerEffect effect = list[i];
            GpuEffect? gpu = GpuEffectRegistry.Find(effect.Code);
            EffectDefinition? definition = EffectRegistry.Find(effect.Code);
            if (gpu is null || definition is null)
            {
                stats.EffectsSkipped++;
                continue;
            }

            EffectValues values = effect.Values;
            if (Bypasses(gpu.Kind))
            {
                continue;
            }

            switch (gpu.Kind)
            {
                case GpuEffectKind.Transform or GpuEffectKind.Stabilize or GpuEffectKind.Blend or GpuEffectKind.Crop:
                    // Llegan por LayerFrame.Transform (con LayerTransform.Stabilization) y LayerFrame.Blend.
                    break;
                case GpuEffectKind.Color when (flags & EffectShaders.Color) == 0:
                    flags |= EffectShaders.Color;
                    perPixel++;
                    SetColor(ref effects, values, definition);
                    if (effect.Resource is { } key)
                    {
                        if (Resources is { } resources && resources.TryGetLut(key, out LutData? data) && data is not null)
                        {
                            lut = _luts.Get(key, data);
                            flags |= EffectShaders.Lut;
                            int size = LutTextures.TextureSize(data.Size);
                            effects.Tones.Z = (size - 1f) / size;
                            effects.Tones.W = 0.5f / size;
                        }
                        else
                        {
                            stats.MissingContent++;
                        }
                    }

                    break;
                case GpuEffectKind.Vignette when (flags & EffectShaders.Vignette) == 0:
                    flags |= EffectShaders.Vignette;
                    perPixel++;
                    effects.Vignette = new(
                        Value(values, definition, 0),
                        Value(values, definition, 1),
                        Math.Max(Value(values, definition, 2), 1e-3f),
                        0);
                    break;
                case GpuEffectKind.Mask:
                    _masks.Add(effect);
                    break;
                case GpuEffectKind.ChromaKey when (advanced & 1) == 0:
                    advanced |= 1;
                    perPixel++;
                    SetChromaKey(ref effects, values, definition);
                    break;
                case GpuEffectKind.ColorWheels when (advanced & 2) == 0:
                    advanced |= 2;
                    perPixel++;
                    SetWheels(ref effects, values, definition);
                    break;
                case GpuEffectKind.Curves when (advanced & 4) == 0:
                    advanced |= 4;
                    perPixel++;
                    if (effect.Resource is { Reference: { } curveKey })
                    {
                        curves = _curves.Upload(curveKey);
                    }
                    else
                    {
                        advanced &= ~4;
                    }

                    break;
                case GpuEffectKind.Blur:
                    _spatial.Add(new SpatialStep(GpuEffectKind.Blur, BlurSigma(Value(values, definition, 0)), 0));
                    break;
                case GpuEffectKind.Sharpen:
                    _spatial.Add(new SpatialStep(GpuEffectKind.Sharpen, Value(values, definition, 1), Value(values, definition, 0)));
                    break;
                default:
                    // Un segundo color o viñeta (van fusionados una vez), una transición o algo que no es de imagen.
                    stats.EffectsSkipped++;
                    break;
            }
        }

        if (advanced != 0)
        {
            flags |= EffectShaders.Advanced;
            effects.Extra = new((advanced & 1) != 0 ? 1 : 0, (advanced & 2) != 0 ? 1 : 0, (advanced & 4) != 0 ? 1 : 0, 0);
        }

        return new LayerPlan(flags, lut, perPixel, curves);
    }

    /// <summary>Lo que la vista previa deja fuera con <see cref="Bypass"/> (antes/después y cuentagotas).</summary>
    private bool Bypasses(GpuEffectKind kind) => kind switch
    {
        GpuEffectKind.Color or GpuEffectKind.Vignette or GpuEffectKind.ColorWheels or GpuEffectKind.Curves => (Bypass & PreviewBypass.Grading) != 0,
        GpuEffectKind.ChromaKey => (Bypass & PreviewBypass.Key) != 0,
        _ => false,
    };

    /// <summary>
    /// Croma (referencia en las pruebas): la clave en CbCr BT.709, la distancia ×2 contra tolerancia y suavidad, el recorte
    /// del borde a 0,45·limpieza por cada lado y la dirección de la clave para quitar el derrame.
    /// </summary>
    private static void SetChromaKey(ref EffectConstants effects, in EffectValues values, EffectDefinition definition)
    {
        float r = Value(values, definition, 0), g = Value(values, definition, 1), b = Value(values, definition, 2);
        float y = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
        float cb = (b - y) / 1.8556f, cr = (r - y) / 1.5748f;
        float length = MathF.Sqrt((cb * cb) + (cr * cr));
        effects.Key0 = new(cb, cr, Value(values, definition, 3), Value(values, definition, 4));
        effects.Key1 = new(Value(values, definition, 5), 0.45f * Value(values, definition, 6),
            length > 1e-4f ? cb / length : 0, length > 1e-4f ? cr / length : 0);
    }

    private static void SetWheels(ref EffectConstants effects, in EffectValues values, EffectDefinition definition)
    {
        EffectValues copy = values;
        float Sum(int channel, int master) => Value(copy, definition, channel) + Value(copy, definition, master);
        effects.Lift = new(Sum(0, 3), Sum(1, 3), Sum(2, 3), 0);
        effects.GammaExp = new(MathF.Pow(2, -Sum(4, 7)), MathF.Pow(2, -Sum(5, 7)), MathF.Pow(2, -Sum(6, 7)), 0);
        effects.Gain = new(1 + Sum(8, 11), 1 + Sum(9, 11), 1 + Sum(10, 11), 0);
    }

    /// <summary>Valor del parámetro <paramref name="slot"/> dentro de los límites del registro (sin NaN).</summary>
    private static float Value(in EffectValues values, EffectDefinition definition, int slot) =>
        (float)definition.Parameters[slot].Clamp(values[slot]);

    private static void SetColor(ref EffectConstants effects, in EffectValues values, EffectDefinition definition)
    {
        float exposure = Value(values, definition, 0);
        float temperature = Value(values, definition, 3);
        float tint = Value(values, definition, 4);
        effects.Grade = new(MathF.Pow(2, exposure), 1 + Value(values, definition, 1), 1 + Value(values, definition, 2), Value(values, definition, 7));
        effects.Balance = new(1 + (0.2f * temperature), 1 - (0.2f * tint), 1 - (0.2f * temperature), 0);
        effects.Tones = new(Value(values, definition, 5), Value(values, definition, 6), 1, 0);
    }

    /// <summary>
    /// Gaussiano separable: si sigma pasa de <see cref="MaxSigmaPerLevel"/> reduce a la mitad tantas veces como haga falta
    /// y desenfoca en ese nivel. Devuelve otra vista (la original sigue siendo de quien llama), la misma si sigma es
    /// despreciable, o <c>null</c> si el pool está lleno o falta un shader.
    /// </summary>
    private ImageView? Blur(ImageView source, float sigma)
    {
        if (!(sigma >= 0.25f))
        {
            return source;
        }

        ID3D11PixelShader* down = _effects.TryGetShader(EffectShader.Downsample, 0);
        ID3D11PixelShader* blur = _effects.TryGetShader(EffectShader.Blur, 0);
        if (down == null || blur == null)
        {
            return null;
        }

        ImageView current = source;
        float sigmaX = sigma;
        float sigmaY = sigma;
        while (Math.Max(sigmaX, sigmaY) > MaxSigmaPerLevel && current.Width > 4 && current.Height > 4)
        {
            int width = (current.Width + 1) / 2;
            int height = (current.Height + 1) / 2;
            RenderTarget? half = _pool.Rent(width, height, IntermediateFormat);
            if (half is null)
            {
                ReturnUnlessSource(current, source);
                return null;
            }

            var next = new ImageView(half, width, height);
            EffectConstants constants = default;
            constants.MapA = current.Map;
            Pass(down, next, constants, current.Target, null);
            ReturnUnlessSource(current, source);
            sigmaX *= (float)width / current.Width;
            sigmaY *= (float)height / current.Height;
            current = next;
        }

        RenderTarget? horizontal = _pool.Rent(current.Width, current.Height, IntermediateFormat);
        RenderTarget? vertical = horizontal is null ? null : _pool.Rent(current.Width, current.Height, IntermediateFormat);
        if (vertical is null)
        {
            _pool.Return(horizontal);
            ReturnUnlessSource(current, source);
            return null;
        }

        var horizontalView = new ImageView(horizontal!, current.Width, current.Height);
        var verticalView = new ImageView(vertical, current.Width, current.Height);
        Pass(blur, horizontalView, Kernel(current, new(1f / current.Width, 0), sigmaX), current.Target, null);
        Pass(blur, verticalView, Kernel(horizontalView, new(0, 1f / current.Height), sigmaY), horizontal!, null);
        _pool.Return(horizontal);
        ReturnUnlessSource(current, source);
        return verticalView;
    }

    private static EffectConstants Kernel(ImageView source, System.Numerics.Vector2 step, float sigma)
    {
        EffectConstants constants = default;
        constants.MapA = source.Map;
        constants.Kernel = new(step.X, step.Y, sigma, MathF.Ceiling(3 * sigma));
        return constants;
    }

    /// <summary>Máscara de enfoque. Devuelve una vista nueva, la misma si no cambia nada, o <c>null</c> si no se pudo.</summary>
    private ImageView? Sharpen(ImageView source, float amount, float sigma)
    {
        ID3D11PixelShader* sharpen = _effects.TryGetShader(EffectShader.Sharpen, 0);
        if (sharpen == null)
        {
            return null;
        }

        if (!(amount > 0))
        {
            return source;
        }

        if (Blur(source, sigma) is not { } blurred)
        {
            return null;
        }

        if (blurred.Target == source.Target)
        {
            return source;
        }

        RenderTarget? output = _pool.Rent(source.Width, source.Height, IntermediateFormat);
        if (output is null)
        {
            _pool.Return(blurred.Target);
            return null;
        }

        var result = new ImageView(output, source.Width, source.Height);
        EffectConstants constants = default;
        constants.MapA = source.Map;
        constants.MapB = blurred.Map;
        constants.Kernel = new(amount, 0, 0, 0);
        Pass(sharpen, result, constants, source.Target, blurred.Target);
        _pool.Return(blurred.Target);
        return result;
    }

    private void ReturnUnlessSource(ImageView view, ImageView source)
    {
        if (view.Target != source.Target)
        {
            _pool.Return(view.Target);
        }
    }

    /// <summary>Una pasada de pantalla completa de <paramref name="imageA"/> (y <paramref name="imageB"/>) a <paramref name="output"/>.</summary>
    private void Pass(ID3D11PixelShader* shader, ImageView output, in EffectConstants constants, RenderTarget imageA, RenderTarget? imageB)
    {
        _quad.Bind(output.Target.Rtv, output.Width, output.Height, shader, null);
        BindInputs(null, null, imageA, imageB);
        _effects.Set(constants);
        _quad.Draw(QuadConstants.FullTarget());
        UnbindInputs();
    }

    private bool DrawTransition(LayerFrame outgoing, LayerSource outgoingPayload, LayerTransition transition, LayerSource? incomingPayload,
        Func<LayerFrame, SourceGeometry>? geometry, in Destination destination, ref FrameStats stats)
    {
        float progress = float.IsNaN(transition.Progress) ? 0 : Math.Clamp(transition.Progress, 0, 1);
        if (incomingPayload is null)
        {
            stats.LayersSkipped++;
        }

        GpuEffectKind kind = GpuEffectRegistry.Find(transition.Code)?.Kind ?? GpuEffectKind.Preset;
        if (kind is not (GpuEffectKind.Crossfade or GpuEffectKind.Dip or GpuEffectKind.Wipe or GpuEffectKind.Push
            or GpuEffectKind.ZoomDissolve or GpuEffectKind.BlurDissolve))
        {
            // Un preajuste sin resolver (falta el paquete) o un código desconocido: fundido cruzado.
            if (kind == GpuEffectKind.Preset)
            {
                stats.MissingContent++;
            }
            else
            {
                stats.EffectsSkipped++;
            }

            kind = GpuEffectKind.Crossfade;
        }

        bool shaderBlend = EffectPipeline.IsShaderBlend(outgoing.Blend);
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Transition, shaderBlend ? EffectShaders.Blend : 0);
        RenderTarget? canvasA = shader == null ? null : _pool.Rent(destination.Width, destination.Height, IntermediateFormat, exact: true);
        RenderTarget? canvasB = canvasA is null ? null : _pool.Rent(destination.Width, destination.Height, IntermediateFormat, exact: true);
        if (canvasA is null || canvasB is null)
        {
            // Sin shader o sin memoria: corte seco en la mitad.
            _pool.Return(canvasA);
            stats.EffectsSkipped++;
            return progress < 0.5f || incomingPayload is null
                ? DrawSingle(outgoing, outgoingPayload, GeometryOf(outgoing, outgoingPayload, geometry), destination, outgoing.Blend, ref stats)
                : DrawSingle(transition.Incoming, incomingPayload.Value, GeometryOf(transition.Incoming, incomingPayload.Value, geometry), destination,
                    outgoing.Blend, ref stats);
        }

        float* clear = stackalloc float[4];
        _context->ClearRenderTargetView(canvasA.Rtv, clear);
        _context->ClearRenderTargetView(canvasB.Rtv, clear);
        var sideA = new Destination(canvasA, destination.Width, destination.Height, destination.SequenceWidth, destination.SequenceHeight);
        var sideB = new Destination(canvasB, destination.Width, destination.Height, destination.SequenceWidth, destination.SequenceHeight);
        DrawSide(outgoing, outgoingPayload, geometry, sideA, ref stats);
        if (incomingPayload is { } incomingSource)
        {
            DrawSide(transition.Incoming, incomingSource, geometry, sideB, ref stats);
        }

        var viewA = new ImageView(canvasA, destination.Width, destination.Height);
        var viewB = new ImageView(canvasB, destination.Width, destination.Height);
        float middle = 4 * progress * (1 - progress);
        EffectValues values = transition.Values;
        EffectDefinition? definition = EffectRegistry.Find(transition.Code);
        EffectConstants effects = default;
        effects.Mix0 = new(MixCode(kind), progress, middle, 0);
        effects.Canvas = new(destination.Width, destination.Height, 0, 0);
        switch (kind)
        {
            case GpuEffectKind.Dip:
                effects.Mix1 = new(Value(values, definition!, 0), Value(values, definition!, 1), Value(values, definition!, 2), 1);
                break;
            case GpuEffectKind.Wipe:
                float radians = Value(values, definition!, 0) * MathF.PI / 180;
                effects.Mix1 = new(MathF.Cos(radians), MathF.Sin(radians), Math.Max(Value(values, definition!, 1), 1e-4f), 0);
                break;
            case GpuEffectKind.Push:
                effects.Mix1 = PushDirection(values[0]);
                break;
            case GpuEffectKind.ZoomDissolve:
                effects.Mix1 = new(Value(values, definition!, 0), 0, 0, 0);
                break;
            case GpuEffectKind.BlurDissolve:
                float sigma = BlurSigma(Value(values, definition!, 0) * middle) * destination.PixelScale;
                viewA = Blur(viewA, sigma) ?? viewA;
                viewB = Blur(viewB, sigma) ?? viewB;
                break;
        }

        RenderTarget? background = null;
        BlendMode blend = outgoing.Blend;
        if (shaderBlend)
        {
            background = CopyBackground(destination, new PixelRect(0, 0, destination.Width, destination.Height), ref effects);
            if (background is null)
            {
                shader = _effects.TryGetShader(EffectShader.Transition, 0);
                blend = BlendMode.Normal;
                stats.EffectsSkipped++;
            }
        }

        if (shader != null)
        {
            effects.BlendInfo.X = (float)blend;
            effects.MapA = viewA.Map;
            effects.MapB = viewB.Map;
            _quad.Bind(destination.Target.Rtv, destination.Width, destination.Height, shader, _effects.BlendState(blend));
            BindInputs(background, null, viewA.Target, viewB.Target);
            _effects.Set(effects);
            _quad.Draw(QuadConstants.FullTarget());
            UnbindInputs();
            stats.EffectsApplied += 1 + (blend == BlendMode.Normal ? 0 : 1);
        }

        _pool.Return(background);
        ReturnUnlessSource(viewA, new ImageView(canvasA, 0, 0));
        ReturnUnlessSource(viewB, new ImageView(canvasB, 0, 0));
        _pool.Return(canvasA);
        _pool.Return(canvasB);
        return true;
    }

    /// <summary>Un lado de la transición en su lienzo transparente, con sus efectos y mezcla normal.</summary>
    private void DrawSide(LayerFrame side, LayerSource payload, Func<LayerFrame, SourceGeometry>? geometry, in Destination canvas,
        ref FrameStats stats)
    {
        SourceGeometry sourceGeometry = GeometryOf(side, payload, geometry);
        if (side.Effects.Count == 0 && payload.Planes is { } planes)
        {
            _quad.Bind(canvas.Target.Rtv, canvas.Width, canvas.Height, QuadPipeline.Shader.Layer);
            DrawPlain(side, planes, sourceGeometry, canvas.SequenceWidth, canvas.SequenceHeight, canvas.PixelScale);
            return;
        }

        DrawSingle(side, payload, sourceGeometry, canvas, BlendMode.Normal, ref stats);
    }

    /// <summary>Número de la mezcla en <c>PsTransition</c>; el desenfoque llega ya aplicado y mezcla como el fundido.</summary>
    private static float MixCode(GpuEffectKind kind) => kind switch
    {
        GpuEffectKind.Dip => 2,
        GpuEffectKind.Wipe => 3,
        GpuEffectKind.Push => 4,
        GpuEffectKind.ZoomDissolve => 5,
        _ => 1,
    };

    /// <summary>
    /// Hacia dónde sale el clip en un empuje. La dirección es una opción, no un parámetro: el motor manda su índice en
    /// <see cref="EffectRegistry.PushDirections"/> (0 izquierda, 1 derecha, 2 arriba, 3 abajo) en el primer valor.
    /// </summary>
    internal static Float4 PushDirection(float index) => (float.IsNaN(index) ? 0 : (int)Math.Clamp(index, 0, 3)) switch
    {
        1 => new(-1, 0, 0, 0),
        2 => new(0, 1, 0, 0),
        3 => new(0, -1, 0, 0),
        _ => new(1, 0, 0, 0),
    };
}
