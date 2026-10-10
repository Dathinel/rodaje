using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Timeline;

// Demanda: qué cuadros pide cada capa, cómo se agrupan en tramos y a qué decodificador va cada uno.
internal sealed partial class TimelineScheduler
{
    /// <summary>Recalcula las ventanas, las reparte entre decodificadores y abre los que falten; devuelve el trabajo a encolar.</summary>
    private List<(IDecodeExecutor, Action)> UpdateDemand(long sequenceFrame, bool force)
    {
        var posts = new List<(IDecodeExecutor, Action)>();
        if (!force && sequenceFrame == _demandFrame)
        {
            return posts;
        }

        _demandDirty = false;
        _demandFrame = sequenceFrame;
        Time time = Time.FromFrames(sequenceFrame, _plan.FrameRate);
        _demandTime = time;
        foreach (MediaState media in _media.Values)
        {
            media.Windows.Clear();
            media.Layers = 0;
        }

        foreach (VideoLayerPlan layer in _plan.Layers)
        {
            if (layer.ClipAt(time) is { } clip)
            {
                long due = clip.FrameAt(time);
                long end = Math.Min(due + _prefetch, clip.LastFrame + 1);
                MediaState media = Media(clip);
                media.Layers++;
                media.Windows.Add(new Window(due, due + 1, Due: true, clip));
                if (end > due + 1)
                {
                    media.Windows.Add(new Window(due + 1, end, Due: false, clip));
                }
            }

            if (layer.NextAfter(time) is { } next && next.Start - time <= PreRollLead)
            {
                long first = next.FirstFrame;
                Media(next).Windows.Add(new Window(first, Math.Min(first + _preRollFrames, next.LastFrame + 1), Due: false, next));
            }
        }

        long now = _time.GetTimestamp();
        _warmUntil = null;
        foreach (VideoLayerPlan layer in _plan.HiddenLayers)
        {
            // Una pista recién ocultada sigue decodificando su tramo, detrás de todo lo visible, por si vuelve a verse.
            if (!_layers.TryGetValue(layer.TrackId, out LayerState? state) || state.HiddenSince is not { } since
                || _time.GetElapsedTime(since, now) >= _idleTimeout || layer.ClipAt(time) is not { } clip)
            {
                continue;
            }

            long due = clip.FrameAt(time);
            Media(clip).Windows.Add(new Window(due, Math.Min(due + _prefetch, clip.LastFrame + 1), Due: false, clip, Warm: true));
            long until = since + (long)(_idleTimeout.TotalSeconds * _time.TimestampFrequency);
            _warmUntil = _warmUntil is { } earlier ? Math.Min(earlier, until) : until;
        }

        var requests = new List<OpenRequest>();
        foreach (DecoderState decoder in _decoders.Values)
        {
            decoder.Assigned.Clear();
        }

        foreach (MediaState media in _media.Values)
        {
            if (media.Windows.Count == 0)
            {
                media.IdleSince ??= now;
                continue;
            }

            media.IdleSince = null;
            media.Requested.RemoveWhere(i => !media.InWindows(i));
            if (media.Failure is not null)
            {
                continue;
            }

            if (media.ResolvePending && !media.Resolving && _resolver is not null)
            {
                media.Resolving = true;
                int layers = media.Layers;
                posts.Add((_service, () => Reresolve(media, layers)));
            }

            Assign(media, requests);
        }

        foreach (DecoderState decoder in _decoders.Values)
        {
            if (!decoder.Closed && decoder.Assigned.Count == 0)
            {
                decoder.IdleSince ??= now;
            }
            else
            {
                decoder.IdleSince = null;
            }
        }

        // Lo más urgente primero; a igual urgencia, los medios sin ningún decodificador: los que ya tienen uno pueden
        // compartirlo si no queda lugar.
        requests.Sort((a, b) => a.Cluster.Urgency != b.Cluster.Urgency
            ? a.Cluster.Urgency.CompareTo(b.Cluster.Urgency)
            : a.Media.Decoders.Count.CompareTo(b.Media.Decoders.Count));
        foreach (OpenRequest request in requests)
        {
            Serve(request, posts);
        }

        foreach (DecoderState decoder in _decoders.Values)
        {
            if (!decoder.Closed && decoder.Decoder is not null && decoder.Assigned.Count > 0 && RequestPump(decoder))
            {
                posts.Add((decoder.Executor!, decoder.Pump));
            }
        }

        return posts;
    }

    /// <summary>Reparte los tramos del medio entre sus decodificadores, el más cercano primero; los que sobran piden uno nuevo.</summary>
    private void Assign(MediaState media, List<OpenRequest> requests)
    {
        List<Cluster> clusters = Clusters(media);
        var candidates = media.Decoders.Where(d => !d.Retiring).ToList();
        var pairs = new List<(Cluster Cluster, DecoderState Decoder, long Cost)>();
        foreach (Cluster cluster in clusters)
        {
            foreach (DecoderState decoder in candidates)
            {
                pairs.Add((cluster, decoder, Cost(decoder, cluster)));
            }
        }

        pairs.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : a.Cluster.Urgency.CompareTo(b.Cluster.Urgency));
        var served = new HashSet<Cluster>();
        var busy = new HashSet<DecoderState>();
        foreach ((Cluster cluster, DecoderState decoder, _) in pairs)
        {
            if (!served.Contains(cluster) && !busy.Contains(decoder))
            {
                decoder.Assigned.AddRange(cluster.Windows);
                cluster.Decoder = decoder;
                served.Add(cluster);
                busy.Add(decoder);
            }
        }

        foreach (Cluster cluster in clusters)
        {
            if (cluster.Decoder is null)
            {
                requests.Add(new OpenRequest(media, cluster));
            }

            // Mientras el decodificador nuevo (otro proxy u original) no tiene listo el cuadro que sigue, el que se retira
            // sigue con los debidos: no hay cortes, y el nuevo adelanta los siguientes sin competir por ellos.
            if ((cluster.Decoder is not { } replacement || !_cache.ContainsFrom(media.Id, cluster.Start + 1, replacement.Id))
                && media.Decoders.Where(d => d.Retiring && d.Decoder is not null).MinBy(d => Cost(d, cluster)) is { } bridge)
            {
                bridge.Assigned.AddRange(cluster.Windows.Where(w => w.Due));
            }
        }
    }

    /// <summary>Abre un decodificador para un tramo sin dueño, dentro del presupuesto; si no se puede, lo suma al más cercano.</summary>
    private void Serve(OpenRequest request, List<(IDecodeExecutor, Action)> posts)
    {
        MediaState media = request.Media;
        Cluster cluster = request.Cluster;
        int live = media.Decoders.Count(d => !d.Retiring);
        long estimate = Estimate(media);
        bool canOpen = live < _maxDecodersPerMedia && MakeRoom(estimate, posts);
        DecoderState? nearest = media.Decoders.Where(d => !d.Retiring).MinBy(d => Cost(d, cluster));
        if (!canOpen && nearest is not null && (cluster.Visible || Cost(nearest, cluster) < SeekCost || RestIsCached(nearest)))
        {
            // Compartir un decodificador que tendría que ir y volver con seeks solo vale para lo que se ve ya, o cuando
            // lo que le queda por mostrar de su clip ya cabe en su caché: si no, el pre-roll le quitaría a la capa visible
            // más de lo que le ahorra al corte.
            nearest.Assigned.AddRange(cluster.Windows);
            nearest.IdleSince = null;
            return;
        }

        if (!canOpen && !cluster.Visible)
        {
            return;
        }

        if (!canOpen)
        {
            _vramOverruns++;
        }

        var decoder = new DecoderState(_nextId++, media)
        {
            Overrun = !canOpen,
            OpenedAt = Stopwatch.GetTimestamp(),
            Opening = true,
            Variant = media.Variant,
            Position = cluster.Start - 1,
            Reserved = estimate,
        };
        decoder.Pump = () => Pump(decoder);
        decoder.Executor = _backend.CreateExecutor();
        decoder.Assigned.AddRange(cluster.Windows);
        _cache.SetSourceLimit(decoder.Id, _maxFramesPerSource);
        media.Decoders.Add(decoder);
        _decoders.Add(decoder.Id, decoder);
        TrackPeak();
        posts.Add((decoder.Executor, () => Open(decoder)));
    }

    /// <summary>Si cabe <paramref name="bytes"/> más en el presupuesto, cerrando primero los decodificadores sin demanda.</summary>
    private bool MakeRoom(long bytes, List<(IDecodeExecutor, Action)> posts)
    {
        long committed = Planned();
        if (committed + bytes <= _vramBudget)
        {
            return true;
        }

        var idle = _decoders.Values
            .Where(d => !d.Closed && d.Assigned.Count == 0 && !d.Opening && d.InFlight is null)
            .OrderByDescending(d => d.Retiring)
            .ThenBy(d => d.IdleSince ?? long.MaxValue)
            .ToList();
        long freed = idle.Sum(d => d.Reserved);
        if (committed - freed + bytes > _vramBudget)
        {
            return false;
        }

        foreach (DecoderState decoder in idle)
        {
            if (committed + bytes <= _vramBudget)
            {
                break;
            }

            committed -= decoder.Reserved;
            Close(decoder, posts);
        }

        return true;
    }

    /// <summary>Lo que le queda a cada clip que sirve el decodificador entra en su caché: puede irse a otro punto sin volver.</summary>
    private static bool RestIsCached(DecoderState decoder) =>
        decoder.Assigned.TrueForAll(w => w.Warm || w.Clip.LastFrame + 1 - w.Start < decoder.CacheLimit);

    private List<Cluster> Clusters(MediaState media)
    {
        var clusters = new List<Cluster>();
        Cluster? current = null;
        foreach (Window window in media.Windows.OrderBy(w => w.Start))
        {
            long urgency = Delay(window, window.Start);
            if (current is not null && window.Start <= current.End + _prefetch)
            {
                current.Add(window, urgency);
            }
            else
            {
                current = new Cluster(window, urgency);
                clusters.Add(current);
            }
        }

        return clusters;
    }

    /// <summary>Cuánto le cuesta al decodificador servir el tramo: 0 si ya está ahí, cuadros por delante si llega sin buscar, o un seek.</summary>
    private static long Cost(DecoderState decoder, Cluster cluster)
    {
        long next = decoder.Position + 1;
        if (next >= cluster.Start && next <= cluster.End)
        {
            return 0;
        }

        if (next < cluster.Start && cluster.Start - next <= ForwardReach)
        {
            return cluster.Start - next;
        }

        return SeekCost + Math.Abs(cluster.Start - next);
    }

    private MediaState Media(VideoClipPlan clip)
    {
        if (!_media.TryGetValue(clip.Source, out MediaState? media))
        {
            media = new MediaState(_nextId++, clip.Source, clip.FrameRate, clip.DisplayWidth, clip.DisplayHeight);
            _media.Add(clip.Source, media);
            _mediaById.Add(media.Id, media);
        }

        return media;
    }

    /// <summary>Más urgente cuanto antes haya que mostrarlo; <see cref="TimelineFrameCache.Unwanted"/> si ninguna ventana lo quiere.</summary>
    private long Urgency(int mediaId, long index)
    {
        long best = TimelineFrameCache.Unwanted;
        if (_mediaById.TryGetValue(mediaId, out MediaState? media))
        {
            foreach (Window window in media.Windows)
            {
                if (index >= window.Start && index < window.End)
                {
                    best = Math.Max(best, long.MaxValue - Delay(window, index));
                }
            }
        }

        return best;
    }

    /// <summary>Cuánto falta, en ticks desde el cuadro de la demanda, para que la capa muestre ese cuadro del origen.</summary>
    private long Delay(Window window, long index)
    {
        if (window.Due)
        {
            return 0;
        }

        VideoClipPlan clip = window.Clip;
        Time shownAt = Time.Max(clip.Start, clip.Start + (Time.FromFrames(index, clip.FrameRate) - clip.SourceIn));
        long delay = Math.Max(0, (shownAt - _demandTime).Ticks);
        return window.Warm ? WarmPenalty.Ticks + delay : delay;
    }

    /// <summary>Cierra los decodificadores sin demanda: ya si se retiran, antes de tiempo si hay muchos abiertos.</summary>
    private void CollectIdle(List<(IDecodeExecutor, Action)> posts)
    {
        long now = _time.GetTimestamp();
        int live = _decoders.Values.Count(d => !d.Closed);
        TimeSpan timeout = live > Math.Max(2, 2 * _plan.Layers.Count) ? _idleTimeout / 4 : _idleTimeout;
        foreach (DecoderState decoder in _decoders.Values.ToList())
        {
            if (!decoder.Closed && decoder.IdleSince is { } since && !decoder.Opening && decoder.InFlight is null
                && (decoder.Retiring || _time.GetElapsedTime(since, now) >= timeout))
            {
                Close(decoder, posts);
            }
        }

        foreach (MediaState media in _media.Values.ToList())
        {
            if (media.Decoders.Count == 0 && media.IdleSince is { } since && _time.GetElapsedTime(since, now) >= _idleTimeout
                && !_layers.Values.Any(l => l.CurrentMedia == media))
            {
                _media.Remove(media.Key);
                _mediaById.Remove(media.Id);
            }
        }
    }

    /// <summary>Saca el decodificador del reparto y de la caché, y encola su cierre en el ejecutor de servicio.</summary>
    private void Close(DecoderState decoder, List<(IDecodeExecutor, Action)> posts)
    {
        decoder.Closed = true;
        decoder.Assigned.Clear();
        decoder.Media.Decoders.Remove(decoder);
        _cache.RemoveSource(decoder.Id);
        _decodersClosed++;
        posts.Add((_service, () => Reap(decoder)));
    }
}
