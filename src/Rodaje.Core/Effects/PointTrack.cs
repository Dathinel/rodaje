using System.Globalization;
using System.Numerics;
using System.Text;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Effects;

/// <summary>Qué sigue un seguimiento (<see cref="EffectRegistry.TrackKinds"/>).</summary>
public enum PointTrackKind
{
    /// <summary>Un punto: posición.</summary>
    Point,

    /// <summary>Dos puntos: posición, escala y giro.</summary>
    Two,

    /// <summary>Cuatro esquinas: perspectiva (para un anclaje de esquinas).</summary>
    Corners,

    /// <summary>Una región (cuatro esquinas de una caja que se mueve como un todo: posición, escala y giro).</summary>
    Region,
}

/// <summary>Un punto que se sigue: dónde está, el patrón que se busca y la zona donde se busca, en fracciones de la imagen.</summary>
public readonly record struct TrackPointSpec(Vector2 Position, Vector2 Pattern, Vector2 Search);

/// <summary>Un punto en un cuadro: posición en la imagen de la capa (0–1) y confianza (0 si se perdió y se estimó).</summary>
public readonly record struct TrackSample(Vector2 Position, float Confidence)
{
    /// <summary>Lo corrigió quien edita: confianza 1 y no lo toca el suavizado.</summary>
    public bool IsLost => Confidence <= 0;
}

/// <summary>
/// Los cuadros de un seguimiento (<see cref="Model.BuiltInEffects.TrackOptions.Samples"/>): <c>ticks@x,y,c;x,y,c|…</c> con los
/// instantes del origen en orden estricto, la misma cantidad de puntos en cada cuadro, x e y en fracciones de la imagen de la
/// capa y c la confianza de 0 a 1. Inmutable.
/// </summary>
public sealed class PointTrack
{
    /// <summary>Unos 25 minutos de un punto a 30 cuadros por segundo, o 6 de cuatro esquinas.</summary>
    public const int MaxTextLength = 4_194_304;

    public const int MaxPoints = 4;

    private readonly Time[] _times;
    private readonly TrackSample[][] _frames;

    public PointTrack(IReadOnlyList<Time> times, IReadOnlyList<TrackSample[]> frames)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(frames);
        if (times.Count != frames.Count || times.Count == 0)
        {
            throw new ArgumentException("Hace falta al menos un cuadro, con sus puntos.");
        }

        _times = [.. times];
        _frames = [.. frames];
        PointCount = _frames[0].Length;
        for (int i = 0; i < _times.Length; i++)
        {
            if ((i > 0 && _times[i] <= _times[i - 1]) || _frames[i].Length != PointCount || PointCount is < 1 or > MaxPoints)
            {
                throw new ArgumentException("Los cuadros van en orden, sin repetir instantes y con los mismos puntos.");
            }
        }
    }

    public int Count => _times.Length;

    public int PointCount { get; }

    public Time First => _times[0];

    public Time Last => _times[^1];

    public Time TimeAt(int index) => _times[index];

    public IReadOnlyList<TrackSample> FrameAt(int index) => _frames[index];

    public IEnumerable<(Time Time, TrackSample[] Points)> Frames() => _times.Select((t, i) => (t, _frames[i]));

    /// <summary>El índice del cuadro en <paramref name="time"/>; negativo si no hay (como <see cref="Array.BinarySearch(Array, object)"/>).</summary>
    public int IndexOf(Time time) => Array.BinarySearch(_times, time);

    /// <summary>
    /// Los puntos en <paramref name="time"/>: el cuadro exacto o la recta entre los dos vecinos; fuera del tramo, el del borde.
    /// </summary>
    public Vector2[] PositionsAt(Time time)
    {
        int index = IndexOf(time);
        if (index >= 0)
        {
            return [.. _frames[index].Select(s => s.Position)];
        }

        int next = ~index;
        if (next == 0)
        {
            return [.. _frames[0].Select(s => s.Position)];
        }

        if (next >= _times.Length)
        {
            return [.. _frames[^1].Select(s => s.Position)];
        }

        float t = (float)((double)(time - _times[next - 1]).Ticks / (_times[next] - _times[next - 1]).Ticks);
        return [.. _frames[next - 1].Select((s, i) => Vector2.Lerp(s.Position, _frames[next][i].Position, t))];
    }

    /// <summary>La menor confianza de los puntos en cada cuadro (para la gráfica).</summary>
    public float ConfidenceAt(int index) => _frames[index].Min(s => s.Confidence);

    /// <summary>
    /// Con los cuadros de <paramref name="update"/> en lugar de los de este en su tramo (de su primero a su último); el resto
    /// queda. Así re-seguir desde un cuadro corregido reemplaza solo lo que se volvió a seguir.
    /// </summary>
    public PointTrack Merge(PointTrack update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.PointCount != PointCount)
        {
            throw new ArgumentException("El seguimiento nuevo tiene otra cantidad de puntos.", nameof(update));
        }

        var times = new List<Time>();
        var frames = new List<TrackSample[]>();
        int j = 0;
        for (int i = 0; i < _times.Length && _times[i] < update.First; i++)
        {
            times.Add(_times[i]);
            frames.Add(_frames[i]);
            j = i + 1;
        }

        times.AddRange(update._times);
        frames.AddRange(update._frames);
        for (int i = j; i < _times.Length; i++)
        {
            if (_times[i] > update.Last)
            {
                times.Add(_times[i]);
                frames.Add(_frames[i]);
            }
        }

        return new PointTrack(times, frames);
    }

    /// <summary>Con el punto <paramref name="point"/> de un cuadro puesto a mano (confianza 1); si el cuadro no estaba, se agrega.</summary>
    public PointTrack WithPoint(Time time, int point, Vector2 position)
    {
        int index = IndexOf(time);
        TrackSample[] frame = index >= 0 ? [.. _frames[index]] : [.. PositionsAt(time).Select(p => new TrackSample(p, 1))];
        frame[point] = new TrackSample(position, 1);
        return Merge(new PointTrack([time], [frame]));
    }

    /// <summary>Solo los cuadros de <paramref name="from"/> a <paramref name="to"/> (incluidos); nulo si no queda ninguno.</summary>
    public PointTrack? Slice(Time from, Time to)
    {
        int a = Array.FindIndex(_times, t => t >= from);
        int b = Array.FindLastIndex(_times, t => t <= to);
        return a < 0 || b < a ? null : new PointTrack(_times[a..(b + 1)], _frames[a..(b + 1)]);
    }

    /// <summary>
    /// Los cuadros dudosos (confianza bajo <paramref name="threshold"/>) toman la recta entre los buenos de los lados, con
    /// la confianza que tenían. Los del borde, sin bueno de un lado, quedan.
    /// </summary>
    public PointTrack FillDoubtful(float threshold)
    {
        TrackSample[][] frames = [.. _frames.Select(f => (TrackSample[])f.Clone())];
        for (int p = 0; p < PointCount; p++)
        {
            int previous = -1;
            for (int i = 0; i < frames.Length; i++)
            {
                if (frames[i][p].Confidence < threshold)
                {
                    continue;
                }

                if (previous >= 0 && i - previous > 1)
                {
                    for (int k = previous + 1; k < i; k++)
                    {
                        float t = (float)((double)(_times[k] - _times[previous]).Ticks / (_times[i] - _times[previous]).Ticks);
                        frames[k][p] = frames[k][p] with { Position = Vector2.Lerp(frames[previous][p].Position, frames[i][p].Position, t) };
                    }
                }

                previous = i;
            }
        }

        return new PointTrack(_times, frames);
    }

    /// <summary>
    /// Suaviza la trayectoria de cada punto con una recta local por mínimos cuadrados (sin achatar el movimiento parejo)
    /// de <paramref name="radius"/> cuadros por lado, con peso gaussiano por distancia y la confianza; los dudosos pesan
    /// poco y los corregidos a mano no se mueven.
    /// </summary>
    public PointTrack Smooth(int radius, float threshold)
    {
        if (radius <= 0 || Count < 3)
        {
            return this;
        }

        TrackSample[][] frames = [.. _frames.Select(f => (TrackSample[])f.Clone())];
        double sigma = Math.Max(0.5, radius / 2.0);
        for (int p = 0; p < PointCount; p++)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                TrackSample sample = _frames[i][p];
                if (sample.Confidence >= 1)
                {
                    continue;
                }

                double sw = 0, st = 0, stt = 0, sx = 0, sy = 0, stx = 0, sty = 0;
                for (int k = Math.Max(0, i - radius); k <= Math.Min(frames.Length - 1, i + radius); k++)
                {
                    TrackSample other = _frames[k][p];
                    double t = k - i;
                    double w = Math.Exp(-0.5 * t * t / (sigma * sigma)) * (other.Confidence >= threshold ? 1 : 0.05);
                    sw += w;
                    st += w * t;
                    stt += w * t * t;
                    sx += w * other.Position.X;
                    sy += w * other.Position.Y;
                    stx += w * t * other.Position.X;
                    sty += w * t * other.Position.Y;
                }

                // Valor de la recta en t = 0: (Σw·Σtt·x − Σt·Σtx…) resuelto como intersección de la regresión.
                double det = (sw * stt) - (st * st);
                double x = det > 1e-9 ? ((stt * sx) - (st * stx)) / det : sx / sw;
                double y = det > 1e-9 ? ((stt * sy) - (st * sty)) / det : sy / sw;
                frames[i][p] = sample with { Position = new Vector2((float)x, (float)y) };
            }
        }

        return new PointTrack(_times, frames);
    }

    private sealed record Parsed(string Text, PointTrack Track);

    private static Parsed? s_last;

    /// <summary>
    /// Como <see cref="Parse"/>, pero recuerda el último texto (por referencia): el visor y la gráfica lo leen en cada
    /// cuadro mientras no cambia.
    /// </summary>
    public static PointTrack? ParseCached(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (s_last is { } last && ReferenceEquals(last.Text, text))
        {
            return last.Track;
        }

        PointTrack? track = Parse(text);
        if (track is not null)
        {
            s_last = new Parsed(text, track);
        }

        return track;
    }

    /// <summary>Lee el texto; nulo si está vacío o no sirve.</summary>
    public static PointTrack? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
        {
            return null;
        }

        var times = new List<Time>();
        var frames = new List<TrackSample[]>();
        ReadOnlySpan<char> span = text;
        int count = -1;
        Span<Range> numbers = stackalloc Range[4];
        foreach (Range part in span.Split('|'))
        {
            ReadOnlySpan<char> frame = span[part];
            int at = frame.IndexOf('@');
            if (at <= 0 || !long.TryParse(frame[..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                || (times.Count > 0 && ticks <= times[^1].Ticks))
            {
                return null;
            }

            var samples = new List<TrackSample>(4);
            ReadOnlySpan<char> body = frame[(at + 1)..];
            foreach (Range pointRange in body.Split(';'))
            {
                ReadOnlySpan<char> point = body[pointRange];
                if (point.Split(numbers, ',') != 3 || samples.Count == MaxPoints
                    || !float.TryParse(point[numbers[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(point[numbers[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    || !float.TryParse(point[numbers[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out float c)
                    || !float.IsFinite(x) || !float.IsFinite(y) || !(c >= 0 && c <= 1))
                {
                    return null;
                }

                samples.Add(new TrackSample(new Vector2(x, y), c));
            }

            if (samples.Count == 0 || (count >= 0 && samples.Count != count))
            {
                return null;
            }

            count = samples.Count;
            times.Add(Time.FromTicks(ticks));
            frames.Add([.. samples]);
        }

        return times.Count == 0 ? null : new PointTrack(times, frames);
    }

    public string Format()
    {
        var text = new StringBuilder(_times.Length * PointCount * 24);
        for (int i = 0; i < _times.Length; i++)
        {
            if (i > 0)
            {
                text.Append('|');
            }

            text.Append(_times[i].Ticks.ToString(CultureInfo.InvariantCulture)).Append('@');
            for (int p = 0; p < PointCount; p++)
            {
                TrackSample s = _frames[i][p];
                if (p > 0)
                {
                    text.Append(';');
                }

                // Seis decimales: una millonésima de la imagen es menos de 0,004 px en 4K.
                text.Append(s.Position.X.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                    .Append(s.Position.Y.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Math.Clamp(s.Confidence, 0, 1).ToString("0.##", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    /// <summary>Puntos de partida (<see cref="Model.BuiltInEffects.TrackOptions.Points"/>); vacía si el texto no sirve.</summary>
    public static TrackPointSpec[] ParsePoints(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var result = new List<TrackPointSpec>();
        ReadOnlySpan<char> span = text;
        Span<Range> numbers = stackalloc Range[7];
        Span<float> v = stackalloc float[6];
        foreach (Range part in span.Split(';'))
        {
            ReadOnlySpan<char> point = span[part];
            if (point.Split(numbers, ',') != 6 || result.Count == MaxPoints)
            {
                return [];
            }

            for (int k = 0; k < 6; k++)
            {
                if (!float.TryParse(point[numbers[k]], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || !float.IsFinite(v[k]))
                {
                    return [];
                }
            }

            result.Add(new TrackPointSpec(new Vector2(v[0], v[1]), new Vector2(Math.Abs(v[2]), Math.Abs(v[3])), new Vector2(Math.Abs(v[4]), Math.Abs(v[5]))));
        }

        return [.. result];
    }

    public static string FormatPoints(IEnumerable<TrackPointSpec> points) =>
        string.Join(';', points.Select(p => string.Join(',',
            new[] { p.Position.X, p.Position.Y, p.Pattern.X, p.Pattern.Y, p.Search.X, p.Search.Y }
                .Select(f => f.ToString("0.######", CultureInfo.InvariantCulture)))));

    /// <summary>Cuántos puntos lleva cada tipo.</summary>
    public static int PointsOf(PointTrackKind kind) => kind switch
    {
        PointTrackKind.Point => 1,
        PointTrackKind.Two => 2,
        _ => 4,
    };

    public static PointTrackKind ParseKind(string? text) => text switch
    {
        "two" => PointTrackKind.Two,
        "corners" => PointTrackKind.Corners,
        "region" => PointTrackKind.Region,
        _ => PointTrackKind.Point,
    };

    public static string KindName(PointTrackKind kind) => EffectRegistry.TrackKinds[(int)kind];
}
