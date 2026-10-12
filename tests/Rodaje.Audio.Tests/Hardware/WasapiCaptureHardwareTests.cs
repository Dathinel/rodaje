using System.Diagnostics;
using Rodaje.Audio.Capture;
using Rodaje.TestMedia;
using Xunit;
using Xunit.Abstractions;

namespace Rodaje.Audio.Tests.Hardware;

/// <summary>La captura WASAPI con el micrófono real del equipo (si lo hay): paquetes seguidos y marcas QPC coherentes.</summary>
public sealed class WasapiCaptureHardwareTests(ITestOutputHelper output)
{
    [AudioDeviceFact]
    public void The_default_microphone_delivers_contiguous_timestamped_packets()
    {
        IReadOnlyList<AudioCaptureDevice> devices = AudioCapture.ListDevices();
        output.WriteLine($"Micrófonos: {string.Join(" · ", devices.Select(d => d.Name + (d.IsDefault ? " (predeterminado)" : "")))}");
        if (devices.Count == 0)
        {
            return;
        }

        using IAudioCapture capture = AudioCapture.Open(null);
        var stamps = new List<(long Qpc, int Frames, bool Gap)>();
        long now100ns = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        capture.StartCapture((mono, qpc, gap) =>
        {
            lock (stamps)
            {
                stamps.Add((qpc, mono.Length, gap));
            }
        });
        Thread.Sleep(1000);
        capture.StopCapture();

        long frames = stamps.Sum(s => (long)s.Frames);
        double worst = 0;
        for (int i = 1; i < stamps.Count; i++)
        {
            double expected = stamps[i - 1].Frames * 10_000_000.0 / capture.SampleRate;
            worst = Math.Max(worst, Math.Abs(stamps[i].Qpc - stamps[i - 1].Qpc - expected) / 10_000.0);
        }

        output.WriteLine($"«{capture.DeviceName}»: {stamps.Count} paquetes, {frames} muestras en 1 s; primera marca {(stamps.Count > 0 ? (stamps[0].Qpc - now100ns) / 10_000.0 : 0):0.0} ms " +
            $"desde el arranque; mayor desvío entre marcas {worst:0.000} ms; discontinuidades {stamps.Count(s => s.Gap)}");
        Assert.InRange(frames, capture.SampleRate * 8 / 10, capture.SampleRate * 12 / 10);
        Assert.True(worst < 2, $"Las marcas QPC se apartan {worst:0.000} ms de lo que dicen los paquetes.");
    }
}
