using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Playback.Composition;

/// <summary>Lo que es igual para todos los cuadros de una secuencia y decide sus píxeles.</summary>
/// <param name="FrameRate">Tasa de la secuencia.</param>
/// <param name="ColorSpace">Espacio de color de la secuencia.</param>
/// <param name="Chain">
/// Huella de toda la cadena que produce el fragmento: shaders de render, de efectos y de exportación, preajuste del
/// codificador y versión de FFmpeg. Cambia con cualquiera de ellos.
/// </param>
public sealed record RecipeFormat(Rational FrameRate, SequenceColorSpace ColorSpace, string Chain);

/// <summary>El medio de una capa de video identificado por su contenido, no por su ruta, y lo que el compositor lee de él.</summary>
/// <param name="ContentHash">Huella del contenido del medio (tamaño y SHA-256 de principio, medio y final).</param>
/// <param name="Format">Formato del cuadro decodificado: píxel, profundidad, matriz, rango, primarios, curva y tamaño original.</param>
public readonly record struct RecipeSource(string ContentHash, string Format);

/// <summary>Lo que la receta necesita saber de fuera de la composición. Lo que falta o aún carga da <c>null</c>.</summary>
public interface IRecipeSources
{
    /// <summary>El medio del cuadro de una capa de video; nulo si falta, está sin conexión o no se conoce.</summary>
    RecipeSource? Source(LayerFrame layer);

    /// <summary>La identidad de un recurso (LUT, imagen de una transición, máscara de imagen) con su versión; nula si falta o no está listo.</summary>
    string? Resource(ResourceKey key);

    /// <summary>La huella de los archivos de fuente de un título; nula si falta alguna.</summary>
    string? Fonts(GeneratedContent content);
}

/// <summary>
/// La receta de un cuadro: todo lo que decide sus píxeles, ya evaluado en ese instante, reducido a una huella de 128 bits
/// (<see cref="RenderCacheKey"/>, D-060). Cada propiedad de los tipos de la composición está en
/// <see cref="Included"/> o en <see cref="Excluded"/>; una prueba exige que no quede ninguna sin clasificar.
/// </summary>
/// <remarks>
/// No hay receta (<c>null</c>) para un cuadro con contenido que falta o aún carga, con un proxy, o con la reducción de
/// ruido temporal activa (su historia es la del clip, no la del cuadro): el caché nunca congela un respaldo.
/// </remarks>
public static class FrameRecipe
{
    /// <summary>Sube cuando cambia cómo se escribe la receta o lo que significa: invalida todo el caché.</summary>
    public const int Version = 1;

    /// <summary>Las propiedades que entran en la huella, por tipo.</summary>
    public static IReadOnlyDictionary<Type, string[]> Included { get; } = new Dictionary<Type, string[]>
    {
        [typeof(CompositionFrame)] = ["Width", "Height", "Layers"],
        [typeof(LayerFrame)] =
        [
            "Nested", "Generated", "TrackIndex", "Frame", "Transform", "SampleAspectRatio", "Rotation", "DisplayWidth", "DisplayHeight",
            "IsProxy", "Effects", "Blend", "Transition", "MotionSamples", "TextMotion", "Next", "Fraction", "Sampling",
        ],
        [typeof(LayerTransform)] = ["X", "Y", "Scale", "RotationDegrees", "Opacity", "HdrReferenceNits", "HdrExpandNits", "Stabilization", "Crop", "Shape", "Pin"],
        [typeof(LayerShape)] = ["AnchorX", "AnchorY", "ScaleX", "ScaleY", "SkewX", "SkewY", "FlipX", "FlipY"],
        [typeof(LayerCornerPin)] = ["TopLeftX", "TopLeftY", "TopRightX", "TopRightY", "BottomRightX", "BottomRightY", "BottomLeftX", "BottomLeftY"],
        [typeof(LayerStabilization)] = ["A", "B", "X", "Y"],
        [typeof(CropRect)] = ["Left", "Top", "Right", "Bottom"],
        [typeof(LayerEffect)] = ["Code", "Values", "Resource"],
        [typeof(LayerTransition)] = ["Code", "Progress", "Values", "Incoming", "Shutter", "Resource"],
        [typeof(TextMotion)] = ["Phase", "Progress", "Word", "WordProgress"],
        [typeof(GeneratedContent)] = ["Key"],
        [typeof(DecodedFrame)] = ["Index"],
        [typeof(ResourceKey)] = ["Reference"],
    };

    /// <summary>Las que quedan fuera a propósito, por tipo, con el motivo en el comentario de cada grupo.</summary>
    public static IReadOnlyDictionary<Type, string[]> Excluded { get; } = new Dictionary<Type, string[]>
    {
        // El instante absoluto: si entrara, mover un tramo lo invalidaría.
        [typeof(CompositionFrame)] = ["TimelineTime"],

        // ClipId no cambia píxeles (solo la historia de la reducción temporal, que no se guarda). El resto sale de los demás campos.
        [typeof(LayerFrame)] = ["ClipId", "IsPlain"],
        [typeof(LayerShape)] = ["IsNone"],
        [typeof(LayerCornerPin)] = ["IsNone"],
        [typeof(LayerStabilization)] = ["Scale", "RotationDegrees"],
        [typeof(CropRect)] = ["Width", "Height", "IsNone"],
        [typeof(LayerTransform)] = [],
        [typeof(LayerEffect)] = [],
        [typeof(LayerTransition)] = [],
        [typeof(TextMotion)] = [],
        [typeof(ResourceKey)] = [],

        // Todo lo que cambia la imagen ya está en Key; ClipId es solo para los avisos.
        [typeof(GeneratedContent)] =
        [
            "Kind", "Text", "Style", "BoxWidth", "SequenceWidth", "SequenceHeight", "UnitScale", "RasterScale", "HighlightStart",
            "HighlightLength", "Spans", "BoxHeight", "Words", "UsesUnits", "ClipId",
        ],

        // El cuadro del origen lo identifican el medio (por contenido) y el índice; el resto es del decodificador.
        [typeof(DecodedFrame)] = ["Pts", "Duration", "End", "Payload", "SizeBytes", "IsReleased"],
    };

    private static readonly int DenoiseCode = EffectRegistry.Find(BuiltInEffects.Denoise)!.Code;

    private static readonly int DenoiseTemporalSlot =
        EffectRegistry.Find(BuiltInEffects.Denoise)!.FindParameter(BuiltInEffects.DenoiseParameters.Temporal)!.Slot;

    /// <summary>La huella del cuadro, o <c>null</c> si no tiene receta válida.</summary>
    public static RenderCacheKey? Compute(CompositionFrame frame, RecipeFormat format, IRecipeSources sources)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(sources);
        using var writer = new Writer(sources);
        writer.Int(Version);
        writer.Long(format.FrameRate.Num);
        writer.Long(format.FrameRate.Den);
        writer.Int((int)format.ColorSpace);
        writer.Text(format.Chain);
        writer.Int(frame.Width);
        writer.Int(frame.Height);
        return writer.Layers(frame.Layers) ? writer.Finish() : null;
    }

    private sealed class Writer(IRecipeSources sources) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[8];

        public void Dispose() => _hash.Dispose();

        public RenderCacheKey Finish()
        {
            Span<byte> digest = stackalloc byte[32];
            _hash.GetHashAndReset(digest);
            return RenderCacheKey.FromHash(digest);
        }

        public bool Layers(IReadOnlyList<LayerFrame> layers)
        {
            Int(layers.Count);
            foreach (LayerFrame layer in layers)
            {
                if (!Layer(layer))
                {
                    return false;
                }
            }

            return true;
        }

        public void Int(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer, value);
            _hash.AppendData(_buffer, 0, 4);
        }

        public void Long(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer, value);
            _hash.AppendData(_buffer, 0, 8);
        }

        public void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Int(bytes.Length);
            _hash.AppendData(bytes);
        }

        private void Double(double value) => Long(BitConverter.DoubleToInt64Bits(value));

        private void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));

        private void Bool(bool value) => Int(value ? 1 : 0);

        private bool Layer(LayerFrame layer)
        {
            if (layer.IsProxy)
            {
                return false;
            }

            Int(layer.TrackIndex);
            if (layer.Nested is { } nested)
            {
                Int(1);
                if (!Layers(nested))
                {
                    return false;
                }
            }
            else if (layer.Generated is { } title)
            {
                if (sources.Fonts(title) is not { } fonts)
                {
                    return false;
                }

                Int(2);
                Text(title.Key);
                Text(fonts);
            }
            else
            {
                if (sources.Source(layer) is not { } source)
                {
                    return false;
                }

                Int(3);
                Text(source.ContentHash);
                Text(source.Format);
                Long(layer.Frame.Index);
                if (layer.Sampling is FrameSampling.Blend or FrameSampling.OpticalFlow && layer.Next is { } next)
                {
                    if (sources.Source(new LayerFrame(layer.TrackIndex, layer.ClipId, next, layer.Transform)) is not { } nextSource)
                    {
                        return false;
                    }

                    Int((int)layer.Sampling);
                    Float(layer.Fraction);
                    Text(nextSource.ContentHash);
                    Text(nextSource.Format);
                    Long(next.Index);
                }
            }

            Transform(layer.Transform);
            Long(layer.SampleAspectRatio.Num);
            Long(layer.SampleAspectRatio.Den);
            Int(layer.Rotation);
            Int(layer.DisplayWidth);
            Int(layer.DisplayHeight);
            Int((int)layer.Blend);
            Int(layer.Effects.Count);
            foreach (LayerEffect effect in layer.Effects)
            {
                if ((effect.Code == DenoiseCode && effect.Values[DenoiseTemporalSlot] > 0) || !Effect(effect.Code, effect.Values, effect.Resource))
                {
                    return false;
                }
            }

            if (layer.MotionSamples is { } samples)
            {
                Int(samples.Count);
                foreach (LayerTransform sample in samples)
                {
                    Transform(sample);
                }
            }
            else
            {
                Int(-1);
            }

            TextMotion motion = layer.TextMotion;
            Int((int)motion.Phase);
            Double(motion.Progress);
            Int(motion.Word);
            Double(motion.WordProgress);
            if (layer.Transition is not { } transition)
            {
                Int(0);
                return true;
            }

            Int(1);
            if (!Effect(transition.Code, transition.Values, transition.Resource))
            {
                return false;
            }

            Float(transition.Progress);
            Bool(transition.Shutter is not null);
            if (transition.Shutter is { } shutter)
            {
                Float(shutter.From);
                Float(shutter.To);
            }

            return Layer(transition.Incoming);
        }

        private bool Effect(int code, EffectValues values, ResourceKey? resource)
        {
            Int(code);
            for (int i = 0; i < EffectDefinition.MaxParameters; i++)
            {
                Float(values[i]);
            }

            Bool(resource is not null);
            if (resource is { } key)
            {
                if (sources.Resource(key) is not { } identity)
                {
                    return false;
                }

                Text(key.Reference);
                Text(identity);
            }

            return true;
        }

        private void Transform(LayerTransform t)
        {
            Double(t.X);
            Double(t.Y);
            Double(t.Scale);
            Double(t.RotationDegrees);
            Double(t.Opacity);
            Double(t.HdrReferenceNits);
            Double(t.HdrExpandNits);
            Bool(t.Stabilization is not null);
            if (t.Stabilization is { } s)
            {
                Double(s.A);
                Double(s.B);
                Double(s.X);
                Double(s.Y);
            }

            Bool(t.Crop is not null);
            if (t.Crop is { } c)
            {
                Double(c.Left);
                Double(c.Top);
                Double(c.Right);
                Double(c.Bottom);
            }

            Bool(t.Shape is not null);
            if (t.Shape is { } shape)
            {
                Double(shape.AnchorX);
                Double(shape.AnchorY);
                Double(shape.ScaleX);
                Double(shape.ScaleY);
                Double(shape.SkewX);
                Double(shape.SkewY);
                Bool(shape.FlipX);
                Bool(shape.FlipY);
            }

            Bool(t.Pin is not null);
            if (t.Pin is { } p)
            {
                Double(p.TopLeftX);
                Double(p.TopLeftY);
                Double(p.TopRightX);
                Double(p.TopRightY);
                Double(p.BottomRightX);
                Double(p.BottomRightY);
                Double(p.BottomLeftX);
                Double(p.BottomLeftY);
            }
        }
    }
}
