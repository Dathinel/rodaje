using System.Diagnostics.CodeAnalysis;
using Rodaje.Playback;

namespace Rodaje.Engine.Timeline;

/// <summary>Cuadros decodificados de todos los orígenes, con un presupuesto global de memoria y un tope por origen.</summary>
/// <remarks>
/// <para>
/// La clave es (origen, índice). Cuando falta lugar se expulsa primero lo que ninguna capa necesita (lo ya
/// mostrado, lo de clips que quedaron atrás), del uso más viejo al más nuevo; después, lo que se necesita más
/// tarde. Si lo que habría que expulsar es más urgente que el cuadro nuevo, se descarta el nuevo.
/// </para>
/// <para>
/// El tope por origen existe porque cada cuadro de D3D11VA retenido ocupa una textura del pool de su
/// decodificador: pasarse lo agota. No es segura entre hilos: la usa el programador bajo su candado.
/// </para>
/// </remarks>
internal sealed class TimelineFrameCache : IDisposable
{
    /// <summary>Urgencia de un cuadro que nadie necesita; cualquier otro valor es mayor.</summary>
    public const long Unwanted = -1;

    private readonly Dictionary<(int Source, long Index), Entry> _entries = [];
    private readonly Dictionary<int, SourceSlot> _sources = [];
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

    public void SetSourceLimit(int source, int maxFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        Slot(source).Limit = maxFrames;
    }

    public int CountOf(int source) => _sources.TryGetValue(source, out SourceSlot? slot) ? slot.Count : 0;

    public bool Contains(int source, long index) => _entries.ContainsKey((source, index));

    /// <summary>El cuadro guardado de mayor índice en <c>[minIndex, maxIndex]</c>, retenido para quien lo pide.</summary>
    public bool TryGetLatest(int source, long minIndex, long maxIndex, [NotNullWhen(true)] out DecodedFrame? frame)
    {
        frame = null;
        if (maxIndex < minIndex)
        {
            return false;
        }

        Entry? best = null;
        if (maxIndex - minIndex < _entries.Count)
        {
            for (long i = maxIndex; i >= minIndex && best is null; i--)
            {
                _entries.TryGetValue((source, i), out best);
            }
        }
        else
        {
            foreach (((int s, long index), Entry entry) in _entries)
            {
                if (s == source && index >= minIndex && index <= maxIndex && (best is null || index > best.Frame.Index))
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
        return true;
    }

    /// <summary>Guarda el cuadro y se queda con su referencia, o lo libera si no hay lugar para algo tan poco urgente.</summary>
    /// <param name="urgency">Urgencia de un cuadro guardado: <see cref="Unwanted"/> o un valor mayor cuanto antes se necesite.</param>
    public bool Add(int source, DecodedFrame frame, Func<int, long, long> urgency)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(urgency);
        if (_entries.ContainsKey((source, frame.Index)))
        {
            frame.Dispose();
            return false;
        }

        SourceSlot slot = Slot(source);
        long incoming = urgency(source, frame.Index);
        while (slot.Count + 1 > slot.Limit || CostBytes + frame.SizeBytes > BudgetBytes)
        {
            bool perSource = slot.Count + 1 > slot.Limit;
            if (!TryFindVictim(perSource ? source : null, urgency, out (int, long) victim, out long victimUrgency)
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

        _entries.Add((source, frame.Index), new Entry(frame) { LastUse = ++_useClock });
        slot.Count++;
        slot.LastSize = frame.SizeBytes;
        CostBytes += frame.SizeBytes;
        return true;
    }

    /// <summary>Si un cuadro de esa urgencia se guardaría ahora (hay lugar o algo menos urgente que expulsar).</summary>
    public bool CanAdmit(int source, long urgency, Func<int, long, long> urgencyOf)
    {
        ArgumentNullException.ThrowIfNull(urgencyOf);
        SourceSlot slot = Slot(source);
        bool perSource = slot.Count + 1 > slot.Limit;
        if (!perSource && CostBytes + slot.LastSize <= BudgetBytes)
        {
            return true;
        }

        return TryFindVictim(perSource ? source : null, urgencyOf, out _, out long victimUrgency) && victimUrgency < urgency;
    }

    /// <summary>Saca todos los cuadros de un origen que se cierra.</summary>
    public int RemoveSource(int source)
    {
        List<(int, long)> keys = [.. _entries.Keys.Where(k => k.Source == source)];
        keys.ForEach(Remove);
        _sources.Remove(source);
        return keys.Count;
    }

    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Frame.Dispose();
        }

        _entries.Clear();
        _sources.Clear();
        CostBytes = 0;
    }

    public void Dispose() => Clear();

    private SourceSlot Slot(int source)
    {
        if (!_sources.TryGetValue(source, out SourceSlot? slot))
        {
            slot = new SourceSlot();
            _sources.Add(source, slot);
        }

        return slot;
    }

    private bool TryFindVictim(int? onlySource, Func<int, long, long> urgency, out (int, long) victim, out long victimUrgency)
    {
        victim = default;
        victimUrgency = long.MaxValue;
        long victimUse = long.MaxValue;
        bool found = false;
        foreach (((int source, long index), Entry entry) in _entries)
        {
            if (onlySource is { } only && source != only)
            {
                continue;
            }

            long u = urgency(source, index);
            if (!found || u < victimUrgency || (u == victimUrgency && entry.LastUse < victimUse))
            {
                (victim, victimUrgency, victimUse, found) = ((source, index), u, entry.LastUse, true);
            }
        }

        return found;
    }

    private void Remove((int Source, long Index) key)
    {
        if (_entries.Remove(key, out Entry? entry))
        {
            CostBytes -= entry.Frame.SizeBytes;
            Slot(key.Source).Count--;
            entry.Frame.Dispose();
        }
    }

    private sealed class Entry(DecodedFrame frame)
    {
        public DecodedFrame Frame { get; } = frame;

        public long LastUse { get; set; }
    }

    private sealed class SourceSlot
    {
        public int Count { get; set; }

        public int Limit { get; set; } = int.MaxValue;

        public long LastSize { get; set; }
    }
}
