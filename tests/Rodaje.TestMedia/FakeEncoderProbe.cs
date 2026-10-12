using System.Collections.Concurrent;
using Rodaje.Media.Export;

namespace Rodaje.TestMedia;

/// <summary>
/// Sonda de codificadores de mentira, la única de las pruebas: responde con <paramref name="behavior"/> y apunta cada
/// llamada, cuántas van a la vez y cuántas veces se reinició.
/// </summary>
public sealed class FakeEncoderProbe(Func<EncoderSpec, ExportProbeResult> behavior) : IExportEncoderProbe
{
    private readonly ConcurrentQueue<string> _ids = new();
    private int _running;
    private int _maxRunning;
    private int _resets;

    public int Calls => _ids.Count;

    public int MaxRunning => Volatile.Read(ref _maxRunning);

    public int Resets => Volatile.Read(ref _resets);

    public int CallsFor(string id) => _ids.Count(i => i == id);

    public ExportProbeResult Probe(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        _ids.Enqueue(encoder.Id);
        int now = Interlocked.Increment(ref _running);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxRunning)) && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
        {
        }

        try
        {
            return behavior(encoder);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    public void Reset() => Interlocked.Increment(ref _resets);
}
