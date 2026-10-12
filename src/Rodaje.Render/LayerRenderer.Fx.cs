using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using Rodaje.Render.Effects;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>
/// Los efectos que van en el intermedio de la capa, uno tras otro: desenfoques (gaussiano, de lente, direccional y radial),
/// nitidez, grano, resplandor, aberración, espejo, distorsión y reducción de ruido. Cada uno puede limitarse con las máscaras
/// que «limitan efectos» (<c>RegionMix</c>). Radios y tamaños llegan en píxeles de la secuencia y se escalan al intermedio.
/// </summary>
internal sealed unsafe partial class LayerRenderer
{
    /// <summary>Muestras a cada lado de una pasada direccional o radial; dos pasadas dan (2K + 1)² muestras efectivas.</summary>
    internal const int LineTaps = 12;

    /// <summary>Radio máximo del disco de lente en píxeles del nivel en que se muestrea (por encima se reduce a la mitad).</summary>
    internal const float MaxDiscRadius = 8;

    /// <summary>Largo máximo, en píxeles del nivel, de un desenfoque direccional antes de reducir a la mitad.</summary>
    internal const float MaxLineRadius = 48;

    private readonly List<LayerEffect> _regionMasks = [];
    private readonly Dictionary<(string Clip, int Index), DenoiseHistory> _history = [];
    private RenderTarget? _regionMask;
    private Float4 _regionMap;
    private long _frameNumber;

    /// <summary>Solo vista previa: el calificador HSL muestra su máscara en gris en lugar de corregir.</summary>
    public bool ShowQualifierMask { get; set; }

    /// <summary>Lo último que salió de una reducción de ruido: el cuadro del origen y el resultado, para mezclar con el siguiente.</summary>
    private sealed class DenoiseHistory
    {
        public RenderTarget? Target;
        public int Width;
        public int Height;
        public long Pts;
        public long Duration;
        public long UsedFrame;
    }

    /// <summary>Cierra un cuadro de la composición: suelta las historias de reducción de ruido que no se usaron en él.</summary>
    public void EndFrame()
    {
        List<(string, int)>? stale = null;
        foreach (KeyValuePair<(string Clip, int Index), DenoiseHistory> entry in _history)
        {
            if (entry.Value.UsedFrame < _frameNumber)
            {
                (stale ??= []).Add(entry.Key);
            }
        }

        if (stale is not null)
        {
            foreach ((string, int) key in stale)
            {
                _pool.Return(_history[key].Target);
                _history.Remove(key);
            }
        }

        _frameNumber++;
    }

    private void ReleaseHistory()
    {
        foreach (DenoiseHistory history in _history.Values)
        {
            _pool.Return(history.Target);
        }

        _history.Clear();
    }

    /// <summary>
    /// Aplica un paso y, si va limitado a las máscaras de efectos, mezcla con lo de antes. Devuelve la vista nueva (la
    /// recibida sigue siendo de quien llama), la misma si no cambia nada, o <c>null</c> si no se pudo.
    /// </summary>
    private ImageView? ApplyStep(ImageView view, in SpatialStep step, float perSequencePixel, LayerFrame layer, bool source)
    {
        ImageView? next = ApplySpatial(view, step, perSequencePixel, layer, source);
        if (next is not { } result || result.Target == view.Target || step.Region == 0 || RegionMaskFor(step) is not { } region)
        {
            return next;
        }

        EffectConstants constants = default;
        constants.Fx0 = new((float)FxOp.RegionMix, step.Region == 2 ? 1 : 0, 0, 0);
        constants.Fx2 = region.Map;
        RenderTarget? shared = _regionMask;
        _regionMask = region.Target;
        ImageView? mixed = FxPass(view, result, constants, view.Width, view.Height);
        _regionMask = shared;
        _pool.Return(result.Target);
        return mixed;
    }

    /// <summary>
    /// La máscara que limita este paso: hoy, la de todas las máscaras de la capa con uso «limitar efectos». Es el punto de
    /// enganche para una máscara propia de cada efecto (otra textura en el espacio de la capa, con su mapa).
    /// </summary>
    private (RenderTarget Target, Float4 Map)? RegionMaskFor(in SpatialStep step) =>
        _regionMask is { } target ? (target, _regionMap) : null;

    private ImageView? ApplySpatial(ImageView view, in SpatialStep step, float perSequencePixel, LayerFrame layer, bool source)
    {
        EffectValues values = step.Values;
        EffectDefinition definition = step.Definition;
        float Px(int slot) => Value(values, definition, slot) * perSequencePixel;
        switch (step.Kind)
        {
            case GpuEffectKind.Blur:
                return Choice(values, definition, BuiltInEffects.BlurOptions.Kind) switch
                {
                    1 => LensBlur(view, Px(0)),
                    2 => LineBlur(view, Px(0), Value(values, definition, 1)),
                    3 => ZoomBlur(view, Px(0), Value(values, definition, 2), Value(values, definition, 3)),
                    _ => Blur(view, BlurSigma(Px(0))),
                };
            case GpuEffectKind.Sharpen:
                return Sharpen(view, Value(values, definition, 0), Px(1), Value(values, definition, 2));
            case GpuEffectKind.Grain:
            {
                float amount = Value(values, definition, 0);
                if (!(amount > 0))
                {
                    return view;
                }

                EffectConstants constants = default;
                constants.Fx0 = new((float)FxOp.Grain, amount, Value(values, definition, 1), Value(values, definition, 2));
                constants.Fx1 = new(Value(values, definition, 3), values[definition.TimeSlot], perSequencePixel, 0);
                return FxPass(view, null, constants, view.Width, view.Height);
            }

            case GpuEffectKind.Glow:
                return Glow(view, Value(values, definition, 0), Px(1), Value(values, definition, 2));
            case GpuEffectKind.ChromaticAberration:
            {
                float amount = Px(0);
                if (!(amount > 0.01f))
                {
                    return view;
                }

                EffectConstants constants = default;
                if (Choice(values, definition, BuiltInEffects.ChromaticAberrationOptions.Mode) == 1)
                {
                    float radians = Value(values, definition, 1) * MathF.PI / 180;
                    constants.Fx0 = new((float)FxOp.Aberration, 1, MathF.Cos(radians) * amount / view.Width, MathF.Sin(radians) * amount / view.Height);
                }
                else
                {
                    float halfDiagonal = 0.5f * MathF.Sqrt((view.Width * view.Width) + (view.Height * view.Height));
                    constants.Fx0 = new((float)FxOp.Aberration, 0, amount / halfDiagonal, 0);
                }

                return FxPass(view, null, constants, view.Width, view.Height);
            }

            case GpuEffectKind.Mirror:
            {
                EffectConstants constants = default;
                constants.Fx0 = new((float)FxOp.Mirror, Choice(values, definition, BuiltInEffects.MirrorOptions.Mode),
                    Value(values, definition, 0), MathF.Round(Value(values, definition, 1)));
                return FxPass(view, null, constants, view.Width, view.Height);
            }

            case GpuEffectKind.LensDistortion:
            {
                float amount = Value(values, definition, 0);
                float zoom = Value(values, definition, 1);
                if (Math.Abs(amount) < 1e-4f && Math.Abs(zoom - 1) < 1e-4f)
                {
                    return view;
                }

                EffectConstants constants = default;
                constants.Fx0 = new((float)FxOp.Distortion, 0.5f * amount, zoom, (float)view.Width / view.Height);
                return FxPass(view, null, constants, view.Width, view.Height);
            }

            case GpuEffectKind.Denoise:
                return Denoise(view, values, definition, step.Index, layer, source);
            default:
                return view;
        }
    }

    /// <summary>
    /// Desenfoque de lente: en luz lineal, un disco uniforme muestreado con la espiral de Vogel en el nivel de la pirámide
    /// donde el radio no pasa de <see cref="MaxDiscRadius"/> píxeles. Devuelve la vista de ese nivel, ya en gamma.
    /// </summary>
    private ImageView? LensBlur(ImageView source, float radius)
    {
        if (!(radius >= 0.5f))
        {
            return source;
        }

        EffectConstants constants = default;
        constants.Fx0 = new((float)FxOp.ToLinear, 0, 0, 0);
        if (FxPass(source, null, constants, source.Width, source.Height) is not { } linear)
        {
            return null;
        }

        ImageView? level = Reduce(linear, ref radius, MaxDiscRadius);
        if (level is not { } current)
        {
            return null;
        }

        constants.Fx0 = new((float)FxOp.LensDisc, radius / current.Width, radius / current.Height, 0);
        constants.Fx1 = new(Math.Clamp(MathF.Ceiling(3.2f * radius * radius), 16, 160), 0, 0, 0);
        ImageView? disc = FxPass(current, null, constants, current.Width, current.Height);
        _pool.Return(current.Target);
        if (disc is not { } blurred)
        {
            return null;
        }

        constants.Fx0 = new((float)FxOp.ToGamma, 0, 0, 0);
        ImageView? result = FxPass(blurred, null, constants, blurred.Width, blurred.Height);
        _pool.Return(blurred.Target);
        return result;
    }

    /// <summary>Movimiento en una dirección: dos pasadas de caja (gruesa y fina) que cubren ±radio.</summary>
    private ImageView? LineBlur(ImageView source, float radius, float degrees)
    {
        if (!(radius >= 0.5f))
        {
            return source;
        }

        ImageView? reduced = Reduce(source, ref radius, MaxLineRadius, keepSource: true);
        if (reduced is not { } level)
        {
            return null;
        }

        float radians = degrees * MathF.PI / 180;
        float stepX = MathF.Cos(radians) * radius / LineTaps / level.Width;
        float stepY = MathF.Sin(radians) * radius / LineTaps / level.Height;
        EffectConstants constants = default;
        constants.Fx0 = new((float)FxOp.LineBlur, stepX, stepY, LineTaps);
        ImageView? coarse = FxPass(level, null, constants, level.Width, level.Height);
        ReturnUnlessSource(level, source);
        if (coarse is not { } first)
        {
            return null;
        }

        constants.Fx0 = new((float)FxOp.LineBlur, stepX / ((2 * LineTaps) + 1), stepY / ((2 * LineTaps) + 1), LineTaps);
        ImageView? fine = FxPass(first, null, constants, first.Width, first.Height);
        _pool.Return(first.Target);
        return fine;
    }

    /// <summary>Zoom desde un centro: la escala varía ±radio en la esquina, en dos pasadas (gruesa y fina).</summary>
    private ImageView? ZoomBlur(ImageView source, float radius, float centerX, float centerY)
    {
        if (!(radius >= 0.5f))
        {
            return source;
        }

        float halfDiagonal = 0.5f * MathF.Sqrt((source.Width * source.Width) + (source.Height * source.Height));
        float span = radius / halfDiagonal / LineTaps;
        EffectConstants constants = default;
        constants.Fx0 = new((float)FxOp.ZoomBlur, centerX, centerY, span);
        constants.Fx1 = new(LineTaps, 0, 0, 0);
        if (FxPass(source, null, constants, source.Width, source.Height) is not { } first)
        {
            return null;
        }

        constants.Fx0.W = span / ((2 * LineTaps) + 1);
        ImageView? fine = FxPass(first, null, constants, first.Width, first.Height);
        _pool.Return(first.Target);
        return fine;
    }

    /// <summary>Resplandor: lo que pasa del umbral, en luz lineal, se desenfoca con el gaussiano y se suma con la intensidad.</summary>
    private ImageView? Glow(ImageView source, float threshold, float radius, float intensity)
    {
        if (!(intensity > 0))
        {
            return source;
        }

        EffectConstants constants = default;
        constants.Fx0 = new((float)FxOp.GlowBright, threshold, 0, 0);
        if (FxPass(source, null, constants, source.Width, source.Height) is not { } bright)
        {
            return null;
        }

        ImageView? blurred = Blur(bright, BlurSigma(radius));
        if (blurred is not { } halo)
        {
            _pool.Return(bright.Target);
            return null;
        }

        ReturnUnlessSource(bright, halo);
        constants.Fx0 = new((float)FxOp.GlowCombine, intensity, 0, 0);
        ImageView? result = FxPass(source, halo, constants, source.Width, source.Height);
        _pool.Return(halo.Target);
        return result;
    }

    /// <summary>
    /// Reducción de ruido: bilateral de 5 × 5 y, si el cuadro anterior de la misma capa (mismo clip y lugar en la pila) es el
    /// inmediatamente anterior del origen, mezcla con su resultado donde la luma casi no cambió. El mismo cuadro compuesto
    /// otra vez (pausa, exportación estricta) da el mismo resultado: la historia solo avanza con un cuadro nuevo.
    /// </summary>
    private ImageView? Denoise(ImageView source, in EffectValues values, EffectDefinition definition, int index, LayerFrame layer, bool image)
    {
        float strength = Value(values, definition, 0);
        float temporal = Value(values, definition, 1);
        float detail = Value(values, definition, 2);
        EffectConstants constants = default;
        ImageView current = source;
        if (strength > 0)
        {
            float spatialSigma = 0.8f + (1.2f * strength);
            float rangeSigma = 0.12f - (0.095f * detail);
            constants.Fx0 = new((float)FxOp.DenoiseSpatial, strength, 1f / source.Width, 1f / source.Height);
            constants.Fx1 = new(0.5f / (spatialSigma * spatialSigma), 0.5f / (rangeSigma * rangeSigma), 0, 0);
            if (FxPass(source, null, constants, source.Width, source.Height) is not { } filtered)
            {
                return null;
            }

            current = filtered;
        }

        // Una capa compuesta no tiene un cuadro del origen propio: solo la parte espacial.
        if (image || !(temporal > 0))
        {
            return current;
        }

        long pts = layer.Frame.Pts.Ticks;
        long duration = layer.Frame.Duration.Ticks;
        (string, int) key = (layer.ClipId, index);
        _history.TryGetValue(key, out DenoiseHistory? history);
        if (history is { Target: { } previous } && history.Width == source.Width && history.Height == source.Height)
        {
            history.UsedFrame = _frameNumber;
            if (history.Pts == pts)
            {
                // El mismo cuadro otra vez: lo que ya salió.
                ReturnUnlessSource(current, source);
                constants.Fx0 = new((float)FxOp.Copy, 0, 0, 0);
                return FxPass(new ImageView(previous, history.Width, history.Height), null, constants, source.Width, source.Height);
            }

            if (Math.Abs(pts - (history.Pts + history.Duration)) <= Math.Max(1, duration / 4))
            {
                constants.Fx0 = new((float)FxOp.DenoiseTemporal, 0.85f * temporal, 0.015f + (0.02f * (1 - detail)), 0.06f + (0.06f * (1 - detail)));
                ImageView? blended = FxPass(current, new ImageView(previous, history.Width, history.Height), constants, source.Width, source.Height);
                ReturnUnlessSource(current, source);
                if (blended is not { } result)
                {
                    return null;
                }

                current = result;
            }
        }

        Remember(key, current, pts, duration);
        return current;
    }

    /// <summary>Guarda una copia del resultado como historia de la capa (la vista sigue siendo de quien llama).</summary>
    private void Remember((string, int) key, ImageView result, long pts, long duration)
    {
        if (!_history.TryGetValue(key, out DenoiseHistory? history))
        {
            history = new DenoiseHistory();
            _history[key] = history;
        }

        if (history.Target is null || history.Width != result.Width || history.Height != result.Height)
        {
            _pool.Return(history.Target);
            history.Target = _pool.Rent(result.Width, result.Height, IntermediateFormat);
            history.Width = result.Width;
            history.Height = result.Height;
        }

        history.UsedFrame = _frameNumber;
        history.Pts = pts;
        history.Duration = duration;
        if (history.Target is null)
        {
            _history.Remove(key);
            return;
        }

        var box = new D3D11_BOX { left = 0, top = 0, right = (uint)result.Width, bottom = (uint)result.Height, front = 0, back = 1 };
        _context->CopySubresourceRegion((ID3D11Resource*)history.Target.Texture, 0, 0, 0, 0, (ID3D11Resource*)result.Target.Texture, 0, &box);
    }

    /// <summary>
    /// Reduce a la mitad (promedio de 2 × 2) mientras <paramref name="radius"/> pase de <paramref name="limit"/>, y lo deja
    /// en píxeles del nivel. Sin <paramref name="keepSource"/> la vista recibida pasa a ser de este método.
    /// </summary>
    private ImageView? Reduce(ImageView source, ref float radius, float limit, bool keepSource = false)
    {
        ID3D11PixelShader* down = _effects.TryGetShader(EffectShader.Downsample, 0);
        if (down == null)
        {
            if (!keepSource)
            {
                _pool.Return(source.Target);
            }

            return null;
        }

        ImageView current = source;
        while (radius > limit && current.Width > 4 && current.Height > 4)
        {
            int width = (current.Width + 1) / 2;
            int height = (current.Height + 1) / 2;
            RenderTarget? half = _pool.Rent(width, height, IntermediateFormat);
            if (half is null)
            {
                if (current.Target != source.Target || !keepSource)
                {
                    _pool.Return(current.Target);
                }

                return null;
            }

            var next = new ImageView(half, width, height);
            EffectConstants constants = default;
            constants.MapA = current.Map;
            Pass(down, next, constants, current.Target, null);
            if (current.Target != source.Target || !keepSource)
            {
                _pool.Return(current.Target);
            }

            radius *= (float)width / current.Width;
            current = next;
        }

        return current;
    }

    /// <summary>Hasta dónde llega un paso fuera del borde de la imagen, en píxeles de la secuencia (margen de un título).</summary>
    internal static float Reach(in SpatialStep step)
    {
        EffectValues values = step.Values;
        return step.Kind switch
        {
            GpuEffectKind.Blur => Value(values, step.Definition, 0),
            GpuEffectKind.Sharpen => 3 * Value(values, step.Definition, 1),
            GpuEffectKind.Glow => Value(values, step.Definition, 1),
            GpuEffectKind.ChromaticAberration => Value(values, step.Definition, 0),
            _ => 0,
        };
    }

    /// <summary>Una pasada de <c>PsFx</c> a un intermedio nuevo del pool, con la máscara de efectos en t7.</summary>
    private ImageView? FxPass(ImageView a, ImageView? b, in EffectConstants constants, int width, int height)
    {
        ID3D11PixelShader* shader = _effects.TryGetShader(EffectShader.Fx, 0);
        RenderTarget? output = shader == null ? null : _pool.Rent(width, height, IntermediateFormat);
        if (output is null)
        {
            return null;
        }

        EffectConstants fx = constants;
        fx.MapA = a.Map;
        if (b is { } second)
        {
            fx.MapB = second.Map;
        }

        var view = new ImageView(output, width, height);
        _quad.Bind(output.Rtv, width, height, shader, null);
        BindInputs(null, null, a.Target, b?.Target, _regionMask);
        _effects.Set(fx);
        _quad.Draw(QuadConstants.FullTarget());
        UnbindInputs();
        return view;
    }
}
