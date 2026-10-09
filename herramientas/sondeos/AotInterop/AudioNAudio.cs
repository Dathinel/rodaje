#if VARIANTE_NAUDIO
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AotInterop;

// Solo para medir: ¿compila NAudio.Wasapi con Native AOT sin advertencias y funciona?
internal static class AudioNAudio
{
    public static void Reproducir(float[] estereo48k)
    {
        const int Hz = 48000;
        var arranque = Stopwatch.StartNew();
        var bytes = new byte[estereo48k.Length * sizeof(float)];
        Buffer.BlockCopy(estereo48k, 0, bytes, 0, bytes.Length);
        var fuente = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(Hz, 2));
        using var salida = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        salida.Init(fuente);
        Console.WriteLine($"NAudio WasapiOut: listo en {arranque.Elapsed.TotalMilliseconds:0.0} ms");
        var registro = new RegistroReloj("NAudio", Hz);
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
