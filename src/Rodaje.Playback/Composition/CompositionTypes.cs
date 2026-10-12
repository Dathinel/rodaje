using System.Runtime.CompilerServices;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.Playback.Composition;

/// <summary>Transformación de una capa ya evaluada para un instante. Ver <c>BuiltInEffects.Transform</c>.</summary>
public readonly record struct LayerTransform(double X, double Y, double Scale, double RotationDegrees, double Opacity)
{
    public static readonly LayerTransform Identity = new(0, 0, 1, 0, 1);

    /// <summary>Corrección de «Estabilizar» del cuadro, que va antes de esta transformación; nula sin estabilizar.</summary>
    public LayerStabilization? Stabilization { get; init; }

    /// <summary>Recorte de «Recortar» (<c>Rodaje.Core.Effects.CropGeometry</c>): lo que queda se ajusta al cuadro como la imagen entera.</summary>
    public Rodaje.Core.Effects.CropRect? Crop { get; init; }
}

/// <summary>
/// Corrección de «Estabilizar» de un cuadro: una similitud sobre el centro de la imagen ajustada al cuadro, medida en
/// altos de esa imagen y con y hacia abajo: <c>x' = A·x − B·y + X</c>, <c>y' = B·x + A·y + Y</c> (B positivo gira en
/// sentido horario). Lleva el recorte automático incluido.
/// </summary>
public readonly record struct LayerStabilization(double A, double B, double X, double Y)
{
    public double Scale => Math.Sqrt((A * A) + (B * B));

    public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;
}

/// <summary>Las correcciones de «Estabilizar» de un tramo de un medio, una por cuadro desde <see cref="FirstFrame"/>.</summary>
public sealed class StabilizationTrack
{
    private readonly LayerStabilization[] _frames;

    public StabilizationTrack(long firstFrame, IEnumerable<LayerStabilization> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        FirstFrame = firstFrame;
        _frames = [.. frames];
        if (_frames.Length == 0)
        {
            throw new ArgumentException("Hace falta al menos un cuadro.", nameof(frames));
        }
    }

    public long FirstFrame { get; }

    public int Count => _frames.Length;

    /// <summary>La corrección del cuadro <paramref name="frameIndex"/> del medio; fuera del tramo analizado, la del borde.</summary>
    public LayerStabilization At(long frameIndex) => _frames[(int)Math.Clamp(frameIndex - FirstFrame, 0, _frames.Length - 1)];
}

/// <summary>
/// Una capa lista para componer: el cuadro decodificado (su payload es la textura), o el descriptor de una capa generada
/// (<see cref="Generated"/>), y cómo dibujarlo. La capa es dueña de una referencia del cuadro y la libera al desecharse.
/// </summary>
public sealed class LayerFrame : IDisposable
{
    public LayerFrame(int trackIndex, string clipId, DecodedFrame frame, LayerTransform transform)
        : this(trackIndex, clipId, frame, transform, new Rational(1, 1), 0)
    {
    }

    public LayerFrame(int trackIndex, string clipId, DecodedFrame frame, LayerTransform transform, Rational sampleAspectRatio, int rotation)
    {
        ArgumentNullException.ThrowIfNull(clipId);
        ArgumentNullException.ThrowIfNull(frame);
        if (rotation is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "La rotación debe ser 0, 90, 180 o 270 grados.");
        }

        TrackIndex = trackIndex;
        ClipId = clipId;
        Frame = frame;
        Transform = transform;
        SampleAspectRatio = sampleAspectRatio;
        Rotation = rotation;
    }

    /// <summary>
    /// Una capa generada (un título): lleva el descriptor en <see cref="Generated"/> y no un cuadro decodificado. Su
    /// <see cref="Frame"/> no tiene textura; lo dibuja quien rasteriza el descriptor.
    /// </summary>
    public LayerFrame(int trackIndex, string clipId, GeneratedContent content, LayerTransform transform)
        : this(trackIndex, clipId, (content ?? throw new ArgumentNullException(nameof(content))).Retain(), transform, new Rational(1, 1), 0)
    {
        Generated = content;
    }

    /// <summary>
    /// Una capa compuesta (un clip compuesto con efectos, transformación o transición propios): sus capas de abajo arriba se
    /// componen en un intermedio transparente del tamaño de la secuencia y ese intermedio se dibuja como una capa más. Es
    /// dueña de sus capas y las libera al desecharse.
    /// </summary>
    public LayerFrame(int trackIndex, string clipId, IReadOnlyList<LayerFrame> nested, LayerTransform transform)
        : this(trackIndex, clipId, new DecodedFrame(0, Time.Zero, Time.FromTicks(1), null, 0), transform, new Rational(1, 1), 0)
    {
        ArgumentNullException.ThrowIfNull(nested);
        Nested = nested;
    }

    /// <summary>Las capas de una capa compuesta, de abajo arriba; nulo en las demás.</summary>
    public IReadOnlyList<LayerFrame>? Nested { get; }

    /// <summary>El descriptor de una capa generada; nulo en una de video.</summary>
    public GeneratedContent? Generated { get; }

    /// <summary>Índice de la pista de video; 0 es la capa de más abajo.</summary>
    public int TrackIndex { get; }

    public string ClipId { get; }

    public DecodedFrame Frame { get; }

    public LayerTransform Transform { get; }

    /// <summary>Forma del píxel del origen (ancho / alto); 1:1 si los píxeles son cuadrados.</summary>
    public Rational SampleAspectRatio { get; }

    /// <summary>Giro del origen en grados, sentido horario: 0, 90, 180 o 270.</summary>
    public int Rotation { get; }

    /// <summary>
    /// Tamaño en pantalla con que se ajusta la capa al cuadro, si no es el de la imagen: un proxy de menor resolución se
    /// coloca con el del original. 0 si se usa el de la imagen con <see cref="SampleAspectRatio"/> y <see cref="Rotation"/>.
    /// </summary>
    public int DisplayWidth { get; init; }

    public int DisplayHeight { get; init; }

    /// <summary>La imagen sale de un proxy de vista previa y no del original.</summary>
    public bool IsProxy { get; init; }

    /// <summary>
    /// Efectos de imagen ya evaluados (etapas <see cref="EffectStage.PerPixel"/> y <see cref="EffectStage.Spatial"/>), en el
    /// orden de la pila del clip. La transformación va en <see cref="Transform"/> y la mezcla en <see cref="Blend"/>.
    /// </summary>
    public IReadOnlyList<LayerEffect> Effects { get; init; } = [];

    public BlendMode Blend { get; init; } = BlendMode.Normal;

    /// <summary>Si la capa está en una transición: esta capa es el clip que sale y la transición trae el que entra.</summary>
    public LayerTransition? Transition { get; init; }

    /// <summary>Sin efectos, mezcla normal ni transición: el camino rápido de siempre.</summary>
    public bool IsPlain => Effects.Count == 0 && Blend == BlendMode.Normal && Transition is null && Nested is null;

    public void Dispose()
    {
        Frame.Dispose();
        if (Nested is { } nested)
        {
            foreach (LayerFrame layer in nested)
            {
                layer.Dispose();
            }
        }

        Transition?.Incoming.Dispose();
    }
}

/// <summary>
/// Valores de los parámetros de un efecto para un instante, en el orden del registro
/// (<see cref="ParameterDefinition.Slot"/>). Va por valor: armar una capa no reserva memoria.
/// </summary>
[InlineArray(EffectDefinition.MaxParameters)]
public struct EffectValues
{
    private float _element0;
}

/// <summary>Un efecto evaluado: su código del registro, sus valores y el recurso que usa (por ejemplo la LUT), si alguno.</summary>
/// <param name="Code">Código de <see cref="EffectRegistry"/>.</param>
/// <param name="Values">Valores en el orden del registro.</param>
/// <param name="Resource">Recurso de <see cref="IEffectResources"/>; nulo si no usa o si todavía no está listo.</param>
public readonly record struct LayerEffect(int Code, EffectValues Values, ResourceKey? Resource);

/// <summary>Identifica un recurso de contenido, normalmente la referencia <c>paquete@versión/elemento</c>.</summary>
public readonly record struct ResourceKey(string Reference);

/// <summary>Los 13 modos de mezcla, en el orden de <see cref="EffectRegistry.BlendModes"/>.</summary>
/// <remarks>Normal, suma, multiplicar y trama van por estado fijo; el resto por shader, con fórmulas W3C en gamma.</remarks>
public enum BlendMode
{
    Normal,
    Add,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    HardLight,
    Darken,
    Lighten,
    Difference,
    Exclusion,
    ColorDodge,
    ColorBurn,
}

/// <summary>
/// Una transición en curso sobre la capa: <see cref="Code"/> es la mezcla del registro (una de paquete llega ya resuelta
/// a la suya, con sus curvas aplicadas a las transformaciones de cada lado).
/// </summary>
/// <param name="Code">Código de la mezcla en <see cref="EffectRegistry"/>.</param>
/// <param name="Progress">De 0 (solo el clip que sale) a 1 (solo el que entra).</param>
/// <param name="Values">Parámetros de la transición en el orden del registro.</param>
/// <param name="Incoming">El clip que entra, con su transformación y sus efectos; la capa de afuera lo libera.</param>
public readonly record struct LayerTransition(int Code, float Progress, EffectValues Values, LayerFrame Incoming);

/// <summary>Todo lo que se ve en un instante de la secuencia, de abajo hacia arriba.</summary>
public sealed class CompositionFrame : IDisposable
{
    public CompositionFrame(Time timelineTime, int width, int height, IReadOnlyList<LayerFrame> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        TimelineTime = timelineTime;
        Width = width;
        Height = height;
        Layers = layers;
    }

    public Time TimelineTime { get; }

    /// <summary>Tamaño de la secuencia en píxeles.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Capas ordenadas de abajo hacia arriba. Vacía en un hueco: se ve negro.</summary>
    public IReadOnlyList<LayerFrame> Layers { get; }

    public void Dispose()
    {
        foreach (LayerFrame layer in Layers)
        {
            layer.Dispose();
        }
    }
}
