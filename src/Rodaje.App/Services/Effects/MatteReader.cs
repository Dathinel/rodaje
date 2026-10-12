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
internal sealed class MatteReader
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, Source> _sources = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string path, double seconds, out MatteFrame? frame)
    {
        frame = null;
        Source source = _sources.GetOrAdd(path, p => new Source(p));
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

    private sealed class Source(string path)
    {
        private readonly Lock _gate = new();
        private ThumbnailDecoder? _decoder;
        private long _failedAt = long.MinValue;
        private long _lastIndex = long.MinValue;
        private MatteFrame? _last;
        private long _stamp;

        public bool TryRead(double seconds, out MatteFrame? frame)
        {
            lock (_gate)
            {
                frame = null;
                if (_decoder is null)
                {
                    if (Environment.TickCount64 - _failedAt < RetryAfter.TotalMilliseconds || !File.Exists(path))
                    {
                        return false;
                    }

                    try
                    {
                        _decoder = ThumbnailDecoder.Open(path);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException
                        or Rodaje.Media.MediaException)
                    {
                        _failedAt = Environment.TickCount64;
                        return false;
                    }
                }

                Rational rate = _decoder.Stream.FrameRate;
                double fps = rate.Num > 0 && rate.Den > 0 ? (double)rate.Num / rate.Den : 30;
                long index = (long)Math.Floor(Math.Max(0, seconds) * fps + 1e-6);
                if (index == _lastIndex && _last is not null)
                {
                    frame = _last;
                    return true;
                }

                int height = Math.Clamp(_decoder.Stream.DisplayHeight, 1, 2160);
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
                _decoder?.Dispose();
                _decoder = null;
                _last = null;
            }
        }
    }
}
