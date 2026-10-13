using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Core.Commands;

/// <summary>Fábricas de comandos para <see cref="MixerEdits"/>.</summary>
public static partial class Edits
{
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el bus nuevo.</remarks>
    public static SequenceEditCommand AddBus(string sequenceId, string name, int? index = null, IIdGenerator? ids = null) =>
        new("Añadir bus", sequenceId, ctx => (IReadOnlyList<string>)[MixerEdits.AddBus(ctx, name, index)], ids);

    /// <summary>Varios buses de una vez (por ejemplo «Voces», «Música» y «Efectos»), en un paso.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: los buses nuevos, en orden.</remarks>
    public static SequenceEditCommand AddBuses(string sequenceId, IReadOnlyList<string> names, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] copy = [.. names];
        return new("Añadir buses", sequenceId, ctx => (IReadOnlyList<string>)[.. copy.Select(n => MixerEdits.AddBus(ctx, n))], ids);
    }

    public static SequenceEditCommand RemoveBus(string sequenceId, string busId, IIdGenerator? ids = null) =>
        new("Quitar bus", sequenceId, ctx => MixerEdits.RemoveBus(ctx, busId), ids);

    public static SequenceEditCommand MoveBus(string sequenceId, string busId, int newIndex, IIdGenerator? ids = null) =>
        new("Mover bus", sequenceId, ctx => MixerEdits.MoveBus(ctx, busId, newIndex), ids);

    public static SequenceEditCommand SetBusState(string sequenceId, string busId, string? name = null, bool? muted = null, bool? solo = null,
        IIdGenerator? ids = null) =>
        new(name is not null ? "Renombrar bus" : solo is not null ? "Solo del bus" : "Silenciar bus", sequenceId,
            ctx => MixerEdits.SetBusState(ctx, busId, name, muted, solo), ids);

    public static SequenceEditCommand SetTrackMix(string sequenceId, string trackId, bool? solo = null, bool? armed = null, IIdGenerator? ids = null) =>
        new(armed is not null ? "Armar grabación" : "Solo de la pista", sequenceId, ctx => MixerEdits.SetTrackMix(ctx, trackId, solo, armed), ids);

    public static SequenceEditCommand SoloExclusive(string sequenceId, string ownerId, IIdGenerator? ids = null) =>
        new("Solo exclusivo", sequenceId, ctx => MixerEdits.SoloExclusive(ctx, ownerId), ids);

    public static SequenceEditCommand SetTrackOutput(string sequenceId, string trackId, string? busId, IIdGenerator? ids = null) =>
        new("Salida de la pista", sequenceId, ctx => MixerEdits.SetTrackOutput(ctx, trackId, busId), ids);

    /// <summary>Crea o cambia un envío; se funde con el siguiente cambio del mismo envío (un arrastre deja un paso).</summary>
    public static SequenceEditCommand SetSend(string sequenceId, string trackId, string busId, double? gainDb = null, bool? preFader = null,
        bool? muted = null, IIdGenerator? ids = null) =>
        new("Envío", sequenceId, ctx => MixerEdits.SetSend(ctx, trackId, busId, gainDb, preFader, muted), ids,
            gainDb is not null && preFader is null && muted is null ? $"send:{trackId}:{busId}" : null);

    public static SequenceEditCommand RemoveSend(string sequenceId, string trackId, string busId, IIdGenerator? ids = null) =>
        new("Quitar envío", sequenceId, ctx => MixerEdits.RemoveSend(ctx, trackId, busId), ids);

    /// <summary>Volumen, paneo o balance de una pista o un bus; se funde con el siguiente cambio del mismo control en el mismo instante.</summary>
    public static SequenceEditCommand SetFader(string sequenceId, string ownerId, string parameter, double value, Time at, IIdGenerator? ids = null) =>
        new(parameter switch { A.Pan => "Paneo", A.Balance => "Balance", _ => "Volumen" }, sequenceId,
            ctx => MixerEdits.SetFader(ctx, ownerId, parameter, value, at), ids, $"fader:{ownerId}:{parameter}:{at.Ticks}");

    /// <summary>Aplica un preajuste a una inserción concreta (clip, pista, bus o mezcla), en un paso.</summary>
    public static SequenceEditCommand ApplyInsertPreset(string sequenceId, string ownerId, string effectId, InsertPreset preset, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new("Preajuste: " + preset.Name, sequenceId, ctx => EffectEdits.ReplaceParameters(ctx, ownerId, effectId, preset.Values), ids);
    }

    public static SequenceEditCommand WriteAutomation(string sequenceId, string ownerId, string parameter, IReadOnlyList<MixerEdits.AutomationPoint> points,
        Time from, Time to, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        MixerEdits.AutomationPoint[] copy = [.. points];
        return new(parameter == A.Pan ? "Automatizar paneo" : parameter == A.Balance ? "Automatizar balance" : "Automatizar volumen", sequenceId,
            ctx => MixerEdits.WriteAutomation(ctx, ownerId, parameter, copy, from, to), ids);
    }
}
