using Rodaje.Audio.Processing;
using Rodaje.Core.Timing;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Clip de audio en muestras de 48 kHz: suena en <c>[StartSample, EndSample)</c> de la línea de tiempo y su muestra
/// <c>s</c> es la <c>SourceInSample + (s - StartSample)</c> del origen. En una transición puede empezar antes de su
/// material (<see cref="SourceInSample"/> negativo): lo que no existe es silencio. Con <see cref="Retime"/> el origen de
/// cada muestra lo da su mapeo.
/// </summary>
/// <param name="Gain">Volumen y fundidos; nulo si suena tal cual.</param>
/// <param name="Retime">Velocidad del clip; nula, suena a 1× como siempre.</param>
/// <param name="Effects">Ecualizador y compresor del clip, antes del volumen y los fundidos; nulo sin ellos.</param>
internal sealed record AudioClipPlan(string ClipId, SourceKey Source, long StartSample, long EndSample, long SourceInSample, GainPlan? Gain = null,
    AudioRetime? Retime = null, AudioEffectsSettings? Effects = null)
{
    /// <summary>
    /// En un compuesto que se mezcla aparte, las pistas de su secuencia (ya en la línea de tiempo): se mezclan, pasan por
    /// <see cref="Effects"/> y suenan con <see cref="Gain"/>. Su <see cref="Source"/> no se usa.
    /// </summary>
    public AudioBusPlan? Bus { get; init; }

    /// <summary>Muestra del origen que suena en la muestra <paramref name="sample"/> de la línea de tiempo.</summary>
    public long SourceSampleAt(long sample) => Retime is { } retime ? retime.SourceSample(sample) : SourceInSample + (sample - StartSample);
}

/// <summary>
/// Velocidad de un clip de audio: su mapeo exacto y si mantiene el tono. Compara por valor, así un cambio de velocidad
/// o de «mantener tono» rehace la mezcla. Un congelado suena en silencio.
/// </summary>
/// <param name="Joined">
/// Si el clip y sus vecinos sin transición entre medias son trozos de uno dividido, el mapeo de todos juntos: el lector
/// sigue ese recorrido y no el de cada trozo, que fuera de él seguiría a la velocidad del borde.
/// </param>
internal sealed record AudioRetime(ClipTimeMap Map, bool PreservePitch, ClipTimeMap? Joined = null)
{
    /// <summary>El recorrido que lee <see cref="Retime.RetimedReader"/>: el de los trozos juntos, o el del clip.</summary>
    public ClipTimeMap Course => Joined ?? Map;

    /// <summary>Muestra del origen en la muestra <paramref name="sample"/> de la línea de tiempo; fuera del clip sigue a la velocidad del borde.</summary>
    public long SourceSample(long sample) =>
        Map.ExtendedSource(Time.FromSamples(sample, TimelinePlan.AudioSampleRate)).ToSamples(TimelinePlan.AudioSampleRate);
}

/// <summary>Las pistas de un compuesto de audio que se mezcla aparte. Compara por valor, como <see cref="AudioPlan.SoundsLike"/>.</summary>
internal sealed class AudioBusPlan(IReadOnlyList<AudioTrackPlan> tracks) : IEquatable<AudioBusPlan>
{
    public IReadOnlyList<AudioTrackPlan> Tracks { get; } = tracks;

    public bool Equals(AudioBusPlan? other) => other is not null && AudioPlan.SameTracks(Tracks, other.Tracks);

    public override bool Equals(object? obj) => Equals(obj as AudioBusPlan);

    public override int GetHashCode() => Tracks.Count;
}

/// <param name="Effects">Ecualizador y compresor de la pista, sobre la suma de sus clips; nulo sin ellos.</param>
internal sealed record AudioTrackPlan(string TrackId, IReadOnlyList<AudioClipPlan> Clips, AudioEffectsSettings? Effects = null);

/// <param name="MasterDb">Ganancia de toda la mezcla, antes del limitador (<c>Sequence.MasterGainDb</c>).</param>
internal sealed record AudioPlan(IReadOnlyList<AudioTrackPlan> Tracks, double MasterDb = 0)
{
    public static AudioPlan Empty { get; } = new([]);

    /// <summary>Mismas pistas con los mismos clips, en el mismo orden, con el mismo volumen y la misma velocidad: lo que suena no cambia.</summary>
    /// <summary>Algún clip o pista lleva ecualizador o compresor, que guardan estado entre lecturas.</summary>
    public bool HasEffects => AllTracks(Tracks).Any(t => t.Effects is not null || t.Clips.Any(c => c.Effects is not null));

    public bool SoundsLike(AudioPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return MasterDb.Equals(other.MasterDb) && SameTracks(Tracks, other.Tracks);
    }

    internal static bool SameTracks(IReadOnlyList<AudioTrackPlan> a, IReadOnlyList<AudioTrackPlan> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int t = 0; t < a.Count; t++)
        {
            if (a[t].TrackId != b[t].TrackId || !Equals(a[t].Effects, b[t].Effects) || !a[t].Clips.SequenceEqual(b[t].Clips))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Las pistas y, detrás de cada compuesto que se mezcla aparte, las de su secuencia (recursivo).</summary>
    public static IEnumerable<AudioTrackPlan> AllTracks(IReadOnlyList<AudioTrackPlan> tracks)
    {
        foreach (AudioTrackPlan track in tracks)
        {
            yield return track;
            foreach (AudioClipPlan clip in track.Clips)
            {
                if (clip.Bus is { } bus)
                {
                    foreach (AudioTrackPlan inner in AllTracks(bus.Tracks))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }
}

/// <summary>Un fundido de potencia constante en <c>[Start, Start + Length)</c> de la línea de tiempo, en muestras.</summary>
/// <param name="Out">De salida (cos) en lugar de entrada (sin).</param>
internal readonly record struct Fade(long Start, long Length, bool Out)
{
    public bool IsNone => Length <= 0;

    public long End => Start + Length;

    /// <summary>Entrada: 0 antes, sin(πt/2) durante y 1 después. Salida: 1 antes, cos(πt/2) durante y 0 después.</summary>
    public double Factor(long sample)
    {
        if (Length <= 0)
        {
            return 1;
        }

        double t = Math.Clamp((double)(sample - Start) / Length, 0, 1);
        return Out ? Math.Cos(t * Math.PI / 2) : Math.Sin(t * Math.PI / 2);
    }
}

/// <summary>
/// Volumen de un clip: su ganancia en dB (keyframes en muestras del origen), la de su pista (en muestras de la
/// secuencia), sus fundidos propios y los de las transiciones. Compara por valor, así un cambio de volumen rehace la mezcla.
/// </summary>
/// <param name="ClipDb">Ganancia del clip, en muestras del origen; nula si es 0 dB.</param>
/// <param name="TrackDb">Ganancia de la pista, en muestras de la secuencia; nula si es 0 dB.</param>
internal sealed record GainPlan(Curve? ClipDb, Curve? TrackDb, Fade FadeIn, Fade FadeOut, Fade CrossIn, Fade CrossOut)
{
    /// <summary>−60 dB o menos es silencio.</summary>
    public const double SilenceDb = Rodaje.Core.Effects.EffectRegistry.SilenceDb;

    public bool HasFades => !FadeIn.IsNone || !FadeOut.IsNone || !CrossIn.IsNone || !CrossOut.IsNone;

    /// <summary>Ganancia en dB en la muestra <paramref name="sample"/> de la secuencia.</summary>
    public double Db(long sample, long sourceSample) =>
        (ClipDb?.Evaluate(sourceSample) ?? 0) + (TrackDb?.Evaluate(sample) ?? 0);

    /// <summary>Producto de los fundidos en la muestra <paramref name="sample"/> de la secuencia.</summary>
    public double Fades(long sample) => FadeIn.Factor(sample) * FadeOut.Factor(sample) * CrossIn.Factor(sample) * CrossOut.Factor(sample);

    /// <summary>Si alguno de los fundidos cambia dentro de <c>[from, to)</c>.</summary>
    public bool FadesWithin(long from, long to) =>
        Within(FadeIn, from, to) || Within(FadeOut, from, to) || Within(CrossIn, from, to) || Within(CrossOut, from, to);

    public static double ToLinear(double db) => db <= SilenceDb ? 0 : Math.Pow(10, db / 20);

    private static bool Within(Fade fade, long from, long to) => !fade.IsNone && fade.Start < to && fade.End > from;
}
