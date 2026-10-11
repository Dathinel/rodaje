using Rodaje.Media.Probe;
using Rodaje.Playback;

namespace Rodaje.Media.Video;

/// <summary>Opciones de <see cref="ReverseFrameReader"/>.</summary>
public sealed record ReverseFrameReaderOptions
{
    /// <summary>
    /// Tope de cuadros decodificados que el lector retiene a la vez (<c>M</c>), contando el tramo que entrega y el que
    /// decodifica por delante. Con D3D11VA el pool del decodificador necesita al menos estas superficies de sobra.
    /// </summary>
    public int MaxRetained { get; init; } = 64;

    /// <summary>
    /// Decodifica el tramo siguiente en otro hilo mientras se entrega el actual: la ventana de dos GOP. Sin ella, cada
    /// tramo se decodifica cuando el anterior se acabó y el tramo puede llegar a <see cref="MaxRetained"/> cuadros.
    /// </summary>
    public bool Lookahead { get; init; } = true;

    /// <summary>Solo se entregan los cuadros para los que devuelve verdadero (los que se verán); nulo, todos.</summary>
    public Func<long, bool>? Wanted { get; init; }
}

/// <summary>
/// Entrega los cuadros de un tramo del origen de mayor a menor índice, exactos: cada tramo de la lectura se decodifica
/// hacia adelante desde su keyframe (<see cref="KeyframeIndex.AtOrBefore"/>), guardando solo los cuadros que se verán, y
/// se entrega al revés. Es determinista: el plan de tramos depende solo de los keyframes, de lo pedido y de <c>M</c>.
/// </summary>
/// <remarks>
/// <para>
/// Plan (<see cref="Plan"/>): se recorren los GOP de arriba abajo; los GOP enteros se juntan en un tramo mientras quepan
/// en <c>C</c> cuadros (<c>M</c>/2 con <see cref="ReverseFrameReaderOptions.Lookahead"/>, <c>M</c> sin ella), porque
/// juntarlos ahorra seeks sin decodificar nada de más. Un GOP con más cuadros que <c>C</c> se parte desde arriba: primero
/// sus últimos <c>C</c> y, en otra pasada desde el mismo keyframe, los anteriores (dos pasadas, o más si hace falta).
/// </para>
/// <para>
/// Con ventana, mientras se entrega un tramo el siguiente se decodifica en otro hilo; el decodificador nunca se usa desde
/// dos hilos a la vez. <see cref="Next"/> se llama desde un solo hilo. Los cuadros entregados son del llamador.
/// </para>
/// </remarks>
public sealed class ReverseFrameReader : IDisposable
{
    private readonly VideoDecoder _decoder;
    private readonly IReadOnlyList<long[]> _chunks;
    private readonly bool _lookahead;
    private readonly CancellationTokenSource _cancel = new();
    private readonly long _decodedAtStart;
    private Stack<DecodedFrame> _current = new();
    private Task<Stack<DecodedFrame>>? _pending;
    private int _nextChunk;
    private bool _disposed;

    /// <param name="decoder">Decodificador del medio; mientras el lector vive no se usa para otra cosa.</param>
    /// <param name="keyframes">Keyframes del mismo archivo (<see cref="KeyframeIndex.Read"/>).</param>
    /// <param name="first">Cuadro más bajo del tramo (el último que se entrega).</param>
    /// <param name="last">Cuadro más alto del tramo (el primero que se entrega).</param>
    /// <param name="options">Tope de cuadros retenidos, ventana y qué cuadros se entregan.</param>
    public ReverseFrameReader(VideoDecoder decoder, KeyframeIndex keyframes, long first, long last, ReverseFrameReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        options ??= new ReverseFrameReaderOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRetained, options.Lookahead ? 2 : 1);
        _decoder = decoder;
        _lookahead = options.Lookahead;
        int chunk = options.Lookahead ? options.MaxRetained / 2 : options.MaxRetained;
        _chunks = Plan(keyframes, first, Math.Min(last, Math.Max(first, keyframes.FrameCount - 1)), chunk, options.Wanted);
        _decodedAtStart = decoder.Statistics.DecodedFrames;
    }

    /// <summary>Tramos que se decodifican, del primero que se entrega al último; cada uno en orden ascendente.</summary>
    public IReadOnlyList<long[]> Chunks => _chunks;

    /// <summary>Cuadros entregados hasta ahora.</summary>
    public long Delivered { get; private set; }

    /// <summary>Cuadros que salieron del decodificador desde que se creó el lector, incluidos los que no se entregan.</summary>
    public long DecodedFrames => _decoder.Statistics.DecodedFrames - _decodedAtStart;

    /// <summary>
    /// Tramos de decodificación de los cuadros <paramref name="wanted"/> de <c>[first, last]</c>, del primero que se
    /// entrega (el más alto) al último, cada uno en orden ascendente y con <paramref name="chunkFrames"/> cuadros como mucho.
    /// </summary>
    public static IReadOnlyList<long[]> Plan(KeyframeIndex keyframes, long first, long last, int chunkFrames, Func<long, bool>? wanted = null)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkFrames);
        var chunks = new List<long[]>();
        var current = new List<long>();
        var gop = new List<long>();
        long top = last;
        while (top >= first)
        {
            long low = Math.Max(first, keyframes.AtOrBefore(top) ?? first);
            gop.Clear();
            for (long i = low; i <= top; i++)
            {
                if (wanted is null || wanted(i))
                {
                    gop.Add(i);
                }
            }

            top = low - 1;
            if (gop.Count == 0)
            {
                continue;
            }

            if (current.Count + gop.Count <= chunkFrames)
            {
                // Un GOP más abajo se decodifica en la misma pasada: se junta por delante (orden ascendente).
                current.InsertRange(0, gop);
                continue;
            }

            Flush(chunks, current);
            int remainder = gop.Count;
            while (remainder > chunkFrames)
            {
                chunks.Add([.. gop.GetRange(remainder - chunkFrames, chunkFrames)]);
                remainder -= chunkFrames;
            }

            current.AddRange(gop.GetRange(0, remainder));
        }

        Flush(chunks, current);
        return chunks;
    }

    /// <summary>El siguiente cuadro en orden descendente; nulo cuando no quedan.</summary>
    /// <remarks>El llamador recibe la única referencia del cuadro y debe liberarlo.</remarks>
    public DecodedFrame? Next(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current.Count == 0)
        {
            if (_pending is { } pending)
            {
                _pending = null;
                try
                {
                    pending.Wait(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Sigue decodificándose; Dispose lo espera y libera lo que traiga.
                    _pending = pending;
                    throw;
                }
                catch (AggregateException)
                {
                    // GetResult relanza la excepción original del tramo.
                }

                _current = pending.GetAwaiter().GetResult();
            }
            else if (_nextChunk < _chunks.Count)
            {
                _current = Decode(_chunks[_nextChunk++], cancellationToken);
            }
            else
            {
                return null;
            }

            if (_lookahead && _nextChunk < _chunks.Count)
            {
                long[] next = _chunks[_nextChunk++];
                CancellationToken token = _cancel.Token;
                _pending = Task.Run(() => Decode(next, token), token);
            }
        }

        if (_current.Count == 0)
        {
            return Next(cancellationToken);
        }

        Delivered++;
        return _current.Pop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancel.Cancel();
        if (_pending is { } pending)
        {
            try
            {
                pending.Wait();
                Release(pending.Result);
            }
            catch (AggregateException)
            {
            }
        }

        Release(_current);
        _cancel.Dispose();
    }

    private Stack<DecodedFrame> Decode(long[] ascending, CancellationToken cancellationToken)
    {
        var frames = new Stack<DecodedFrame>(ascending.Length);
        try
        {
            foreach (long index in ascending)
            {
                if (_decoder.GetFrame(index, cancellationToken) is { } frame)
                {
                    frames.Push(frame);
                }
            }

            return frames;
        }
        catch
        {
            Release(frames);
            throw;
        }
    }

    private static void Release(Stack<DecodedFrame> frames)
    {
        while (frames.Count > 0)
        {
            frames.Pop().Dispose();
        }
    }

    private static void Flush(List<long[]> chunks, List<long> current)
    {
        if (current.Count > 0)
        {
            chunks.Add([.. current]);
            current.Clear();
        }
    }
}
