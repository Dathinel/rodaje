using System.Diagnostics;

namespace Rodaje.Export.Running;

internal readonly struct ExportMeasurement(Action<string, TimeSpan>? measure, string phase) : IDisposable
{
    private readonly long _start = measure is null ? 0 : Stopwatch.GetTimestamp();

    public void Dispose() => measure?.Invoke(phase, Stopwatch.GetElapsedTime(_start));
}
