using Rodaje.Core.Effects;
using Rodaje.Core.Model;
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

/// <summary>
/// Un efecto que va en el intermedio, en el orden de la pila: sus valores evaluados (radios en píxeles de la secuencia), su
/// lugar en la pila (para la historia de la reducción de ruido y sus máscaras propias) y dónde se aplica respecto de las
/// máscaras de efectos (0 en toda la imagen, 1 dentro, 2 fuera). Los efectos por píxel limitados por máscaras propias también
/// van aquí, con <c>Region</c> 1.
/// </summary>
internal readonly record struct SpatialStep(GpuEffectKind Kind, EffectValues Values, EffectDefinition Definition, int Index, int Region);

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
        _regionMasks.Clear();
        _scopedMasks.Clear();
        ResolveMaskView(layer);
        IReadOnlyList<LayerEffect> list = layer.Effects;
        ulong scoped = ScopedEffects(list);
        int flags = 0;
        int perPixel = 0;
        ID3D11ShaderResourceView* lut = null;
        ID3D11ShaderResourceView* curves = null;
        int advanced = 0;
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

            bool limited = i < 64 && (scoped & (1UL << i)) != 0;
            switch (gpu.Kind)
            {
                case GpuEffectKind.Transform or GpuEffectKind.Stabilize or GpuEffectKind.Blend or GpuEffectKind.Crop:
                    // Llegan por LayerFrame.Transform (con LayerTransform.Stabilization) y LayerFrame.Blend.
                    break;
                case GpuEffectKind.Mask:
                    int target = MaskTarget(values);
                    if (target == -1)
                    {
                        // Sin destino: recorta la capa o, con el uso «limitar efectos», marca la zona de los efectos con zona.
                        (values[MaskGeometry.UseSlot] >= 0.5f ? _regionMasks : _masks).Add(effect);
                    }
                    else if (target >= 0 && target < Math.Min(list.Count, 64) && (scoped & (1UL << target)) != 0)
                    {
                        _scopedMasks.Add((target, effect));
                    }

                    // Sin destino válido (su efecto está apagado o no existe) no hace nada.
                    break;
                case GpuEffectKind.Color or GpuEffectKind.Vignette or GpuEffectKind.ChromaKey or GpuEffectKind.ColorWheels
                    or GpuEffectKind.Curves or GpuEffectKind.HslQualifier when limited:
                    _spatial.Add(new SpatialStep(gpu.Kind, values, definition, i, 1));
                    break;
                case GpuEffectKind.Color or GpuEffectKind.Vignette or GpuEffectKind.ChromaKey or GpuEffectKind.ColorWheels
                    or GpuEffectKind.Curves or GpuEffectKind.HslQualifier:
                    if (AddPerPixel(effect, gpu.Kind, definition, ref effects, ref flags, ref advanced, ref lut, ref curves, ref stats))
                    {
                        perPixel++;
                    }
                    else
                    {
                        // Un segundo color, viñeta, croma, ruedas o curvas: van fusionados una vez.
                        stats.EffectsSkipped++;
                    }

                    break;
                case GpuEffectKind.Blur or GpuEffectKind.Sharpen or GpuEffectKind.Grain or GpuEffectKind.Glow
                    or GpuEffectKind.ChromaticAberration or GpuEffectKind.Denoise or GpuEffectKind.Mirror or GpuEffectKind.LensDistortion
                    or GpuEffectKind.Pixelate:
                    int region = Choice(values, definition, BuiltInEffects.RegionOptions.Region);
                    _spatial.Add(new SpatialStep(gpu.Kind, values, definition, i, limited ? Math.Max(region, 1) : region));
                    break;
                case GpuEffectKind.Reframe360:
                    // Cambia la geometría de toda la capa: nunca se limita a las máscaras.
                    _spatial.Add(new SpatialStep(gpu.Kind, values, definition, i, 0));
                    break;
                default:
                    // Una transición o algo que no es de imagen.
                    stats.EffectsSkipped++;
                    break;
            }
        }

        if (advanced != 0)
        {
            flags |= EffectShaders.Advanced;
            effects.Extra = new((advanced & 1) != 0 ? 1 : 0, (advanced & 2) != 0 ? 1 : 0, (advanced & 4) != 0 ? 1 : 0, 0);
            effects.Qual3.Z = (advanced & 8) != 0 ? 1 : 0;
            effects.Qual3.Y = (advanced & 8) != 0 && ShowQualifierMask ? 1 : 0;
        }

        return new LayerPlan(flags, lut, perPixel, curves);
    }

    /// <summary>Los efectos de imagen (de los 64 primeros) que limita alguna máscara de la capa, como máscara de bits.</summary>
    private static ulong ScopedEffects(IReadOnlyList<LayerEffect> list)
    {
        ulong scoped = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Code != MaskCode)
            {
                continue;
            }

            int target = MaskTarget(list[i].Values);
            if (target >= 0 && target < Math.Min(list.Count, 64)
                && GpuEffectRegistry.Find(list[target].Code)?.Kind is GpuEffectKind.Color or GpuEffectKind.Vignette or GpuEffectKind.ChromaKey
                    or GpuEffectKind.ColorWheels or GpuEffectKind.Curves or GpuEffectKind.HslQualifier or GpuEffectKind.Blur or GpuEffectKind.Sharpen
                    or GpuEffectKind.Pixelate or GpuEffectKind.Grain or GpuEffectKind.Glow or GpuEffectKind.ChromaticAberration
                    or GpuEffectKind.Denoise or GpuEffectKind.Mirror or GpuEffectKind.LensDistortion)
            {
                scoped |= 1UL << target;
            }
        }

        return scoped;
    }

    /// <summary>A qué limita una máscara: −1 la capa, el índice del efecto, o −2 nada (<see cref="MaskGeometry.TargetSlot"/>).</summary>
    internal static int MaskTarget(in EffectValues values)
    {
        float slot = values[MaskGeometry.TargetSlot];
        return float.IsNaN(slot) || Math.Abs(slot) < 0.5f ? -1 : slot < 0 ? -2 : (int)MathF.Round(slot) - 1;
    }

    /// <summary>
    /// Suma un efecto por píxel a las constantes y banderas de <c>PsGraded</c>. <c>false</c> si ya hay uno de su clase
    /// (cada uno va fusionado una sola vez).
    /// </summary>
    private bool AddPerPixel(LayerEffect effect, GpuEffectKind kind, EffectDefinition definition, ref EffectConstants effects, ref int flags,
        ref int advanced, ref ID3D11ShaderResourceView* lut, ref ID3D11ShaderResourceView* curves, ref FrameStats stats)
    {
        EffectValues values = effect.Values;
        switch (kind)
        {
            case GpuEffectKind.Color when (flags & EffectShaders.Color) == 0:
                flags |= EffectShaders.Color;
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

                return true;
            case GpuEffectKind.Vignette when (flags & EffectShaders.Vignette) == 0:
                flags |= EffectShaders.Vignette;
                effects.Vignette = new(
                    Value(values, definition, 0),
                    Value(values, definition, 1),
                    Math.Max(Value(values, definition, 2), 1e-3f),
                    0);
                effects.Vignette2 = new(Value(values, definition, 3), Value(values, definition, 4), Value(values, definition, 5), 0);
                return true;
            case GpuEffectKind.ChromaKey when (advanced & 1) == 0:
                advanced |= 1;
                SetChromaKey(ref effects, values, definition);
                return true;
            case GpuEffectKind.ColorWheels when (advanced & 2) == 0:
                advanced |= 2;
                SetWheels(ref effects, values, definition);
                return true;
            case GpuEffectKind.Curves when (advanced & 4) == 0:
                advanced |= 4;
                if (effect.Resource is { Reference: { } curveKey })
                {
                    curves = _curves.Upload(curveKey);
                    effects.Qual3.W = _curves.HasHueCurves(curveKey) ? 1 : 0;
                }
                else
                {
                    advanced &= ~4;
                }

                return true;
            case GpuEffectKind.HslQualifier when (advanced & 8) == 0:
                advanced |= 8;
                SetQualifier(ref effects, values, definition);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Un efecto por píxel solo, sobre un intermedio (lo limita una máscara): <c>PsGraded</c> lee el intermedio como capa
    /// compuesta. Devuelve la vista nueva o nula si falta su variante o memoria.
    /// </summary>
    private ImageView? GradeIntermediate(ImageView view, LayerEffect effect, ref FrameStats stats)
    {
        GpuEffect? gpu = GpuEffectRegistry.Find(effect.Code);
        EffectDefinition? definition = EffectRegistry.Find(effect.Code);
        if (gpu is null || definition is null)
        {
            return null;
        }

        EffectConstants constants = default;
        int flags = 0, advanced = 0;
        ID3D11ShaderResourceView* lut = null;
        ID3D11ShaderResourceView* curves = null;
        AddPerPixel(effect, gpu.Kind, definition, ref constants, ref flags, ref advanced, ref lut, ref curves, ref stats);
        if (advanced != 0)
        {
            flags |= EffectShaders.Advanced;
            constants.Extra = new((advanced & 1) != 0 ? 1 : 0, (advanced & 2) != 0 ? 1 : 0, (advanced & 4) != 0 ? 1 : 0, 0);
            constants.Qual3.Z = (advanced & 8) != 0 ? 1 : 0;
            constants.Qual3.Y = (advanced & 8) != 0 && ShowQualifierMask ? 1 : 0;
        }

        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Graded, flags);
        RenderTarget? output = shader == null ? null : _pool.Rent(view.Width, view.Height, IntermediateFormat);
        if (output is null)
        {
            return null;
        }

        constants.Extra.W = 1;
        constants.MapA = view.Map;
        var result = new ImageView(output, view.Width, view.Height);
        _quad.Bind(output.Rtv, view.Width, view.Height, shader, null);
        BindInputs(null, lut, view.Target, null, null, curves);
        _effects.Set(constants);
        _quad.Draw(QuadConstants.FullTarget());
        UnbindInputs();
        return result;
    }

    /// <summary>Pixelar: bloques de <paramref name="block"/> píxeles del intermedio. Una vista nueva, o nula sin shader o memoria.</summary>
    private ImageView? Pixelate(ImageView source, float block)
    {
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Pixelate, 0);
        if (shader == null)
        {
            return null;
        }

        if (!(block > 1))
        {
            return source;
        }

        RenderTarget? output = _pool.Rent(source.Width, source.Height, IntermediateFormat);
        if (output is null)
        {
            return null;
        }

        var result = new ImageView(output, source.Width, source.Height);
        EffectConstants constants = default;
        constants.MapA = source.Map;
        constants.Kernel = new(block, 0, 0, 0);
        constants.Canvas = new(source.Width, source.Height, 0, 0);
        Pass(shader, result, constants, source.Target, null);
        return result;
    }

    /// <summary>Lo que la vista previa deja fuera con <see cref="Bypass"/> (antes/después y cuentagotas).</summary>
    private bool Bypasses(GpuEffectKind kind) => kind switch
    {
        GpuEffectKind.Color or GpuEffectKind.Vignette or GpuEffectKind.ColorWheels or GpuEffectKind.Curves => (Bypass & PreviewBypass.Grading) != 0,
        GpuEffectKind.HslQualifier => (Bypass & (PreviewBypass.Grading | PreviewBypass.Qualifier)) != 0,
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

    /// <summary>
    /// Calificador: tono en vueltas, bordes de saturación y luma, y la corrección (giro de tono en CbCr, ganancia de
    /// saturación y exposición). Referencia en las pruebas.
    /// </summary>
    private static void SetQualifier(ref EffectConstants effects, in EffectValues values, EffectDefinition definition)
    {
        float shift = Value(values, definition, 8) * MathF.PI / 180;
        effects.Qual0 = new(Value(values, definition, 0) / 360, Value(values, definition, 1) / 360, Value(values, definition, 2) / 360,
            Choice(values, definition, BuiltInEffects.HslQualifierOptions.Invert));
        effects.Qual1 = new(Value(values, definition, 3), Value(values, definition, 4), Value(values, definition, 5), Value(values, definition, 6));
        effects.Qual2 = new(Value(values, definition, 7), MathF.Cos(shift), MathF.Sin(shift), 1 + Value(values, definition, 9));
        effects.Qual3.X = MathF.Pow(2, Value(values, definition, 10));
    }

    /// <summary>Índice de la opción de elección <paramref name="option"/>, que el motor deja tras los parámetros; 0 si falta.</summary>
    internal static int Choice(in EffectValues values, EffectDefinition definition, string option)
    {
        int slot = definition.Parameters.Length;
        foreach (OptionDefinition candidate in definition.Options)
        {
            if (candidate.Kind != OptionKind.Choice)
            {
                continue;
            }

            if (candidate.Name == option)
            {
                float value = values[slot];
                return float.IsNaN(value) ? 0 : Math.Clamp((int)value, 0, candidate.Choices.Length - 1);
            }

            slot++;
        }

        return 0;
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
        effects.Skin = new(Value(values, definition, 8), 0, 0, 0);
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
    private ImageView? Sharpen(ImageView source, float amount, float sigma, float threshold = 0)
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
        constants.Kernel = new(amount, threshold, 0, 0);
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
        if (MixCode(kind) == 0)
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
        int motion = transition.Shutter is { } shutterRange && shutterRange.To > shutterRange.From ? EffectShaders.Motion : 0;
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Transition, (shaderBlend ? EffectShaders.Blend : 0) | motion);
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
        EffectDefinition definition = EffectRegistry.Find(transition.Code) is { IsTransition: true } known ? known : EffectRegistry.Find(BuiltInTransitions.Crossfade)!;
        EffectConstants effects = default;
        int samples = motion != 0 ? ShutterSamples : 1;
        effects.Mix0 = new(MixCode(kind), progress, middle, samples);
        effects.Fx0 = transition.Shutter is { } open ? new(Shutter(open.From, progress), Shutter(open.To, progress), 0, 0) : default;
        effects.Canvas = new(destination.Width, destination.Height, 0, 0);
        ID3D11ShaderResourceView* gradient = null;
        if (kind == GpuEffectKind.BlurDissolve)
        {
            float sigma = BlurSigma(Value(values, definition, 0) * middle) * destination.PixelScale;
            viewA = Blur(viewA, sigma) ?? viewA;
            viewB = Blur(viewB, sigma) ?? viewB;
        }
        else if (kind == GpuEffectKind.Luma && transition.Resource is { } image)
        {
            if (Resources is { } resources && resources.TryGetMatte(image, 0, out MatteFrame? frame) && frame is not null
                && frame.Width is >= 1 and <= 8192 && frame.Height is >= 1 and <= 8192 && frame.Gray.Length >= (long)frame.Width * frame.Height)
            {
                gradient = _maskTextures.UploadMatte(frame);
            }
            else
            {
                stats.MissingContent++;
            }
        }

        SetTransition(ref effects, kind, values, definition, gradient != null);

        RenderTarget? background = null;
        BlendMode blend = outgoing.Blend;
        if (shaderBlend)
        {
            background = CopyBackground(destination, new PixelRect(0, 0, destination.Width, destination.Height), ref effects);
            if (background is null)
            {
                shader = _effects.TryGetShader(EffectShader.Transition, motion);
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
            _context->PSSetShaderResources(9, 1, &gradient);
            _effects.Set(effects);
            _quad.Draw(QuadConstants.FullTarget());
            ID3D11ShaderResourceView* none = null;
            _context->PSSetShaderResources(9, 1, &none);
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

    /// <summary>Muestras de la mezcla a lo largo del obturador abierto (desenfoque de movimiento de la transición).</summary>
    internal const int ShutterSamples = 16;

    /// <summary>Un extremo del obturador entre 0 y 1; si no es un número, el avance (sin desenfoque de ese lado).</summary>
    private static float Shutter(float value, float progress) => float.IsNaN(value) ? progress : Math.Clamp(value, 0, 1);

    /// <summary>
    /// Número de la mezcla en <c>PsTransition</c>, o 0 si la GPU no la conoce; el desenfoque llega ya aplicado y mezcla como
    /// el fundido.
    /// </summary>
    private static float MixCode(GpuEffectKind kind) => kind switch
    {
        GpuEffectKind.Crossfade or GpuEffectKind.BlurDissolve => 1,
        GpuEffectKind.Dip => 2,
        GpuEffectKind.Wipe => 3,
        GpuEffectKind.Push => 4,
        GpuEffectKind.ZoomDissolve => 5,
        GpuEffectKind.Slide => 6,
        GpuEffectKind.ZoomBlur => 7,
        GpuEffectKind.Spin => 8,
        GpuEffectKind.LightWipe => 9,
        GpuEffectKind.Glitch => 10,
        GpuEffectKind.FilmBurn => 11,
        GpuEffectKind.Flash => 12,
        GpuEffectKind.Luma => 13,
        GpuEffectKind.Bounce => 14,
        _ => 0,
    };

    /// <summary>Los parámetros de cada mezcla en <c>mix1</c>, <c>key0</c> y <c>key1</c> (ver <c>MixAt</c> en el HLSL).</summary>
    private static void SetTransition(ref EffectConstants effects, GpuEffectKind kind, in EffectValues values, EffectDefinition d, bool image)
    {
        EffectValues v = values;
        float V(int slot) => Value(v, d, slot);
        Float4 Direction(int slot)
        {
            float radians = V(slot) * MathF.PI / 180;
            return new(MathF.Cos(radians), MathF.Sin(radians), 0, 0);
        }

        switch (kind)
        {
            case GpuEffectKind.Dip:
                effects.Mix1 = new(V(0), V(1), V(2), 1);
                break;
            case GpuEffectKind.Wipe:
            case GpuEffectKind.Luma:
            {
                Float4 direction = Direction(0);
                effects.Mix1 = new(direction.X, direction.Y, Math.Max(V(1), 1e-4f), V(2));
                effects.Key0 = new(V(3), V(4), V(5), 0);
                if (kind == GpuEffectKind.Luma)
                {
                    int choices = d.Parameters.Length;
                    effects.Key1 = new(v[choices], v[choices + 1] >= 0.5f ? 1 : 0, image ? 1 : 0, V(0) / 360);
                }

                break;
            }

            case GpuEffectKind.Push:
                effects.Mix1 = PushDirection(v[d.Parameters.Length]);
                break;
            case GpuEffectKind.Bounce:
            {
                Float4 direction = PushDirection(v[d.Parameters.Length]);
                effects.Mix1 = new(direction.X, direction.Y, V(0), 0);
                break;
            }

            case GpuEffectKind.ZoomDissolve:
                effects.Mix1 = new(V(0), 0, 0, 0);
                break;
            case GpuEffectKind.Slide:
            {
                // Corrido para salir del todo también en diagonal: el lado mayor de la dirección vale 1.
                Float4 direction = Direction(0);
                float scale = Math.Max(Math.Abs(direction.X), Math.Abs(direction.Y));
                effects.Mix1 = new(direction.X / scale, direction.Y / scale, V(1), 0);
                break;
            }

            case GpuEffectKind.ZoomBlur:
                effects.Mix1 = new(V(0), V(1), V(2), 0);
                break;
            case GpuEffectKind.Spin:
                effects.Mix1 = new(V(0), V(1), 0, 0);
                break;
            case GpuEffectKind.LightWipe:
            {
                Float4 direction = Direction(0);
                effects.Mix1 = new(direction.X, direction.Y, Math.Max(V(1), 1e-4f), V(2));
                effects.Key0 = new(V(3), V(4), V(5), V(6));
                break;
            }

            case GpuEffectKind.Glitch:
                effects.Mix1 = new(V(0), MathF.Round(V(1)), V(2), MathF.Round(V(3)));
                break;
            case GpuEffectKind.FilmBurn:
                effects.Mix1 = new(V(0), MathF.Round(V(4)), 0, 0);
                effects.Key0 = new(V(1), V(2), V(3), 0);
                break;
            case GpuEffectKind.Flash:
                effects.Mix1 = new(V(0), V(1), 0, 0);
                effects.Key0 = new(V(2), V(3), V(4), 0);
                break;
        }
    }

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
