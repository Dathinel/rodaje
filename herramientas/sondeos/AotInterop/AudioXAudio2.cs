#if !GRAFICOS_TERRAFX
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace AotInterop;

// XAudio2 con Vortice.XAudio2.
internal static class AudioXAudio2
{
    public static void Reproducir(float[] estereo48k)
    {
        const int Hz = 48000;
        var arranque = Stopwatch.StartNew();
        using IXAudio2 motor = XAudio2.XAudio2Create(ProcessorSpecifier.UseDefaultProcessor);
        using IXAudio2MasteringVoice maestra = motor.CreateMasteringVoice(2, Hz);
        using IXAudio2SourceVoice voz = motor.CreateSourceVoice(WaveFormat.CreateIeeeFloatWaveFormat(Hz, 2));
        Console.WriteLine($"XAudio2 (Vortice): motor y voces creados en {arranque.Elapsed.TotalMilliseconds:0.0} ms");

        var fijado = GCHandle.Alloc(estereo48k, GCHandleType.Pinned);
        try
        {
            using var buffer = new AudioBuffer(fijado.AddrOfPinnedObject(), (uint)(estereo48k.Length * sizeof(float)), BufferFlags.EndOfStream);
            voz.SubmitSourceBuffer(buffer);
            var registro = new RegistroReloj("XAudio2", Hz);
            ulong total = (ulong)(estereo48k.Length / 2);
            var reloj = Stopwatch.StartNew();
            voz.Start();
            while (reloj.Elapsed.TotalSeconds < total / (double)Hz + 0.5)
            {
                ulong reproducidas = voz.State.SamplesPlayed;
                registro.Anotar(reloj.Elapsed.TotalSeconds, reproducidas / (double)Hz);
                if (reproducidas >= total)
                    break;
                Thread.Sleep(10);
            }
            PerformanceData p = motor.PerformanceData;
            Console.WriteLine($"  latencia actual del motor: {p.CurrentLatencyInSamples} muestras ({p.CurrentLatencyInSamples * 1000.0 / Hz:0.0} ms), " +
                $"glitches={p.GlitchesSinceEngineStarted}");
            registro.Resumir();
            voz.Stop();
        }
        finally
        {
            fijado.Free();
        }
    }
}
#endif
