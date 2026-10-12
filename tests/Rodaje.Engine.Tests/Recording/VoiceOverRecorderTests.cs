using System.Buffers.Binary;
using Rodaje.Audio.Capture;
using Rodaje.Core.Timing;
using Rodaje.Engine.Recording;
using Xunit;
using Xunit.Abstractions;

namespace Rodaje.Engine.Tests.Recording;

/// <summary>
/// Voz en off con un micrófono falso: paquetes con su instante de captura y un reloj de salida con latencia. Un clic que
/// la persona hace al oír una posición tiene que quedar en esa posición de la toma, a la muestra.
/// </summary>
public sealed class VoiceOverRecorderTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48_000;
    private const long QpcPerSecond = 10_000_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-voz-en-off-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(-0.300, 0, false)]
    [InlineData(0.137, 0, false)]
    [InlineData(-0.050, 3, true)]
    public void A_click_made_on_hearing_a_position_lands_on_that_position(double captureLead, int droppedPackets, bool discontinuity)
    {
        // La salida empieza a sonar el cursor (12 s) en el instante QPC 5 s; la entrada empieza captureLead antes o después.
        Time cursor = Time.FromSeconds(12);
        long heardStart = 5 * QpcPerSecond;
        Time? HeardAt(long qpc) => qpc < heardStart ? null : cursor + Time.FromSeconds((qpc - heardStart) / (double)QpcPerSecond);

        // La persona hace clic al oír 12,5 s y 14,25 s de la línea de tiempo.
        long[] clickQpc = [heardStart + (QpcPerSecond / 2), heardStart + (9 * QpcPerSecond / 4)];
        var capture = new FakeCapture();
        string path = Path.Combine(_folder, "toma.wav");
        using var recorder = new VoiceOverRecorder(capture, path, cursor, HeardAt);
        recorder.Begin();

        long captureStart = heardStart + (long)(captureLead * QpcPerSecond);
        const int packet = 480;
        for (int p = 0; p < 300; p++)
        {
            long first = captureStart + (p * packet * QpcPerSecond / Rate);
            if (p >= 100 && p < 100 + droppedPackets)
            {
                continue;
            }

            float[] samples = new float[packet];
            for (int i = 0; i < packet; i++)
            {
                long at = first + (i * QpcPerSecond / Rate);
                samples[i] = clickQpc.Any(c => Math.Abs(at - c) * Rate < QpcPerSecond / 2) ? 0.8f : 0.01f;
            }

            capture.Push(samples, first, discontinuity && p == 100 + droppedPackets);
        }

        VoiceOverTake take = recorder.Finish();
        float[] wave = ReadMono(take.Path);
        int[] found = [.. Enumerable.Range(0, wave.Length).Where(i => wave[i] > 0.5f)];
        output.WriteLine($"Entrada {captureLead * 1000:+0;−0} ms, {droppedPackets} paquetes perdidos: clics en las muestras {string.Join(", ", found)} " +
            $"(esperadas 24000 y 108000); toma de {take.Duration.ToSeconds():0.000} s desde {take.Start.ToSeconds()} s");
        Assert.Equal(cursor, take.Start);
        Assert.Equal([24_000, 108_000], found);
        Assert.Equal(take.Frames, wave.Length);
        Assert.Equal(captureLead > 0 ? 0f : 0.01f, wave[0]);
    }

    private static float[] ReadMono(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (id == "data")
            {
                return [.. Enumerable.Range(0, size / 4).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8 + (4 * i))))];
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException("Sin datos.");
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private CapturePacketHandler? _handler;
        private bool _stopped;

        public int SampleRate => Rate;

        public string DeviceName => "Micrófono de prueba";

        public void StartCapture(CapturePacketHandler handler) => _handler = handler;

        public void Push(float[] samples, long qpc, bool discontinuity)
        {
            if (!_stopped)
            {
                _handler?.Invoke(samples, qpc, discontinuity);
            }
        }

        public void StopCapture() => _stopped = true;

        public void Dispose()
        {
        }
    }
}
