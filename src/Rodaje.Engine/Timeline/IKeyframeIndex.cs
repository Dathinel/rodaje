namespace Rodaje.Engine.Timeline;

/// <summary>Keyframes de un archivo de video, en índices de cuadro. Inmutable y seguro entre hilos.</summary>
internal interface IKeyframeIndex
{
    /// <summary>El último keyframe en o antes de <paramref name="frame"/>; -1 si no hay.</summary>
    long AtOrBefore(long frame);

    /// <summary>El primer keyframe en o después de <paramref name="frame"/>; <see cref="long.MaxValue"/> si no hay.</summary>
    long AtOrAfter(long frame);
}

/// <summary>
/// Un <see cref="IClipVideo"/> que conoce sus keyframes. Con él, de 16× a 100× la reproducción muestra el keyframe
/// anterior al cuadro debido; sin él, todo es exacto.
/// </summary>
internal interface IKeyframeSource
{
    /// <summary>Nulo mientras no se conoce; se puede leer desde cualquier hilo.</summary>
    IKeyframeIndex? Keyframes { get; }
}

/// <summary>Índice sobre una lista ordenada de keyframes.</summary>
internal sealed class SortedKeyframes : IKeyframeIndex
{
    private readonly long[] _frames;

    public SortedKeyframes(IEnumerable<long> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = [.. frames.Distinct().Order()];
    }

    public long AtOrBefore(long frame)
    {
        int i = Array.BinarySearch(_frames, frame);
        i = i >= 0 ? i : ~i - 1;
        return i >= 0 ? _frames[i] : -1;
    }

    public long AtOrAfter(long frame)
    {
        int i = Array.BinarySearch(_frames, frame);
        i = i >= 0 ? i : ~i;
        return i < _frames.Length ? _frames[i] : long.MaxValue;
    }
}
