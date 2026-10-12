namespace Rodaje.Module.Vision;

/// <summary>Detecciones de un cuadro analizado, en segundos del clip.</summary>
internal sealed record ReframeFrame(double Time, IReadOnlyList<Box> Boxes);

/// <summary>Un punto de la trayectoria: centro del recorte normalizado al cuadro fuente y acercamiento (1 = recorte máximo).</summary>
internal readonly record struct Keyframe(double Time, double X, double Y, double Scale);

internal sealed record ReframeResult(IReadOnlyList<Keyframe> Keyframes, double Inside, int Tracks);

/// <summary>
/// Reencuadre automático, sin modelo. (1) Seguimiento: asociación por IoU contra la predicción de un Kalman de velocidad
/// constante; el sujeto principal es la pista con más área·confianza acumulada (grande y estable). (2) Para cada cuadro, el
/// centro del recorte puede moverse en un «tubo» alrededor del sujeto sin que se salga; el ancho del tubo crece con el
/// suavizado. (3) La trayectoria es la cuerda tensa por el tubo (camino más corto, algoritmo del embudo): quieta mientras el
/// sujeto cabe, paneos lineales cuando no; los quiebres son los keyframes. Cada eje se resuelve aparte y se unen los quiebres.
/// </summary>
internal static class Reframer
{
    private const double TrackTimeout = 1.0;
    private const double MinIou = 0.1;

    public static ReframeResult Run(double aspect, int sourceWidth, int sourceHeight, IReadOnlyList<ReframeFrame> frames, double smoothing)
    {
        double w = sourceWidth, h = sourceHeight;
        (double cropW, double cropH) = w / h > aspect ? (aspect * h, h) : (w, w / aspect);
        smoothing = Math.Clamp(smoothing, 0, 1);
        var ordered = frames.OrderBy(f => f.Time).ToList();
        List<Track> tracks = BuildTracks(ordered);
        Track? main = tracks.MaxBy(t => t.Score);
        if (main is null)
        {
            return new ReframeResult([new Keyframe(ordered.Count > 0 ? ordered[0].Time : 0, 0.5, 0.5, 1)], 1, 0);
        }

        int n = ordered.Count;
        double[] times = [.. ordered.Select(f => f.Time)];
        Box[] subject = Fill(main, n);
        double tube = 0.2 + (0.6 * smoothing);
        double[] pathX = Path(times, subject, b => (b.X + (b.W / 2)) * w, b => b.W * w, cropW, w, tube, out List<int> breaksX);
        double[] pathY = Path(times, subject, b => (b.Y + (b.H / 2)) * h, b => b.H * h, cropH, h, tube, out List<int> breaksY);

        var keyframes = breaksX.Union(breaksY).Order()
            .Select(i => new Keyframe(times[i], pathX[i] / w, pathY[i] / h, 1)).ToList();
        for (int k = keyframes.Count - 2; k >= 1; k--)
        {
            if (Collinear(keyframes[k - 1], keyframes[k], keyframes[k + 1]))
            {
                keyframes.RemoveAt(k);
            }
        }

        if (keyframes.Count == 2 && Math.Abs(keyframes[0].X - keyframes[1].X) < 1e-9 && Math.Abs(keyframes[0].Y - keyframes[1].Y) < 1e-9)
        {
            keyframes.RemoveAt(1);
        }

        int measured = 0, inside = 0;
        foreach ((int index, Box box) in main.Measured)
        {
            measured++;
            double left = (pathX[index] - (cropW / 2)) / w, top = (pathY[index] - (cropH / 2)) / h;
            const double tolerance = 1e-3;
            if (box.X >= left - tolerance && box.X + box.W <= left + (cropW / w) + tolerance
                && box.Y >= top - tolerance && box.Y + box.H <= top + (cropH / h) + tolerance)
            {
                inside++;
            }
        }

        return new ReframeResult(keyframes, measured == 0 ? 1 : (double)inside / measured, tracks.Count);
    }

    private static bool Collinear(Keyframe a, Keyframe b, Keyframe c)
    {
        double span = c.Time - a.Time;
        if (span <= 0)
        {
            return true;
        }

        double f = (b.Time - a.Time) / span;
        return Math.Abs(a.X + ((c.X - a.X) * f) - b.X) < 1e-4 && Math.Abs(a.Y + ((c.Y - a.Y) * f) - b.Y) < 1e-4;
    }

    private static List<Track> BuildTracks(List<ReframeFrame> frames)
    {
        var tracks = new List<Track>();
        for (int i = 0; i < frames.Count; i++)
        {
            double t = frames[i].Time;
            var alive = tracks.Where(k => t - k.LastSeen <= TrackTimeout).ToList();
            var candidates = new List<(double Score, Track Track, int Box)>();
            for (int b = 0; b < frames[i].Boxes.Count; b++)
            {
                Box box = frames[i].Boxes[b];
                foreach (Track track in alive)
                {
                    Box predicted = track.Predict(t);
                    double iou = Box.Iou(predicted, box);
                    double distance = Math.Sqrt(Math.Pow(Cx(predicted) - Cx(box), 2) + Math.Pow(Cy(predicted) - Cy(box), 2));
                    double reach = 0.75 * Math.Max(Math.Max(predicted.W, predicted.H), Math.Max(box.W, box.H));
                    if (iou >= MinIou || distance < reach)
                    {
                        candidates.Add((iou + (1 - (distance / Math.Max(reach, 1e-6))), track, b));
                    }
                }
            }

            var usedTracks = new HashSet<Track>();
            var usedBoxes = new HashSet<int>();
            foreach ((_, Track track, int b) in candidates.OrderByDescending(c => c.Score))
            {
                if (!usedTracks.Contains(track) && !usedBoxes.Contains(b))
                {
                    usedTracks.Add(track);
                    usedBoxes.Add(b);
                    track.Update(i, t, frames[i].Boxes[b]);
                }
            }

            for (int b = 0; b < frames[i].Boxes.Count; b++)
            {
                if (!usedBoxes.Contains(b))
                {
                    var track = new Track();
                    track.Update(i, t, frames[i].Boxes[b]);
                    tracks.Add(track);
                }
            }
        }

        return tracks;
    }

    private static double Cx(Box b) => b.X + (b.W / 2);

    private static double Cy(Box b) => b.Y + (b.H / 2);

    /// <summary>La caja del sujeto en cada cuadro: la medida, interpolada en los huecos y sostenida antes y después.</summary>
    private static Box[] Fill(Track track, int count)
    {
        var result = new Box[count];
        var measured = track.Measured;
        for (int k = 0; k < measured.Count; k++)
        {
            (int index, Box box) = measured[k];
            result[index] = box;
            if (k + 1 < measured.Count)
            {
                (int next, Box nextBox) = measured[k + 1];
                for (int j = index + 1; j < next; j++)
                {
                    float f = (float)(j - index) / (next - index);
                    result[j] = new Box(box.X + ((nextBox.X - box.X) * f), box.Y + ((nextBox.Y - box.Y) * f),
                        box.W + ((nextBox.W - box.W) * f), box.H + ((nextBox.H - box.H) * f), 0);
                }
            }
        }

        for (int j = 0; j < measured[0].Index; j++)
        {
            result[j] = measured[0].Box;
        }

        for (int j = measured[^1].Index + 1; j < count; j++)
        {
            result[j] = measured[^1].Box;
        }

        return result;
    }

    /// <summary>
    /// Centro del recorte en un eje para cada cuadro (en píxeles), por el camino más corto entre las «puertas»
    /// [lo, hi] de cada cuadro. <paramref name="breaks"/> recibe los índices de los quiebres (incluye el primero y el último).
    /// </summary>
    private static double[] Path(double[] times, Box[] subject, Func<Box, double> center, Func<Box, double> size, double crop, double source,
        double tube, out List<int> breaks)
    {
        int n = times.Length;
        double min = crop / 2, max = source - (crop / 2);
        var lo = new double[n];
        var hi = new double[n];
        var looseLo = new double[n];
        var looseHi = new double[n];
        double loose = tube + (0.4 * (1 - tube));
        for (int i = 0; i < n; i++)
        {
            double slack = Math.Max(0, (crop - size(subject[i])) / 2);
            double c = center(subject[i]);
            lo[i] = Math.Clamp(c - (slack * tube), min, max);
            hi[i] = Math.Clamp(c + (slack * tube), min, max);
            looseLo[i] = Math.Clamp(c - (slack * loose), min, max);
            looseHi[i] = Math.Clamp(c + (slack * loose), min, max);
        }

        // Extremos libres: donde más dura la cámara quieta desde cada punta.
        lo[0] = hi[0] = Rest(lo, hi, 0, 1);
        lo[n - 1] = hi[n - 1] = Rest(lo, hi, n - 1, -1);

        var vertices = new List<(int Index, double Value)> { (0, lo[0]) };
        int apex = 0;
        double value = lo[0];
        while (apex < n - 1)
        {
            double slopeLo = double.NegativeInfinity, slopeHi = double.PositiveInfinity;
            int indexLo = apex, indexHi = apex;
            int next = -1;
            double nextValue = 0;
            for (int j = apex + 1; j < n; j++)
            {
                double dt = Math.Max(times[j] - times[apex], 1e-9);
                double a = (lo[j] - value) / dt, b = (hi[j] - value) / dt;
                if (a > slopeHi)
                {
                    next = indexHi;
                    nextValue = hi[indexHi];
                    break;
                }

                if (b < slopeLo)
                {
                    next = indexLo;
                    nextValue = lo[indexLo];
                    break;
                }

                if (a > slopeLo)
                {
                    (slopeLo, indexLo) = (a, j);
                }

                if (b < slopeHi)
                {
                    (slopeHi, indexHi) = (b, j);
                }
            }

            if (next < 0)
            {
                next = n - 1;
                nextValue = lo[n - 1];
            }

            vertices.Add((next, nextValue));
            (apex, value) = (next, nextValue);
        }

        // La cuerda tensa toca bordes ruidosos y deja quiebres mínimos: se quitan los que, sin ellos, el tramo recto sigue
        // dentro de un tubo algo más ancho (que todavía deja al sujeto dentro del recorte).
        bool Fits((int Index, double Value) a, (int Index, double Value) b)
        {
            double span = times[b.Index] - times[a.Index];
            for (int i = a.Index; i <= b.Index; i++)
            {
                double v = span <= 0 ? b.Value : a.Value + ((b.Value - a.Value) * (times[i] - times[a.Index]) / span);
                if (v < looseLo[i] - 1e-9 || v > looseHi[i] + 1e-9)
                {
                    return false;
                }
            }

            return true;
        }

        for (bool changed = true; changed;)
        {
            changed = false;
            for (int k = 1; k + 1 < vertices.Count; k++)
            {
                if (Fits(vertices[k - 1], vertices[k + 1]))
                {
                    vertices.RemoveAt(k);
                    changed = true;
                }
            }
        }

        // Derivas casi imperceptibles (menos del 1 % del cuadro) se vuelven cámara quieta si cabe.
        for (int k = 0; k + 1 < vertices.Count; k++)
        {
            (int index, double v) = vertices[k + 1];
            (int, double) flat = (index, vertices[k].Value);
            if (Math.Abs(v - vertices[k].Value) < 0.01 * source && Fits(vertices[k], flat) && (k + 2 >= vertices.Count || Fits(flat, vertices[k + 2])))
            {
                vertices[k + 1] = flat;
            }
        }

        var path = new double[n];
        for (int v = 0; v + 1 < vertices.Count; v++)
        {
            (int i0, double v0) = vertices[v];
            (int i1, double v1) = vertices[v + 1];
            double span = times[i1] - times[i0];
            for (int i = i0; i <= i1; i++)
            {
                path[i] = span <= 0 ? v1 : v0 + ((v1 - v0) * (times[i] - times[i0]) / span);
            }
        }

        if (n == 1)
        {
            path[0] = lo[0];
        }

        breaks = [.. vertices.Select(v => v.Index).Distinct()];
        return path;
    }

    private static double Rest(double[] lo, double[] hi, int start, int step)
    {
        double low = lo[start], high = hi[start];
        for (int i = start + step; i >= 0 && i < lo.Length; i += step)
        {
            if (Math.Max(low, lo[i]) > Math.Min(high, hi[i]))
            {
                break;
            }

            (low, high) = (Math.Max(low, lo[i]), Math.Min(high, hi[i]));
        }

        return (low + high) / 2;
    }

    private sealed class Track
    {
        private readonly Kalman _x = new(), _y = new();
        private float _w, _h;

        public List<(int Index, Box Box)> Measured { get; } = [];

        public double LastSeen { get; private set; }

        public double Score { get; private set; }

        public Box Predict(double time)
        {
            float cx = (float)_x.Predict(time), cy = (float)_y.Predict(time);
            return new Box(cx - (_w / 2), cy - (_h / 2), _w, _h, 0);
        }

        public void Update(int index, double time, Box box)
        {
            _x.Update(time, Cx(box));
            _y.Update(time, Cy(box));
            (_w, _h) = Measured.Count == 0 ? (box.W, box.H) : ((0.7f * _w) + (0.3f * box.W), (0.7f * _h) + (0.3f * box.H));
            Measured.Add((index, box));
            LastSeen = time;
            Score += box.Area * Math.Max(box.P, 0.1);
        }
    }

    /// <summary>Kalman de posición y velocidad en un eje (coordenadas normalizadas, tiempo en segundos).</summary>
    private sealed class Kalman
    {
        private const double ProcessNoise = 0.5;
        private const double MeasurementNoise = 0.0004;
        private double _p, _v, _time;
        private double _p00 = 1, _p01, _p11 = 1;
        private bool _started;

        public double Predict(double time) => _p + (_v * Math.Clamp(time - _time, 0, TrackTimeout));

        public void Update(double time, double measurement)
        {
            if (!_started)
            {
                (_p, _v, _time, _started) = (measurement, 0, time, true);
                (_p00, _p01, _p11) = (MeasurementNoise, 0, 0.25);
                return;
            }

            double dt = Math.Clamp(time - _time, 0, TrackTimeout);
            _time = time;
            _p += _v * dt;
            double q = ProcessNoise * dt;
            double p00 = _p00 + (2 * dt * _p01) + (dt * dt * _p11) + (q * dt * dt / 4);
            double p01 = _p01 + (dt * _p11) + (q * dt / 2);
            double p11 = _p11 + q;
            double s = p00 + MeasurementNoise;
            double k0 = p00 / s, k1 = p01 / s;
            double residual = measurement - _p;
            _p += k0 * residual;
            _v += k1 * residual;
            (_p00, _p01, _p11) = ((1 - k0) * p00, (1 - k0) * p01, p11 - (k1 * p01));
        }
    }
}
