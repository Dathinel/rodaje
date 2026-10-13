using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

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
            media.Priority = int.MinValue;
        }

        // De arriba abajo: la pista que tapa a las demás pide primero; lo que queda bajo una capa opaca de pantalla
        // completa, al final.
        bool covered = false;
        for (int i = _plan.Layers.Count - 1; i >= 0; i--)
        {
            VideoLayerPlan layer = _plan.Layers[i];
            int priority = covered ? layer.TrackIndex - CoveredPenalty : layer.TrackIndex;
            AddLaneWindows(layer, time, sequenceFrame, priority);
            if (layer.Companion is { } companion)
            {
                AddLaneWindows(companion, time, sequenceFrame, priority);
            }

            covered |= !layer.IsNested && CoversFrame(layer, time);
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
            Media(clip).Windows.Add(new Window(due, WindowEnd(clip, due, sequenceFrame + _prefetch - 1), Due: false, clip, Warm: true));
            long until = since + (long)(_idleTimeout.TotalSeconds * _time.TimestampFrequency);
            _warmUntil = _warmUntil is { } earlier ? Math.Min(earlier, until) : until;
        }

        var requests = new List<OpenRequest>();
        foreach (DecoderState decoder in _decoders.Values)
        {
            decoder.Assigned.Clear();
        }

        foreach (MediaState media in _media.Values.OrderByDescending(m => m.Priority))
        {
            if (media.Windows.Count == 0)
            {
                media.IdleSince ??= now;
                continue;
            }

            media.IdleSince = null;
            media.Requested.RemoveWhere(i => !media.InWindows(i));
            if ((media.PlaybackRate >= ApproximateRate || media.Reverse) && media.Variant is { } variant && media.CurrentKeyframes is null
                && media.KeyframesRequested != variant.Path)
            {
                // Hace falta saber dónde están los keyframes para mostrar el cercano o para leer en reversa por GOP; se
                // leen una vez, fuera del candado.
                media.KeyframesRequested = variant.Path;
                posts.Add((_service, () => ReadKeyframes(media, variant.Path)));
            }

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

        // Lo más urgente primero (lo que ya se debe mostrar tiene urgencia 0); a igual urgencia, los medios sin ningún
        // decodificador (los que ya tienen uno pueden compartirlo si no queda lugar) y después la pista de arriba. La pista
        // no pasa por delante de eso: en 4K con cortes abría decodificadores de más y cerraba los que las de abajo
        // necesitaban enseguida (VRAM del proceso 1825 MB con 1485 de presupuesto y 71 refrescos tarde).
        requests.Sort(static (a, b) =>
        {
            int c = a.Cluster.Urgency.CompareTo(b.Cluster.Urgency);
            c = c != 0 ? c : a.Media.Decoders.Count.CompareTo(b.Media.Decoders.Count);
            return c != 0 ? c : b.Cluster.Priority.CompareTo(a.Cluster.Priority);
        });
        foreach (OpenRequest request in requests)
        {
            Serve(request, posts);
        }

        foreach (DecoderState decoder in _decoders.Values.OrderByDescending(d => d.Assigned.Count == 0 ? int.MinValue : d.Assigned.Max(w => w.Priority)))
        {
            if (!decoder.Closed && decoder.Decoder is not null && decoder.Assigned.Count > 0 && RequestPump(decoder))
            {
                posts.Add((decoder.Executor!, decoder.Pump));
            }
        }

        return posts;
    }

    /// <summary>Lo que resta a la prioridad de una capa tapada: queda detrás de todas las visibles.</summary>
    private const int CoveredPenalty = 1 << 20;

    /// <summary>
    /// Las ventanas del carril, en tiempo de la secuencia: el cuadro debido y lo que se mostrará en los próximos
    /// <c>PrefetchFrames</c> cuadros de la secuencia; y, cerca de un corte, lo que el clip siguiente mostrará en sus primeros
    /// <c>PreRollFrames</c>. Con velocidad, el tramo del origen se estira o se encoge con ella; con una curva, cada
    /// cuadro adelantado es el que mostrará su instante, y el keyframe cercano se usa solo si la velocidad instantánea no
    /// baja de 16× en todo el tramo.
    /// </summary>
    private void AddLaneWindows(VideoLayerPlan lane, Time time, long sequenceFrame, int priority)
    {
        bool running = _clock.IsRunning;
        if (lane.ClipAt(time) is { } clip)
        {
            MediaState media = Media(clip);
            long due = Due(clip, media, time, running, out _, out bool approximate);
            long last = sequenceFrame + _prefetch - 1;
            long end = WindowEnd(clip, due, last);
            media.Layers++;
            media.Priority = Math.Max(media.Priority, priority);
            media.Windows.Add(new Window(due, due + 1, Due: true, clip, Priority: priority, Approximate: approximate));
            if (!clip.Forward)
            {
                // En reversa lo que viene está debajo: hasta el keyframe del GOP siguiente al de lo adelantado (dos GOP),
                // para que el GOP de abajo se decodifique mientras se muestra este.
                long low = ReverseWindowStart(clip, media, clip.ShownUntil(last), gops: 2);
                if (low < due)
                {
                    media.Windows.Add(new Window(low, due, Due: false, clip, Priority: priority));
                }
            }
            else if (end > due + 1)
            {
                // Lo adelantado se aproxima solo si la velocidad no baja de 16× hasta mostrarlo (una curva o una transición la cambian).
                bool ahead = approximate && clip.MinRate(time, Time.FromFrames(last + 1, _plan.FrameRate)) >= ApproximateRate;
                media.Windows.Add(new Window(due + 1, end, Due: false, clip, Priority: priority, Approximate: ahead));
            }
        }

        if (lane.NextAfter(time) is { } next && next.Start - time <= PreRollLead)
        {
            long first = next.FirstFrame;
            MediaState media = Media(next);
            media.Priority = Math.Max(media.Priority, priority);
            long last = next.FirstSequenceFrame + _preRollFrames - 1;
            if (!next.Forward)
            {
                // Un clip en reversa empieza por el final de su material: el pre-roll pide su último GOP entero.
                long low = ReverseWindowStart(next, media, next.ShownUntil(last), gops: 1);
                media.Windows.Add(new Window(low, first + 1, Due: false, next, Priority: priority));
                return;
            }

            bool approximate = Approximates(next, media, next.Start, running)
                && next.MinRate(next.Start, Time.FromFrames(last + 1, _plan.FrameRate)) >= ApproximateRate;
            media.Windows.Add(new Window(first, WindowEnd(next, first, last), Due: false, next, Priority: priority, Approximate: approximate));
        }
    }

    /// <summary>
    /// Comienzo de la ventana de un clip en reversa que mostrará hasta <paramref name="lowest"/>: el keyframe de su GOP y,
    /// con <paramref name="gops"/> = 2, el del GOP de abajo; sin keyframes conocidos, <paramref name="lowest"/>.
    /// </summary>
    private static long ReverseWindowStart(VideoClipPlan clip, MediaState media, long lowest, int gops)
    {
        long low = lowest;
        if (media.CurrentKeyframes is { } keyframes)
        {
            for (int g = 0; g < gops && low > clip.LastFrame; g++)
            {
                long key = keyframes.AtOrBefore(g == 0 ? low : low - 1);
                if (key < 0)
                {
                    break;
                }

                low = key;
            }
        }

        return Math.Max(clip.LastFrame, low);
    }

    /// <summary>Fin de una ventana que empieza en <paramref name="start"/>: lo mostrado hasta el cuadro <paramref name="lastSequenceFrame"/> de la secuencia.</summary>
    private static long WindowEnd(VideoClipPlan clip, long start, long lastSequenceFrame) =>
        clip.Forward ? Math.Max(start, clip.ShownUntil(lastSequenceFrame)) + 1 : start + 1;

    /// <summary>
    /// El cuadro del origen que la capa debe mostrar: el exacto o, reproduciendo de 16× a 100× con los keyframes del archivo
    /// conocidos y sin un seek pendiente, el keyframe anterior (sin salir del clip).
    /// </summary>
    private long Due(VideoClipPlan clip, MediaState media, Time time, bool running, out long exact, out bool approximate)
    {
        exact = clip.FrameAt(time);
        approximate = Approximates(clip, media, time, running);
        return approximate ? Math.Max(media.CurrentKeyframes!.AtOrBefore(exact), clip.FirstFrame) : exact;
    }

    private bool Approximates(VideoClipPlan clip, MediaState media, Time time, bool running) =>
        running && !_seekPending && clip.Forward && clip.RateAt(time) >= ApproximateRate && media.CurrentKeyframes is not null;

    /// <summary>
    /// El primer cuadro desde <paramref name="index"/> que la ventana necesita: el que se mostrará o, aproximando, el keyframe
    /// que se mostrará; <see cref="long.MaxValue"/> si no hay ninguno.
    /// </summary>
    private static long NextWanted(in Window window, MediaState media, long index)
    {
        VideoClipPlan clip = window.Clip;
        if (window.Due)
        {
            return index;
        }

        if (window.Approximate && media.CurrentKeyframes is { } keyframes)
        {
            if (index <= clip.FirstFrame)
            {
                return clip.FirstFrame;
            }

            // El keyframe k se ve cuando el cuadro exacto llega al primer keyframe desde index; el que se ve es el anterior a ese exacto.
            long key = keyframes.AtOrAfter(index);
            long shown = key == long.MaxValue ? long.MaxValue : clip.NextShown(key);
            return shown == long.MaxValue ? long.MaxValue : keyframes.AtOrBefore(shown);
        }

        return clip.NextShown(index);
    }

    /// <summary>
    /// El clip de la capa en <paramref name="time"/> tapa todo el cuadro: sin transición, sin mezcla, opaco, sin mover ni
    /// achicar y con la forma de la secuencia.
    /// </summary>
    private bool CoversFrame(VideoLayerPlan layer, Time time)
    {
        if (layer.ClipAt(time) is not { } clip || clip.Outgoing?.Covers(time) == true
            || layer.Companion?.ClipAt(time) is not null || clip.DisplayWidth <= 0 || clip.DisplayHeight <= 0
            || (long)clip.DisplayWidth * _plan.Height != (long)clip.DisplayHeight * _plan.Width)
        {
            return false;
        }

        if (clip.Effects is not { } effects)
        {
            return true;
        }

        LayerTransform t = EffectPlan.Transform(effects, clip.EffectTimeAt(time));
        return effects.Blend == BlendMode.Normal && effects.Stabilization is null && !effects.MayRevealBelow && t.X == 0 && t.Y == 0 && t.Scale >= 1 && t.Opacity >= 1 && t.RotationDegrees % 360 == 0;
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
                if (decoder.ReverseClip == cluster.ReverseClip)
                {
                    pairs.Add((cluster, decoder, Cost(decoder, cluster)));
                }
            }
        }

        // Lo que se ve ya elige primero: un decodificador que se adelantó a un pre-roll vuelve a la capa visible con un
        // seek en vez de dejarla sin decodificador (y abrir otro).
        pairs.Sort((a, b) => a.Cluster.Visible != b.Cluster.Visible ? (a.Cluster.Visible ? -1 : 1)
            : a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : a.Cluster.Urgency.CompareTo(b.Cluster.Urgency));
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
                && Bridge(media, cluster) is { } bridge)
            {
                bridge.Assigned.AddRange(cluster.Windows.Where(w => w.Due));
            }
        }
    }

    /// <summary>El decodificador que se retira más cerca del tramo (y en su mismo sentido); nulo si no hay.</summary>
    private static DecoderState? Bridge(MediaState media, Cluster cluster)
    {
        DecoderState? best = null;
        long bestCost = long.MaxValue;
        foreach (DecoderState decoder in media.Decoders)
        {
            if (decoder.Retiring && decoder.Decoder is not null && decoder.ReverseClip == cluster.ReverseClip
                && Cost(decoder, cluster) is var cost && cost < bestCost)
            {
                (best, bestCost) = (decoder, cost);
            }
        }

        return best;
    }

    /// <summary>Abre un decodificador para un tramo sin dueño, dentro del presupuesto; si no se puede, lo suma al más cercano.</summary>
    private void Serve(OpenRequest request, List<(IDecodeExecutor, Action)> posts)
    {
        MediaState media = request.Media;
        Cluster cluster = request.Cluster;
        if (Continuing(media, cluster, out bool share) is { } continuing)
        {
            // El decodificador que ya avanza por el medio llega solo a este pre-roll: abrir otro solo costaría una pausa
            // (crear el decodificador de D3D11VA toma el candado del contexto que usa la composición).
            if (share)
            {
                continuing.Assigned.AddRange(cluster.Windows);
                continuing.IdleSince = null;
            }

            return;
        }

        if (Leaving(media, cluster) is { } leaving)
        {
            // Lo que le queda al clip que sale cabe en un paso atrás del decodificador que ya sirve al que entra.
            leaving.Assigned.AddRange(cluster.Windows);
            leaving.IdleSince = null;
            return;
        }

        int live = media.Decoders.Count(d => !d.Retiring);
        long estimate = Estimate(media);
        bool canOpen = live < _maxDecodersPerMedia && MakeRoom(estimate, posts);
        DecoderState? nearest = media.Decoders.Where(d => !d.Retiring && d.ReverseClip == cluster.ReverseClip).MinBy(d => Cost(d, cluster));
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
            ReverseClip = cluster.ReverseClip,
        };
        decoder.Pump = () => Pump(decoder);
        decoder.Executor = _backend.CreateExecutor();
        _openedWhilePlaying += _clock.IsRunning ? 1 : 0;
        decoder.Assigned.AddRange(cluster.Windows);
        _cache.SetSourceLimit(decoder.Id, _maxFramesPerSource);
        media.Decoders.Add(decoder);
        _decoders.Add(decoder.Id, decoder);
        TrackPeak();
        posts.Add((decoder.Executor, () => Open(decoder)));
    }

    /// <summary>
    /// El decodificador del medio que, avanzando con el clip que ya sirve, llega sin buscar keyframe al pre-roll
    /// <paramref name="cluster"/>: ese clip termina pasado el primer cuadro del tramo o a menos de
    /// <see cref="ForwardReach"/> cuadros de él, y lo pasa antes de que se deba mostrar o deja de verse poco después
    /// (<see cref="ContinuationOverlap"/>: es el que sale en la transición hacia ese tramo). Es el caso de los cortes sin
    /// salto en el material (una velocidad que cambia donde terminó el clip anterior, con o sin transición): el pre-roll
    /// abría otro decodificador del mismo medio para cuadros por los que el primero iba a pasar igual. Nulo si no hay
    /// ninguno: el tramo necesita el suyo.
    /// </summary>
    /// <param name="share">
    /// Verdadero si el tramo ya se le asigna; si no, espera sin decodificador. Se asigna cuando el decodificador lo alcanza
    /// sin buscar y, además, el tramo empieza después de todo lo que muestra el clip y la ventana del clip ya llegó a su
    /// último cuadro (pedirlo no lo hace volver atrás), o el clip no llega a tiempo por sí solo: en una transición desde un
    /// 2× hacia un 0,5× el que entra va por delante del que sale, y el decodificador se adelanta una vez y vuelve con un
    /// seek, que no detiene la composición como abrir otro. Si el clip pasa por el tramo a tiempo, se espera: sus ventanas
    /// terminan en el mismo tramo. Un decodificador que ya se adelantó sigue sirviendo el tramo.
    /// </param>
    private DecoderState? Continuing(MediaState media, Cluster cluster, out bool share)
    {
        share = false;
        if (cluster.Visible || cluster.ReverseClip is not null || !cluster.Windows.TrueForAll(w => !w.Warm && w.Clip.Forward))
        {
            return null;
        }

        foreach (DecoderState decoder in media.Decoders)
        {
            if (decoder.Retiring || decoder.Closed || decoder.ReverseClip is not null)
            {
                continue;
            }

            if (decoder.Position >= cluster.Start)
            {
                // Ya se adelantó a este tramo por el clip que sale: lo que falte está a un paso, aunque sea hacia atrás.
                share = decoder.Position <= cluster.End + ForwardReach && decoder.Assigned.Exists(w => CatchesUp(w, cluster));
                if (share)
                {
                    return decoder;
                }

                continue;
            }

            foreach (Window window in decoder.Assigned)
            {
                VideoClipPlan clip = window.Clip;
                long highest = clip.HighestFrame;
                if (window.Warm || !clip.Forward || highest < cluster.Start - ForwardReach)
                {
                    continue;
                }

                bool passes = ShownDelay(clip, Math.Min(cluster.Start, highest)) <= cluster.Urgency;
                bool reachable = Cost(decoder, cluster) < SeekCost;
                if (passes)
                {
                    share = reachable && cluster.Start > highest && window.End > highest;
                    return decoder;
                }

                if (cluster.Start <= highest && CatchesUp(window, cluster))
                {
                    share = reachable;
                    return decoder;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Lo que puede seguir viéndose el clip de un decodificador después de que deba mostrarse un pre-roll que ese
    /// decodificador sirve adelantándose: una transición habitual, no dos capas que se pisan durante mucho tiempo.
    /// </summary>
    private static readonly Time ContinuationOverlap = Time.FromSeconds(2);

    /// <summary>
    /// Reproduciendo, el decodificador del medio que puede servir de paso un tramo visible al que le quedan pocos cuadros:
    /// todos sus clips dejan de verse en menos de <c>PreRollFrames</c> cuadros de la secuencia y el decodificador está a
    /// menos de <see cref="ForwardReach"/> cuadros. Es el que sale de una transición hacia un clip más rápido del mismo
    /// medio: al final queda detrás del que entra, sus ventanas se separan y abrir un decodificador para sus últimos
    /// cuadros detenía la vista previa; volver un paso con un seek no.
    /// </summary>
    private DecoderState? Leaving(MediaState media, Cluster cluster)
    {
        Time soon = _demandTime + Time.FromFrames(_preRollFrames, _plan.FrameRate);
        if (!_clock.IsRunning || !cluster.Visible || cluster.ReverseClip is not null || !cluster.Windows.TrueForAll(w => w.Clip.End <= soon))
        {
            return null;
        }

        DecoderState? best = null;
        foreach (DecoderState decoder in media.Decoders)
        {
            if (!decoder.Retiring && !decoder.Closed && decoder.ReverseClip is null && decoder.Assigned.Count > 0
                && Math.Abs(cluster.Start - decoder.Position) <= ForwardReach
                && (best is null || Math.Abs(cluster.Start - decoder.Position) < Math.Abs(cluster.Start - best.Position)))
            {
                best = decoder;
            }
        }

        return best;
    }

    /// <summary>
    /// El clip de la ventana sale poco después de que deba mostrarse el tramo (<see cref="ContinuationOverlap"/>), lo
    /// alcanza antes de salir y va al menos tan rápido como los clips del tramo: el que entra en la transición va por
    /// delante, pero el que sale lo pasa y los dos siguen juntos.
    /// </summary>
    private bool CatchesUp(in Window window, Cluster cluster)
    {
        VideoClipPlan clip = window.Clip;
        if (window.Warm || !clip.Forward || clip.HighestFrame < cluster.Start
            || (clip.End - _demandTime).Ticks > cluster.Urgency + ContinuationOverlap.Ticks)
        {
            return false;
        }

        double rate = Math.Abs(clip.RateAt(clip.End - Time.FromTicks(1)));
        return cluster.Windows.TrueForAll(w => w.Clip.MaxRate <= rate);
    }

    /// <summary>Cuánto falta, en ticks desde el cuadro de la demanda, para que el clip muestre ese cuadro o uno posterior.</summary>
    private long ShownDelay(VideoClipPlan clip, long index) => Math.Max(0, (clip.ShownAt(index) - _demandTime).Ticks);

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
        decoder.Assigned.TrueForAll(w => w.Warm || w.Clip.ShownFrom(w.Clip.Forward ? w.Start : w.End - 1) < decoder.CacheLimit);

    /// <summary>Tramos del medio: ventanas cercanas juntas, salvo las de un clip en reversa, que van en su propio tramo.</summary>
    private List<Cluster> Clusters(MediaState media)
    {
        var clusters = new List<Cluster>();
        Cluster? current = null;
        IEnumerable<Window> ordered = media.Reverse
            ? media.Windows.OrderBy(w => Cluster.ReverseKey(w), StringComparer.Ordinal).ThenBy(w => w.Start)
            : media.Windows.OrderBy(w => w.Start);
        foreach (Window window in ordered)
        {
            long urgency = Delay(window, window.Clip.Forward ? window.Start : window.End - 1);
            if (current is not null && current.ReverseClip == Cluster.ReverseKey(window) && window.Start <= current.End + _prefetch)
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
            (media.PlaybackRate, media.Reverse) = _rates.GetValueOrDefault(clip.Source, (1, false));
            _media.Add(clip.Source, media);
            _mediaById.Add(media.Id, media);
        }

        return media;
    }

    /// <summary>
    /// Más urgente cuanto antes haya que mostrarlo; <see cref="TimelineFrameCache.Unwanted"/> si ninguna ventana lo quiere,
    /// como los cuadros que la velocidad salta.
    /// </summary>
    private long Urgency(int mediaId, long index)
    {
        long best = TimelineFrameCache.Unwanted;
        if (_mediaById.TryGetValue(mediaId, out MediaState? media))
        {
            foreach (Window window in media.Windows)
            {
                if (index >= window.Start && index < window.End && NextWanted(window, media, index) == index)
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
        Time shownAt = clip.ShownAt(index);
        long delay = Math.Max(0, (shownAt - _demandTime).Ticks);
        return window.Warm ? WarmPenalty.Ticks + delay : delay;
    }

    /// <summary>Cierra los decodificadores sin demanda: ya si se retiran, antes de tiempo si hay muchos abiertos.</summary>
    private void CollectIdle(List<(IDecodeExecutor, Action)> posts)
    {
        long now = _time.GetTimestamp();
        int live = _decoders.Values.Count(d => !d.Closed);
        bool many = live > Math.Max(2, 2 * _plan.Layers.Count);
        TimeSpan timeout = many ? _idleTimeout / 4 : _idleTimeout;
        foreach (DecoderState decoder in _decoders.Values.ToList())
        {
            if (!decoder.Closed && decoder.IdleSince is { } since && !decoder.Opening && decoder.InFlight is null
                && (decoder.Retiring || (_time.GetElapsedTime(since, now) >= timeout && (many || !KeptForCut(decoder)))))
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

    /// <summary>
    /// Reproduciendo, un decodificador ocioso se guarda mientras su medio vuelva en un corte de las próximas
    /// <see cref="IdleKeepHorizon"/>: el pre-roll de ese corte lo reutiliza con un seek en vez de abrir otro, y abrir uno
    /// (crear el decodificador de D3D11VA) es una pausa de la vista previa. No pasa del presupuesto: <see cref="MakeRoom"/>
    /// cierra primero los ociosos. En pausa, o con muchos abiertos, se cierra a su tiempo como siempre.
    /// </summary>
    private bool KeptForCut(DecoderState decoder)
    {
        if (!_clock.IsRunning || decoder.ReverseClip is not null || decoder.Variant != decoder.Media.Variant)
        {
            return false;
        }

        foreach (VideoLayerPlan lane in _plan.Lanes)
        {
            if (lane.NextAfter(_demandTime) is { } next && next.Source == decoder.Media.Key && next.Forward
                && next.Start - _demandTime <= IdleKeepHorizon)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hasta dónde mira <see cref="KeptForCut"/>: más que el plazo de los ociosos, menos que guardar todo.</summary>
    private static readonly Time IdleKeepHorizon = Time.FromSeconds(30);

    /// <summary>Saca el decodificador del reparto y de la caché, y encola su cierre en el ejecutor de servicio.</summary>
    private void Close(DecoderState decoder, List<(IDecodeExecutor, Action)> posts)
    {
        decoder.Closed = true;
        decoder.Assigned.Clear();
        decoder.Media.Decoders.Remove(decoder);
        _cache.RemoveSource(decoder.Id);
        _decodersClosed++;

        // Fuera del candado, antes de esperarlo: lo que esté decodificando se descarta igual.
        posts.Add((_service, decoder.Epoch.Cancel));
        posts.Add((_service, () => Reap(decoder)));
    }
}
