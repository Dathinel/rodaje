using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Timeline;

// Decodificación: abrir, pedir cuadros, recibirlos, liberar y cerrar.
internal sealed partial class TimelineScheduler
{
    private void Open(DecoderState decoder)
    {
        MediaState media = decoder.Media;
        Variant? variant;
        int layers;
        lock (_gate)
        {
            variant = decoder.Variant ?? media.Variant;
            layers = media.Layers;
        }

        if (variant is null)
        {
            Variant resolved = ResolveVariant(media, layers);
            lock (_gate)
            {
                media.Variant ??= resolved;
                variant = media.Variant;
            }
        }

        KeyframeIndex? keyframes;
        lock (_gate)
        {
            decoder.Variant = variant;
            keyframes = media.KeyframesPath == variant.Path ? (media.Keyframes as MediaClipBackend.MediaKeyframes)?.Index : null;
        }

        // Los modos aproximados son solo para la vista previa (el motor nunca exporta): los pide la reproducción de 16× a 100×.
        VideoDecoderOptions options = _decoderOptions with
        {
            HardwarePoolSizer = request => SizePool(decoder, request),
            SkipNonReference = true,
            Keyframes = keyframes,
        };
        IClipVideo? video = null;
        Exception? error = null;
#pragma warning disable CA1031 // Un archivo que no abre deja su capa vacía; se informa y la reproducción sigue.
        try
        {
            video = _backend.OpenVideo(variant.Path, options);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error is not null && variant.IsProxy)
        {
            // Un proxy dañado no deja la capa vacía: se vuelve al original.
            variant = new Variant(media.Key.Path, IsProxy: false, media.DisplayWidth, media.DisplayHeight);
            lock (_gate)
            {
                media.Variant = variant;
                decoder.Variant = variant;
            }

            try
            {
                video = _backend.OpenVideo(variant.Path, options);
                error = null;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
#pragma warning restore CA1031

        bool discard;
        bool post = false;
        bool assess = false;
        double rate = 1;
        bool reverse = false;
        List<(IDecodeExecutor, Action)> posts = [];
        lock (_gate)
        {
            decoder.Opening = false;
            discard = _disposed || decoder.Closed;
            if (!discard && error is not null)
            {
                media.Failure = error.Message;
                _decodeErrors++;
                Close(decoder, posts);
            }
            else if (!discard && video is not null)
            {
                decoder.Decoder = video;
                bool learned = LearnKeyframes(decoder);
                decoder.Look = variant.IsProxy
                    ? new Look(new Rational(1, 1), 0, media.DisplayWidth, media.DisplayHeight, IsProxy: true)
                    : new Look(video.Stream.SampleAspectRatio, NormalizeRotation(video.Stream.Rotation), 0, 0, IsProxy: false);
                if (!decoder.PoolKnown)
                {
                    decoder.CacheLimit = PlanSourceLimit(video, decoder.ReverseClip is not null);
                    _cache.SetSourceLimit(decoder.Id, decoder.CacheLimit);
                }

                assess = !variant.IsProxy && _resolver is not null
                    && (!media.Assessed || media.AssessedRate != PolicyRate(media) || media.AssessedReverse != media.Reverse);
                media.Assessed |= assess;
                rate = PolicyRate(media);
                reverse = media.Reverse;
                media.AssessedRate = assess ? rate : media.AssessedRate;
                media.AssessedReverse = assess ? reverse : media.AssessedReverse;
                if (learned && media.Reverse)
                {
                    // Las ventanas en reversa van de keyframe en keyframe: se rehacen antes de pedir nada.
                    posts.AddRange(UpdateDemand(CurrentSequenceFrame(), force: true));
                }

                post = RequestPump(decoder);
            }
        }

        if (discard)
        {
            video?.Dispose();
            return;
        }

        PostAll(posts);
        if (error is not null)
        {
            DecodeFailed?.Invoke(this, new TimelineDecodeFailedEventArgs(media.Key.MediaId, media.Key.Path, -1, error));
        }

        if (assess)
        {
            Assess(video!.Info, layers, rate, reverse);
        }

        if (post)
        {
            decoder.Executor!.Post(decoder.Pump);
        }
    }

    /// <summary>En el ejecutor de servicio: lee los keyframes del archivo y, si sigue siendo el del medio, los guarda.</summary>
    private void ReadKeyframes(MediaState media, string path)
    {
        IKeyframeIndex? keyframes = _backend.ReadKeyframes(path);
        List<(IDecodeExecutor, Action)> posts = [];
        lock (_gate)
        {
            if (keyframes is not null && !_disposed && media.Variant?.Path == path)
            {
                media.Keyframes = keyframes;
                media.KeyframesPath = path;
                _demandDirty = true;
                if (media.Reverse)
                {
                    // Un decodificador en reversa no debe pedir otra pasada con las ventanas de antes de saber los GOP.
                    posts = UpdateDemand(CurrentSequenceFrame(), force: true);
                }
            }
        }

        PostAll(posts);
    }

    /// <summary>Toma los keyframes del archivo del decodificador si ya los conoce (puede saberlos después de abrir); verdadero si son nuevos.</summary>
    private static bool LearnKeyframes(DecoderState decoder)
    {
        MediaState media = decoder.Media;
        if (decoder.Variant is { } variant && variant == media.Variant && media.KeyframesPath != variant.Path
            && decoder.Decoder is IKeyframeSource { Keyframes: { } keyframes })
        {
            media.Keyframes = keyframes;
            media.KeyframesPath = variant.Path;
            return true;
        }

        return false;
    }

    /// <summary>Tope de cuadros sin pool de D3D11VA (CPU): lo que pide la opción, sin que uno solo se coma la caché.</summary>
    private int PlanSourceLimit(IClipVideo video, bool reverse)
    {
        VideoStreamInfo stream = video.Stream;
        long frame = Math.Max(1, (long)stream.Width * stream.Height * (stream.BitDepth > 8 ? 2 : 1) * 3 / 2);
        long bytes = reverse ? _maxCacheBytes / 2 : _maxCacheBytes;
        return (int)Math.Clamp(bytes / frame, 2, reverse ? ReverseSoftwareCacheFrames : _maxFramesPerSource);
    }

    private static bool RequestPump(DecoderState decoder)
    {
        if (decoder.PumpPosted || decoder.InFlight is not null)
        {
            return false;
        }

        decoder.PumpPosted = true;
        return true;
    }

    // Se pide primero lo que antes haya que mostrar, sea de la capa que sea. Un cuadro adelantado que la caché ya
    // descartó por falta de lugar se vuelve a pedir solo cuando ahora sí cabría: si no, decodificarlo expulsaría
    // otro y así sin fin. Y mientras no quepa, tampoco se adelantan los que siguen: decodificarlos obligaría a volver
    // atrás con un seek por el que faltó. El cuadro debido siempre se repide. Solo se piden los cuadros que se
    // mostrarán: los que la velocidad salta los decodifica el decodificador de paso, sin entregarlos.
    private bool TryFindMissing(DecoderState decoder, out long index, out DecodeAccuracy accuracy)
    {
        MediaState media = decoder.Media;
        index = 0;
        accuracy = DecodeAccuracy.Exact;
        long bestDelay = long.MaxValue;
        Window? reverse = null;
        foreach (Window window in decoder.Assigned)
        {
            if (!window.Clip.Forward)
            {
                // En reversa lo más urgente de la ventana es lo más alto: se baja hasta el primero que falta.
                if (FirstMissingFromTop(decoder, window) is { } found && Delay(window, found) < bestDelay)
                {
                    (index, bestDelay, reverse) = (found, Delay(window, found), window);
                    accuracy = DecodeAccuracy.Exact;
                }

                continue;
            }

            for (long i = NextWanted(window, media, window.Start); i < window.End; i = NextWanted(window, media, i + 1))
            {
                long delay = Delay(window, i);
                if (delay >= bestDelay || media.Unavailable.Contains(i) || IsCachedFor(decoder, i) || IsShown(decoder, i)
                    || InFlightElsewhere(decoder, i))
                {
                    continue;
                }

                if (!window.Due && media.Requested.Contains(i) && !_cache.CanAdmit(decoder.Id, long.MaxValue - delay, _urgency))
                {
                    break;
                }

                (index, bestDelay, reverse) = (i, delay, null);

                // Un keyframe que se muestra aproximando se pide sin decodificar lo que hay entre medio.
                accuracy = window.Approximate && media.CurrentKeyframes?.AtOrBefore(i) == i ? DecodeAccuracy.Keyframe : DecodeAccuracy.Exact;
            }
        }

        if (reverse is { } chosen)
        {
            // Lo más urgente en reversa es lo último que se decodifica de su tramo: se empieza por lo más bajo del tramo.
            return ReverseBatchStart(decoder, chosen, index, bestDelay, out index);
        }

        return bestDelay != long.MaxValue;
    }

    /// <summary>
    /// El cuadro más alto de la ventana en reversa que todavía hay que pedir: lo que se mostrará antes. Nulo si no falta
    /// nada o si lo que falta ya se pidió y no cabría en la caché.
    /// </summary>
    private long? FirstMissingFromTop(DecoderState decoder, in Window window)
    {
        MediaState media = decoder.Media;
        VideoClipPlan clip = window.Clip;
        long top = window.Due ? window.Start : clip.PreviousShown(window.End - 1);
        for (long i = top; i >= window.Start; i = clip.PreviousShown(i - 1))
        {
            if (media.Unavailable.Contains(i) || IsCachedFor(decoder, i) || IsShown(decoder, i) || InFlightElsewhere(decoder, i))
            {
                if (window.Due)
                {
                    return null;
                }

                continue;
            }

            if (!window.Due && media.Requested.Contains(i) && !_cache.CanAdmit(decoder.Id, long.MaxValue - Delay(window, i), _urgency))
            {
                return null;
            }

            return i;
        }

        return null;
    }

    /// <summary>
    /// En reversa, el tramo que termina en <paramref name="top"/> (lo más urgente que falta) se decodifica hacia adelante
    /// desde su keyframe, como <see cref="Rodaje.Media.Video.ReverseFrameReader"/>: se pide primero lo más bajo que falta
    /// del tramo y después lo de arriba, sin buscar entre medias. El tramo no pasa del keyframe ni de lo que cabe en la
    /// caché junto a lo que ya espera mostrarse; si no cabe el GOP entero, el resto va en otra pasada. Un tramo que
    /// obliga a buscar el keyframe solo empieza si llena al menos media caché (o lo que quede del GOP), salvo que lo de
    /// arriba se deba mostrar pronto: un tramo de pocos cuadros pagaría el GOP entero por cada uno.
    /// </summary>
    private bool ReverseBatchStart(DecoderState decoder, in Window window, long top, long delay, out long index)
    {
        MediaState media = decoder.Media;
        VideoClipPlan clip = window.Clip;
        long floor = clip.LowestFrame;
        long lowestWindow = long.MaxValue;
        long highestWindow = top;
        foreach (Window w in decoder.Assigned)
        {
            if (w.Clip.ClipId == clip.ClipId && !w.Clip.Forward)
            {
                lowestWindow = Math.Min(lowestWindow, w.Start);
                highestWindow = Math.Max(highestWindow, w.End - 1);
            }
        }

        floor = Math.Max(floor, lowestWindow);
        if (media.CurrentKeyframes?.AtOrBefore(top) is { } key and >= 0)
        {
            floor = Math.Max(floor, key);
        }

        // Un tramo empezado sigue subiendo hasta lo más urgente aunque entre tanto quepa más abajo: si el tramo se
        // alargara hacia abajo con cada cuadro mostrado, cada alargue buscaría el keyframe otra vez.
        long position = decoder.Position;
        if (decoder.Delivered > 0 && position >= floor && position < top)
        {
            for (long j = clip.NextShown(position + 1); j <= top && j - position <= ForwardReach; j = clip.NextShown(j + 1))
            {
                if (!media.Unavailable.Contains(j) && !IsCachedFor(decoder, j) && !IsShown(decoder, j) && !InFlightElsewhere(decoder, j))
                {
                    index = j;
                    return true;
                }
            }
        }

        // Lo que ya espera en la caché por encima ocupa lugar hasta que se muestre.
        int waiting = 0;
        for (long i = clip.NextShown(top + 1); i <= highestWindow; i = clip.NextShown(i + 1))
        {
            waiting += _cache.ContainsFrom(media.Id, i, decoder.Id) ? 1 : 0;
        }

        int room = Math.Max(1, decoder.CacheLimit - ReverseCacheMargin - waiting);
        index = top;
        int count = 1;
        int total = 1;
        for (long j = clip.PreviousShown(top - 1); j >= floor; j = clip.PreviousShown(j - 1))
        {
            total++;
            if (count >= room)
            {
                continue;
            }

            count++;
            if (!media.Unavailable.Contains(j) && !IsCachedFor(decoder, j) && !IsShown(decoder, j) && !InFlightElsewhere(decoder, j))
            {
                index = j;
            }
        }

        bool continues = decoder.Delivered > 0 && position < index && index - position <= ForwardReach;
        bool soon = delay <= Time.FromFrames(_prefetch, _plan.FrameRate).Ticks;
        if (soon || continues)
        {
            return true;
        }

        // Sin saber dónde empieza el GOP, el tramo podría empezar a mitad de él y obligar a otra pasada: lo que no urge
        // espera a que se lean los keyframes.
        if (media.CurrentKeyframes is null)
        {
            return false;
        }

        // El GOP entero si cabe en la caché (una pasada); si no, de a media caché.
        int capacity = Math.Max(1, decoder.CacheLimit - ReverseCacheMargin);
        return count >= (total <= capacity ? total : Math.Max(1, capacity / 2));
    }

    /// <summary>El cuadro está guardado y le sirve a <paramref name="decoder"/>: no salió de un archivo que se está retirando.</summary>
    private bool IsCachedFor(DecoderState decoder, long index) =>
        _cache.TryGetSlot(decoder.Media.Id, index, out int slot)
        && (decoder.Retiring || !_decoders.TryGetValue(slot, out DecoderState? source) || !source.Retiring);

    private static bool InFlightElsewhere(DecoderState decoder, long index)
    {
        foreach (DecoderState other in decoder.Media.Decoders)
        {
            if (other != decoder && other.InFlight?.Index == index)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Alguna capa ya muestra ese cuadro, decodificado del mismo archivo que usa <paramref name="decoder"/>.</summary>
    private bool IsShown(DecoderState decoder, long index)
    {
        foreach (LayerState layer in _layers.Values)
        {
            if (layer.IsShowing(decoder.Media, index) && layer.CurrentVariant == decoder.Variant)
            {
                return true;
            }
        }

        return false;
    }

    private void Pump(DecoderState decoder)
    {
        while (true)
        {
            Job? job = null;
            IClipVideo? video = null;
            List<(IDecodeExecutor, Action)> posts = [];
            lock (_gate)
            {
                decoder.PumpPosted = false;
                if (_disposed || decoder.Closed || decoder.Decoder is null || decoder.InFlight is not null)
                {
                    return;
                }

                if (_clock.IsRunning)
                {
                    // Sin esperar al próximo refresco: lo que el reloj ya dejó atrás no se decodifica.
                    long now = CurrentSequenceFrame();
                    if (now > _demandFrame)
                    {
                        posts = UpdateDemand(now, force: false);
                        posts.RemoveAll(p => p.Item2 == decoder.Pump);
                        decoder.PumpPosted = false;
                    }
                }

                if (!decoder.Closed && TryFindMissing(decoder, out long index, out DecodeAccuracy accuracy))
                {
                    decoder.Media.Requested.Add(index);
                    job = decoder.InFlight = new Job(index, decoder.Epoch, accuracy);
                    decoder.Position = index;
                    video = decoder.Decoder;
                }
            }

            PostAll(posts);
            if (job is null || video is null)
            {
                return;
            }

            ValueTask<DecodedFrame?> pending;
            try
            {
                pending = video.GetFrameAsync(job.Index, job.Accuracy, job.Epoch.Token);
            }
            catch (Exception ex)
            {
                pending = ValueTask.FromException<DecodedFrame?>(ex);
            }

            if (!pending.IsCompleted)
            {
                _ = pending.AsTask().ContinueWith(
                    task =>
                    {
                        if (Complete(decoder, job, new ValueTask<DecodedFrame?>(task)))
                        {
                            decoder.Executor!.Post(decoder.Pump);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            Complete(decoder, job, pending);
        }
    }

    private bool Complete(DecoderState decoder, Job job, ValueTask<DecodedFrame?> pending)
    {
        DecodedFrame? frame = null;
        Exception? error = null;
        try
        {
            frame = pending.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (job.Epoch.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        MediaState media = decoder.Media;
        DecodedFrame? discard = null;
        bool post;
        lock (_gate)
        {
            decoder.InFlight = null;
            bool current = job.Epoch == decoder.Epoch;
            if (_disposed || decoder.Closed)
            {
                discard = frame;
            }
            else if (frame is not null && frame.Index != job.Index && (job.Accuracy == DecodeAccuracy.Exact || frame.Index > job.Index))
            {
                discard = frame;
                error = new InvalidOperationException($"El decodificador devolvió el cuadro {frame.Index} cuando se pidió el {job.Index}.");
            }
            else if (frame is not null)
            {
                if (!decoder.Retiring && _cache.TryGetSlot(media.Id, frame.Index, out int slot) && slot != decoder.Id
                    && _decoders.TryGetValue(slot, out DecoderState? older) && older.Retiring)
                {
                    // El mismo cuadro del archivo nuevo reemplaza al del que se retira.
                    _cache.Remove(media.Id, frame.Index);
                }

                _cache.Add(media.Id, decoder.Id, frame, _urgency);
                LearnKeyframes(decoder);

                // Un decodificador que reemplaza a otro ya entrega: puede que el que se retira deje de hacer falta.
                _demandDirty |= !decoder.Retiring && media.Decoders.Exists(d => d.Retiring);
                if (decoder.Delivered++ == 0)
                {
                    // Lo que tarda un decodificador nuevo en entregar, promediado: de eso depende cuánto antes empezar el pre-roll.
                    double startup = Stopwatch.GetElapsedTime(decoder.OpenedAt).TotalSeconds;
                    _startupSeconds = _startupSeconds <= 0 ? startup : (0.7 * _startupSeconds) + (0.3 * startup);
                    if (!decoder.PoolKnown)
                    {
                        // Sin pool de D3D11VA (decodificación en la CPU): no ocupa VRAM de decodificador.
                        decoder.Reserved = 0;
                        decoder.PoolKnown = true;
                    }


                }
            }

            // Uno aproximado puede traer un cuadro anterior al pedido: se guarda con su índice real y el pedido no se repite.
            if (!_disposed && !decoder.Closed && current && (frame is null || error is not null || frame.Index != job.Index))
            {
                media.Unavailable.Add(job.Index);
            }

            if (!_disposed && error is not null)
            {
                _decodeErrors++;
            }

            post = !_disposed && !decoder.Closed && RequestPump(decoder);
        }

        discard?.Dispose();
        if (error is not null)
        {
            DecodeFailed?.Invoke(this, new TimelineDecodeFailedEventArgs(media.Key.MediaId, media.Key.Path, job.Index, error));
        }

        return post;
    }

    /// <summary>
    /// Suelta cuadros en el ejecutor de servicio: el último cuadro de un decodificador cerrado arrastra la destrucción de su
    /// pool de texturas, que no debe caer en el hilo de la ventana.
    /// </summary>
    private void Release(List<DecodedFrame> frames, List<(IDecodeExecutor, Action)> posts)
    {
        if (frames.Count > 0)
        {
            posts.Add((_service, () => frames.ForEach(f => f.Dispose())));
        }
    }

    private static void PostAll(List<(IDecodeExecutor Executor, Action Work)> posts)
    {
        foreach ((IDecodeExecutor executor, Action work) in posts)
        {
            executor.Post(work);
        }
    }

    /// <summary>
    /// En el ejecutor de servicio: libera el decodificador en su propio hilo, después de lo que ya tenía en cola, espera a
    /// que ese hilo termine y recién entonces deja de contar su VRAM. El hilo de la ventana nunca espera esto.
    /// </summary>
    private void Reap(DecoderState decoder)
    {
        if (decoder.Executor is { } executor)
        {
            executor.Post(() => DisposeDecoder(decoder));
            (executor as IDisposable)?.Dispose();
        }

        lock (_gate)
        {
            decoder.Reserved = 0;
            _decoders.Remove(decoder.Id);
        }
    }

    /// <summary>En el hilo del decodificador: lo libera si todavía no se liberó (lo pueden pedir el cierre y la liberación).</summary>
    private void DisposeDecoder(DecoderState decoder)
    {
        IClipVideo? video;
        lock (_gate)
        {
            video = decoder.Decoder;
            decoder.Decoder = null;
        }

        video?.Dispose();
    }
}
