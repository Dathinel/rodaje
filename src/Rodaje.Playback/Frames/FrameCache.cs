using System.Diagnostics.CodeAnalysis;

namespace Rodaje.Playback;

/// <summary>Caché LRU de cuadros por índice, acotada por la suma de <see cref="DecodedFrame.SizeBytes"/>.</summary>
/// <remarks>
/// Es dueña de una referencia de cada cuadro que guarda y la devuelve al expulsarlo. Los cuadros que
/// entrega van retenidos: quien los recibe debe llamar <see cref="DecodedFrame.Dispose"/>.
/// Segura entre hilos.
/// </remarks>
public sealed class FrameCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, LinkedListNode<DecodedFrame>> _byIndex = [];
    private readonly LinkedList<DecodedFrame> _recency = new();
    private long _costBytes;

    public FrameCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
    }

    public long BudgetBytes { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byIndex.Count;
            }
        }
    }

    public long CostBytes
    {
        get
        {
            lock (_gate)
            {
                return _costBytes;
            }
        }
    }

    /// <summary>Guarda el cuadro y se queda con su referencia. Si ya había uno con ese índice, libera el nuevo.</summary>
    /// <remarks>Nunca expulsa el cuadro recién agregado, aunque por sí solo supere el presupuesto.</remarks>
    public bool Add(DecodedFrame frame)
    {
        List<DecodedFrame>? released = null;
        bool added = Add(frame, ref released);
        released?.ForEach(f => f.Dispose());
        return added;
    }

    /// <summary>Como <see cref="Add(DecodedFrame)"/>, pero deja en <paramref name="released"/> lo que hay que liberar
    /// para que el llamador lo libere fuera de sus propios candados.</summary>
    internal bool Add(DecodedFrame frame, ref List<DecodedFrame>? released)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool added;
        lock (_gate)
        {
            added = !_byIndex.ContainsKey(frame.Index);
            if (added)
            {
                _byIndex.Add(frame.Index, _recency.AddFirst(frame));
                _costBytes += frame.SizeBytes;
                while (_costBytes > BudgetBytes && _recency.Count > 1)
                {
                    DecodedFrame oldest = _recency.Last!.Value;
                    RemoveNode(_recency.Last);
                    (released ??= []).Add(oldest);
                }
            }
        }

        if (!added)
        {
            (released ??= []).Add(frame);
        }

        return added;
    }

    public bool Contains(long index)
    {
        lock (_gate)
        {
            return _byIndex.ContainsKey(index);
        }
    }

    public bool TryGet(long index, [NotNullWhen(true)] out DecodedFrame? frame) =>
        TryGetLatest(index, index, out frame);

    /// <summary>Entrega el cuadro guardado de mayor índice dentro de <c>[minIndex, maxIndex]</c>.</summary>
    public bool TryGetLatest(long minIndex, long maxIndex, [NotNullWhen(true)] out DecodedFrame? frame)
    {
        lock (_gate)
        {
            LinkedListNode<DecodedFrame>? best = null;
            if (maxIndex >= minIndex && maxIndex - minIndex < _byIndex.Count)
            {
                for (long i = maxIndex; i >= minIndex && best is null; i--)
                {
                    _byIndex.TryGetValue(i, out best);
                }
            }
            else if (maxIndex >= minIndex)
            {
                foreach ((long index, LinkedListNode<DecodedFrame> node) in _byIndex)
                {
                    if (index >= minIndex && index <= maxIndex && (best is null || index > best.Value.Index))
                    {
                        best = node;
                    }
                }
            }

            if (best is not null && best.Value.TryRetain())
            {
                _recency.Remove(best);
                _recency.AddFirst(best);
                frame = best.Value;
                return true;
            }
        }

        frame = null;
        return false;
    }

    public bool Remove(long index)
    {
        DecodedFrame? removed = null;
        lock (_gate)
        {
            if (_byIndex.TryGetValue(index, out LinkedListNode<DecodedFrame>? node))
            {
                removed = node.Value;
                RemoveNode(node);
            }
        }

        removed?.Dispose();
        return removed is not null;
    }

    /// <summary>Saca los cuadros de <c>[fromIndex, toIndex)</c>; cuesta lo menor entre el rango y lo guardado.</summary>
    public int RemoveRange(long fromIndex, long toIndex)
    {
        List<DecodedFrame>? removed = null;
        lock (_gate)
        {
            if (toIndex > fromIndex && toIndex - fromIndex <= _byIndex.Count)
            {
                for (long i = fromIndex; i < toIndex; i++)
                {
                    if (_byIndex.TryGetValue(i, out LinkedListNode<DecodedFrame>? node))
                    {
                        (removed ??= []).Add(node.Value);
                        RemoveNode(node);
                    }
                }
            }
            else if (toIndex > fromIndex)
            {
                for (LinkedListNode<DecodedFrame>? node = _recency.First; node is not null;)
                {
                    LinkedListNode<DecodedFrame>? next = node.Next;
                    if (node.Value.Index >= fromIndex && node.Value.Index < toIndex)
                    {
                        (removed ??= []).Add(node.Value);
                        RemoveNode(node);
                    }

                    node = next;
                }
            }
        }

        removed?.ForEach(f => f.Dispose());
        return removed?.Count ?? 0;
    }

    public void Clear()
    {
        List<DecodedFrame> all;
        lock (_gate)
        {
            all = [.. _recency];
            _recency.Clear();
            _byIndex.Clear();
            _costBytes = 0;
        }

        all.ForEach(f => f.Dispose());
    }

    public void Dispose() => Clear();

    private void RemoveNode(LinkedListNode<DecodedFrame> node)
    {
        _recency.Remove(node);
        _byIndex.Remove(node.Value.Index);
        _costBytes -= node.Value.SizeBytes;
    }
}
