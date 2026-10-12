namespace Rodaje.Module.Subtitles;

/// <summary>
/// Parte el audio en tramos de hasta <see cref="MaxSeconds"/> cortando en el silencio más hondo. Whisper.net 1.9.1 con
/// DTW deja de devolver segmentos a partir de la segunda o tercera ventana de 30 s de una misma llamada (sin error ni
/// aviso, comprobado el 11-oct en GPU y CPU); con un tramo por llamada cada ventana empieza de cero y no pasa.
/// </summary>
internal static class AudioChunks
{
    public const double MaxSeconds = 28;
    public const double MinSeconds = 15;

    public static IReadOnlyList<(int Start, int Length)> Split(ReadOnlySpan<float> samples, int rate)
    {
        int window = rate / 50;
        int max = (int)(MaxSeconds * rate);
        int min = (int)(MinSeconds * rate);
        List<(int, int)> chunks = [];
        int start = 0;
        while (samples.Length - start > max)
        {
            int best = start + max;
            double quietest = double.MaxValue;
            for (int at = start + min; at + window <= start + max; at += window)
            {
                double energy = 0;
                for (int i = at; i < at + window; i++)
                {
                    energy += samples[i] * samples[i];
                }

                // A igual silencio, el corte más tardío: tramos más largos, menos llamadas.
                if (energy <= quietest)
                {
                    (quietest, best) = (energy, at + (window / 2));
                }
            }

            chunks.Add((start, best - start));
            start = best;
        }

        if (samples.Length > start)
        {
            chunks.Add((start, samples.Length - start));
        }

        return chunks;
    }
}
