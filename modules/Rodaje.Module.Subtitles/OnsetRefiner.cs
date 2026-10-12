namespace Rodaje.Module.Subtitles;

/// <summary>
/// Ajusta con la energía del audio el inicio de las palabras que vienen tras un silencio. El DTW de Whisper acierta
/// dentro de la frase pero llega tarde (0,5–1 s) a la primera palabra tras una pausa; el inicio por marcas de tiempo
/// llega temprano cuando la ventana empieza en silencio. Aquí se busca el arranque de la voz entre las dos.
/// </summary>
internal sealed class OnsetRefiner
{
    private const double Frame = 0.01;
    private const double MinPause = 0.2;
    private const int Sustain = 5;

    private readonly double[] _db;

    public OnsetRefiner(ReadOnlySpan<float> samples, int rate)
    {
        int window = (int)(rate * Frame);
        _db = new double[samples.Length / window];
        for (int f = 0; f < _db.Length; f++)
        {
            double sum = 0;
            foreach (float s in samples.Slice(f * window, window))
            {
                sum += s * s;
            }

            _db[f] = 10 * Math.Log10((sum / window) + 1e-12);
        }
    }

    public TranscriptSegment Refine(TranscriptSegment segment, double previousEnd)
    {
        if (segment.Words.Count == 0)
        {
            return segment;
        }

        double threshold = Threshold(Math.Max(previousEnd, segment.Start - 0.5), segment.End);
        List<TranscriptWord> words = [.. segment.Words];
        for (int i = 0; i < words.Count; i++)
        {
            double from = i == 0 ? Math.Max(previousEnd, segment.Start - 0.3) : words[i - 1].Start;
            double to = Math.Min(words[i].Start + 0.1, words[i].End);
            if (i > 0 && !HasPause(from, words[i].Start, threshold))
            {
                continue;
            }

            if (Onset(Math.Max(from, LastPauseEnd(from, to, threshold)), to, threshold) is { } onset && onset < words[i].End)
            {
                words[i] = words[i] with { Start = WordBuilder.Round(onset) };
                if (i > 0 && words[i - 1].End > onset)
                {
                    words[i - 1] = words[i - 1] with { End = WordBuilder.Round(Math.Max(words[i - 1].Start, onset)) };
                }
            }
        }

        double start = Math.Min(segment.Start, words[0].Start);
        return segment with { Start = WordBuilder.Round(start), Words = words };
    }

    /// <summary>Entre el ruido de fondo y el nivel de la voz del tramo, sin contar el silencio digital.</summary>
    private double Threshold(double from, double to)
    {
        double[] frames = [.. Frames(from, to).Select(f => _db[f]).Where(d => d > -90).Order()];
        if (frames.Length == 0)
        {
            return -60;
        }

        double floor = frames[frames.Length / 10];
        double voice = frames[frames.Length * 9 / 10];
        return floor + (0.3 * (voice - floor));
    }

    private bool HasPause(double from, double to, double threshold) => LastPauseEnd(from, to, threshold) > from;

    /// <summary>El final del último silencio de al menos <see cref="MinPause"/> en el tramo, o <paramref name="from"/>.</summary>
    private double LastPauseEnd(double from, double to, double threshold)
    {
        int run = 0;
        double end = from;
        foreach (int f in Frames(from, to))
        {
            run = _db[f] < threshold ? run + 1 : 0;
            if (run * Frame >= MinPause)
            {
                end = (f + 1) * Frame;
            }
        }

        return end;
    }

    /// <summary>El primer cuadro de 10 ms que abre <see cref="Sustain"/> seguidos por encima del umbral.</summary>
    private double? Onset(double from, double to, double threshold)
    {
        int run = 0;
        foreach (int f in Frames(from, to + (Sustain * Frame)))
        {
            run = _db[f] >= threshold ? run + 1 : 0;
            if (run == Sustain)
            {
                double at = (f - Sustain + 1) * Frame;
                return at <= to ? at : null;
            }
        }

        return null;
    }

    private IEnumerable<int> Frames(double from, double to)
    {
        int first = Math.Max(0, (int)Math.Floor(from / Frame));
        int last = Math.Min(_db.Length, (int)Math.Ceiling(to / Frame));
        for (int f = first; f < last; f++)
        {
            yield return f;
        }
    }
}
