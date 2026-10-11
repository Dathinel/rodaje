using Rodaje.Audio;
using Rodaje.Audio.Metering;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline.Retime;

namespace Rodaje.Engine.Timeline;

/// <summary>Mezcla de todas las pistas de audio de la secuencia, lista para el dispositivo: float estéreo a 48 kHz.</summary>
/// <remarks>
/// <para>
/// La mezcla se calcula por delante en un hilo propio (<see cref="BufferedAudioSource"/>), así que
/// <see cref="Read"/> y <see cref="Seek"/>, que llegan del hilo de audio, solo copian memoria. El seek se
/// propaga a <see cref="MixRenderer"/> en ese hilo, que reposiciona cada decodificador al volver a necesitarlo.
/// </para>
/// <para><see cref="Reload"/> llega del hilo de la ventana: cambia el plan y vuelve a mezclar lo que aún no suena.</para>
/// <para>
/// La mezcla se mide al producirse, por delante de lo que suena, y cada bloque guarda su muestra de la línea de tiempo
/// en <see cref="Levels"/>: quien dibuja los medidores lee los del instante que da el reloj del dispositivo.
/// </para>
/// </remarks>
internal sealed class AudioMixer : IAudioSampleSource, IDisposable
{
    /// <summary>Lo ya mezclado que se conserva tras una edición: cubre el tiempo de reabrir y reposicionar sin cortes.</summary>
    private const int KeepAfterReloadFrames = TimelinePlan.AudioSampleRate * 3 / 100;

    private readonly MixRenderer _renderer;
    private readonly BufferedAudioSource _buffer;

    public AudioMixer(AudioPlan plan, Func<string, IPcmSource> openDecoder, TimeSpan readAhead, TimeSpan idleTimeout, TimeProvider time)
    {
        Levels = new AudioLevelRing();
        _renderer = new MixRenderer(plan, openDecoder, idleTimeout, time, new AudioLevelAnalyzer(Levels));
        int capacity = (int)Math.Ceiling(readAhead.TotalSeconds * TimelinePlan.AudioSampleRate);
        try
        {
            _buffer = new BufferedAudioSource(_renderer, capacity);
        }
        catch
        {
            _renderer.Dispose();
            throw;
        }
    }

    /// <summary>Niveles de la mezcla por bloques, con su posición en la línea de tiempo. Se lee desde cualquier hilo.</summary>
    public AudioLevelRing Levels { get; }

    public int DecodersOpen => _renderer.DecodersOpen;

    public long Underruns => _buffer.Underruns;

    public long Errors => _renderer.Errors + _buffer.Errors;

    internal int BufferedFrames => _buffer.BufferedFrames;

    public int Read(Span<float> interleaved) => _buffer.Read(interleaved);

    public void Seek(Time position) => _buffer.Seek(position);

    public void Reload(AudioPlan plan)
    {
        _renderer.SetPlan(plan);
        _buffer.Rewind(KeepAfterReloadFrames);
    }

    public void Dispose() => _buffer.Dispose();
}

/// <summary>Suma, muestra a muestra, los clips de las pistas de audio que suenan en cada instante.</summary>
/// <remarks>
/// <para>
/// El instante de la línea de tiempo se cuenta en muestras de 48 kHz. Un clip suena en
/// <c>[StartSample, EndSample)</c> y su muestra <c>s</c> es la <c>SourceInSample + (s - StartSample)</c> del
/// origen, así que los bordes entre clips son exactos a la muestra; los huecos son silencio.
/// </para>
/// <para>
/// Los decodificadores se agrupan por medio: un clip toma uno libre de su medio, preferentemente el que
/// quedó justo donde empieza (dos mitades de un corte siguen sin seek), y lo devuelve al terminar. Uno libre
/// durante <c>idleTimeout</c> se cierra. Un medio que falla al abrirse o decodificarse queda en silencio
/// hasta el siguiente plan.
/// </para>
/// <para>
/// La suma pasa por <see cref="SoftClip"/>: idéntica bajo el umbral y acotada por debajo de 1 por encima. Si hay un
/// analizador, mide lo que sale del limitador y marca los bloques en los que la suma pasó de 1 antes de él.
/// </para>
/// <para>Se usa desde un solo hilo, el de la mezcla, salvo <see cref="SetPlan"/> y los contadores.</para>
/// </remarks>
internal sealed class MixRenderer : IPcmSource
{
    public const float Knee = 0.9f;

    /// <summary>Muestras por bloque de ganancia: se evalúa en sus bordes y se interpola dentro.</summary>
    public const int GainBlock = 64;

    /// <summary>
    /// Cuánto puede cambiar por bloque la ganancia tras una edición: con 4 dB la rampa lineal no da pasos de más de
    /// 0,1 dB entre muestras y 60 dB se recorren en 20 ms.
    /// </summary>
    public const double SlewDbPerBlock = 4;

    private const int Channels = 2;

    private readonly Func<string, IPcmSource> _open;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;
    private readonly List<Reader> _readers = [];
    private readonly HashSet<SourceKey> _failed = [];
    private readonly AudioLevelAnalyzer? _levels;
    private AudioPlan _plan;
    private AudioPlan? _pendingPlan;
    private float[] _scratch = new float[4096 * Channels];
    private float[] _beforeLimiter = [];
    private long _position;
    private int _decodersOpen;
    private long _errors;

    // Al cambiar de plan, la ganancia de cada clip que ya sonaba parte de la que tenía y llega a la nueva sin saltos.
    private Dictionary<string, Slew> _slews = new(StringComparer.Ordinal);
    private AudioPlan? _retiredPlan;
    private Dictionary<string, Slew>? _retiredSlews;
    private bool _anchorPending;
    private long _anchor;

    public MixRenderer(AudioPlan plan, Func<string, IPcmSource> openDecoder, TimeSpan idleTimeout, TimeProvider time, AudioLevelAnalyzer? levels = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(openDecoder);
        ArgumentNullException.ThrowIfNull(time);
        _plan = plan;
        _open = openDecoder;
        _idleTimeout = idleTimeout;
        _time = time;
        _levels = levels;
    }

    /// <summary>Muestra de la línea de tiempo que devolverá el próximo <see cref="Read"/>.</summary>
    public long Position => _position;

    public int DecodersOpen => Volatile.Read(ref _decodersOpen);

    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Lo adopta la mezcla en su próxima lectura o seek. Seguro desde cualquier hilo.</summary>
    public void SetPlan(AudioPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Volatile.Write(ref _pendingPlan, plan);
    }

    /// <summary>Siempre llena todo lo pedido: más allá de los clips hay silencio.</summary>
    public int Read(Span<float> interleaved)
    {
        AdoptPlan();
        if (_anchorPending)
        {
            AnchorSlews(_position);
        }
        else if (_retiredPlan is not null && _position - _anchor > TimelinePlan.AudioSampleRate)
        {
            _retiredPlan = null;
            _retiredSlews = null;
        }

        int frames = interleaved.Length / Channels;
        interleaved = interleaved[..(frames * Channels)];
        interleaved.Clear();
        long from = _position;
        long to = from + frames;
        foreach (Reader reader in _readers)
        {
            if (reader.Clip is { } clip && (clip.EndSample <= from || clip.StartSample >= to))
            {
                Release(reader);
            }
        }

        foreach (AudioTrackPlan track in _plan.Tracks)
        {
            for (int i = FirstEndingAfter(track.Clips, from); i < track.Clips.Count && track.Clips[i].StartSample < to; i++)
            {
                AudioClipPlan clip = track.Clips[i];
                long start = Math.Max(from, clip.StartSample);
                long end = Math.Min(to, clip.EndSample);
                MixClip(clip, start, end, interleaved.Slice((int)(start - from) * Channels, (int)(end - start) * Channels));
            }
        }

        Span<float> beforeLimiter = default;
        if (_levels is not null)
        {
            if (_beforeLimiter.Length < interleaved.Length)
            {
                _beforeLimiter = new float[interleaved.Length];
            }

            beforeLimiter = _beforeLimiter.AsSpan(0, interleaved.Length);
            interleaved.CopyTo(beforeLimiter);
        }

        for (int i = 0; i < interleaved.Length; i++)
        {
            interleaved[i] = SoftClip(interleaved[i]);
        }

        _levels?.Process(interleaved, from, beforeLimiter);
        _position = to;
        CloseIdle();
        return frames;
    }

    public void Seek(Time position)
    {
        AdoptPlan();
        _position = Math.Max(0, position.ToSamples(TimelinePlan.AudioSampleRate));
        if (_retiredPlan is not null)
        {
            // La edición rebobina la mezcla hasta aquí: lo que ya suena salió con la ganancia del plan anterior.
            AnchorSlews(_position);
            _retiredPlan = null;
            _retiredSlews = null;
        }

        _levels?.Restart(_position);
        foreach (Reader reader in _readers)
        {
            Release(reader);
        }
    }

    public void Dispose()
    {
        foreach (Reader reader in _readers)
        {
            reader.Pcm.Dispose();
        }

        _readers.Clear();
        Volatile.Write(ref _decodersOpen, 0);
    }

    /// <summary>Identidad hasta <see cref="Knee"/>; por encima se curva con tanh hacia 1 sin alcanzarlo.</summary>
    public static float SoftClip(float sample)
    {
        float magnitude = MathF.Abs(sample);
        if (magnitude <= Knee || float.IsNaN(sample))
        {
            return float.IsNaN(sample) ? 0 : sample;
        }

        float headroom = 1 - Knee;
        float shaped = Knee + (headroom * MathF.Tanh((magnitude - Knee) / headroom));
        return MathF.CopySign(MathF.Min(shaped, 1f - 1e-6f), sample);
    }

    private void MixClip(AudioClipPlan clip, long start, long end, Span<float> destination)
    {
        if (_failed.Contains(clip.Source))
        {
            return;
        }

        if (clip.Retime is { } retime)
        {
            MixRetimed(clip, retime, start, end, destination);
            return;
        }

        long sourceSample = clip.SourceInSample + (start - clip.StartSample);
        if (sourceSample < 0)
        {
            // Una transición adelantó el clip más allá del principio de su medio: eso es silencio.
            long silent = Math.Min(end - start, -sourceSample);
            start += silent;
            sourceSample += silent;
            destination = destination[(int)(silent * Channels)..];
            if (start >= end)
            {
                return;
            }
        }

        Reader? reader = Acquire(clip, sourceSample);
        if (reader is null)
        {
            return;
        }

        int frames = (int)(end - start);
        if (_scratch.Length < frames * Channels)
        {
            _scratch = new float[frames * Channels];
        }

        Span<float> scratch = _scratch.AsSpan(0, frames * Channels);
#pragma warning disable CA1031 // Un medio dañado no puede callar la mezcla: queda en silencio y se cuenta.
        try
        {
            if (reader.Next != sourceSample)
            {
                reader.Pcm.Seek(Time.FromSamples(sourceSample, TimelinePlan.AudioSampleRate));
                reader.Next = sourceSample;
            }

            int got = 0;
            while (got < frames)
            {
                int read = reader.Pcm.Read(scratch[(got * Channels)..]);
                if (read <= 0)
                {
                    break;
                }

                got += read;
            }

            reader.Next += got;
            AddScaled(clip, start, scratch[..(got * Channels)], destination);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
            _failed.Add(clip.Source);
            Close(reader);
        }
#pragma warning restore CA1031

        if (clip.EndSample <= end && reader.Clip == clip)
        {
            Release(reader);
        }
    }

    /// <summary>
    /// Un clip con velocidad: el decodificador lo lee <see cref="RetimedReader"/>, que se queda con el lector mientras
    /// muestre el mismo origen en cada instante (<see cref="RetimedReader.Serves"/>). Un congelado es silencio.
    /// </summary>
    private void MixRetimed(AudioClipPlan clip, AudioRetime retime, long start, long end, Span<float> destination)
    {
        if (retime.Map.Freeze)
        {
            return;
        }

        Reader? reader = Acquire(clip, -1);
        if (reader is null)
        {
            return;
        }

        int frames = (int)(end - start);
        if (_scratch.Length < frames * Channels)
        {
            _scratch = new float[frames * Channels];
        }

        Span<float> scratch = _scratch.AsSpan(0, frames * Channels);
#pragma warning disable CA1031 // Un medio dañado no puede callar la mezcla: queda en silencio y se cuenta.
        try
        {
            if (reader.Retimed is not { } retimed || !retimed.Serves(retime))
            {
                retimed = new RetimedReader(retime, reader.Pcm, reader.Next);
                reader.Retimed = retimed;
            }
            else
            {
                retimed.Adopt(retime);
            }

            retimed.Read(start, scratch, reader.Next);
            reader.Next = retimed.DecoderNext;
            AddScaled(clip, start, scratch, destination);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
            _failed.Add(clip.Source);
            Close(reader);
        }
#pragma warning restore CA1031

        if (clip.EndSample <= end && reader.Clip == clip)
        {
            Release(reader);
        }
    }

    /// <summary>
    /// Suma el clip con su volumen: la ganancia en dB se evalúa en los bordes de bloques de <see cref="GainBlock"/>
    /// muestras (alineados a la línea de tiempo) y se interpola en línea recta dentro de cada uno; los fundidos van exactos
    /// por muestra mientras cambian.
    /// </summary>
    private void AddScaled(AudioClipPlan clip, long start, ReadOnlySpan<float> source, Span<float> destination)
    {
        GainPlan? gain = clip.Gain;
        Slew slew = default;
        bool slewing = _slews.Count > 0 && _slews.TryGetValue(clip.ClipId, out slew);
        if (gain is null && !slewing)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] += source[i];
            }

            return;
        }

        int frames = source.Length / Channels;
        long sample = start;
        int done = 0;
        while (done < frames)
        {
            long blockStart = sample - (((sample % GainBlock) + GainBlock) % GainBlock);
            int count = (int)Math.Min(frames - done, blockStart + GainBlock - sample);
            double g0 = Level(clip, gain, slewing ? slew : default, blockStart);
            double g1 = Level(clip, gain, slewing ? slew : default, blockStart + GainBlock);
            bool fading = gain is not null && gain.FadesWithin(sample, sample + count);
            double fixedFade = fading ? 1 : gain?.Fades(sample) ?? 1;
            for (int k = 0; k < count; k++)
            {
                long s = sample + k;
                double g = (g0 + ((g1 - g0) * (s - blockStart) / GainBlock)) * (fading ? gain!.Fades(s) : fixedFade);
                int at = (done + k) * Channels;
                destination[at] += (float)(source[at] * g);
                destination[at + 1] += (float)(source[at + 1] * g);
            }

            done += count;
            sample += count;
        }
    }

    /// <summary>Ganancia lineal sin fundidos en <paramref name="sample"/>: la del plan más lo que falta para llegar a ella desde la anterior.</summary>
    private static double Level(AudioClipPlan clip, GainPlan? gain, Slew slew, long sample)
    {
        double db = gain is null ? 0 : gain.Db(sample, clip.SourceSampleAt(sample));
        return GainPlan.ToLinear(db + slew.Offset(sample));
    }

    private static double PlanDb(AudioClipPlan clip, long sample) =>
        Math.Max(GainPlan.SilenceDb, clip.Gain?.Db(sample, clip.SourceSampleAt(sample)) ?? 0);

    /// <summary>Para cada clip que sonaba en <paramref name="at"/> con otra ganancia, arranca desde la que tenía.</summary>
    private void AnchorSlews(long at)
    {
        _anchorPending = false;
        _anchor = at;
        var slews = new Dictionary<string, Slew>(StringComparer.Ordinal);
        if (_retiredPlan is { } retired)
        {
            foreach (AudioTrackPlan track in _plan.Tracks)
            {
                foreach (AudioClipPlan clip in track.Clips)
                {
                    if (clip.StartSample > at || clip.EndSample <= at || Find(retired, clip.ClipId) is not { } old
                        || old.StartSample > at || old.EndSample <= at)
                    {
                        continue;
                    }

                    double before = Math.Max(GainPlan.SilenceDb, PlanDb(old, at) + (_retiredSlews?.GetValueOrDefault(old.ClipId).Offset(at) ?? 0));
                    double offset = before - PlanDb(clip, at);
                    if (Math.Abs(offset) > 0.001)
                    {
                        // Empieza a acercarse en el borde del bloque siguiente: el bloque partido queda con la ganancia vieja.
                        slews[clip.ClipId] = new Slew(offset, at + ((GainBlock - (at % GainBlock)) % GainBlock));
                    }
                }
            }
        }

        _slews = slews;
    }

    private static AudioClipPlan? Find(AudioPlan plan, string clipId)
    {
        foreach (AudioTrackPlan track in plan.Tracks)
        {
            foreach (AudioClipPlan clip in track.Clips)
            {
                if (clip.ClipId == clipId)
                {
                    return clip;
                }
            }
        }

        return null;
    }

    private Reader? Acquire(AudioClipPlan clip, long sourceSample)
    {
        Reader? chosen = _readers.Find(r => r.Clip == clip)
            ?? (clip.Retime is { } retime ? _readers.Find(r => r.Clip is null && r.Source == clip.Source && r.Retimed?.Serves(retime) == true) : null)
            ?? _readers.Find(r => r.Clip is null && r.Source == clip.Source && r.Next == sourceSample)
            ?? _readers.Find(r => r.Clip is null && r.Source == clip.Source);
        if (chosen is null)
        {
#pragma warning disable CA1031 // Un medio que no abre queda en silencio; la mezcla sigue con los demás.
            try
            {
                chosen = new Reader(clip.Source, _open(clip.Source.Path));
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _errors);
                _failed.Add(clip.Source);
                return null;
            }
#pragma warning restore CA1031

            _readers.Add(chosen);
            Interlocked.Increment(ref _decodersOpen);
        }

        chosen.Clip = clip;
        chosen.IdleSince = null;
        return chosen;
    }

    private void Release(Reader reader)
    {
        if (reader.Clip is not null)
        {
            reader.Clip = null;
            reader.IdleSince = _time.GetTimestamp();
        }
    }

    private static void Close(Reader reader)
    {
        reader.Pcm.Dispose();
        reader.Closed = true;
        reader.Clip = null;
    }

    private void CloseIdle()
    {
        long now = _time.GetTimestamp();
        foreach (Reader reader in _readers)
        {
            if (!reader.Closed && reader.IdleSince is { } since && _time.GetElapsedTime(since, now) >= _idleTimeout)
            {
                Close(reader);
            }
        }

        int removed = _readers.RemoveAll(r => r.Closed);
        if (removed > 0)
        {
            Interlocked.Add(ref _decodersOpen, -removed);
        }
    }

    private void AdoptPlan()
    {
        if (Interlocked.Exchange(ref _pendingPlan, null) is not { } plan)
        {
            return;
        }

        _retiredPlan = _plan;
        _retiredSlews = _slews;
        _anchorPending = true;
        _plan = plan;
        _failed.Clear();
        foreach (Reader reader in _readers)
        {
            Release(reader);
        }
    }

    private static int FirstEndingAfter(IReadOnlyList<AudioClipPlan> clips, long sample)
    {
        int lo = 0;
        int hi = clips.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (clips[mid].EndSample <= sample)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Diferencia en dB entre la ganancia que sonaba en <paramref name="From"/> y la del plan nuevo; se agota a <see cref="SlewDbPerBlock"/> por bloque.</summary>
    private readonly record struct Slew(double Db, long From)
    {
        public double Offset(long sample)
        {
            if (Db == 0)
            {
                return 0;
            }

            double left = Math.Abs(Db) - (SlewDbPerBlock * Math.Max(0, sample - From) / GainBlock);
            return left <= 0 ? 0 : Math.CopySign(left, Db);
        }
    }

    private sealed class Reader(SourceKey source, IPcmSource pcm)
    {
        public SourceKey Source { get; } = source;

        public IPcmSource Pcm { get; } = pcm;

        /// <summary>Clip al que sirve ahora; <c>null</c> si está libre.</summary>
        public AudioClipPlan? Clip { get; set; }

        /// <summary>Muestra del origen que devolverá su próxima lectura; -1 si no se sabe.</summary>
        public long Next { get; set; } = -1;

        public long? IdleSince { get; set; }

        /// <summary>Lo que lee un clip con velocidad; se conserva mientras el clip siga igual, con su historia de trozos.</summary>
        public RetimedReader? Retimed { get; set; }

        public bool Closed { get; set; }
    }
}
