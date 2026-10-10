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

        lock (_gate)
        {
            decoder.Variant = variant;
        }

        VideoDecoderOptions options = _decoderOptions with { HardwarePoolSizer = request => SizePool(decoder, request) };
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
                decoder.Look = variant.IsProxy
                    ? new Look(new Rational(1, 1), 0, media.DisplayWidth, media.DisplayHeight, IsProxy: true)
                    : new Look(video.Stream.SampleAspectRatio, NormalizeRotation(video.Stream.Rotation), 0, 0, IsProxy: false);
                if (!decoder.PoolKnown)
                {
                    decoder.CacheLimit = PlanSourceLimit(video);
                    _cache.SetSourceLimit(decoder.Id, decoder.CacheLimit);
                }

                assess = !variant.IsProxy && _resolver is not null && !media.Assessed;
                media.Assessed |= assess;
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
            Assess(video!.Info, layers);
        }

        if (post)
        {
            decoder.Executor!.Post(decoder.Pump);
        }
    }

    /// <summary>Tope de cuadros sin pool de D3D11VA (CPU): lo que pide la opción, sin que uno solo se coma la caché.</summary>
    private int PlanSourceLimit(IClipVideo video)
    {
        VideoStreamInfo stream = video.Stream;
        long frame = Math.Max(1, (long)stream.Width * stream.Height * (stream.BitDepth > 8 ? 2 : 1) * 3 / 2);
        return (int)Math.Clamp(_maxCacheBytes / frame, 2, _maxFramesPerSource);
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
    // atrás con un seek por el que faltó. El cuadro debido siempre se repide.
    private bool TryFindMissing(DecoderState decoder, out long index)
    {
        MediaState media = decoder.Media;
        index = 0;
        long bestDelay = long.MaxValue;
        foreach (Window window in decoder.Assigned)
        {
            for (long i = window.Start; i < window.End; i++)
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

                (index, bestDelay) = (i, delay);
            }
        }

        return bestDelay != long.MaxValue;
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

                if (!decoder.Closed && TryFindMissing(decoder, out long index))
                {
                    decoder.Media.Requested.Add(index);
                    job = decoder.InFlight = new Job(index, decoder.Epoch);
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
                pending = video.GetFrameAsync(job.Index, job.Epoch.Token);
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
            else if (frame is not null && frame.Index != job.Index)
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

            if (!_disposed && !decoder.Closed && current && (frame is null || error is not null))
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
            executor.Post(() =>
            {
                IClipVideo? video;
                lock (_gate)
                {
                    video = decoder.Decoder;
                    decoder.Decoder = null;
                }

                video?.Dispose();
            });
            (executor as IDisposable)?.Dispose();
        }

        lock (_gate)
        {
            decoder.Reserved = 0;
            _decoders.Remove(decoder.Id);
        }
    }
}
