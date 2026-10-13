using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Core.Editing;

/// <summary>
/// Ediciones del mezclador: buses, salida, envíos, solo y grabación de las pistas de audio, el fader (volumen, paneo y
/// balance de <c>rodaje.audio</c>) de una pista o un bus y la escritura de automatización.
/// </summary>
/// <remarks>
/// El solo y el armado se pueden cambiar en una pista bloqueada, como el silencio: no editan su contenido. El resto
/// (fader, salida, envíos) sí, y se rechaza en una pista bloqueada.
/// </remarks>
public static class MixerEdits
{
    /// <summary>Un punto de la automatización que se escribe, en tiempo de la secuencia.</summary>
    public readonly record struct AutomationPoint(Time At, double Value);

    /// <returns>Identificador del bus nuevo.</returns>
    public static string AddBus(EditContext ctx, string name, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new EditException("El bus necesita un nombre.");
        }

        List<AudioBus> buses = ctx.Sequence.AudioBuses;
        int at = index ?? buses.Count;
        if (at < 0 || at > buses.Count)
        {
            throw new EditException($"La posición {at} no existe: hay {buses.Count} buses.");
        }

        var bus = new AudioBus { Id = ctx.Ids.NewId("bus"), Name = name.Trim() };
        buses.Insert(at, bus);
        return bus.Id;
    }

    /// <summary>Quita el bus; las pistas que salían a él pasan a la mezcla y sus envíos a él desaparecen.</summary>
    public static void RemoveBus(EditContext ctx, string busId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        AudioBus bus = FindBus(ctx, busId);
        foreach (Track track in ctx.Sequence.AudioTracks)
        {
            if (track.Output == busId || track.Sends.Exists(s => s.BusId == busId))
            {
                EditContext.EnsureUnlocked(track);
                if (track.Output == busId)
                {
                    track.Output = null;
                }

                track.Sends.RemoveAll(s => s.BusId == busId);
            }
        }

        ctx.Sequence.AudioBuses.Remove(bus);
    }

    public static void MoveBus(EditContext ctx, string busId, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        AudioBus bus = FindBus(ctx, busId);
        List<AudioBus> buses = ctx.Sequence.AudioBuses;
        if (newIndex < 0 || newIndex >= buses.Count)
        {
            throw new EditException($"La posición {newIndex} no existe: hay {buses.Count} buses.");
        }

        buses.Remove(bus);
        buses.Insert(newIndex, bus);
    }

    /// <summary>Cambia nombre, silencio o solo del bus; lo que va en <c>null</c> no cambia.</summary>
    public static void SetBusState(EditContext ctx, string busId, string? name = null, bool? muted = null, bool? solo = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        AudioBus bus = FindBus(ctx, busId);
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            throw new EditException("El bus necesita un nombre.");
        }

        bus.Name = name?.Trim() ?? bus.Name;
        bus.Muted = muted ?? bus.Muted;
        bus.Solo = solo ?? bus.Solo;
    }

    /// <summary>Solo y grabación de una pista de audio (también bloqueada); lo que va en <c>null</c> no cambia.</summary>
    public static void SetTrackMix(EditContext ctx, string trackId, bool? solo = null, bool? armed = null)
    {
        Track track = AudioTrack(ctx, trackId);
        track.Solo = solo ?? track.Solo;
        track.Armed = armed ?? track.Armed;
    }

    /// <summary>Solo exclusivo: la pista (o el bus) queda como única con solo; si ya lo era, se quitan todos.</summary>
    public static void SoloExclusive(EditContext ctx, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Sequence sequence = ctx.Sequence;
        bool alone = sequence.AudioTracks.Count(t => t.Solo) + sequence.AudioBuses.Count(b => b.Solo) == 1
            && (sequence.FindTrack(ownerId)?.Solo == true || sequence.FindBus(ownerId)?.Solo == true);
        foreach (Track track in sequence.AudioTracks)
        {
            track.Solo = !alone && track.Id == ownerId;
        }

        foreach (AudioBus bus in sequence.AudioBuses)
        {
            bus.Solo = !alone && bus.Id == ownerId;
        }

        if (sequence.FindTrack(ownerId) is null && sequence.FindBus(ownerId) is null)
        {
            throw new EditException("La pista ya no está en la secuencia.");
        }
    }

    /// <summary>A qué bus sale la pista; nulo, a la mezcla.</summary>
    public static void SetTrackOutput(EditContext ctx, string trackId, string? busId)
    {
        Track track = AudioTrack(ctx, trackId);
        EditContext.EnsureUnlocked(track);
        if (busId is not null)
        {
            FindBus(ctx, busId);
        }

        track.Output = busId;
    }

    /// <summary>Crea o cambia el envío de la pista al bus; lo que va en <c>null</c> no cambia (uno nuevo sale post-fader a 0 dB).</summary>
    public static void SetSend(EditContext ctx, string trackId, string busId, double? gainDb = null, bool? preFader = null, bool? muted = null)
    {
        Track track = AudioTrack(ctx, trackId);
        EditContext.EnsureUnlocked(track);
        FindBus(ctx, busId);
        AudioSend? send = track.Sends.Find(s => s.BusId == busId);
        if (send is null)
        {
            send = new AudioSend { BusId = busId };
            track.Sends.Add(send);
        }

        if (gainDb is { } db)
        {
            if (!double.IsFinite(db))
            {
                throw new EditException("El nivel del envío necesita un número.");
            }

            send.GainDb = Math.Clamp(db, AudioSend.MinGainDb, AudioSend.MaxGainDb);
        }

        send.PreFader = preFader ?? send.PreFader;
        send.Muted = muted ?? send.Muted;
    }

    public static void RemoveSend(EditContext ctx, string trackId, string busId)
    {
        Track track = AudioTrack(ctx, trackId);
        EditContext.EnsureUnlocked(track);
        if (track.Sends.RemoveAll(s => s.BusId == busId) == 0)
        {
            throw new EditException("La pista no tiene un envío a ese bus.");
        }
    }

    /// <summary>
    /// El fader de una pista de audio o de un bus como un deslizador: sin keyframes cambia el valor fijo; con keyframes
    /// fija el de <paramref name="at"/> (tiempo de la secuencia). <paramref name="parameter"/> es el volumen, el paneo o
    /// el balance de <c>rodaje.audio</c>. Crea el efecto si falta.
    /// </summary>
    public static void SetFader(EditContext ctx, string ownerId, string parameter, double value, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Parameter target = FaderParameter(ctx, ownerId, parameter, out ParameterDefinition definition);
        if (!double.IsFinite(value))
        {
            throw new EditException("El fader necesita un número.");
        }

        value = definition.Clamp(value);
        if (target.Keyframes.Count > 0)
        {
            Upsert(target.Keyframes, new Keyframe(at, value, Interpolation.Linear));
        }
        else
        {
            target.Value = value;
        }
    }

    /// <summary>
    /// Escribe lo que se tocó en vivo (modo «tocar»): quita los keyframes de <c>[from, to]</c> y pone los de
    /// <paramref name="points"/> (ya en ese tramo y en orden), con un keyframe en <paramref name="from"/> con lo que valía
    /// antes y otro en <paramref name="to"/> donde la curva de antes retoma. Así fuera del tramo todo suena igual.
    /// </summary>
    public static void WriteAutomation(EditContext ctx, string ownerId, string parameter, IReadOnlyList<AutomationPoint> points, Time from, Time to)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || to <= from || from < Time.Zero)
        {
            throw new EditException("No hay nada que escribir en la automatización.");
        }

        Parameter target = FaderParameter(ctx, ownerId, parameter, out ParameterDefinition definition);
        double before = target.Evaluate(from);
        double after = target.Evaluate(to);
        List<Keyframe> keys = target.Keyframes;
        keys.RemoveAll(k => k.Time >= from && k.Time <= to);
        Upsert(keys, new Keyframe(from, definition.Clamp(before), Interpolation.Linear));
        foreach (AutomationPoint point in points)
        {
            if (point.At > from && point.At < to && double.IsFinite(point.Value))
            {
                Upsert(keys, new Keyframe(point.At, definition.Clamp(point.Value), Interpolation.Linear));
            }
        }

        Upsert(keys, new Keyframe(to, definition.Clamp(after), Interpolation.Linear));
    }

    /// <summary>El parámetro del fader de la pista o el bus, creando <c>rodaje.audio</c> (y el parámetro) si faltan.</summary>
    private static Parameter FaderParameter(EditContext ctx, string ownerId, string parameter, out ParameterDefinition definition)
    {
        if (parameter is not (A.GainDb or A.Pan or A.Balance))
        {
            throw new EditException($"«{parameter}» no es del fader.");
        }

        definition = EffectRegistry.Find(BuiltInEffects.Audio)!.FindParameter(parameter)!;
        List<EffectInstance> effects;
        if (ctx.Sequence.FindBus(ownerId) is { } bus)
        {
            effects = bus.Effects;
        }
        else
        {
            Track track = AudioTrack(ctx, ownerId);
            EditContext.EnsureUnlocked(track);
            effects = track.Effects;
        }

        EffectInstance? audio = effects.Find(e => e.Type == BuiltInEffects.Audio && e.Enabled) ?? effects.Find(e => e.Type == BuiltInEffects.Audio);
        if (audio is null)
        {
            audio = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Audio };
            effects.Insert(0, audio);
        }

        audio.Enabled = true;
        if (!audio.Parameters.TryGetValue(parameter, out Parameter? target))
        {
            target = new Parameter { Value = definition.DefaultValue };
            audio.Parameters[parameter] = target;
        }

        return target;
    }

    private static void Upsert(List<Keyframe> keys, Keyframe keyframe)
    {
        int index = keys.FindIndex(k => k.Time >= keyframe.Time);
        if (index >= 0 && keys[index].Time == keyframe.Time)
        {
            keys[index] = keyframe;
        }
        else
        {
            keys.Insert(index < 0 ? keys.Count : index, keyframe);
        }
    }

    private static AudioBus FindBus(EditContext ctx, string busId) =>
        ctx.Sequence.FindBus(busId) ?? throw new EditException("El bus ya no está en la secuencia.");

    private static Track AudioTrack(EditContext ctx, string trackId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Track track = ctx.Sequence.FindTrack(trackId) ?? throw new EditException("La pista ya no está en la secuencia.");
        return track.Kind == TrackKind.Audio ? track : throw new EditException("El mezclador es de las pistas de audio.");
    }
}
