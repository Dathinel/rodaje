#if GRAFICOS_TERRAFX
using System.Diagnostics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.XAUDIO2;

namespace AotInterop;

// XAudio2 con TerraFX.Interop.Windows.
internal static unsafe class AudioXAudio2
{
    public static void Reproducir(float[] estereo48k)
    {
        const int Hz = 48000;
        var arranque = Stopwatch.StartNew();
        IXAudio2* motor;
        Comprobar(XAudio2Create(&motor, 0, XAUDIO2_DEFAULT_PROCESSOR), "XAudio2Create");
        IXAudio2MasteringVoice* maestra;
        Comprobar(motor->CreateMasteringVoice(&maestra, 2, Hz, 0, null, null, AUDIO_STREAM_CATEGORY.AudioCategory_GameEffects), "CreateMasteringVoice");
        var formato = new WAVEFORMATEX
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = 2,
            nSamplesPerSec = Hz,
            nAvgBytesPerSec = Hz * 8,
            nBlockAlign = 8,
            wBitsPerSample = 32,
        };
        IXAudio2SourceVoice* voz;
        Comprobar(motor->CreateSourceVoice(&voz, &formato, 0, XAUDIO2_DEFAULT_FREQ_RATIO, null, null, null), "CreateSourceVoice");
        Console.WriteLine($"XAudio2 (TerraFX): motor y voces creados en {arranque.Elapsed.TotalMilliseconds:0.0} ms");

        fixed (float* datos = estereo48k)
        {
            var buffer = new XAUDIO2_BUFFER
            {
                Flags = XAUDIO2_END_OF_STREAM,
                AudioBytes = (uint)(estereo48k.Length * sizeof(float)),
                pAudioData = (byte*)datos,
            };
            Comprobar(voz->SubmitSourceBuffer(&buffer, null), "SubmitSourceBuffer");
            var registro = new RegistroReloj("XAudio2", Hz);
            ulong total = (ulong)(estereo48k.Length / 2);
            var reloj = Stopwatch.StartNew();
            Comprobar(voz->Start(0, 0), "Start");
            while (reloj.Elapsed.TotalSeconds < total / (double)Hz + 0.5)
            {
                XAUDIO2_VOICE_STATE estado;
                voz->GetState(&estado, 0);
                registro.Anotar(reloj.Elapsed.TotalSeconds, estado.SamplesPlayed / (double)Hz);
                if (estado.SamplesPlayed >= total)
                    break;
                Thread.Sleep(10);
            }
            XAUDIO2_PERFORMANCE_DATA p;
            motor->GetPerformanceData(&p);
            Console.WriteLine($"  latencia actual del motor: {p.CurrentLatencyInSamples} muestras ({p.CurrentLatencyInSamples * 1000.0 / Hz:0.0} ms), " +
                $"glitches={p.GlitchesSinceEngineStarted}");
            registro.Resumir();
            voz->Stop(0, 0);
        }
        voz->DestroyVoice();
        maestra->DestroyVoice();
        motor->Release();
    }

    private static void Comprobar(HRESULT hr, string que)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{que}: HRESULT 0x{hr.Value:X8}");
    }
}
#endif
