using Rodaje.Core.Timing;
using Rodaje.Media.Video;

namespace Rodaje.App.Services.Media;

/// <summary>
/// Miniaturas y formas de onda de los medios, para la línea de tiempo y la biblioteca. Todas las llamadas se hacen desde
/// el hilo de la interfaz y nunca lo bloquean: devuelven lo que ya está en memoria y piden lo que falta a un hilo de
/// trabajo; cuando llega, se avisa con <see cref="Changed"/> (también en el hilo de la interfaz) y se vuelve a pedir.
/// </summary>
/// <remarks>Los tiempos son del origen del medio (como <c>Clip.SourceIn</c> y <c>Clip.SourceOut</c>).</remarks>
public interface IClipVisuals
{
    /// <summary>Llegaron miniaturas o picos de un medio: lo que lo muestre debe volver a pedirlos.</summary>
    event EventHandler<ClipVisualsChangedEventArgs>? Changed;

    /// <summary>
    /// Una tira de <paramref name="count"/> miniaturas repartidas en <c>[sourceStart, sourceEnd)</c>, de al menos
    /// <paramref name="height"/> píxeles de alto. Las que faltan vienen en <c>null</c> y se piden.
    /// </summary>
    Filmstrip GetFilmstrip(string mediaId, Time sourceStart, Time sourceEnd, int count, int height);

    /// <summary>
    /// Picos mínimo y máximo de <paramref name="buckets"/> tramos iguales de <c>[sourceStart, sourceEnd)</c>, entre −1 y 1.
    /// Mientras se calcula, lo que todavía no se leyó vale 0 y <see cref="WaveformSlice.IsComplete"/> es <c>false</c>.
    /// </summary>
    WaveformSlice GetWaveform(string mediaId, Time sourceStart, Time sourceEnd, int buckets);

    /// <summary>Una miniatura suelta (por ejemplo, la portada en la biblioteca), o <c>null</c> mientras se hace.</summary>
    BgraImage? GetThumbnail(string mediaId, Time sourceTime, int height);

    /// <summary>
    /// Para quien pide miniaturas una a una (la tira de la línea de tiempo): ajusta el instante a la rejilla de
    /// <paramref name="spacing"/> y la pide; mientras no esté, devuelve la más cercana ya hecha de ese archivo y alto.
    /// </summary>
    BgraImage? GetNearestThumbnail(string mediaId, Time sourceTime, Time spacing, int height);
}

public enum ClipVisualKind
{
    Thumbnails,
    Waveform,
}

/// <summary>Llegó algo nuevo para el medio <see cref="MediaId"/> (se avisa una vez por cada medio que use el mismo archivo).</summary>
public sealed class ClipVisualsChangedEventArgs(string mediaId, ClipVisualKind kind) : EventArgs
{
    public string MediaId { get; } = mediaId;

    public ClipVisualKind Kind { get; } = kind;
}

/// <summary>Miniaturas de una tira: <see cref="Frames"/>[i] es la del instante <see cref="Times"/>[i], o <c>null</c> si falta.</summary>
public sealed record Filmstrip(IReadOnlyList<BgraImage?> Frames, IReadOnlyList<Time> Times, bool IsComplete)
{
    public static readonly Filmstrip Empty = new([], [], true);
}

/// <summary>Picos de una forma de onda, por tramo.</summary>
public sealed record WaveformSlice(float[] Min, float[] Max, bool IsComplete, bool HasAudio)
{
    public static readonly WaveformSlice None = new([], [], true, false);

    public int Buckets => Min.Length;
}
