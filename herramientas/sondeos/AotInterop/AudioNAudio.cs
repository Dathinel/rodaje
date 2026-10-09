#if VARIANTE_NAUDIO
using System.Diagnostics;
using NAudio.Wave;

namespace AotInterop;

// NAudio 3.1 ya usa [GeneratedComInterface]; aquí se mide si compila sin advertencias y su reloj.
internal static class AudioNAudio
{
    public static void Reproducir(float[] estereo48k)
    {
        Medir(estereo48k, bajaLatencia: false);
        Medir(estereo48k, bajaLatencia: true);
    }

    private static void Medir(float[] estereo48k, bool bajaLatencia)
    {
        const int Hz = 48000;
        var arranque = Stopwatch.StartNew();
        var bytes = new byte[estereo48k.Length * sizeof(float)];
        Buffer.BlockCopy(estereo48k, 0, bytes, 0, bytes.Length);
        using var fuente = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(Hz, 2));
        var constructor = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(20);
        if (bajaLatencia)
            constructor = constructor.WithLowLatency(false);
        using WasapiPlayer salida = constructor.Build();
        salida.Init(fuente);
        string nombre = bajaLatencia ? "NAudio baja lat." : "NAudio";
        Console.WriteLine($"{nombre} WasapiPlayer: listo en {arranque.Elapsed.TotalMilliseconds:0.0} ms, latencia {salida.LatencyMilliseconds} ms, " +
            $"IAudioClient3 activo={salida.LowLatencyActive} {salida.LowLatencyUnavailableReason}");
        var registro = new RegistroReloj(nombre, Hz);
        var reloj = Stopwatch.StartNew();
        salida.Play();
        while (salida.PlaybackState == PlaybackState.Playing && reloj.Elapsed.TotalSeconds < 3)
        {
            long posBytes = salida.GetPosition();
            registro.Anotar(reloj.Elapsed.TotalSeconds, posBytes / (double)salida.OutputWaveFormat.AverageBytesPerSecond);
            Thread.Sleep(10);
        }
        registro.Resumir();
    }
}
#endif
