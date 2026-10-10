using System.Diagnostics;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Media.Proxies;

/// <summary>Mide en este equipo a cuántas veces el tiempo real se decodifica un video.</summary>
public static class DecodeSpeedProbe
{
    /// <summary>
    /// Decodifica en orden hasta <paramref name="frames"/> cuadros desde <paramref name="startFrame"/> (sin contar el
    /// primero, que paga la apertura y el seek) y devuelve cuadros por segundo dividido por la tasa del video.
    /// </summary>
    /// <remarks>Usa las mismas opciones que la reproducción, así que con un dispositivo mide D3D11VA.</remarks>
    public static double Measure(string path, VideoDecoderOptions? options = null, int frames = 90, long startFrame = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 2);
        using VideoDecoder decoder = VideoDecoder.Open(path, options);
        long first = Math.Clamp(startFrame, 0, Math.Max(0, decoder.FrameCount - frames));
        using (DecodedFrame? warmup = decoder.GetFrame(first, cancellationToken))
        {
            if (warmup is null)
            {
                throw new MediaException($"No se pudo decodificar el cuadro {first} de {path} para medir la velocidad.");
            }
        }

        var watch = Stopwatch.StartNew();
        int decoded = 0;
        for (long n = first + 1; n < first + frames; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n, cancellationToken);
            if (frame is null)
            {
                break;
            }

            decoded++;
        }

        watch.Stop();
        if (decoded == 0)
        {
            throw new MediaException($"{path} tiene muy pocos cuadros para medir la velocidad de decodificación.");
        }

        double fps = decoded / Math.Max(watch.Elapsed.TotalSeconds, 1e-6);
        return fps / decoder.FrameRate.ToDouble();
    }
}
