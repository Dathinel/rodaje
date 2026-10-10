using Rodaje.Core.Timing;
using Rodaje.Media;
using Rodaje.Media.Audio;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;

namespace Rodaje.App.Services.Media;

/// <summary>Lee miniaturas de un archivo; vive en un solo hilo de trabajo.</summary>
public interface IThumbnailReader : IDisposable
{
    BgraImage? Decode(Time sourceTime, int height, CancellationToken cancellationToken);
}

/// <summary>Lee el audio de un archivo de principio a fin.</summary>
public interface IAudioSampleReader : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    /// <summary>Muestras por canal que se esperan (para reservar), o 0 si no se sabe.</summary>
    long ExpectedSamples { get; }

    /// <summary>Llena el búfer intercalado y devuelve los cuadros de muestra leídos; 0 al final.</summary>
    int Read(Span<float> interleaved);
}

/// <summary>De dónde salen las miniaturas, el audio y la huella de cada archivo; en las pruebas se reemplaza.</summary>
public interface IVisualsSources
{
    /// <exception cref="MediaException">No se puede abrir o no tiene video.</exception>
    IThumbnailReader OpenThumbnails(string path);

    /// <summary>El audio del archivo, o <c>null</c> si no tiene.</summary>
    IAudioSampleReader? OpenAudio(string path);

    /// <summary>Huella del contenido (cambia si el archivo cambia), para la caché en disco.</summary>
    string Fingerprint(string path);
}

/// <summary>Con FFmpeg: <see cref="ThumbnailDecoder"/>, <see cref="AudioDecoder"/> y <see cref="SourceFingerprint"/>.</summary>
public sealed partial class FfmpegVisualsSources : IVisualsSources
{
    public IThumbnailReader OpenThumbnails(string path) => new Thumbnails(ThumbnailDecoder.Open(path));

    public IAudioSampleReader? OpenAudio(string path)
    {
        try
        {
            return new Audio(AudioDecoder.Open(path));
        }
        catch (MediaException) when (File.Exists(path))
        {
            // Sin flujo de audio (o uno que FFmpeg no decodifica): no hay forma de onda.
            return null;
        }
    }

    public string Fingerprint(string path) => SourceFingerprint.Compute(path).Key;

    private sealed partial class Thumbnails(ThumbnailDecoder decoder) : IThumbnailReader
    {
        public BgraImage? Decode(Time sourceTime, int height, CancellationToken cancellationToken) =>
            decoder.Decode(sourceTime, height, cancellationToken);

        public void Dispose() => decoder.Dispose();
    }

    private sealed partial class Audio(AudioDecoder decoder) : IAudioSampleReader
    {
        public int SampleRate => AudioDecoder.OutputSampleRate;

        public int Channels => AudioDecoder.OutputChannels;

        public long ExpectedSamples => decoder.Duration.ToSamples(AudioDecoder.OutputSampleRate);

        public int Read(Span<float> interleaved) => decoder.Read(interleaved);

        public void Dispose() => decoder.Dispose();
    }
}
