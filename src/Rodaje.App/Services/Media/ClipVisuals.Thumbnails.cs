using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;

namespace Rodaje.App.Services.Media;

public sealed partial class ClipVisuals
{
    private void EnqueueThumbnails(string path, int height, List<long> times)
    {
        long[] sorted = [.. times.Distinct().Order()];
        string signature = $"{path}|{height}|{sorted[0]}|{sorted[^1]}|{sorted.Length}";
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_batchesBySignature.Remove(signature, out Batch? existing))
            {
                _batches.Remove(existing);
            }

            var batch = new Batch(path, height, sorted, ++_sequence, signature);
            _batches.Add(batch);
            _batchesBySignature[signature] = batch;
            while (_batches.Count > MaxQueuedBatches)
            {
                Batch oldest = _batches.Max!;
                _batches.Remove(oldest);
                _batchesBySignature.Remove(oldest.Signature);
            }

            Monitor.PulseAll(_gate);
        }
    }

    private void ThumbnailLoop()
    {
        var readers = new List<OpenReader>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Batch? batch;
                lock (_gate)
                {
                    while (_batches.Count == 0 && !_disposed)
                    {
                        if (!Monitor.Wait(_gate, TimeSpan.FromSeconds(2)))
                        {
                            CloseIdle(readers);
                        }
                    }

                    if (_disposed)
                    {
                        return;
                    }

                    batch = _batches.Min!;
                    _batches.Remove(batch);
                    _batchesBySignature.Remove(batch.Signature);
                }

                ProcessBatch(batch, readers);
                CloseIdle(readers);
            }
        }
        finally
        {
            foreach (OpenReader open in readers)
            {
                DisposeQuietly(open.Reader);
            }
        }
    }

    private void ProcessBatch(Batch batch, List<OpenReader> readers)
    {
        string? fingerprint = null;
        for (int i = batch.Next; i < batch.Times.Length; i++)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            if (i > batch.Next && YieldTo(batch, i))
            {
                return;
            }

            var key = new ThumbnailKey(batch.Path, batch.Times[i], batch.Height);
            if (_thumbnails.Contains(key) || !Claim(key))
            {
                // Hecha, o la está haciendo otro hilo: llegará con su aviso.
                continue;
            }

            try
            {
                fingerprint ??= FingerprintOf(batch.Path);
                BgraImage? image = _thumbnailDisk?.TryRead(fingerprint, batch.Height, batch.Times[i]);
                if (image is not null)
                {
                    Interlocked.Increment(ref _diskHits);
                }
                else
                {
                    IThumbnailReader reader = ReaderFor(batch.Path, readers);
                    image = reader.Decode(Time.FromTicks(batch.Times[i]), batch.Height, _stop.Token);
                    if (image is null)
                    {
                        // Sin ningún cuadro decodificable: se recuerda para no volver a abrirlo en cada redibujo.
                        Fail(batch.Path, "El archivo no tiene cuadros de video.", readers);
                        return;
                    }

                    Interlocked.Increment(ref _decodedCount);
                    _thumbnailDisk?.Write(fingerprint, batch.Height, batch.Times[i], image);
                }

                _thumbnails.Add(key, image);
                lock (_gate)
                {
                    string index = IndexKey(key.Path, key.Height);
                    if (!_indexByFile.TryGetValue(index, out SortedSet<long>? times))
                    {
                        _indexByFile[index] = times = [];
                    }

                    times.Add(key.TimeTicks);
                }

                MarkChanged(batch.Path, ClipVisualKind.Thumbnails);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Cualquier fallo de un archivo, incluso inesperado, se queda en ese archivo: el hilo sigue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Fail(batch.Path, ex.Message, readers);
                return;
            }
            finally
            {
                Release(key);
            }
        }
    }

    private void Fail(string path, string reason, List<OpenReader> readers)
    {
        Close(path, readers);
        _failedThumbnails[path] = reason;
        MarkChanged(path, ClipVisualKind.Thumbnails);
    }

    private static string IndexKey(string path, int height) => path.ToUpperInvariant() + "|" + height.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long? Nearest(SortedSet<long> times, long target)
    {
        if (times.Count == 0)
        {
            return null;
        }

        long? below = times.GetViewBetween(long.MinValue, target) is { Count: > 0 } low ? low.Max : null;
        long? above = times.GetViewBetween(target, long.MaxValue) is { Count: > 0 } high ? high.Min : null;
        return (below, above) switch
        {
            ({ } b, { } a) => target - b <= a - target ? b : a,
            ({ } b, null) => b,
            (null, { } a) => a,
            _ => null,
        };
    }

    private bool Claim(ThumbnailKey key)
    {
        lock (_gate)
        {
            return _inFlight.Add(key);
        }
    }

    private void Release(ThumbnailKey key)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
        }
    }

    /// <summary>Si llegó una tanda más nueva, la actual vuelve a la cola desde <paramref name="next"/> y cede el paso.</summary>
    private bool YieldTo(Batch batch, int next)
    {
        lock (_gate)
        {
            if (_batches.Count == 0 || _batches.Min!.Sequence < batch.Sequence)
            {
                return false;
            }

            if (!_batchesBySignature.ContainsKey(batch.Signature))
            {
                batch.Next = next;
                _batches.Add(batch);
                _batchesBySignature[batch.Signature] = batch;
            }

            return true;
        }
    }

    private IThumbnailReader ReaderFor(string path, List<OpenReader> readers)
    {
        int index = readers.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            OpenReader found = readers[index];
            readers[index] = found with { LastUsed = Stopwatch.GetTimestamp() };
            return found.Reader;
        }

        while (readers.Count >= Math.Max(1, _options.MaxOpenDecoders))
        {
            OpenReader oldest = readers.MinBy(r => r.LastUsed)!;
            DisposeQuietly(oldest.Reader);
            readers.Remove(oldest);
        }

        IThumbnailReader reader = _sources.OpenThumbnails(path);
        readers.Add(new OpenReader(path, reader, Stopwatch.GetTimestamp()));
        return reader;
    }

    private static void Close(string path, List<OpenReader> readers)
    {
        int index = readers.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            DisposeQuietly(readers[index].Reader);
            readers.RemoveAt(index);
        }
    }

    private static void DisposeQuietly(IThumbnailReader reader)
    {
        try
        {
            reader.Dispose();
        }
#pragma warning disable CA1031 // Un decodificador que falla al cerrarse no debe tumbar el hilo.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void CloseIdle(List<OpenReader> readers)
    {
        for (int i = readers.Count - 1; i >= 0; i--)
        {
            if (Stopwatch.GetElapsedTime(readers[i].LastUsed) > _options.DecoderIdleTimeout)
            {
                DisposeQuietly(readers[i].Reader);
                readers.RemoveAt(i);
            }
        }
    }

    private sealed class Batch(string path, int height, long[] times, long sequence, string signature)
    {
        public string Path { get; } = path;

        public int Height { get; } = height;

        public long[] Times { get; } = times;

        public long Sequence { get; } = sequence;

        public string Signature { get; } = signature;

        public int Next { get; set; }
    }

    private sealed record OpenReader(string Path, IThumbnailReader Reader, long LastUsed);
}
