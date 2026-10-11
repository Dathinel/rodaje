using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Timeline;

// Presentación: qué cuadro de cada capa se muestra en cada refresco, seek y pre-roll.
internal sealed partial class TimelineScheduler
{
    private void Present(LayerState layer, VideoClipPlan clip, MediaState media, long due, bool running, bool approximate,
        List<DecodedFrame> released)
    {
        VideoClipPlan? active = layer.ActiveClip;
        if (active is null || !active.SameMapping(clip))
        {
            // Mismo clip con otros tiempos (una edición mientras suena) no es un corte: espera su cuadro exacto.
            bool edited = active?.ClipId == clip.ClipId;
            bool natural = running && !_seekPending && !layer.Resync && !edited;
            if (natural)
            {
                layer.Cuts++;
                if (_cache.Contains(media.Id, due))
                {
                    layer.PreRollHits++;
                }
                else
                {
                    layer.PreRollMisses++;
                    layer.AwaitingCut = true;
                }
            }

            layer.ActiveClip = clip;
            layer.LowerBound = natural ? clip.FirstFrame : due;
            layer.CountDrops = false;
        }
        else if (layer.Resync)
        {
            layer.LowerBound = due;
            layer.CountDrops = false;
        }

        layer.Resync = false;
        if (layer.CurrentClip?.ClipId == clip.ClipId && layer.CurrentMedia == media)
        {
            // Tras una edición el plan es otro objeto: la transformación del cuadro en pantalla sale del nuevo.
            layer.CurrentClip = clip;
        }

        // Si el medio cambió de archivo (llegó su proxy), el cuadro en pantalla se reemplaza aunque sea el debido.
        bool outdated = layer.CurrentMedia == media && layer.CurrentVariant != media.Variant;
        if (layer.IsShowing(media, due) && !outdated)
        {
            return;
        }

        if (due < layer.LowerBound)
        {
            // Volver atrás no es perder cuadros; reemplazar el mismo cuadro por el del archivo nuevo, tampoco.
            layer.CountDrops &= outdated && layer.Current is { } shown && shown.Index <= due;
            layer.LowerBound = due;
        }

        if (!_cache.TryGetLatest(media.Id, layer.LowerBound, due, out DecodedFrame? frame, out int slot))
        {
            return;
        }

        Variant? variant = _decoders.TryGetValue(slot, out DecoderState? source) ? source.Variant : null;
        if (outdated && variant != media.Variant && layer.Current?.Index == frame.Index)
        {
            // El mismo cuadro del archivo anterior: se sigue mostrando el que ya está hasta que llegue el nuevo.
            released.Add(frame);
            return;
        }

        // Saltar de keyframe en keyframe a más de 16× es lo pedido, no una pérdida.
        if (layer.CountDrops && running && !approximate && layer.CurrentMedia == media && layer.Current is { } previous)
        {
            layer.Dropped += clip.ShownBetween(previous.Index, frame.Index);
        }

        bool replacement = layer.CurrentMedia == media && layer.Current?.Index == frame.Index;
        layer.Release(released);
        layer.Current = frame;
        layer.CurrentClip = clip;
        layer.CurrentMedia = media;
        layer.CurrentLook = source?.Look ?? Look.Square;
        layer.CurrentVariant = variant;
        layer.Shown += replacement ? 0 : 1;
        layer.LowerBound = frame.Index + 1;
        layer.CountDrops = true;
    }

    private LayerState Layer(VideoLayerPlan plan)
    {
        if (!_layers.TryGetValue(plan.TrackId, out LayerState? layer))
        {
            layer = new LayerState(plan.TrackId) { Resync = true };
            _layers.Add(plan.TrackId, layer);
        }

        return layer;
    }

    private Transition SeekLocked(Time position)
    {
        Time last = _plan.Duration > Time.Zero ? _plan.Duration - Time.FromTicks(1) : Time.Zero;
        Time clamped = position < Time.Zero ? Time.Zero : position > last ? last : position;
        _clock.Seek(clamped);
        if (!_seekPending)
        {
            // El tope de la espera cuenta desde el primer seek de una racha: arrastrar el cursor no la alarga sin fin.
            _seekAt = _time.GetTimestamp();
            _partialShown = false;
        }
        var stale = new List<CancellationTokenSource>();
        foreach (DecoderState decoder in _decoders.Values)
        {
            stale.Add(decoder.Epoch);
            decoder.Epoch = new CancellationTokenSource();
        }

        foreach (MediaState media in _media.Values)
        {
            media.Unavailable.Clear();
            media.Requested.Clear();
        }

        foreach (LayerState layer in _layers.Values)
        {
            layer.Resync = true;
            layer.AwaitingCut = false;
        }

        _seekPending = true;
        _endSignaled = false;
        _seeks++;
        return new Transition(stale, UpdateDemand(clamped.ToFrames(_plan.FrameRate), force: true));
    }

    private long CurrentSequenceFrame() => _plan.FrameStart(_clock.Position).ToFrames(_plan.FrameRate);

    /// <summary>
    /// Cuánto antes del corte empieza el pre-roll: lo pedido, o más si un decodificador nuevo tarda en entregar su primer
    /// cuadro (varias capas 4K se reparten el decodificador de la GPU), hasta 2 s.
    /// </summary>
    private Time PreRollLead => _preRoll <= Time.Zero || _startupSeconds <= 0
        ? _preRoll
        : Time.Max(_preRoll, Time.FromSeconds(Math.Min(MaxPreRollSeconds, StartupMargin * _startupSeconds)));
}
