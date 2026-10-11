using System.Diagnostics;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

// Presupuesto de memoria de video: reservas de los pools de D3D11VA y tamaño de la caché de cada decodificador.
internal sealed partial class TimelineScheduler
{
    /// <summary>VRAM de los pools ya creados, incluidos los de decodificadores que se están cerrando.</summary>
    private long Committed() => _decoders.Values.Where(d => d.PoolKnown).Sum(d => d.Reserved);

    /// <summary>Lo comprometido para decidir aperturas: los pools vivos y lo estimado de los que todavía abren.</summary>
    private long Planned() => _decoders.Values.Where(d => !d.Closed).Sum(d => d.Reserved);

    /// <summary>Uno por capa visible y, si alguna tiene cortes, dos más para el pre-roll.</summary>
    private int ExpectedDecoders()
    {
        int layers = Math.Max(1, _plan.Layers.Count);
        int withCuts = _plan.Layers.Count(l => l.Clips.Count > 1);
        return layers + Math.Min(2, withCuts);
    }

    private bool AnyClosingPool() => _decoders.Values.Any(d => d.Closed && d.PoolKnown && d.Reserved > 0);

    private void TrackPeak() => _vramPeak = Math.Max(_vramPeak, Committed());

    /// <summary>Lo mínimo que ocupará un decodificador nuevo del medio: referencias, superficies retenidas y la caché mínima.</summary>
    private long Estimate(MediaState media)
    {
        Variant? variant = media.Variant;
        if (media.LearnedSurfaceBytes > 0 && media.LearnedPath == variant?.Path)
        {
            return (media.LearnedReferences + HeldSurfaces + OverheadSurfaces + MinCacheFrames) * media.LearnedSurfaceBytes;
        }

        int width = variant?.Width > 0 ? variant.Width : media.DisplayWidth > 0 ? media.DisplayWidth : 1920;
        int height = variant?.Height > 0 ? variant.Height : media.DisplayHeight > 0 ? media.DisplayHeight : 1080;
        long surface = (long)((width + 15) & ~15) * ((height + 15) & ~15) * 3 / 2;
        int references = _decoderOptions.CompactReferencePool ? CompactReferenceEstimate : ReferenceEstimate;
        return (references + HeldSurfaces + OverheadSurfaces + MinCacheFrames) * surface;
    }

    /// <summary>
    /// Texturas extra del pool que el decodificador está por crear: las retenidas fuera de la caché y la caché que quepa
    /// en el presupuesto, repartido entre los decodificadores que se esperan (uno por capa visible y dos de pre-roll si hay cortes).
    /// </summary>
    /// <remarks>
    /// Corre en el hilo del decodificador, al crear el pool. Si no cabe porque otros pools todavía se están liberando,
    /// espera a que terminen (como mucho <see cref="PoolWait"/>): así el presupuesto acota la VRAM de verdad.
    /// </remarks>
    private int SizePool(DecoderState decoder, HardwarePoolRequest request)
    {
        long waitStart = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (_gate)
            {
                if (decoder.Closed || _disposed)
                {
                    return HeldSurfaces + MinCacheFrames;
                }

                long surface = Math.Max(1, request.SurfaceBytes);
                long estimated = decoder.Reserved;
                long others = Planned() - estimated;
                int fixedSurfaces = request.ReferenceSurfaces + HeldSurfaces + OverheadSurfaces;
                int expected = Math.Max(_decoders.Values.Count(d => !d.Closed), ExpectedDecoders());
                long fit = ((_vramBudget - others) / surface) - fixedSurfaces;
                long share = (_vramBudget / expected / surface) - fixedSurfaces;
                long byCache = Math.Max(2, _maxCacheBytes / surface);
                int limit = (int)Math.Clamp(Math.Min(Math.Min(fit, share), byCache), MinCacheFrames, UsefulCacheFrames(decoder, request));
                long pool = (fixedSurfaces + limit) * surface;
                bool wait = Committed() + pool > _vramBudget && AnyClosingPool() && Stopwatch.GetElapsedTime(waitStart) < PoolWait;
                if (!wait)
                {
                    double waited = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
                    if (waited > 1)
                    {
                        _poolWaits++;
                        _poolWaitMs += waited;
                    }

                    return CommitPool(decoder, request, surface, limit, others + estimated <= _vramBudget && others + pool > _vramBudget);
                }
            }

            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// La caché que le sirve al decodificador: lo adelantado, el pre-roll y dos de margen; en reversa, todo lo que admite
    /// el pool (un GOP y lo adelantado, o lo más cerca de eso que se pueda: si no cabe, el GOP va en varias pasadas).
    /// </summary>
    private int UsefulCacheFrames(DecoderState decoder, HardwarePoolRequest request) =>
        decoder.ReverseClip is null
            ? _usefulCacheFrames
            : Math.Max(MinCacheFrames, VideoDecoder.MaxHardwarePoolSurfaces - request.ReferenceSurfaces - HeldSurfaces);

    private int CommitPool(DecoderState decoder, HardwarePoolRequest request, long surface, int limit, bool crossedBudget)
    {
        _cache.SetSourceLimit(decoder.Id, limit);
        decoder.CacheLimit = limit;
        decoder.Reserved = (request.ReferenceSurfaces + HeldSurfaces + OverheadSurfaces + limit) * surface;
        decoder.PoolKnown = true;
        if (!decoder.Overrun && crossedBudget)
        {
            // La estimación al abrir se quedó corta (el primer decodificador de un medio aún no se conoce).
            decoder.Overrun = true;
            _vramOverruns++;
        }

        MediaState media = decoder.Media;
        media.LearnedPath = decoder.Variant?.Path;
        media.LearnedReferences = request.ReferenceSurfaces;
        media.LearnedSurfaceBytes = surface;
        TrackPeak();
        return HeldSurfaces + limit;
    }
}
