using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.App.Services.Audio;

/// <summary>Qué es una tira del mezclador.</summary>
public enum StripKind
{
    Track,
    Bus,
    Master,
}

/// <summary>
/// Una tira del mezclador leída del modelo: de quién es (pista, bus o la mezcla), sus inserciones y su fader en un instante.
/// </summary>
/// <param name="OwnerId">Lo que reciben las ediciones: id de la pista, del bus o de la secuencia (la mezcla).</param>
public sealed record MixerStrip(StripKind Kind, string OwnerId, string Name)
{
    public bool Muted { get; init; }

    public bool Solo { get; init; }

    public bool Armed { get; init; }

    public bool Locked { get; init; }

    public string? Output { get; init; }

    public IReadOnlyList<AudioSend> Sends { get; init; } = [];

    /// <summary>Inserciones en el orden de la pila (sin el fader).</summary>
    public IReadOnlyList<EffectInstance> Inserts { get; init; } = [];

    /// <summary>Volumen en dB en el instante pedido (en la mezcla, la ganancia maestra).</summary>
    public double GainDb { get; init; }

    /// <summary>Paneo y balance de −100 a 100.</summary>
    public double Pan { get; init; }

    public double Balance { get; init; }

    /// <summary>El volumen o el paneo tienen keyframes (automatización).</summary>
    public bool GainAutomated { get; init; }

    public bool PanAutomated { get; init; }

    /// <summary>La clave de su medidor en la mezcla; nula en la mezcla, que usa los medidores generales.</summary>
    public string? MeterKey => Kind switch
    {
        StripKind.Track => DynamicsMonitor.ForTrack(OwnerId),
        StripKind.Bus => DynamicsMonitor.ForBus(OwnerId),
        _ => null,
    };

    /// <summary>La clave con la que se mira una de sus inserciones en el panel de dinámica.</summary>
    public string MonitorKey => Kind switch
    {
        StripKind.Track => DynamicsMonitor.ForTrack(OwnerId),
        StripKind.Bus => DynamicsMonitor.ForBus(OwnerId),
        _ => DynamicsMonitor.ForMix(),
    };
}

/// <summary>Lógica del mezclador sin interfaz: las tiras de una secuencia, la curva del fader y el orden de las inserciones.</summary>
public static class MixerModel
{
    public const double FaderMinDb = -60;

    public const double FaderMaxDb = 12;

    /// <summary>Nombres de los buses que se crean de una vez.</summary>
    public static IReadOnlyList<string> StandardBuses { get; } = ["Voces", "Música", "Efectos"];

    // Recorrido del fader por tramos lineales (dB, fracción de la carrera): más resolución cerca de 0 dB, como en una mesa.
    private static readonly (double Db, double Position)[] Taper =
        [(-60, 0), (-40, 0.1), (-30, 0.2), (-20, 0.35), (-10, 0.55), (-5, 0.67), (0, 0.78), (6, 0.89), (12, 1)];

    /// <summary>Las marcas de la escala del fader.</summary>
    public static IReadOnlyList<int> FaderTicks { get; } = [12, 6, 0, -5, -10, -20, -30, -40, -60];

    /// <summary>Las tiras de la secuencia en el instante <paramref name="at"/>: pistas de audio, buses y la mezcla.</summary>
    public static IReadOnlyList<MixerStrip> Strips(Sequence sequence, Time at)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var strips = new List<MixerStrip>();
        foreach (Track track in sequence.AudioTracks)
        {
            strips.Add(WithFader(new MixerStrip(StripKind.Track, track.Id, track.Name)
            {
                Muted = track.Muted,
                Solo = track.Solo,
                Armed = track.Armed,
                Locked = track.Locked,
                Output = track.Output,
                Sends = track.Sends,
                Inserts = Inserts(track.Effects),
            }, track.Effects, at));
        }

        foreach (AudioBus bus in sequence.AudioBuses)
        {
            strips.Add(WithFader(new MixerStrip(StripKind.Bus, bus.Id, bus.Name) { Muted = bus.Muted, Solo = bus.Solo, Inserts = Inserts(bus.Effects) },
                bus.Effects, at));
        }

        strips.Add(new MixerStrip(StripKind.Master, sequence.Id, "Mezcla") { Inserts = Inserts(sequence.MasterEffects), GainDb = sequence.MasterGainDb });
        return strips;
    }

    /// <summary>Las inserciones de una pila: todo menos el fader (<c>rodaje.audio</c>).</summary>
    public static IReadOnlyList<EffectInstance> Inserts(IReadOnlyList<EffectInstance> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        return [.. effects.Where(e => e.Type != BuiltInEffects.Audio)];
    }

    /// <summary>La pila de efectos de una tira (para reordenar).</summary>
    public static List<EffectInstance>? Effects(Sequence sequence, MixerStrip strip)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(strip);
        return strip.Kind switch
        {
            StripKind.Track => sequence.FindTrack(strip.OwnerId)?.Effects,
            StripKind.Bus => sequence.FindBus(strip.OwnerId)?.Effects,
            _ => sequence.MasterEffects,
        };
    }

    /// <summary>
    /// Dónde va en la pila completa la inserción <paramref name="movedId"/> para que las inserciones queden en
    /// <paramref name="insertOrder"/>: delante de la que la sigue, o detrás de la última. Es el índice para <see cref="Edits.MoveEffect"/>.
    /// </summary>
    public static int MoveIndex(IReadOnlyList<EffectInstance> effects, string movedId, IReadOnlyList<string> insertOrder)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(insertOrder);
        var rest = effects.Where(e => e.Id != movedId).ToList();
        int position = insertOrder.ToList().IndexOf(movedId);
        if (position < 0)
        {
            throw new ArgumentException("La inserción no está en el orden pedido.", nameof(movedId));
        }

        for (int i = position + 1; i < insertOrder.Count; i++)
        {
            int next = rest.FindIndex(e => e.Id == insertOrder[i]);
            if (next >= 0)
            {
                return next;
            }
        }

        int last = -1;
        for (int i = position - 1; i >= 0 && last < 0; i--)
        {
            last = rest.FindIndex(e => e.Id == insertOrder[i]);
        }

        return last < 0 ? rest.Count : last + 1;
    }

    /// <summary>Fracción de la carrera del fader (0 abajo, 1 arriba) para <paramref name="db"/>.</summary>
    public static double FaderPosition(double db)
    {
        if (double.IsNaN(db) || db <= Taper[0].Db)
        {
            return 0;
        }

        for (int i = 1; i < Taper.Length; i++)
        {
            if (db <= Taper[i].Db)
            {
                (double d0, double p0) = Taper[i - 1];
                (double d1, double p1) = Taper[i];
                return p0 + ((db - d0) / (d1 - d0) * (p1 - p0));
            }
        }

        return 1;
    }

    /// <summary>dB de una posición del fader; abajo del todo es silencio (−60).</summary>
    public static double FaderDb(double position)
    {
        position = Math.Clamp(position, 0, 1);
        for (int i = 1; i < Taper.Length; i++)
        {
            if (position <= Taper[i].Position)
            {
                (double d0, double p0) = Taper[i - 1];
                (double d1, double p1) = Taper[i];
                return d0 + ((position - p0) / (p1 - p0) * (d1 - d0));
            }
        }

        return FaderMaxDb;
    }

    /// <summary>«−3,0 dB», «0 dB», «−∞».</summary>
    public static string FormatDb(double db) =>
        db <= FaderMinDb ? "−∞" : Math.Abs(db) < 0.05 ? "0,0" : (db > 0 ? "+" : "−") + Math.Abs(db).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>«C», «I 30», «D 100».</summary>
    public static string FormatPan(double pan) =>
        Math.Abs(pan) < 0.5 ? "C" : (pan < 0 ? "I " : "D ") + Math.Round(Math.Abs(pan)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static MixerStrip WithFader(MixerStrip strip, IReadOnlyList<EffectInstance> effects, Time at)
    {
        EffectInstance? fader = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        Parameter? gain = fader?.Parameters.GetValueOrDefault(A.GainDb);
        Parameter? pan = fader?.Parameters.GetValueOrDefault(A.Pan);
        return strip with
        {
            GainDb = gain?.Evaluate(at) ?? 0,
            Pan = pan?.Evaluate(at) ?? 0,
            Balance = fader?.Parameters.GetValueOrDefault(A.Balance)?.Evaluate(at) ?? 0,
            GainAutomated = gain?.Keyframes.Count > 0,
            PanAutomated = pan?.Keyframes.Count > 0,
        };
    }
}

/// <summary>
/// La escritura en vivo del modo «tocar»: mientras se sostiene un control y suena, guarda lo que vale en cada instante y lo
/// escribe en la automatización como un paso del historial (ya reducido: un punto cada vez que cambia de verdad). Al
/// soltar, la curva vuelve a la de antes en <see cref="ReturnTime"/>.
/// </summary>
public sealed class TouchRecorder(string ownerId, string parameter, Time start)
{
    /// <summary>Lo que tarda en volver a la curva de antes al soltar.</summary>
    public static readonly Time ReturnTime = Time.FromSeconds(0.1);

    private readonly List<MixerEdits.AutomationPoint> _points = [];

    public string OwnerId { get; } = ownerId;

    public string Parameter { get; } = parameter;

    public Time Start { get; } = start;

    /// <summary>Lo que cambia menos que esto respecto de la recta entre sus vecinos no deja punto (dB o unidades de paneo).</summary>
    public double Tolerance { get; init; } = 0.1;

    public IReadOnlyList<MixerEdits.AutomationPoint> Points => _points;

    /// <summary>Anota el valor de <paramref name="at"/>; los instantes que no avanzan (pausa, vuelta atrás) se ignoran.</summary>
    public void Add(Time at, double value)
    {
        if (at <= Start || (_points.Count > 0 && at <= _points[^1].At))
        {
            return;
        }

        _points.Add(new MixerEdits.AutomationPoint(at, value));
    }

    /// <summary>Los puntos sin los que caen sobre la recta entre sus vecinos (Ramer-Douglas-Peucker).</summary>
    public IReadOnlyList<MixerEdits.AutomationPoint> Thinned()
    {
        if (_points.Count <= 2)
        {
            return [.. _points];
        }

        bool[] keep = new bool[_points.Count];
        keep[0] = keep[^1] = true;
        Simplify(0, _points.Count - 1, keep);
        return [.. _points.Where((_, i) => keep[i])];
    }

    /// <summary>El comando que escribe lo tocado: de <see cref="Start"/> a lo último más <see cref="ReturnTime"/>. Nulo si no hay nada.</summary>
    public SequenceEditCommand? Command(string sequenceId)
    {
        IReadOnlyList<MixerEdits.AutomationPoint> points = Thinned();
        if (points.Count == 0)
        {
            return null;
        }

        return Edits.WriteAutomation(sequenceId, OwnerId, Parameter, points, Start, points[^1].At + ReturnTime);
    }

    private void Simplify(int first, int last, bool[] keep)
    {
        MixerEdits.AutomationPoint a = _points[first], b = _points[last];
        double worst = 0;
        int index = -1;
        for (int i = first + 1; i < last; i++)
        {
            double t = (double)(_points[i].At - a.At).Ticks / (b.At - a.At).Ticks;
            double line = a.Value + ((b.Value - a.Value) * t);
            double error = Math.Abs(_points[i].Value - line);
            if (error > worst)
            {
                worst = error;
                index = i;
            }
        }

        if (index >= 0 && worst > Tolerance)
        {
            keep[index] = true;
            Simplify(first, index, keep);
            Simplify(index, last, keep);
        }
    }
}
