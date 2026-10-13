using Rodaje.Audio.Processing;
using Rodaje.Core.Model;
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

    /// <summary>Keyframes del compresor (en muestras del origen, como el volumen); nulo si no tiene.</summary>
    public DynamicsAutomation? Automation { get; init; }

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

/// <param name="Effects">Inserciones de la pista, sobre la suma de sus clips; nulo sin ellas.</param>
internal sealed record AudioTrackPlan(string TrackId, IReadOnlyList<AudioClipPlan> Clips, AudioEffectsSettings? Effects = null)
{
    /// <summary>Keyframes del compresor de la pista, en muestras de la línea de tiempo; nulo si no tiene.</summary>
    public DynamicsAutomation? Automation { get; init; }

    /// <summary>
    /// Si otro compresor de su nivel la usa como sidechain, cuántas muestras por delante se mezcla (la mayor anticipación
    /// de quien la escucha); −1 si no es llave de nadie. La llave es lo que sale de su fader.
    /// </summary>
    public int KeyAhead { get; init; } = -1;

    /// <summary>Volumen, paneo y balance después de las inserciones; nulo si la deja tal cual.</summary>
    public FaderPlan? Fader { get; init; }

    /// <summary>Bus al que sale (solo en la secuencia principal); nulo, a la mezcla.</summary>
    public string? Output { get; init; }

    /// <summary>Envíos a buses que suenan (solo en la secuencia principal).</summary>
    public IReadOnlyList<AudioSendPlan> Sends { get; init; } = [];

    /// <summary>Su salida suena; falso si un solo de otra la calla (sigue sonando en sus envíos o como llave).</summary>
    public bool Direct { get; init; } = true;

    /// <summary>Muestras por delante que se mezcla para los buses con retraso a los que llega (su anticipación).</summary>
    public int BusAhead { get; init; }

    /// <summary>Cuánto por delante se mezcla: lo que piden su llave y sus buses; 0, al momento.</summary>
    public int Ahead => Math.Max(Math.Max(0, KeyAhead), BusAhead);

    public bool SameMix(AudioTrackPlan other) =>
        Equals(Fader, other.Fader) && Output == other.Output && Sends.SequenceEqual(other.Sends) && Direct == other.Direct && BusAhead == other.BusAhead;
}

/// <summary>
/// El fader de una pista o un bus: volumen en dB, paneo y balance (−100 a 100) en muestras de la línea de tiempo. Compara
/// por valor: un cambio rehace la mezcla.
/// </summary>
internal sealed record FaderPlan(Curve? Db, Curve? Pan, Curve? Balance)
{
    /// <summary>Ganancia en dB en <paramref name="sample"/> (0 sin curva).</summary>
    public double GainDb(long sample) => Db?.Evaluate(sample) ?? 0;

    /// <summary>Paneo de −1 a 1.</summary>
    public double PanAt(long sample) => (Pan?.Evaluate(sample) ?? 0) / 100;

    /// <summary>Balance de −1 a 1.</summary>
    public double BalanceAt(long sample) => (Balance?.Evaluate(sample) ?? 0) / 100;
}

/// <summary>Un envío a un bus: su nivel y si sale antes del fader y el paneo de la pista.</summary>
internal sealed record AudioSendPlan(string BusId, double GainDb, bool PreFader);

/// <summary>Un bus del mezclador que suena: sus inserciones y su fader. Lo que recibe se arma en la mezcla.</summary>
internal sealed record MixBusPlan(string BusId, AudioEffectsSettings? Effects, FaderPlan? Fader)
{
    /// <summary>Lo que retrasa su cadena: las pistas que llegan a él se mezclan por delante eso.</summary>
    public int Latency => Effects?.LatencyFrames(TimelinePlan.AudioSampleRate) ?? 0;
}

/// <summary>Umbral, relación y mezcla (en %) animados de un compresor; las posiciones las fija quien lo arma.</summary>
internal sealed record DynamicsAutomation(Curve Threshold, Curve Ratio, Curve Mix);

/// <param name="MasterDb">Ganancia de toda la mezcla, antes del limitador (<c>Sequence.MasterGainDb</c>).</param>
internal sealed record AudioPlan(IReadOnlyList<AudioTrackPlan> Tracks, double MasterDb = 0)
{
    public static AudioPlan Empty { get; } = new([]);

    /// <summary>Inserciones de toda la mezcla, después de la ganancia maestra y antes del limitador suave.</summary>
    public AudioEffectsSettings? MasterEffects { get; init; }

    /// <summary>Los buses que suenan, en el orden del mezclador; suman sus pistas y van a la mezcla.</summary>
    public IReadOnlyList<MixBusPlan> Buses { get; init; } = [];

    public DynamicsAutomation? MasterAutomation { get; init; }

    /// <summary>La mezcla, algún bus, clip o pista lleva inserciones, o hay sidechain o mezcla adelantada: guardan estado entre lecturas.</summary>
    public bool HasEffects => MasterEffects is not null || Buses.Any(b => b.Effects is not null)
        || AllTracks(Tracks).Any(t => t.Effects is not null || t.Ahead > 0 || t.KeyAhead >= 0 || t.Clips.Any(c => c.Effects is not null));

    /// <summary>Mismas pistas con los mismos clips, en el mismo orden, con el mismo volumen y la misma velocidad: lo que suena no cambia.</summary>

    public bool SoundsLike(AudioPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return MasterDb.Equals(other.MasterDb) && Equals(MasterEffects, other.MasterEffects) && Equals(MasterAutomation, other.MasterAutomation)
            && Buses.SequenceEqual(other.Buses) && SameTracks(Tracks, other.Tracks);
    }

    internal static bool SameTracks(IReadOnlyList<AudioTrackPlan> a, IReadOnlyList<AudioTrackPlan> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int t = 0; t < a.Count; t++)
        {
            if (a[t].TrackId != b[t].TrackId || !Equals(a[t].Effects, b[t].Effects) || !Equals(a[t].Automation, b[t].Automation)
                || a[t].KeyAhead != b[t].KeyAhead || !a[t].SameMix(b[t]) || !a[t].Clips.SequenceEqual(b[t].Clips))
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

/// <summary>Forma de un fundido de audio.</summary>
internal enum FadeShape
{
    /// <summary>Potencia constante: seno y coseno, la suma de los cuadrados vale 1.</summary>
    Power,

    /// <summary>Ganancia constante: lineal, la suma vale 1.</summary>
    Gain,

    /// <summary>Exponencial: lineal en decibelios, de −60 dB a 0.</summary>
    Exponential,
}

/// <summary>Un fundido en <c>[Start, Start + Length)</c> de la línea de tiempo, en muestras; de potencia constante si no se dice otra cosa.</summary>
/// <param name="Out">De salida en lugar de entrada.</param>
internal readonly record struct Fade(long Start, long Length, bool Out)
{
    /// <summary>La forma; la de una transición la da su tipo.</summary>
    public FadeShape Shape { get; init; }

    /// <summary>La curva del avance de la transición (nula, lineal).</summary>
    public Interpolation? Curve { get; init; }

    public BezierEase? Ease { get; init; }

    public bool IsNone => Length <= 0;

    public long End => Start + Length;

    /// <summary>
    /// Entrada: 0 antes, la forma del avance e durante y 1 después. Salida: 1 antes, la de 1 − e durante y 0 después. Con
    /// potencia constante es sin(πe/2) y cos(πe/2).
    /// </summary>
    public double Factor(long sample)
    {
        if (Length <= 0)
        {
            return 1;
        }

        double t = Math.Clamp((double)(sample - Start) / Length, 0, 1);
        double e = Curve is null ? t : TransitionInstance.EaseProgress(Curve, Ease, t);
        return Shape switch
        {
            FadeShape.Gain => Out ? 1 - e : e,
            FadeShape.Exponential => Exponential(Out ? 1 - e : e),
            _ => Out ? Math.Cos(e * Math.PI / 2) : Math.Sin(e * Math.PI / 2),
        };
    }

    /// <summary>De 0 a 1 lineal en decibelios a lo largo de 60 dB, corrida para valer exactamente 0 y 1 en los extremos.</summary>
    public static double Exponential(double x) => x <= 0 ? 0 : x >= 1 ? 1 : (Math.Pow(1000, x) - 1) / 999;

    public static FadeShape ShapeOf(string type) => type switch
    {
        BuiltInTransitions.AudioGain => FadeShape.Gain,
        BuiltInTransitions.AudioExponential => FadeShape.Exponential,
        _ => FadeShape.Power,
    };
}

/// <summary>
/// Volumen de un clip: su ganancia en dB (keyframes en muestras del origen), sus fundidos propios y los de las
/// transiciones, y su paneo y balance. Compara por valor, así un cambio de volumen rehace la mezcla. El volumen de la pista
/// va en su fader, después de sus inserciones.
/// </summary>
/// <param name="ClipDb">Ganancia del clip, en muestras del origen; nula si es 0 dB.</param>
/// <param name="TrackDb">Ganancia que se suma a la del clip, en muestras de la secuencia; nula si es 0 dB.</param>
internal sealed record GainPlan(Curve? ClipDb, Curve? TrackDb, Fade FadeIn, Fade FadeOut, Fade CrossIn, Fade CrossOut)
{
    /// <summary>Paneo del clip (−100 a 100, en muestras del origen); nulo en el centro.</summary>
    public Curve? Pan { get; init; }

    /// <summary>Balance del clip (−100 a 100, en muestras del origen); nulo en el centro.</summary>
    public Curve? Balance { get; init; }

    public bool HasPan => Pan is not null || Balance is not null;

    /// <summary>La matriz de paneo en la muestra del origen <paramref name="sourceSample"/>.</summary>
    public PanMatrix PanAt(long sourceSample) =>
        PanLaw.Matrix((Pan?.Evaluate(sourceSample) ?? 0) / 100, (Balance?.Evaluate(sourceSample) ?? 0) / 100);

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
