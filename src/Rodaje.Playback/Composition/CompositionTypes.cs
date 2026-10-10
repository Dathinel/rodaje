using Rodaje.Core.Timing;

namespace Rodaje.Playback.Composition;

/// <summary>Transformación de una capa ya evaluada para un instante. Ver <c>BuiltInEffects.Transform</c>.</summary>
public readonly record struct LayerTransform(double X, double Y, double Scale, double RotationDegrees, double Opacity)
{
    public static readonly LayerTransform Identity = new(0, 0, 1, 0, 1);
}

/// <summary>
/// Una capa lista para componer: el cuadro decodificado (su payload es la textura) y cómo dibujarlo.
/// La capa es dueña de una referencia del cuadro y la libera al desecharse.
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

    public void Dispose() => Frame.Dispose();
}

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
