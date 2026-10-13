using Rodaje.Audio;
using Rodaje.Audio.Metering;
using Rodaje.Audio.Processing;
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
    private bool _hadEffects;

    public AudioMixer(AudioPlan plan, Func<string, IPcmSource> openDecoder, TimeSpan readAhead, TimeSpan idleTimeout, TimeProvider time)
    {
        Levels = new AudioLevelRing();
        _renderer = new MixRenderer(plan, openDecoder, idleTimeout, time, new AudioLevelAnalyzer(Levels), stripMeters: true);
        _hadEffects = plan.HasEffects;
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

        // Con ecualizador o compresor la mezcla vuelve a un borde de lectura, donde guardó su estado: sigue sin saltos.
        bool effects = plan.HasEffects || _hadEffects;
        _hadEffects = plan.HasEffects;
        _buffer.Rewind(KeepAfterReloadFrames, alignToChunk: effects);
    }

    /// <summary>Lo que hace el compresor que se mira, por bloques en la posición en que suena. Se lee desde cualquier hilo.</summary>
    public DynamicsMeterRing Dynamics => _renderer.Dynamics;

    /// <summary>
    /// Cambia el compresor que se mira. Si cambia lo que se escucha (sidechain o A/B), la mezcla vuelve a un borde de
    /// lectura cercano para que se oiga en unos 30 ms y no tras todo lo mezclado por delante.
    /// </summary>
    public void Monitor(DynamicsMonitor? monitor)
    {
        DynamicsMonitor? previous = _renderer.Monitor;
        if (Equals(previous, monitor))
        {
            return;
        }

        _renderer.Monitor = monitor;
        if (previous is { Listen: true } or { Bypass: true } || monitor is { Listen: true } or { Bypass: true })
        {
            _buffer.Rewind(KeepAfterReloadFrames, alignToChunk: true);
        }
    }

    /// <summary>Niveles de cada tira del mezclador (pistas y buses), por su clave. Se lee desde cualquier hilo.</summary>
    public IReadOnlyDictionary<string, AudioLevelRing> StripLevels => _renderer.StripLevels;

    /// <summary>
    /// Inserciones que se escuchan sin efecto (A/B de la vista previa); nula, ninguna. Si cambia, la mezcla vuelve a un
    /// borde de lectura cercano para que se oiga enseguida.
    /// </summary>
    public void BypassInserts(IReadOnlySet<string>? inserts)
    {
        IReadOnlySet<string>? previous = _renderer.BypassedInserts;
        if ((previous?.Count ?? 0) == 0 && (inserts?.Count ?? 0) == 0)
        {
            return;
        }

        if (previous is not null && inserts is not null && previous.SetEquals(inserts))
        {
            return;
        }

        _renderer.BypassedInserts = inserts;
        _buffer.Rewind(KeepAfterReloadFrames, alignToChunk: true);
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

    /// <summary>Clave de la cadena de efectos de la mezcla (<see cref="DynamicsMonitor"/>).</summary>
    public const string MasterKey = "mezcla:";

    /// <summary>Cuánto puede moverse por bloque el paneo o el balance (de −1 a 1) tras una edición: un barrido entero en 43 ms.</summary>
    public const double SlewPanPerBlock = 0.0625;

    /// <summary>Cuadros por bloque del medidor del compresor que se mira y de los medidores de las tiras.</summary>
    private const int MeterBlock = 256;

    private const int Channels = 2;

    private readonly Func<string, IPcmSource> _open;
    private readonly Dictionary<string, Chain> _chains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AheadTrack> _ahead = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _busNames = new(StringComparer.Ordinal);
    private readonly List<float[]> _trackMix = [];
    private readonly List<float[]> _busMix = [];
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;
    private readonly List<Reader> _readers = [];
    private readonly HashSet<SourceKey> _failed = [];
    private readonly AudioLevelAnalyzer? _levels;
    private readonly bool _limit;
    private readonly bool _stripMeters;
    private AudioPlan _plan;
    private double _masterDb;
    private AudioPlan? _pendingPlan;
    private DynamicsMonitor? _monitor;
    private IReadOnlySet<string>? _bypassed;
    private float[] _scratch = new float[4096 * Channels];
    private float[] _beforeLimiter = [];
    private float[] _busOut = [];
    private long _position;
    private int _decodersOpen;
    private long _errors;
    private Dictionary<string, AudioLevelRing> _strips = new(StringComparer.Ordinal);

    // Al cambiar de plan, la ganancia de cada clip que ya sonaba parte de la que tenía y llega a la nueva sin saltos; lo
    // mismo el fader (volumen, paneo y balance) de cada pista y bus.
    private Dictionary<string, Slew> _slews = new(StringComparer.Ordinal);
    private Dictionary<string, FaderSlew> _faderSlews = new(StringComparer.Ordinal);
    private AudioPlan? _retiredPlan;
    private Dictionary<string, Slew>? _retiredSlews;
    private Dictionary<string, FaderSlew>? _retiredFaderSlews;
    private bool _anchorPending;
    private long _anchor;

    /// <param name="limit">Pasa la suma por <see cref="SoftClip"/>; sin él (para medir un clip) sale tal cual.</param>
    /// <param name="stripMeters">Mide cada pista y cada bus (<see cref="StripLevels"/>), para el mezclador de la vista previa.</param>
    public MixRenderer(AudioPlan plan, Func<string, IPcmSource> openDecoder, TimeSpan idleTimeout, TimeProvider time, AudioLevelAnalyzer? levels = null,
        bool limit = true, bool stripMeters = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(openDecoder);
        ArgumentNullException.ThrowIfNull(time);
        _plan = plan;
        _masterDb = plan.MasterDb;
        _limit = limit;
        _stripMeters = stripMeters;
        _open = openDecoder;
        _idleTimeout = idleTimeout;
        _time = time;
        _levels = levels;
        if (stripMeters)
        {
            _strips = StripRings(plan, _strips);
        }
    }

    /// <summary>
    /// Niveles de cada tira del mezclador por su clave (<see cref="TrackKey"/> o <see cref="BusKey"/>), después de su fader
    /// y con la posición en que suena. Se reemplaza entero al cambiar de plan; se lee desde cualquier hilo.
    /// </summary>
    public IReadOnlyDictionary<string, AudioLevelRing> StripLevels => Volatile.Read(ref _strips);

    /// <summary>Inserciones que se escuchan sin efecto (A/B de la vista previa), por su identificador. Seguro desde cualquier hilo.</summary>
    public IReadOnlySet<string>? BypassedInserts
    {
        get => Volatile.Read(ref _bypassed);
        set => Volatile.Write(ref _bypassed, value);
    }

    /// <summary>Muestra de la línea de tiempo que devolverá el próximo <see cref="Read"/>.</summary>
    public long Position => _position;

    public int DecodersOpen => Volatile.Read(ref _decodersOpen);

    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Lo que hace el compresor que se mira (<see cref="Monitor"/>), por bloques y en la posición en que suena.</summary>
    public DynamicsMeterRing Dynamics { get; } = new();

    /// <summary>El compresor que se mira y, solo en la escucha, si se oye su sidechain o se compara sin él. Seguro desde cualquier hilo.</summary>
    public DynamicsMonitor? Monitor
    {
        get => Volatile.Read(ref _monitor);
        set => Volatile.Write(ref _monitor, value);
    }

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
        foreach (Chain chain in _chains.Values)
        {
            chain.Save(from);
        }

        foreach (AheadTrack ahead in _ahead.Values)
        {
            ahead.Save(from);
        }

        foreach (Reader reader in _readers)
        {
            if (reader.Clip is { } clip && (clip.EndSample <= from || clip.StartSample >= to))
            {
                Release(reader);
            }
        }

        if (_plan.MasterEffects is { } master)
        {
            RunChain(MasterKey, master, _plan.MasterAutomation, null, new ChainInput(InputKind.Master, null, null, 0), from, interleaved);
        }
        else
        {
            MixAll(from, to, interleaved);
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

        for (int i = 0; _limit && i < interleaved.Length; i++)
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
        _masterDb = _plan.MasterDb;
        if (_retiredPlan is not null)
        {
            // La edición rebobina la mezcla hasta aquí: lo que ya suena salió con la ganancia del plan anterior.
            AnchorSlews(_position);
            _retiredPlan = null;
            _retiredSlews = null;
            _retiredFaderSlews = null;
        }

        _levels?.Restart(_position);
        foreach (Chain chain in _chains.Values)
        {
            chain.Restore(_position);
        }

        foreach (AheadTrack ahead in _ahead.Values)
        {
            ahead.Restore(_position);
        }

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

    /// <summary>
    /// La mezcla de <c>[from, to)</c> antes de las inserciones de la mezcla: las pistas que salen directo, los buses con lo
    /// que les llega y la ganancia maestra.
    /// </summary>
    private void MixAll(long from, long to, Span<float> output)
    {
        MixTracks(_plan.Tracks, from, to, output, 0);
        int frames = (int)(to - from);
        IReadOnlyList<MixBusPlan> buses = _plan.Buses;
        for (int b = 0; b < buses.Count; b++)
        {
            MixBusPlan bus = buses[b];
            if (_busOut.Length < frames * Channels)
            {
                _busOut = new float[frames * Channels];
            }

            Span<float> mixed = _busOut.AsSpan(0, frames * Channels);
            if (bus.Effects is { } effects)
            {
                RunChain(BusName(bus.BusId), effects, null, null, new ChainInput(InputKind.MixBus, null, null, 0) { Bus = bus }, from, mixed);
            }
            else
            {
                FillBus(bus, from, to, mixed);
            }

            ApplyFader(BusName(bus.BusId), bus.Fader, from, mixed);
            Meter(BusName(bus.BusId), from, mixed);
            for (int i = 0; i < mixed.Length; i++)
            {
                output[i] += mixed[i];
            }
        }

        ApplyMaster(output);
    }

    /// <summary>Lo que recibe el bus en <c>[from, to)</c>: las pistas que salen a él y los envíos, de lo ya mezclado por delante.</summary>
    private void FillBus(MixBusPlan bus, long from, long to, Span<float> into)
    {
        into.Clear();
        int frames = (int)(to - from);
        IReadOnlyList<AudioTrackPlan> tracks = _plan.Tracks;
        for (int t = 0; t < tracks.Count; t++)
        {
            AudioTrackPlan track = tracks[t];
            if (!_ahead.TryGetValue(track.TrackId, out AheadTrack? ahead))
            {
                continue;
            }

            if (track.Direct && track.Output == bus.BusId)
            {
                Add(ahead.Post(from, frames), into, 1);
            }

            for (int i = 0; i < track.Sends.Count; i++)
            {
                AudioSendPlan send = track.Sends[i];
                if (send.BusId == bus.BusId)
                {
                    Add(send.PreFader ? ahead.Pre(from, frames) : ahead.Post(from, frames), into, (float)GainPlan.ToLinear(send.GainDb));
                }
            }
        }
    }

    private static void Add(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        if (source.Length < destination.Length)
        {
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] += source[i] * gain;
        }
    }

    /// <summary>
    /// Suma las pistas en <paramref name="output"/> (<c>[from, to)</c>), cada una con sus inserciones y su fader. Las que
    /// otro compresor escucha, y en la secuencia principal todas si hay buses, se mezclan por delante en un búfer propio
    /// (<see cref="AudioTrackPlan.Ahead"/>): primero las llaves, para que quien las escucha tenga lo que todavía no sale, y
    /// los buses con retraso leen lo suyo adelantado. Las que salen a un bus o calla un solo no se suman aquí.
    /// </summary>
    /// <param name="depth">Nivel de compuestos: cada uno usa sus propios intermedios.</param>
    private void MixTracks(IReadOnlyList<AudioTrackPlan> tracks, long from, long to, Span<float> output, int depth)
    {
        bool route = depth == 0 && _plan.Buses.Count > 0;
        int frames = (int)(to - from);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int t = 0; t < tracks.Count; t++)
            {
                AudioTrackPlan track = tracks[t];
                if ((pass == 0) == (track.KeyAhead >= 0) && (route || track.Ahead > 0 || track.KeyAhead >= 0))
                {
                    Ahead(track).Ensure(this, track, from, to + track.Ahead, depth);
                }
            }
        }

        for (int t = 0; t < tracks.Count; t++)
        {
            AudioTrackPlan track = tracks[t];
            ReadOnlySpan<float> mixed;
            if (_ahead.TryGetValue(track.TrackId, out AheadTrack? ahead) && (route || track.Ahead > 0 || track.KeyAhead >= 0))
            {
                mixed = ahead.Post(from, frames);
            }
            else
            {
                Span<float> own = Buffer(_trackMix, depth, output.Length);
                TrackOutput(track, from, to, own, depth);
                ApplyFader(TrackName(track.TrackId), track.Fader, from, own);
                mixed = own;
            }

            if (depth == 0)
            {
                Meter(TrackName(track.TrackId), from, mixed);
            }

            if (track.Direct && (depth > 0 || track.Output is null))
            {
                Add(mixed, output, 1);
            }
        }
    }

    /// <summary>Lo que sale de las inserciones de la pista en <c>[from, to)</c>, antes del fader, en <paramref name="output"/> (que se limpia).</summary>
    private void TrackOutput(AudioTrackPlan track, long from, long to, Span<float> output, int depth)
    {
        output.Clear();
        if (track.Effects is { } effects)
        {
            // La pista suena también en sus huecos: la cola de una reverb y la relajación del compresor siguen.
            RunChain(TrackName(track.TrackId), effects, track.Automation, null, new ChainInput(InputKind.Track, null, track, depth), from, output);
        }
        else
        {
            MixTrackClips(track, from, to, output, depth);
        }
    }

    /// <summary>
    /// El fader de una pista o un bus en <paramref name="io"/> (desde <paramref name="start"/>): el volumen se evalúa en los
    /// bordes de bloques de <see cref="GainBlock"/> muestras alineados a la línea de tiempo y se interpola en línea recta,
    /// igual que el paneo y el balance (su matriz). Tras una edición parte de lo que sonaba (<see cref="FaderSlew"/>).
    /// </summary>
    private void ApplyFader(string key, FaderPlan? fader, long start, Span<float> io)
    {
        FaderSlew slew = default;
        bool slewing = _faderSlews.Count > 0 && _faderSlews.TryGetValue(key, out slew);
        if (fader is null && !slewing)
        {
            return;
        }

        int frames = io.Length / Channels;
        long sample = start;
        int done = 0;
        while (done < frames)
        {
            long blockStart = sample - (((sample % GainBlock) + GainBlock) % GainBlock);
            int count = (int)Math.Min(frames - done, blockStart + GainBlock - sample);
            (double g0, PanMatrix m0) = FaderAt(fader, slew, blockStart);
            (double g1, PanMatrix m1) = FaderAt(fader, slew, blockStart + GainBlock);
            bool panning = !m0.IsIdentity || !m1.IsIdentity;
            for (int k = 0; k < count; k++)
            {
                long s = sample + k;
                float t = (float)(s - blockStart) / GainBlock;
                float g = (float)(g0 + ((g1 - g0) * t));
                int at = (done + k) * Channels;
                float l = io[at], r = io[at + 1];
                if (panning)
                {
                    PanMatrix m = PanMatrix.Lerp(m0, m1, t);
                    (l, r) = ((l * m.LeftToLeft) + (r * m.RightToLeft), (l * m.LeftToRight) + (r * m.RightToRight));
                }

                io[at] = l * g;
                io[at + 1] = r * g;
            }

            done += count;
            sample += count;
        }
    }

    private static (double Gain, PanMatrix Pan) FaderAt(FaderPlan? fader, FaderSlew slew, long sample)
    {
        double db = (fader?.GainDb(sample) ?? 0) + slew.Db.Offset(sample);
        double pan = (fader?.PanAt(sample) ?? 0) + slew.Pan.Offset(sample);
        double balance = (fader?.BalanceAt(sample) ?? 0) + slew.Balance.Offset(sample);
        return (GainPlan.ToLinear(db), PanLaw.Matrix(pan, balance));
    }

    /// <summary>Publica los niveles de una tira del mezclador en bloques de <see cref="MeterBlock"/> cuadros.</summary>
    private void Meter(string key, long start, ReadOnlySpan<float> samples)
    {
        if (!_stripMeters || !_strips.TryGetValue(key, out AudioLevelRing? ring))
        {
            return;
        }

        int frames = samples.Length / Channels;
        for (int done = 0; done < frames; done += MeterBlock)
        {
            int n = Math.Min(MeterBlock, frames - done);
            float peakL = 0, peakR = 0;
            double squaresL = 0, squaresR = 0;
            for (int i = done * Channels; i < (done + n) * Channels; i += Channels)
            {
                float l = samples[i], r = samples[i + 1];
                peakL = MathF.Max(peakL, MathF.Abs(l));
                peakR = MathF.Max(peakR, MathF.Abs(r));
                squaresL += l * l;
                squaresR += r * r;
            }

            ring.Write(new LevelBlock(start + done, n, peakL, peakR, peakL, peakR, squaresL, squaresR, peakL > 1 || peakR > 1));
        }
    }

    /// <summary>Un anillo por tira (pistas de la secuencia principal y buses); los de las tiras que siguen se conservan.</summary>
    private static Dictionary<string, AudioLevelRing> StripRings(AudioPlan plan, Dictionary<string, AudioLevelRing> previous)
    {
        var rings = new Dictionary<string, AudioLevelRing>(StringComparer.Ordinal);
        foreach (string key in plan.Tracks.Select(t => TrackKey(t.TrackId)).Concat(plan.Buses.Select(b => BusKey(b.BusId))))
        {
            rings[key] = previous.GetValueOrDefault(key) ?? new AudioLevelRing(1024);
        }

        return rings;
    }

    /// <summary>Suma los clips de la pista que suenan en <c>[from, to)</c>, sin los efectos de la pista.</summary>
    private void MixTrackClips(AudioTrackPlan track, long from, long to, Span<float> output, int depth)
    {
        for (int i = FirstEndingAfter(track.Clips, from); i < track.Clips.Count && track.Clips[i].StartSample < to; i++)
        {
            AudioClipPlan clip = track.Clips[i];
            long start = Math.Max(from, clip.StartSample);
            long end = Math.Min(to, clip.EndSample);
            Span<float> slice = output.Slice((int)(start - from) * Channels, (int)(end - start) * Channels);
            if (clip.Bus is { } nested)
            {
                MixBus(clip, nested, start, end, slice, depth);
            }
            else
            {
                MixClip(clip, start, end, slice);
            }
        }
    }

    /// <summary>Un compuesto que se mezcla aparte: sus pistas, después su ecualizador y compresor, y su volumen y fundidos.</summary>
    private void MixBus(AudioClipPlan clip, AudioBusPlan bus, long start, long end, Span<float> destination, int depth)
    {
        Span<float> mix = Buffer(_busMix, depth, destination.Length);
        if (clip.Effects is { } effects)
        {
            RunChain(clip.ClipId, effects, clip.Automation, clip, new ChainInput(InputKind.Bus, clip, null, depth), start, mix);
        }
        else
        {
            MixTracks(bus.Tracks, start, end, mix, depth + 1);
        }

        AddScaled(clip, start, mix, destination);
    }

    /// <summary>Un intermedio limpio del nivel <paramref name="depth"/>; crece la primera vez y después no reserva.</summary>
    private static Span<float> Buffer(List<float[]> buffers, int depth, int length)
    {
        while (buffers.Count <= depth)
        {
            buffers.Add([]);
        }

        if (buffers[depth].Length < length)
        {
            buffers[depth] = new float[length];
        }

        Span<float> buffer = buffers[depth].AsSpan(0, length);
        buffer.Clear();
        return buffer;
    }

    // Los identificadores de clips, pistas y buses no se repiten en el proyecto; el prefijo solo separa los usos.
    internal static string TrackKey(string trackId) => "pista:" + trackId;

    internal static string BusKey(string busId) => "bus:" + busId;

    /// <summary><see cref="TrackKey"/> sin armar un texto nuevo en cada lectura.</summary>
    private string TrackName(string trackId)
    {
        if (!_names.TryGetValue(trackId, out string? name))
        {
            name = TrackKey(trackId);
            _names[trackId] = name;
        }

        return name;
    }

    private string BusName(string busId)
    {
        if (!_busNames.TryGetValue(busId, out string? name))
        {
            name = BusKey(busId);
            _busNames[busId] = name;
        }

        return name;
    }

    private AheadTrack Ahead(AudioTrackPlan track)
    {
        if (!_ahead.TryGetValue(track.TrackId, out AheadTrack? ahead))
        {
            ahead = new AheadTrack();
            _ahead[track.TrackId] = ahead;
        }

        return ahead;
    }

    /// <summary>
    /// Llena <paramref name="output"/> (<c>[start, start + n)</c> de la línea de tiempo) con la salida de la cadena de
    /// <paramref name="key"/>. Con anticipación o limitador la entrada se pide adelantada su retraso, así la salida cae en
    /// su sitio; si no sigue a lo último que procesó, la cadena empieza de cero y se ceba con ese tramo.
    /// </summary>
    /// <param name="curveClip">El clip cuyos keyframes van en tiempo del origen; nulo en una pista o la mezcla.</param>
    private void RunChain(string key, AudioEffectsSettings settings, DynamicsAutomation? automation, AudioClipPlan? curveClip, ChainInput input,
        long start, Span<float> output)
    {
        if (!_chains.TryGetValue(key, out Chain? chain))
        {
            chain = new Chain(new AudioEffectChain(settings, TimelinePlan.AudioSampleRate));
            _chains[key] = chain;
        }
        else
        {
            chain.Effects.Configure(settings);
        }

        AudioEffectChain effects = chain.Effects;
        DynamicsMonitor? monitor = Monitor;
        bool watched = monitor is not null && monitor.Key == key;
        effects.Bypass = watched && monitor!.Bypass;
        effects.ListenSidechain = watched && monitor!.Listen;
        effects.WatchedInsert = watched ? monitor!.Insert : null;
        IReadOnlySet<string>? bypassed = BypassedInserts;
        if (!ReferenceEquals(effects.BypassedInserts, bypassed))
        {
            effects.BypassedInserts = bypassed;
        }

        int latency = effects.Latency;
        int frames = output.Length / Channels;
        long inputStart = start + latency;
        if (chain.Next != inputStart)
        {
            effects.Reset();
            if (latency > 0)
            {
                Span<float> prime = chain.Prime(latency * Channels);
                Fill(input, start, inputStart, prime);
                Process(effects, prime, settings, automation, curveClip, start, meter: false);
            }
        }

        Fill(input, inputStart, inputStart + frames, output);
        Process(effects, output, settings, automation, curveClip, inputStart, watched);
        chain.Next = inputStart + frames;
    }

    /// <summary>La entrada de una cadena en <c>[from, to)</c>, sin sus efectos.</summary>
    private void Fill(ChainInput input, long from, long to, Span<float> into)
    {
        into.Clear();
        switch (input.Kind)
        {
            case InputKind.Clip:
                ReadClip(input.Clip!, from, to, into);
                break;
            case InputKind.Track:
                MixTrackClips(input.Track!, from, to, into, input.Depth);
                break;
            case InputKind.Bus:
                MixTracks(input.Clip!.Bus!.Tracks, from, to, into, input.Depth + 1);
                break;
            case InputKind.MixBus:
                FillBus(input.Bus!, from, to, into);
                break;
            default:
                MixAll(from, to, into);
                break;
        }
    }

    /// <summary>
    /// Pasa <paramref name="samples"/> (la entrada desde <paramref name="inputStart"/>) por la cadena. Con keyframes va por
    /// bloques de <see cref="GainBlock"/> alineados a la línea de tiempo: umbral y relación fijos en cada uno y la mezcla en
    /// línea recta, así el resultado no depende de cómo se parta la lectura. El compresor que se mira se mide por bloques.
    /// </summary>
    private void Process(AudioEffectChain effects, Span<float> samples, AudioEffectsSettings settings, DynamicsAutomation? automation,
        AudioClipPlan? curveClip, long inputStart, bool meter)
    {
        int frames = samples.Length / Channels;
        int latency = effects.Latency;
        if (automation is null)
        {
            effects.ClearAutomation();
        }

        int done = 0;
        while (done < frames)
        {
            long s = inputStart + done;
            int n = frames - done;
            if (automation is not null)
            {
                long block = s - (((s % GainBlock) + GainBlock) % GainBlock);
                n = (int)Math.Min(n, block + GainBlock - s);
                double mix0 = automation.Mix.Evaluate(CurvePosition(curveClip, block - latency)) / 100;
                double mix1 = automation.Mix.Evaluate(CurvePosition(curveClip, block + GainBlock - latency)) / 100;
                long at = CurvePosition(curveClip, block);
                effects.Automate(automation.Threshold.Evaluate(at), automation.Ratio.Evaluate(at),
                    mix0 + ((mix1 - mix0) * (s - block) / GainBlock), (mix1 - mix0) / GainBlock);
            }

            if (meter)
            {
                n = Math.Min(n, MeterBlock);
            }

            effects.Process(samples.Slice(done * Channels, n * Channels), KeySignal(settings, s, n));
            if (meter)
            {
                Dynamics.Write(s - latency, n, effects.Reading);
            }

            done += n;
        }
    }

    private static long CurvePosition(AudioClipPlan? clip, long sample) => clip is null ? sample : clip.SourceSampleAt(sample);

    /// <summary>La mezcla de la pista sidechain en <c>[from, from + frames)</c>; vacío si no hay llave o no cubre ese tramo.</summary>
    private ReadOnlySpan<float> KeySignal(AudioEffectsSettings settings, long from, int frames) =>
        settings.SidechainTrackId is { } id && _ahead.TryGetValue(id, out AheadTrack? key) ? key.Post(from, frames) : default;

    /// <summary>
    /// El ecualizador y el compresor de la mezcla y la ganancia maestra; tras un cambio llega a la nueva a
    /// <see cref="SlewDbPerBlock"/> por bloque, como el volumen de los clips. Tras un seek ya está en la del plan.
    /// </summary>
    private void ApplyMaster(Span<float> mix)
    {
        double target = _plan.MasterDb;
        if (_masterDb == target)
        {
            if (target != 0)
            {
                float gain = (float)GainPlan.ToLinear(target);
                for (int i = 0; i < mix.Length; i++)
                {
                    mix[i] *= gain;
                }
            }

            return;
        }

        const double step = SlewDbPerBlock / GainBlock;
        for (int i = 0; i + 1 < mix.Length; i += Channels)
        {
            _masterDb = _masterDb < target ? Math.Min(target, _masterDb + step) : Math.Max(target, _masterDb - step);
            float gain = (float)GainPlan.ToLinear(_masterDb);
            mix[i] *= gain;
            mix[i + 1] *= gain;
        }
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
        int frames = (int)(end - start);
        if (clip.Effects is { } effects)
        {
            if (!_chains.TryGetValue(clip.ClipId, out Chain? chain))
            {
                chain = new Chain(new AudioEffectChain(effects, TimelinePlan.AudioSampleRate));
                _chains[clip.ClipId] = chain;
            }

            Span<float> processed = chain.Output(frames * Channels);
            RunChain(clip.ClipId, effects, clip.Automation, clip, new ChainInput(InputKind.Clip, clip, null, 0), start, processed);
            AddScaled(clip, start, processed, destination);
            return;
        }

        if (_scratch.Length < frames * Channels)
        {
            _scratch = new float[frames * Channels];
        }

        Span<float> scratch = _scratch.AsSpan(0, frames * Channels);
        scratch.Clear();
        ReadClip(clip, start, end, scratch);
        AddScaled(clip, start, scratch, destination);
    }

    /// <summary>
    /// El origen del clip en <c>[start, end)</c> de la línea de tiempo, sin volumen ni efectos, en <paramref name="into"/>
    /// (limpio). Puede pasar del final del clip (la anticipación de un compresor): sigue el medio, y donde no hay, silencio.
    /// </summary>
    private void ReadClip(AudioClipPlan clip, long start, long end, Span<float> into)
    {
        if (_failed.Contains(clip.Source))
        {
            return;
        }

        if (clip.Retime is { } retime)
        {
            ReadRetimed(clip, retime, start, end, into);
            return;
        }

        long sourceSample = clip.SourceInSample + (start - clip.StartSample);
        if (sourceSample < 0)
        {
            // Una transición adelantó el clip más allá del principio de su medio: eso es silencio.
            long silent = Math.Min(end - start, -sourceSample);
            start += silent;
            sourceSample += silent;
            into = into[(int)(silent * Channels)..];
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
                int read = reader.Pcm.Read(into[(got * Channels)..(frames * Channels)]);
                if (read <= 0)
                {
                    break;
                }

                got += read;
            }

            reader.Next += got;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
            _failed.Add(clip.Source);
            Close(reader);
            into.Clear();
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
    private void ReadRetimed(AudioClipPlan clip, AudioRetime retime, long start, long end, Span<float> into)
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

            retimed.Read(start, into[..(frames * Channels)], reader.Next);
            reader.Next = retimed.DecoderNext;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _errors);
            _failed.Add(clip.Source);
            Close(reader);
            into.Clear();
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
            PanMatrix m0 = PanMatrix.Identity, m1 = PanMatrix.Identity;
            if (gain is { HasPan: true })
            {
                m0 = gain.PanAt(clip.SourceSampleAt(blockStart));
                m1 = gain.PanAt(clip.SourceSampleAt(blockStart + GainBlock));
            }

            bool panning = !m0.IsIdentity || !m1.IsIdentity;
            for (int k = 0; k < count; k++)
            {
                long s = sample + k;
                double g = (g0 + ((g1 - g0) * (s - blockStart) / GainBlock)) * (fading ? gain!.Fades(s) : fixedFade);
                int at = (done + k) * Channels;
                float l = source[at], r = source[at + 1];
                if (panning)
                {
                    PanMatrix m = PanMatrix.Lerp(m0, m1, (float)(s - blockStart) / GainBlock);
                    (l, r) = ((l * m.LeftToLeft) + (r * m.RightToLeft), (l * m.LeftToRight) + (r * m.RightToRight));
                }

                destination[at] += (float)(l * g);
                destination[at + 1] += (float)(r * g);
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

    /// <summary>
    /// Para cada clip que sonaba en <paramref name="at"/> con otra ganancia, y cada fader de pista o bus que cambió, arranca
    /// desde lo que tenía.
    /// </summary>
    private void AnchorSlews(long at)
    {
        _anchorPending = false;
        _anchor = at;
        var slews = new Dictionary<string, Slew>(StringComparer.Ordinal);
        var faders = new Dictionary<string, FaderSlew>(StringComparer.Ordinal);
        long next = at + ((GainBlock - (at % GainBlock)) % GainBlock);
        if (_retiredPlan is { } retired)
        {
            foreach ((string key, FaderPlan? before, FaderPlan? now) in Faders(retired, _plan))
            {
                FaderSlew old = _retiredFaderSlews?.GetValueOrDefault(key) ?? default;
                double db = Math.Max(GainPlan.SilenceDb, (before?.GainDb(at) ?? 0) + old.Db.Offset(at)) - Math.Max(GainPlan.SilenceDb, now?.GainDb(at) ?? 0);
                double pan = (before?.PanAt(at) ?? 0) + old.Pan.Offset(at) - (now?.PanAt(at) ?? 0);
                double balance = (before?.BalanceAt(at) ?? 0) + old.Balance.Offset(at) - (now?.BalanceAt(at) ?? 0);
                if (Math.Abs(db) > 0.001 || Math.Abs(pan) > 1e-4 || Math.Abs(balance) > 1e-4)
                {
                    faders[key] = new FaderSlew(new Slew(db, next), new Slew(pan, next, SlewPanPerBlock), new Slew(balance, next, SlewPanPerBlock));
                }
            }

            foreach (AudioTrackPlan track in AudioPlan.AllTracks(_plan.Tracks))
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
        _faderSlews = faders;
    }

    /// <summary>Los faders de las pistas y buses que están en los dos planes, por su clave, con el de antes y el de ahora.</summary>
    private static IEnumerable<(string Key, FaderPlan? Before, FaderPlan? Now)> Faders(AudioPlan before, AudioPlan now)
    {
        var old = new Dictionary<string, FaderPlan?>(StringComparer.Ordinal);
        foreach (AudioTrackPlan track in AudioPlan.AllTracks(before.Tracks))
        {
            old[TrackKey(track.TrackId)] = track.Fader;
        }

        foreach (MixBusPlan bus in before.Buses)
        {
            old[BusKey(bus.BusId)] = bus.Fader;
        }

        foreach (AudioTrackPlan track in AudioPlan.AllTracks(now.Tracks))
        {
            if (old.TryGetValue(TrackKey(track.TrackId), out FaderPlan? fader) && !Equals(fader, track.Fader))
            {
                yield return (TrackKey(track.TrackId), fader, track.Fader);
            }
        }

        foreach (MixBusPlan bus in now.Buses)
        {
            if (old.TryGetValue(BusKey(bus.BusId), out FaderPlan? fader) && !Equals(fader, bus.Fader))
            {
                yield return (BusKey(bus.BusId), fader, bus.Fader);
            }
        }
    }

    private static AudioClipPlan? Find(AudioPlan plan, string clipId)
    {
        foreach (AudioTrackPlan track in AudioPlan.AllTracks(plan.Tracks))
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
        // Sin lambdas: se llama en cada lectura y no debe reservar memoria.
        Reader? chosen = null;
        for (int pass = 0; pass < 4 && chosen is null; pass++)
        {
            foreach (Reader r in _readers)
            {
                bool match = pass switch
                {
                    0 => r.Clip == clip,
                    1 => clip.Retime is { } retime && r.Clip is null && r.Source == clip.Source && r.Retimed?.Serves(retime) == true,
                    2 => r.Clip is null && r.Source == clip.Source && r.Next == sourceSample,
                    _ => r.Clip is null && r.Source == clip.Source,
                };
                if (match)
                {
                    chosen = r;
                    break;
                }
            }
        }
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
        if (Interlocked.Exchange(ref _pendingPlan, null) is { } plan)
        {
            Adopt(plan);
        }
    }

    /// <summary>Aparte de <see cref="AdoptPlan"/>: sus lambdas reservarían memoria en cada lectura aunque no haya plan nuevo.</summary>
    private void Adopt(AudioPlan plan)
    {
        _retiredPlan = _plan;
        _retiredSlews = _slews;
        _retiredFaderSlews = _faderSlews;
        _anchorPending = true;
        _plan = plan;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (plan.MasterEffects is not null)
        {
            used.Add(MasterKey);
        }

        foreach (MixBusPlan bus in plan.Buses)
        {
            if (bus.Effects is not null)
            {
                used.Add(BusKey(bus.BusId));
            }
        }

        if (plan.Buses.Count > 0)
        {
            keys.UnionWith(plan.Tracks.Select(t => t.TrackId));
        }

        foreach (AudioTrackPlan track in AudioPlan.AllTracks(plan.Tracks))
        {
            if (track.Effects is not null)
            {
                used.Add(TrackKey(track.TrackId));
            }

            if (track.KeyAhead >= 0 || track.Ahead > 0)
            {
                keys.Add(track.TrackId);
            }

            foreach (AudioClipPlan clip in track.Clips)
            {
                if (clip.Effects is not null)
                {
                    used.Add(clip.ClipId);
                }
            }
        }

        foreach (string key in _chains.Keys.Where(k => !used.Contains(k)).ToList())
        {
            _chains.Remove(key);
        }

        foreach (string key in _ahead.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _ahead.Remove(key);
        }

        if (_stripMeters)
        {
            Volatile.Write(ref _strips, StripRings(plan, _strips));
        }

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
    /// <param name="PerBlock">Lo que se agota por bloque: <see cref="SlewDbPerBlock"/> en una ganancia.</param>
    private readonly record struct Slew(double Db, long From, double PerBlock = SlewDbPerBlock)
    {
        public double Offset(long sample)
        {
            if (Db == 0)
            {
                return 0;
            }

            double left = Math.Abs(Db) - (PerBlock * Math.Max(0, sample - From) / GainBlock);
            return left <= 0 ? 0 : Math.CopySign(left, Db);
        }
    }

    /// <summary>Lo que le falta al volumen (dB), al paneo y al balance de un fader para llegar al plan nuevo.</summary>
    private readonly record struct FaderSlew(Slew Db, Slew Pan, Slew Balance);

    private enum InputKind
    {
        Clip,
        Track,
        Bus,
        MixBus,
        Master,
    }

    /// <summary>De dónde sale la entrada de una cadena: el origen de un clip, los clips de una pista, un compuesto o toda la mezcla.</summary>
    private readonly record struct ChainInput(InputKind Kind, AudioClipPlan? Clip, AudioTrackPlan? Track, int Depth)
    {
        /// <summary>El bus del mezclador cuya entrada es (<see cref="InputKind.MixBus"/>).</summary>
        public MixBusPlan? Bus { get; init; }
    }

    /// <summary>
    /// La cadena de un clip, una pista o la mezcla con la muestra de entrada que espera a continuación y su estado al
    /// empezar cada una de las últimas lecturas: tras una edición la mezcla vuelve a uno de esos bordes y sigue como si no
    /// hubiera vuelto. Lleva sus propios intermedios, así las cadenas anidadas no se pisan.
    /// </summary>
    private sealed class Chain(AudioEffectChain effects)
    {
        private const int History = 64;

        private readonly long[] _at = new long[History];
        private readonly long[] _next = new long[History];
        private readonly double[][] _states = new double[History][];
        private float[] _output = [];
        private float[] _prime = [];
        private int _count;
        private int _head;

        public AudioEffectChain Effects { get; } = effects;

        /// <summary>Muestra de la línea de tiempo (de entrada) que sigue a lo último procesado; -1 si no se sabe.</summary>
        public long Next { get; set; } = -1;

        public Span<float> Output(int length) => Grow(ref _output, length);

        public Span<float> Prime(int length) => Grow(ref _prime, length);

        public void Save(long position)
        {
            int slot = (_head + _count) % History;
            if (_count == History)
            {
                _head = (_head + 1) % History;
            }
            else
            {
                _count++;
            }

            _at[slot] = position;
            _next[slot] = Next;
            int length = Effects.StateLength;
            if (_states[slot]?.Length != length)
            {
                _states[slot] = new double[length];
            }

            Effects.SaveState(_states[slot]);
        }

        public void Restore(long position)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                int slot = (_head + i) % History;
                if (_at[slot] == position && _states[slot].Length == Effects.StateLength)
                {
                    Effects.LoadState(_states[slot]);
                    Next = _next[slot];
                    _count = i;
                    return;
                }
            }

            Next = -1;
            _count = 0;
        }

        private static Span<float> Grow(ref float[] buffer, int length)
        {
            if (buffer.Length < length)
            {
                buffer = new float[length];
            }

            return buffer.AsSpan(0, length);
        }
    }

    /// <summary>
    /// Una pista que se mezcla por delante: lo que sale de su fader (y, si tiene envíos antes del fader, lo de antes) en
    /// <c>[from, to + Ahead)</c>, que suena en <c>[from, to)</c>, que los compresores que la escuchan y los buses con
    /// retraso leen adelantado. Al empezar cada lectura guarda lo que llevaba adelantado, como las cadenas su estado.
    /// </summary>
    private sealed class AheadTrack
    {
        private const int History = 64;

        private readonly long[] _at = new long[History];
        private readonly long[] _savedBase = new long[History];
        private readonly float[][] _savedPost = new float[History][];
        private readonly float[][] _savedPre = new float[History][];
        private float[] _post = [];
        private float[] _pre = [];
        private long _base;
        private int _frames = -1;
        private bool _hasPre;
        private int _count;
        private int _head;

        /// <summary>Asegura <c>[from, need)</c>: conserva lo ya mezclado desde <paramref name="from"/> y mezcla el resto.</summary>
        public void Ensure(MixRenderer mixer, AudioTrackPlan track, long from, long need, int depth)
        {
            bool pre = false;
            for (int i = 0; i < track.Sends.Count; i++)
            {
                pre |= track.Sends[i].PreFader;
            }

            if (_frames >= 0 && pre == _hasPre && _base <= from && from <= _base + _frames)
            {
                int drop = (int)(from - _base);
                int keep = (int)Math.Min(_frames - drop, need - from);
                Array.Copy(_post, drop * Channels, _post, 0, keep * Channels);
                if (pre)
                {
                    Array.Copy(_pre, drop * Channels, _pre, 0, keep * Channels);
                }

                _frames = keep;
            }
            else
            {
                _frames = 0;
            }

            _hasPre = pre;
            _base = from;
            int total = (int)(need - from);
            if (_post.Length < total * Channels)
            {
                Array.Resize(ref _post, total * Channels);
            }

            if (pre && _pre.Length < total * Channels)
            {
                Array.Resize(ref _pre, total * Channels);
            }

            if (total > _frames)
            {
                long start = from + _frames;
                Span<float> post = _post.AsSpan(_frames * Channels, (total - _frames) * Channels);
                if (pre)
                {
                    Span<float> dry = _pre.AsSpan(_frames * Channels, post.Length);
                    mixer.TrackOutput(track, start, need, dry, depth);
                    dry.CopyTo(post);
                }
                else
                {
                    mixer.TrackOutput(track, start, need, post, depth);
                }

                mixer.ApplyFader(mixer.TrackName(track.TrackId), track.Fader, start, post);
            }

            _frames = total;
        }

        /// <summary>Lo que sale del fader en <c>[from, from + frames)</c>; vacío si no lo tiene.</summary>
        public ReadOnlySpan<float> Post(long from, int frames) => Slice(_post, from, frames);

        /// <summary>Lo de antes del fader (para los envíos pre-fader); vacío si no lo tiene.</summary>
        public ReadOnlySpan<float> Pre(long from, int frames) => _hasPre ? Slice(_pre, from, frames) : default;

        private ReadOnlySpan<float> Slice(float[] data, long from, int frames) =>
            _frames >= 0 && from >= _base && from + frames <= _base + _frames
                ? data.AsSpan((int)(from - _base) * Channels, frames * Channels)
                : default;

        /// <summary>Guarda lo que hay desde <paramref name="position"/> (lo de antes no se vuelve a leer).</summary>
        public void Save(long position)
        {
            int slot = (_head + _count) % History;
            if (_count == History)
            {
                _head = (_head + 1) % History;
            }
            else
            {
                _count++;
            }

            _at[slot] = position;
            if (_frames < 0 || position < _base || position > _base + _frames)
            {
                _savedBase[slot] = long.MinValue;
                return;
            }

            _savedBase[slot] = position;
            int offset = (int)(position - _base) * Channels;
            int length = (_frames * Channels) - offset;
            Copy(_post, offset, length, ref _savedPost[slot]);
            if (_hasPre)
            {
                Copy(_pre, offset, length, ref _savedPre[slot]);
            }
            else
            {
                _savedPre[slot] = [];
            }
        }

        public void Restore(long position)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                int slot = (_head + i) % History;
                if (_at[slot] == position && _savedBase[slot] != long.MinValue)
                {
                    float[] post = _savedPost[slot];
                    if (_post.Length < post.Length)
                    {
                        Array.Resize(ref _post, post.Length);
                    }

                    post.CopyTo(_post, 0);
                    _hasPre = _savedPre[slot].Length > 0;
                    if (_hasPre)
                    {
                        if (_pre.Length < post.Length)
                        {
                            Array.Resize(ref _pre, post.Length);
                        }

                        _savedPre[slot].CopyTo(_pre, 0);
                    }

                    _base = _savedBase[slot];
                    _frames = post.Length / Channels;
                    _count = i;
                    return;
                }
            }

            _frames = -1;
            _count = 0;
        }

        private static void Copy(float[] from, int offset, int length, ref float[] into)
        {
            if (into is null || into.Length != length)
            {
                into = new float[length];
            }

            from.AsSpan(offset, length).CopyTo(into);
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

/// <summary>
/// El compresor que el panel mira: su clave (<see cref="ForClip"/>, <see cref="ForTrack"/> o <see cref="ForMix"/>) y, solo
/// para escuchar, si suena su sidechain o la señal sin comprimir (A/B). No cambia el proyecto ni la exportación.
/// </summary>
public sealed record DynamicsMonitor(string Key, bool Listen = false, bool Bypass = false)
{
    /// <summary>La inserción que se mide (por su identificador); nula, el primer compresor.</summary>
    public string? Insert { get; init; }

    public static string ForClip(string clipId) => clipId;

    public static string ForTrack(string trackId) => MixRenderer.TrackKey(trackId);

    public static string ForBus(string busId) => MixRenderer.BusKey(busId);

    public static string ForMix() => MixRenderer.MasterKey;
}
