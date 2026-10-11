using System.Diagnostics.CodeAnalysis;
using Rodaje.Playback;

namespace Rodaje.Engine.Timeline;

/// <summary>Cuadros decodificados de todos los medios, con un presupuesto global de memoria y un tope por decodificador.</summary>
/// <remarks>
/// <para>
/// La clave es (medio, índice): un cuadro sirve a cualquier capa de ese medio, lo haya decodificado el decodificador
/// que sea. Cada cuadro recuerda además de qué decodificador salió (su ranura), porque retiene una textura del pool de
/// ese decodificador: el tope por ranura impide agotar el pool, y al cerrarse el decodificador sus cuadros se sacan.
/// </para>
/// <para>
/// Cuando falta lugar se expulsa primero lo que ninguna capa necesita (lo ya mostrado, lo de clips que quedaron atrás),
/// del uso más viejo al más nuevo; después, lo que se necesita más tarde. Si lo que habría que expulsar es más urgente
/// que el cuadro nuevo, se descarta el nuevo. No es segura entre hilos: la usa el programador bajo su candado.
/// </para>
/// </remarks>
internal sealed class TimelineFrameCache : IDisposable
{
    /// <summary>Urgencia de un cuadro que nadie necesita; cualquier otro valor es mayor.</summary>
    public const long Unwanted = -1;

    private readonly Dictionary<(int Group, long Index), Entry> _entries = [];
    private readonly Dictionary<int, SlotState> _slots = [];
    private long _useClock;

    public TimelineFrameCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
    }

    public long BudgetBytes { get; }

    public long CostBytes { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Cuadros descartados o expulsados por falta de lugar.</summary>
    public long Evictions { get; private set; }

    public void SetSourceLimit(int slot, int maxFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        Slot(slot).Limit = maxFrames;
    }

    /// <summary>Cuadros guardados que salieron del decodificador <paramref name="slot"/>.</summary>
    public int CountOf(int slot) => _slots.TryGetValue(slot, out SlotState? state) ? state.Count : 0;

    public bool Contains(int group, long index) => _entries.ContainsKey((group, index));

    /// <summary>Si el cuadro está guardado y salió del decodificador <paramref name="slot"/>.</summary>
    public bool ContainsFrom(int group, long index, int slot) => TryGetSlot(group, index, out int found) && found == slot;

    /// <summary>De qué decodificador salió el cuadro guardado, si lo está.</summary>
    public bool TryGetSlot(int group, long index, out int slot)
    {
        bool found = _entries.TryGetValue((group, index), out Entry? entry);
        slot = found ? entry!.Slot : -1;
        return found;
    }

    /// <summary>Saca un cuadro (por ejemplo, para guardar en su lugar el mismo cuadro decodificado de otro archivo).</summary>
    public void Remove(int group, long index) => Remove((group, index));

    /// <summary>El cuadro guardado de mayor índice en <c>[minIndex, maxIndex]</c>, retenido para quien lo pide, y su ranura.</summary>
    public bool TryGetLatest(int group, long minIndex, long maxIndex, [NotNullWhen(true)] out DecodedFrame? frame, out int slot)
    {
        frame = null;
        slot = -1;
        if (maxIndex < minIndex)
        {
            return false;
        }

        Entry? best = null;
        if (maxIndex - minIndex < _entries.Count)
        {
            for (long i = maxIndex; i >= minIndex && best is null; i--)
            {
                _entries.TryGetValue((group, i), out best);
            }
        }
        else
        {
            foreach (((int g, long index), Entry entry) in _entries)
            {
                if (g == group && index >= minIndex && index <= maxIndex && (best is null || index > best.Frame.Index))
                {
                    best = entry;
                }
            }
        }

        if (best is null || !best.Frame.TryRetain())
        {
            return false;
        }

        best.LastUse = ++_useClock;
        frame = best.Frame;
        slot = best.Slot;
        return true;
    }

    /// <summary>
    /// El cuadro guardado de menor índice en <c>[minIndex, maxIndex]</c>, retenido para quien lo pide, y su ranura: lo que
    /// usa la reversa, donde el cuadro más cercano al debido es el más bajo que todavía no se pasó.
    /// </summary>
    public bool TryGetEarliest(int group, long minIndex, long maxIndex, [NotNullWhen(true)] out DecodedFrame? frame, out int slot)
    {
        frame = null;
        slot = -1;
        if (maxIndex < minIndex)
        {
            return false;
        }

        Entry? best = null;
        if (maxIndex - minIndex < _entries.Count)
        {
            for (long i = minIndex; i <= maxIndex && best is null; i++)
            {
                _entries.TryGetValue((group, i), out best);
            }
        }
        else
        {
            foreach (((int g, long index), Entry entry) in _entries)
            {
                if (g == group && index >= minIndex && index <= maxIndex && (best is null || index < best.Frame.Index))
                {
                    best = entry;
                }
            }
        }

        if (best is null || !best.Frame.TryRetain())
        {
            return false;
        }

        best.LastUse = ++_useClock;
        frame = best.Frame;
        slot = best.Slot;
        return true;
    }

    /// <summary>Guarda el cuadro y se queda con su referencia, o lo libera si no hay lugar para algo tan poco urgente.</summary>
    /// <param name="urgency">Urgencia de un cuadro guardado por (medio, índice): <see cref="Unwanted"/> o un valor mayor cuanto antes se necesite.</param>
    public bool Add(int group, int slot, DecodedFrame frame, Func<int, long, long> urgency)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(urgency);
        if (_entries.ContainsKey((group, frame.Index)))
        {
            frame.Dispose();
            return false;
        }

        SlotState state = Slot(slot);
        long incoming = urgency(group, frame.Index);
        while (state.Count + 1 > state.Limit || CostBytes + frame.SizeBytes > BudgetBytes)
        {
            bool perSlot = state.Count + 1 > state.Limit;
            if (!TryFindVictim(perSlot ? slot : null, urgency, out (int, long) victim, out long victimUrgency)
                || victimUrgency > incoming
                || (victimUrgency == incoming && incoming != Unwanted))
            {
                if (CostBytes == 0)
                {
                    break;
                }

                Evictions++;
                frame.Dispose();
                return false;
            }

            Remove(victim);
            Evictions++;
        }

        _entries.Add((group, frame.Index), new Entry(frame, slot) { LastUse = ++_useClock });
        state.Count++;
        state.LastSize = frame.SizeBytes;
        CostBytes += frame.SizeBytes;
        return true;
    }

    /// <summary>Si un cuadro de esa urgencia, decodificado en <paramref name="slot"/>, se guardaría ahora.</summary>
    public bool CanAdmit(int slot, long urgency, Func<int, long, long> urgencyOf)
    {
        ArgumentNullException.ThrowIfNull(urgencyOf);
        SlotState state = Slot(slot);
        bool perSlot = state.Count + 1 > state.Limit;
        if (!perSlot && CostBytes + state.LastSize <= BudgetBytes)
        {
            return true;
        }

        return TryFindVictim(perSlot ? slot : null, urgencyOf, out _, out long victimUrgency) && victimUrgency < urgency;
    }

    /// <summary>Saca todos los cuadros de un decodificador que se cierra, y su tope.</summary>
    public int RemoveSource(int slot)
    {
        int removed = RemoveFrames(slot);
        _slots.Remove(slot);
        return removed;
    }

    /// <summary>Saca los cuadros que salieron de un decodificador, que sigue abierto con su tope.</summary>
    public int RemoveFrames(int slot)
    {
        List<(int, long)> keys = [.. _entries.Where(e => e.Value.Slot == slot).Select(e => e.Key)];
        keys.ForEach(Remove);
        return keys.Count;
    }

    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Frame.Dispose();
        }

        _entries.Clear();
        _slots.Clear();
        CostBytes = 0;
    }

    public void Dispose() => Clear();

    private SlotState Slot(int slot)
    {
        if (!_slots.TryGetValue(slot, out SlotState? state))
        {
            state = new SlotState();
            _slots.Add(slot, state);
        }

        return state;
    }

    private bool TryFindVictim(int? onlySlot, Func<int, long, long> urgency, out (int, long) victim, out long victimUrgency)
    {
        victim = default;
        victimUrgency = long.MaxValue;
        long victimUse = long.MaxValue;
        bool found = false;
        foreach (((int group, long index), Entry entry) in _entries)
        {
            if (onlySlot is { } only && entry.Slot != only)
            {
                continue;
            }

            long u = urgency(group, index);
            if (!found || u < victimUrgency || (u == victimUrgency && entry.LastUse < victimUse))
            {
                (victim, victimUrgency, victimUse, found) = ((group, index), u, entry.LastUse, true);
            }
        }

        return found;
    }

    private void Remove((int Group, long Index) key)
    {
        if (_entries.Remove(key, out Entry? entry))
        {
            CostBytes -= entry.Frame.SizeBytes;
            Slot(entry.Slot).Count--;
            entry.Frame.Dispose();
        }
    }

    private sealed class Entry(DecodedFrame frame, int slot)
    {
        public DecodedFrame Frame { get; } = frame;

        public int Slot { get; } = slot;

        public long LastUse { get; set; }
    }

    private sealed class SlotState
    {
        public int Count { get; set; }

        public int Limit { get; set; } = int.MaxValue;

        public long LastSize { get; set; }
    }
}
