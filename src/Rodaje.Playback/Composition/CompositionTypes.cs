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
    {
        ArgumentNullException.ThrowIfNull(clipId);
        ArgumentNullException.ThrowIfNull(frame);
        TrackIndex = trackIndex;
        ClipId = clipId;
        Frame = frame;
        Transform = transform;
    }

    /// <summary>Índice de la pista de video; 0 es la capa de más abajo.</summary>
    public int TrackIndex { get; }

    public string ClipId { get; }

    public DecodedFrame Frame { get; }

    public LayerTransform Transform { get; }

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
