namespace Rodaje.Audio.Processing;

/// <summary>
/// La cadena de inserciones de un clip, una pista, un bus o la mezcla, sobre estéreo intercalado y en el orden de la
/// pila. El mismo código suena en la vista previa y se escribe en la exportación: la misma entrada con el mismo estado da
/// la misma salida muestra a muestra.
/// </summary>
/// <remarks>
/// <para>
/// La salida va <see cref="Latency"/> muestras por detrás de la entrada (la suma de las etapas): quien la usa le da la
/// entrada adelantada eso mismo. El sidechain llega alineado con la entrada y la cadena lo retrasa lo que retrasan las
/// etapas anteriores al primer compresor, que es quien lo escucha.
/// </para>
/// <para>
/// <see cref="Configure"/> cambia los ajustes sin tocar el estado; si cambia la lista, cada etapa que sigue (mismo
/// identificador y tipo) conserva el suyo, así que reordenar no corta las colas de una reverb.
/// </para>
/// <para>
/// <see cref="Bypass"/>, <see cref="ListenSidechain"/> y <see cref="BypassedInserts"/> son de la escucha: no cambian el
/// estado (la etapa sigue procesando por debajo) y salen con el mismo retraso, así se cambian sin saltos.
/// </para>
/// <para>No reserva memoria al procesar mientras no cambien la lista ni el tamaño del bloque.</para>
/// </remarks>
public sealed class AudioEffectChain
{
    private const int Channels = 2;

    private readonly double _sampleRate;
    private Slot[] _slots = [];
    private int _compressor = -1;
    private int _keyDelay;
    private double[] _keyLine = [];
    private int _keyPosition;
    private float[] _keyDelayed = [];
    private IReadOnlySet<string>? _bypassed;

    public AudioEffectChain(AudioEffectsSettings settings, double sampleRate = 48_000)
    {
        _sampleRate = sampleRate;
        Configure(settings);
    }

    public AudioEffectsSettings Settings { get; private set; } = null!;

    /// <summary>Muestras que la salida va por detrás de la entrada.</summary>
    public int Latency { get; private set; }

    /// <summary>Comparar A/B del primer compresor: sale su entrada tal cual (con el mismo retraso) y él sigue por debajo.</summary>
    public bool Bypass { get; set; }

    /// <summary>Escuchar el sidechain del primer compresor: sale lo que oye su detector, con el mismo retraso.</summary>
    public bool ListenSidechain { get; set; }

    /// <summary>Inserciones que se escuchan sin efecto (A/B de la vista previa), por su identificador; nula, ninguna.</summary>
    public IReadOnlySet<string>? BypassedInserts
    {
        get => _bypassed;
        set
        {
            _bypassed = value;
            foreach (Slot slot in _slots)
            {
                slot.Bypassed = value is not null && value.Contains(slot.Id);
            }
        }
    }

    /// <summary>La inserción cuya lectura da <see cref="Reading"/>; nula, el primer compresor.</summary>
    public string? WatchedInsert { get; set; }

    /// <summary>Lo que hizo en la última lectura la inserción que se mira (<see cref="WatchedInsert"/>).</summary>
    public DynamicsReading Reading
    {
        get
        {
            foreach (Slot slot in _slots)
            {
                if (WatchedInsert is null ? slot.Stage is EqualizerCompressorStage { Compressor: not null } : slot.Id == WatchedInsert)
                {
                    return slot.Stage.Reading;
                }
            }

            return default;
        }
    }

    /// <summary>Tamaño de lo que se guarda con <see cref="SaveState"/>; cambia si cambia la lista.</summary>
    public int StateLength { get; private set; }

    public void SaveState(Span<double> into)
    {
        int at = 0;
        foreach (Slot slot in _slots)
        {
            slot.Stage.SaveState(into.Slice(at, slot.Stage.StateLength));
            at += slot.Stage.StateLength;
        }

        _keyLine.CopyTo(into[at..]);
        into[at + _keyLine.Length] = _keyPosition;
    }

    public void LoadState(ReadOnlySpan<double> from)
    {
        int at = 0;
        foreach (Slot slot in _slots)
        {
            slot.Stage.LoadState(from.Slice(at, slot.Stage.StateLength));
            at += slot.Stage.StateLength;
        }

        from.Slice(at, _keyLine.Length).CopyTo(_keyLine);
        _keyPosition = (int)from[at + _keyLine.Length];
    }

    public void Configure(AudioEffectsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Equals(Settings))
        {
            return;
        }

        IReadOnlyList<AudioInsert> inserts = settings.Inserts;
        bool same = inserts.Count == _slots.Length;
        for (int i = 0; same && i < inserts.Count; i++)
        {
            same = inserts[i].Id == _slots[i].Id && inserts[i].Settings.GetType() == _slots[i].Type;
        }

        if (same)
        {
            for (int i = 0; i < inserts.Count; i++)
            {
                _slots[i].Stage.Configure(inserts[i].Settings.Limited());
            }
        }
        else
        {
            var slots = new Slot[inserts.Count];
            for (int i = 0; i < inserts.Count; i++)
            {
                AudioInsert insert = inserts[i];
                Slot? kept = Array.Find(_slots, s => s.Id == insert.Id && s.Type == insert.Settings.GetType());
                if (kept is not null && !Array.Exists(slots, s => ReferenceEquals(s, kept)))
                {
                    kept.Stage.Configure(insert.Settings.Limited());
                    slots[i] = kept;
                }
                else
                {
                    IAudioStage stage = insert.Settings.Limited().CreateStage(_sampleRate);
                    stage.Reset();
                    slots[i] = new Slot(insert.Id, insert.Settings.GetType(), stage);
                }
            }

            _slots = slots;
            BypassedInserts = _bypassed;
        }

        Settings = settings;
        _compressor = Array.FindIndex(_slots, s => s.Stage is EqualizerCompressorStage { Compressor: not null });
        int keyDelay = 0;
        int latency = 0;
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Prepare();
            if (i < _compressor)
            {
                keyDelay += _slots[i].Stage.Latency;
            }

            latency += _slots[i].Stage.Latency;
        }

        Latency = latency;
        if (keyDelay != _keyDelay)
        {
            _keyDelay = keyDelay;
            _keyLine = new double[keyDelay * Channels];
            _keyPosition = 0;
        }

        int length = 0;
        foreach (Slot slot in _slots)
        {
            length += slot.Stage.StateLength;
        }

        StateLength = length + _keyLine.Length + 1;
    }

    /// <summary>
    /// Umbral, relación y mezcla del primer compresor para el tramo que sigue (sus keyframes), acotados a sus rangos; una
    /// pendiente que no es un número no mueve la mezcla.
    /// </summary>
    public void Automate(double thresholdDb, double ratio, double mix, double mixSlope)
    {
        if (_compressor >= 0)
        {
            ((EqualizerCompressorStage)_slots[_compressor].Stage).Automate(CompressorSettings.LimitThreshold(thresholdDb),
                CompressorSettings.LimitRatio(ratio), CompressorSettings.LimitMix(mix), double.IsFinite(mixSlope) ? mixSlope : 0);
        }
    }

    public void ClearAutomation()
    {
        if (_compressor >= 0)
        {
            ((EqualizerCompressorStage)_slots[_compressor].Stage).ClearAutomation();
        }
    }

    public void Reset()
    {
        foreach (Slot slot in _slots)
        {
            slot.Stage.Reset();
            slot.ClearDry();
        }

        Array.Clear(_keyLine);
        _keyPosition = 0;
    }

    /// <summary>
    /// Procesa en el sitio; <paramref name="key"/> es el sidechain externo alineado con la entrada (vacío: el propio audio).
    /// Las muestras que no son números entran como silencio (<see cref="AudioSamples.Sanitize(Span{float})"/>), así no quedan en el
    /// estado de los filtros y lo que sigue suena.
    /// </summary>
    public void Process(Span<float> interleaved, ReadOnlySpan<float> key = default)
    {
        if (_slots.Length == 0)
        {
            return;
        }

        AudioSamples.Sanitize(interleaved);
        if (_compressor >= 0)
        {
            var compressor = (EqualizerCompressorStage)_slots[_compressor].Stage;
            compressor.Bypass = Bypass;
            compressor.ListenSidechain = ListenSidechain;
        }

        ReadOnlySpan<float> delayedKey = DelayKey(key, interleaved.Length);
        for (int i = 0; i < _slots.Length; i++)
        {
            Slot slot = _slots[i];
            ReadOnlySpan<float> k = i == _compressor ? delayedKey : default;
            if (slot.Bypassed)
            {
                slot.ProcessBypassed(interleaved, k);
            }
            else
            {
                slot.Stage.Process(interleaved, k);
            }
        }
    }

    private ReadOnlySpan<float> DelayKey(ReadOnlySpan<float> key, int length)
    {
        if (_compressor < 0 || key.Length < length || length == 0)
        {
            return default;
        }

        if (_keyDelayed.Length < length)
        {
            _keyDelayed = new float[length];
        }

        if (_keyDelay == 0)
        {
            Span<float> copy = _keyDelayed.AsSpan(0, length);
            key[..length].CopyTo(copy);
            AudioSamples.Sanitize(copy);
            return copy;
        }

        int line = _keyLine.Length;
        int position = _keyPosition;
        for (int i = 0; i < length; i++)
        {
            int at = (position * Channels) + (i & 1);
            _keyDelayed[i] = (float)_keyLine[at];
            _keyLine[at] = AudioSamples.Sanitize(key[i]);
            if ((i & 1) == 1 && ++position * Channels == line)
            {
                position = 0;
            }
        }

        _keyPosition = position;
        return _keyDelayed.AsSpan(0, length);
    }

    /// <summary>Una inserción con su etapa y, para el A/B, la entrada retrasada lo que la etapa retrasa.</summary>
    private sealed class Slot(string id, Type type, IAudioStage stage)
    {
        private float[] _dry = [];
        private float[] _copy = [];
        private int _dryPosition;

        public string Id { get; } = id;

        public Type Type { get; } = type;

        public IAudioStage Stage { get; } = stage;

        public bool Bypassed { get; set; }

        public void Prepare()
        {
            int length = Stage.Latency * Channels;
            if (_dry.Length != length)
            {
                _dry = new float[length];
                _dryPosition = 0;
            }
        }

        public void ClearDry()
        {
            Array.Clear(_dry);
            _dryPosition = 0;
        }

        /// <summary>La etapa procesa una copia (su estado sigue igual que sin A/B) y sale la entrada con su retraso.</summary>
        public void ProcessBypassed(Span<float> io, ReadOnlySpan<float> key)
        {
            if (_copy.Length < io.Length)
            {
                _copy = new float[io.Length];
            }

            Span<float> copy = _copy.AsSpan(0, io.Length);
            io.CopyTo(copy);
            Stage.Process(copy, key);
            if (_dry.Length == 0)
            {
                return;
            }

            for (int i = 0; i < io.Length; i++)
            {
                (io[i], _dry[_dryPosition]) = (_dry[_dryPosition], io[i]);
                if (++_dryPosition == _dry.Length)
                {
                    _dryPosition = 0;
                }
            }
        }
    }
}
