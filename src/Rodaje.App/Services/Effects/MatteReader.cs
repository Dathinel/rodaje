using System.Collections.Concurrent;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Services.Effects;

/// <summary>
/// Lee los cuadros de las máscaras de imagen (video gris FFV1 en <c>&lt;proyecto&gt;.medios</c>) cuando los pide el render.
/// Un decodificador por archivo, que avanza sin buscar mientras los cuadros van hacia delante; el último cuadro se guarda,
/// así un cuadro repetido (en pausa) no se vuelve a leer. Un archivo que no se puede abrir no se reintenta enseguida.
/// </summary>
/// <remarks>
/// Cada archivo deja su decodificador abierto (memoria nativa y el archivo tomado) y su último cuadro: al abrir otro
/// proyecto <see cref="Clear"/> los cierra todos, o se quedaban abiertos hasta cerrar la app.
/// </remarks>
internal sealed class MatteReader
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IMatteDecoder> _open;

    /// <param name="open">Abre el decodificador de un archivo; por omisión, <see cref="ThumbnailDecoder"/> (las pruebas dan uno falso).</param>
    public MatteReader(Func<string, IMatteDecoder>? open = null)
    {
        _open = open ?? (path => new ThumbnailMatteDecoder(ThumbnailDecoder.Open(path)));
    }

    /// <summary>Archivos con su lector en memoria (para las pruebas de fugas).</summary>
    internal int OpenSources => _sources.Count;

    public bool TryGet(string path, double seconds, out MatteFrame? frame)
    {
        frame = null;
        Source source = _sources.GetOrAdd(path, p => new Source(p, _open));
        return source.TryRead(seconds, out frame);
    }

    /// <summary>Olvida un archivo (por ejemplo, porque «Quitar fondo» lo volvió a escribir).</summary>
    public void Forget(string path)
    {
        if (_sources.TryRemove(path, out Source? source))
        {
            source.Close();
        }
    }

    /// <summary>Cierra todos los decodificadores y suelta sus cuadros (se abrió otro proyecto).</summary>
    public void Clear()
    {
        foreach (string path in _sources.Keys)
        {
            Forget(path);
        }
    }

    private sealed class Source(string path, Func<string, IMatteDecoder> open)
    {
        private readonly Lock _gate = new();
        private IMatteDecoder? _decoder;
        private bool _closed;
        // Nulo mientras no falló. Con long.MinValue, «ahora - _failedAt» se desbordaba a negativo y parecía un fallo
        // reciente: el decodificador no se abría nunca y las máscaras de imagen no se veían.
        private long? _failedAt;
        private long _lastIndex = long.MinValue;
        private MatteFrame? _last;
        private long _stamp;

        public bool TryRead(double seconds, out MatteFrame? frame)
        {
            lock (_gate)
            {
                frame = null;
                if (_closed)
                {
                    // Otro hilo lo cerró mientras este esperaba: no se reabre un decodificador que ya nadie cerraría.
                    return false;
                }

                if (_decoder is null)
                {
                    if ((_failedAt is { } failedAt && Environment.TickCount64 - failedAt < RetryAfter.TotalMilliseconds) || !File.Exists(path))
                    {
                        return false;
                    }

                    try
                    {
                        _decoder = open(path);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
                        or Rodaje.Media.MediaException)
                    {
                        _failedAt = Environment.TickCount64;
                        return false;
                    }
                }

                Rational rate = _decoder.FrameRate;
                double fps = rate.Num > 0 && rate.Den > 0 ? (double)rate.Num / rate.Den : 30;
                long index = (long)Math.Floor(Math.Max(0, seconds) * fps + 1e-6);
                if (index == _lastIndex && _last is not null)
                {
                    frame = _last;
                    return true;
                }

                int height = Math.Clamp(_decoder.DisplayHeight, 1, 2160);
                BgraImage? image;
                try
                {
                    image = _decoder.Decode(Time.FromSeconds((index + 0.5) / fps), height);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or Rodaje.Media.MediaException)
                {
                    image = null;
                }

                if (image is null)
                {
                    return false;
                }

                var gray = new byte[image.Width * image.Height];
                ReadOnlySpan<byte> pixels = image.Pixels;
                for (int i = 0; i < gray.Length; i++)
                {
                    gray[i] = pixels[(i * 4) + 1];
                }

                _last = new MatteFrame(image.Width, image.Height, gray, ++_stamp);
                _lastIndex = index;
                frame = _last;
                return true;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _decoder?.Dispose();
                _decoder = null;
                _last = null;
            }
        }
    }
}

/// <summary>Lo que <see cref="MatteReader"/> necesita de un decodificador.</summary>
internal interface IMatteDecoder : IDisposable
{
    Rational FrameRate { get; }

    int DisplayHeight { get; }

    BgraImage? Decode(Time sourceTime, int height);
}

internal sealed class ThumbnailMatteDecoder(ThumbnailDecoder decoder) : IMatteDecoder
{
    public Rational FrameRate => decoder.Stream.FrameRate;

    public int DisplayHeight => decoder.Stream.DisplayHeight;

    public BgraImage? Decode(Time sourceTime, int height) => decoder.Decode(sourceTime, height);

    public void Dispose() => decoder.Dispose();
}
