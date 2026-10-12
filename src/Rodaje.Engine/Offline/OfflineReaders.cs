using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine.Offline;

/// <summary>
/// Los decodificadores propios de la exportación: por medio, hasta <c>maxPerSource</c>, cada uno en su hilo y con una
/// cola de cuadros exactos pedidos en orden. Los cuadros pedidos por delante se guardan en <see cref="Slot"/> hasta que
/// pasa el último instante que los usa.
/// </summary>
/// <remarks>
/// <para>
/// Cada lector tiene una capacidad (cuadros pedidos o guardados a la vez) que sale del presupuesto de VRAM al crear su
/// pool de D3D11VA: así la lectura por adelantado nunca agota las superficies del decodificador.
/// </para>
/// <para>
/// Un clip invertido tiene su propio lector, que no comparten los demás: decodifica por tramos desde el keyframe con
/// <see cref="ReverseFrameReader"/> y entrega de mayor a menor índice. Su <c>M</c> (cuadros retenidos) sale de las
/// superficies de sobra del pool (<see cref="IClipVideo.SpareSurfaces"/>) menos lo que retienen el decodificador, el margen
/// y los cuadros pedidos.
/// </para>
/// <para>
/// Hilos: <see cref="Request"/>, <see cref="Wait"/>, <see cref="ReleaseBefore"/> y <see cref="Reset"/> vienen del hilo
/// de la exportación; cada lector abre y decodifica en el suyo. Un solo candado protege el estado y nunca se retiene
/// mientras se abre, decodifica o cierra un archivo.
/// </para>
/// </remarks>
internal sealed class OfflineReaders : IDisposable
{
    /// <summary>Superficies que retiene el propio decodificador además de sus referencias (cola de salida, reordenación).</summary>
    internal const int DecoderHeldSurfaces = 5;

    /// <summary>Margen para lo que retienen la composición en curso y el compositor.</summary>
    internal const int SurfaceMargin = 3;

    /// <summary>Hasta cuántos cuadros por delante de un lector se pide sin preferir otro (el decodificador avanza sin seek).</summary>
    internal const int ForwardReach = 64;

    /// <summary>El cuadro que el lector en reversa sacó de más y guarda para la próxima petición.</summary>
    internal const int ReverseHeldSurfaces = 1;

    /// <summary>Menor <c>M</c> que admite <see cref="ReverseFrameReader"/> con su ventana de dos tramos.</summary>
    internal const int MinReverseRetained = 2;

    /// <summary><c>M</c> sin pool de D3D11VA (cuadros en la memoria del sistema).</summary>
    internal const int SoftwareReverseRetained = 32;

    private readonly object _gate = new();
    private readonly IClipBackend _backend;
    private readonly VideoDecoderOptions _decoderOptions;
    private readonly int _maxPerSource;
    private readonly int _lookahead;
    private readonly long _vramBudget;
    private readonly Dictionary<SourceKey, Source> _sources = [];
    private readonly List<Task> _closing = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly Stack<Slot> _free = new();
    private readonly List<Slot> _gone = [];
    private readonly List<DecodedFrame> _released = [];
    private readonly List<Reader> _closingNow = [];
    private long _stamp;
    private long _reserved;
    private bool _disposed;

    public OfflineReaders(IClipBackend backend, VideoDecoderOptions decoderOptions, int maxPerSource, int lookahead, long vramBudget)
    {
        _backend = backend;
        _decoderOptions = decoderOptions with { HardwarePoolSizer = null, SkipNonReference = false, Keyframes = null };
        _maxPerSource = maxPerSource;
        _lookahead = lookahead;
        _vramBudget = vramBudget;
    }

    /// <summary>VRAM comprometida por los pools de los decodificadores abiertos.</summary>
    public long ReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _reserved;
            }
        }
    }

    /// <summary>Pico de <see cref="ReservedBytes"/>.</summary>
    public long PeakReservedBytes { get; private set; }

    /// <summary>Decodificadores abiertos ahora.</summary>
    public int OpenReaders
    {
        get
        {
            lock (_gate)
            {
                return _sources.Values.Sum(s => s.Readers.Count + s.Reversed.Count);
            }
        }
    }

    /// <summary>Decodificadores abiertos en total, para las pruebas.</summary>
    public int ReadersCreated { get; private set; }

    /// <summary>Cuadros guardados o pedidos.</summary>
    public int Slots
    {
        get
        {
            lock (_gate)
            {
                return _sources.Values.Sum(s => s.Slots.Count);
            }
        }
    }

    /// <summary>Lectores cuyo pool no cabía en el presupuesto y se abrieron con la capacidad mínima.</summary>
    public int VramOverruns { get; private set; }

    /// <summary>
    /// Pide el cuadro <paramref name="index"/> de <paramref name="key"/>, que se usará hasta <paramref name="lastUse"/>.
    /// Con <paramref name="mustHave"/> falso (lectura por adelantado) no se pide si el lector elegido está lleno.
    /// </summary>
    /// <param name="clip">El clip que lo muestra; si va en reversa, lo decodifica su lector hacia atrás.</param>
    /// <returns>El hueco del cuadro; nulo si no había lugar.</returns>
    public Slot? Request(SourceKey key, long index, Time lastUse, bool mustHave, VideoClipPlan? clip = null)
    {
        Reader? post = null;
        Slot? slot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sources.TryGetValue(key, out Source? source))
            {
                source = new Source(key);
                _sources.Add(key, source);
            }

            if (source.Slots.TryGetValue(index, out slot))
            {
                slot.LastUse = Time.Max(slot.LastUse, lastUse);
                return slot;
            }

            Reader reader = clip is { Forward: false } reversed ? ChooseReverse(source, reversed) : Choose(source, index);
            if (!mustHave && reader.Assigned >= reader.Capacity)
            {
                return null;
            }

            slot = _free.Count > 0 ? _free.Pop() : new Slot();
            slot.Reuse(source, reader, index, lastUse);
            source.Slots.Add(index, slot);
            reader.Assigned++;
            reader.LastIndex = index;
            reader.Stamp = ++_stamp;
            reader.LastUse = Time.Max(reader.LastUse, lastUse);
            reader.Queue.Enqueue(slot);
            if (!reader.PumpPosted)
            {
                reader.PumpPosted = true;
                post = reader;
            }
        }

        post?.Executor.Post(post.Pump);
        return slot;
    }

    /// <summary>Hay lugar para pedir <paramref name="index"/> por delante sin pasar la capacidad del lector que lo tomaría.</summary>
    public bool HasRoom(SourceKey key, long index, VideoClipPlan? clip = null)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(key, out Source? source) || source.Slots.ContainsKey(index))
            {
                return true;
            }

            Reader? reader = clip is { Forward: false } ? source.Reversed.GetValueOrDefault(clip.ClipId) : Peek(source, index);
            return reader is null || reader.Assigned < reader.Capacity;
        }
    }

    /// <summary>Espera a que el cuadro esté decodificado y devuelve una referencia propia (que desecha quien la recibe).</summary>
    /// <exception cref="ExportException">El medio no abre (<see cref="ExportErrorCode.MediaMissing"/>) o el cuadro no se decodifica.</exception>
    public DecodedFrame Wait(Slot slot, TimeSpan timeout, long frame, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (!slot.Done)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                long left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    throw new ExportException(ExportErrorCode.DecodeFailed,
                        $"El cuadro {slot.Index} de «{Path.GetFileName(slot.Source.Key.Path)}» no llegó en {timeout.TotalSeconds:0} s.")
                    {
                        Frame = frame,
                        Culprit = slot.Source.Key.Path,
                    };
                }

                Monitor.Wait(_gate, (int)Math.Min(left, 50));
            }

            if (slot.Frame is { } decoded && decoded.TryRetain())
            {
                return decoded;
            }

            string name = Path.GetFileName(slot.Source.Key.Path);
            if (slot.Source.Failure is { } failure)
            {
                throw new ExportException(ExportErrorCode.MediaMissing, $"No se pudo abrir «{name}»: {failure.Message}", failure)
                {
                    Frame = frame,
                    Culprit = slot.Source.Key.Path,
                };
            }

            throw new ExportException(ExportErrorCode.DecodeFailed,
                $"El cuadro {slot.Index} de «{name}» no se pudo decodificar{(slot.Error is { } e ? ": " + e.Message : ".")}", slot.Error)
            {
                Frame = frame,
                Culprit = slot.Source.Key.Path,
            };
        }
    }

    /// <summary>Suelta los cuadros que nadie usará desde <paramref name="time"/> y cierra los lectores sin trabajo desde hace <paramref name="idle"/>.</summary>
    public void ReleaseBefore(Time time, Time idle)
    {
        List<DecodedFrame> frames = _released;
        List<Reader> close = _closingNow;
        lock (_gate)
        {
            foreach (Source source in _sources.Values)
            {
                _gone.Clear();
                foreach (Slot slot in source.Slots.Values)
                {
                    if (slot.LastUse < time)
                    {
                        _gone.Add(slot);
                    }
                }

                foreach (Slot slot in _gone)
                {
                    source.Slots.Remove(slot.Index);
                    Drop(slot, frames);
                }

                for (int i = source.Readers.Count - 1; i >= 0; i--)
                {
                    Reader reader = source.Readers[i];
                    if (reader.Assigned == 0 && reader.LastUse + idle < time)
                    {
                        close.Add(reader);
                        source.Readers.RemoveAt(i);
                    }
                }

                foreach (Reader reader in source.Reversed.Values)
                {
                    if (reader.Assigned == 0 && reader.LastUse + idle < time)
                    {
                        close.Add(reader);
                    }
                }

                foreach (Reader reader in close)
                {
                    if (reader.Reverse is { } clip && reader.Source == source)
                    {
                        source.Reversed.Remove(clip.ClipId);
                    }
                }
            }
        }

        foreach (DecodedFrame frame in frames)
        {
            frame.Dispose();
        }

        foreach (Reader reader in close)
        {
            Close(reader);
        }

        frames.Clear();
        close.Clear();
    }

    /// <summary>Suelta todo lo pedido (un salto hacia atrás); los decodificadores siguen abiertos.</summary>
    public void Reset()
    {
        List<DecodedFrame> frames = [];
        lock (_gate)
        {
            foreach (Source source in _sources.Values)
            {
                foreach (Slot slot in source.Slots.Values)
                {
                    Drop(slot, frames);
                }

                source.Slots.Clear();
                foreach (Reader reader in source.Readers.Concat(source.Reversed.Values))
                {
                    reader.Queue.Clear();
                    reader.LastUse = Time.Zero;
                }
            }
        }

        frames.ForEach(f => f.Dispose());
    }

    public void Dispose()
    {
        List<DecodedFrame> frames = [];
        List<Reader> readers = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Source source in _sources.Values)
            {
                foreach (Slot slot in source.Slots.Values)
                {
                    Drop(slot, frames);
                }

                source.Slots.Clear();
                readers.AddRange(source.Readers);
                readers.AddRange(source.Reversed.Values);
                source.Readers.Clear();
                source.Reversed.Clear();
            }

            Monitor.PulseAll(_gate);
        }

        _cancel.Cancel();
        frames.ForEach(f => f.Dispose());
        readers.ForEach(Close);
        Task[] closing;
        lock (_gate)
        {
            closing = [.. _closing];
        }

        Task.WaitAll(closing);
        _cancel.Dispose();
    }

    /// <summary>Suelta el hueco; si su decodificador ya terminó con él, vuelve a la reserva para reutilizarse.</summary>
    private void Drop(Slot slot, List<DecodedFrame> frames)
    {
        if (slot.Released)
        {
            return;
        }

        slot.Released = true;
        slot.Reader.Assigned--;
        if (slot.Frame is { } frame)
        {
            frames.Add(frame);
            slot.Frame = null;
        }

        if (slot.Done)
        {
            _free.Push(slot);
        }
    }

    /// <summary>
    /// El lector que tomaría el cuadro: el que lo alcanza avanzando menos, luego uno que tenga que volver poco (reversa),
    /// luego uno nuevo si quedan, y si no el usado hace más tiempo.
    /// </summary>
    private Reader Choose(Source source, long index)
    {
        if (Peek(source, index) is { } reader)
        {
            return reader;
        }

        reader = new Reader(this, source, _backend.CreateExecutor(), _lookahead);
        source.Readers.Add(reader);
        ReadersCreated++;
        return reader;
    }

    /// <summary>El lector propio del clip invertido; no lo comparte con lo que va hacia adelante ni con otro clip.</summary>
    private Reader ChooseReverse(Source source, VideoClipPlan clip)
    {
        if (!source.Reversed.TryGetValue(clip.ClipId, out Reader? reader))
        {
            reader = new Reader(this, source, _backend.CreateExecutor(), _lookahead) { Reverse = clip };
            source.Reversed.Add(clip.ClipId, reader);
            ReadersCreated++;
        }

        return reader;
    }

    private Reader? Peek(Source source, long index)
    {
        Reader? forward = null;
        Reader? backward = null;
        Reader? fresh = null;
        Reader? oldest = null;
        foreach (Reader reader in source.Readers)
        {
            if (reader.LastIndex < 0)
            {
                fresh ??= reader;
            }
            else if (index > reader.LastIndex && index - reader.LastIndex <= ForwardReach)
            {
                forward = forward is null || reader.LastIndex > forward.LastIndex ? reader : forward;
            }
            else if (index < reader.LastIndex && reader.LastIndex - index <= ForwardReach)
            {
                backward = backward is null || reader.LastIndex < backward.LastIndex ? reader : backward;
            }

            oldest = oldest is null || reader.Stamp < oldest.Stamp ? reader : oldest;
        }

        return forward ?? backward ?? fresh ?? (source.Readers.Count < _maxPerSource ? null : oldest);
    }

    private void Close(Reader reader)
    {
        lock (_gate)
        {
            reader.Closed = true;
            reader.Queue.Clear();
            _reserved -= reader.Reserved;
            reader.Reserved = 0;
        }

        // El decodificador se libera en su hilo, después de lo que estuviera haciendo; quien exporta no espera.
        Task task = Task.Run(() =>
        {
            reader.Executor.Post(() =>
            {
                IClipVideo? video;
                lock (_gate)
                {
                    video = reader.Video;
                    reader.Video = null;
                }

                StopBackward(reader);
                video?.Dispose();
            });
            (reader.Executor as IDisposable)?.Dispose();
        });
        lock (_gate)
        {
            _closing.RemoveAll(t => t.IsCompleted);
            _closing.Add(task);
        }
    }

    /// <summary>En el hilo del lector: abre el archivo si hace falta y decodifica su cola en orden.</summary>
    private void Pump(Reader reader)
    {
        while (true)
        {
            Slot? slot;
            IClipVideo? video;
            bool open;
            lock (_gate)
            {
                if (_disposed || reader.Closed || reader.Queue.Count == 0)
                {
                    reader.PumpPosted = false;
                    return;
                }

                slot = reader.Queue.Dequeue();
                if (slot.Released)
                {
                    continue;
                }

                video = reader.Video;
                open = video is null && reader.Source.Failure is null;
            }

            Exception? error = null;
            if (open)
            {
                try
                {
                    video = _backend.OpenVideo(reader.Source.Key.Path, _decoderOptions with
                    {
                        HardwarePoolSizer = request => SizePool(reader, request),
                    });
                }
#pragma warning disable CA1031 // El fallo de un medio se entrega a quien espera el cuadro como ExportException.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    error = ex;
                }

                bool keep;
                lock (_gate)
                {
                    if (error is not null)
                    {
                        reader.Source.Failure ??= error;
                    }

                    keep = error is null && !reader.Closed && !_disposed;
                    if (keep)
                    {
                        reader.Video = video;
                        reader.Source.Stream ??= video!.Stream;
                    }
                }

                if (error is null && !keep)
                {
                    // Se cerró mientras abría: nadie más lo va a liberar.
                    video!.Dispose();
                    video = null;
                }
            }

            DecodedFrame? frame = null;
            if (video is null)
            {
                error ??= reader.Source.Failure;
            }
            else
            {
                try
                {
                    frame = reader.Reverse is { } clip ? Backward(reader, video, clip, slot.Index) : Exact(video, slot.Index);
                    if (frame is null)
                    {
                        error = new InvalidOperationException("El medio no tiene ese cuadro.");
                    }
                    else if (frame.Index != slot.Index)
                    {
                        error = new InvalidOperationException($"El decodificador devolvió el cuadro {frame.Index} cuando se pidió el {slot.Index}.");
                        frame.Dispose();
                        frame = null;
                    }
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // Ídem: el error viaja al hilo de la exportación.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    error = ex;
                }
            }

            bool discard;
            lock (_gate)
            {
                discard = slot.Released || _disposed;
                if (!discard)
                {
                    slot.Frame = frame;
                    slot.Error = error;
                }

                slot.Done = true;
                Monitor.PulseAll(_gate);
            }

            if (discard)
            {
                frame?.Dispose();
            }
        }
    }

    private DecodedFrame? Exact(IClipVideo video, long index)
    {
        ValueTask<DecodedFrame?> pending = video.GetFrameAsync(index, DecodeAccuracy.Exact, _cancel.Token);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// En el hilo del lector en reversa: el cuadro <paramref name="index"/> desde su <see cref="ReverseFrameReader"/>, que
    /// se rehace desde <paramref name="index"/> si se pide algo por encima de lo que todavía puede entregar (la primera
    /// vez o tras un salto). Lo que no llega por ahí (un instante fuera de la rejilla de la secuencia) sale exacto.
    /// </summary>
    private DecodedFrame? Backward(Reader reader, IClipVideo video, VideoClipPlan clip, long index)
    {
        if (reader.Backward is null || index > reader.Top)
        {
            StopBackward(reader);
            DecodedFrame? first = null;
            if (!reader.Primed)
            {
                // El pool de D3D11VA nace con el primer cuadro: este, exacto, y con él se sabe cuántas superficies sobran.
                reader.Primed = true;
                first = Exact(video, index);
                if (first is null)
                {
                    return null;
                }
            }

            long top = first is null ? index : index - 1;
            reader.Top = top;
            if (top >= clip.LowestFrame)
            {
                IKeyframeIndex? keyframes = KeyframesOf(reader.Source, video);
                reader.Backward = new ReverseFrameReader(video, k => keyframes?.AtOrBefore(k) is >= 0 and var at ? at : null,
                    clip.LowestFrame, top, new ReverseFrameReaderOptions
                    {
                        MaxRetained = ReverseRetained(reader, video),
                        Wanted = reader.EveryFrame ? null : clip.IsShown,
                    });
            }

            if (first is not null)
            {
                return first;
            }
        }

        while ((reader.Held ?? reader.Backward?.Next(_cancel.Token)) is { } frame)
        {
            reader.Held = null;
            if (frame.Index > index)
            {
                frame.Dispose();
                reader.Top = frame.Index - 1;
                continue;
            }

            if (frame.Index == index)
            {
                reader.Top = index - 1;
                return frame;
            }

            reader.Held = frame;
            reader.Top = frame.Index;
            break;
        }

        // El plan no lo traía: exacto, y desde ahora el lector hacia atrás entrega todos los cuadros.
        StopBackward(reader);
        reader.EveryFrame = true;
        return Exact(video, index);
    }

    /// <summary><c>M</c>: las superficies de sobra del pool menos lo que retienen el decodificador, el margen y los cuadros pedidos.</summary>
    private int ReverseRetained(Reader reader, IClipVideo video)
    {
        if (video.SpareSurfaces is not { } spare)
        {
            return SoftwareReverseRetained;
        }

        int capacity;
        lock (_gate)
        {
            capacity = reader.Capacity;
        }

        return Math.Max(MinReverseRetained, spare - DecoderHeldSurfaces - SurfaceMargin - ReverseHeldSurfaces - capacity);
    }

    /// <summary>Keyframes del archivo, leídos una vez por origen; nulo si no se conocen (cada tramo busca su keyframe solo).</summary>
    private IKeyframeIndex? KeyframesOf(Source source, IClipVideo video)
    {
        lock (_gate)
        {
            if (source.KeyframesRead)
            {
                return source.Keyframes;
            }
        }

        IKeyframeIndex? read = (video as IKeyframeSource)?.Keyframes ?? _backend.ReadKeyframes(source.Key.Path);
        lock (_gate)
        {
            if (!source.KeyframesRead)
            {
                (source.Keyframes, source.KeyframesRead) = (read, true);
            }

            return source.Keyframes;
        }
    }

    /// <summary>En el hilo del lector (o al cerrarlo, también en él): suelta el lector hacia atrás y el cuadro que guardaba.</summary>
    private static void StopBackward(Reader reader)
    {
        reader.Backward?.Dispose();
        reader.Backward = null;
        reader.Held?.Dispose();
        reader.Held = null;
    }

    /// <summary>
    /// En el hilo del lector, al crear el pool de D3D11VA: reserva las referencias, lo que retiene el decodificador, el
    /// margen y la lectura por adelantado que quepa en el presupuesto (como mínimo 2 cuadros). Un lector en reversa
    /// reserva además su <c>M</c>, hasta llenar las 64 superficies del pool.
    /// </summary>
    private int SizePool(Reader reader, HardwarePoolRequest request)
    {
        long surface = Math.Max(1, request.SurfaceBytes);
        int fixedSurfaces = request.ReferenceSurfaces + DecoderHeldSurfaces + SurfaceMargin;
        if (reader.Reverse is not null)
        {
            return SizeReversePool(reader, request.ReferenceSurfaces, surface, fixedSurfaces + ReverseHeldSurfaces);
        }

        lock (_gate)
        {
            long free = _vramBudget - _reserved;
            int fits = (int)Math.Clamp((free / surface) - fixedSurfaces, int.MinValue / 2, int.MaxValue / 2);
            int capacity = Math.Clamp(fits, 2, _lookahead);
            if (fits < 2)
            {
                VramOverruns++;
            }

            reader.Capacity = capacity;
            reader.Reserved = (fixedSurfaces + capacity) * surface;
            if (!reader.Closed)
            {
                _reserved += reader.Reserved;
                PeakReservedBytes = Math.Max(PeakReservedBytes, _reserved);
            }

            return DecoderHeldSurfaces + SurfaceMargin + capacity;
        }
    }

    /// <summary>
    /// Como <see cref="SizePool"/> para un lector en reversa: lo que quepa en el presupuesto, hasta las 64 superficies del
    /// pool, se reparte entre los cuadros pedidos (hasta la lectura por adelantado) y el <c>M</c> del lector hacia atrás.
    /// </summary>
    private int SizeReversePool(Reader reader, int references, long surface, int fixedSurfaces)
    {
        lock (_gate)
        {
            long free = _vramBudget - _reserved;
            int fits = (int)Math.Clamp((free / surface) - fixedSurfaces, int.MinValue / 2, int.MaxValue / 2);
            int minimum = 2 + MinReverseRetained;
            if (fits < minimum)
            {
                VramOverruns++;
            }

            int room = Math.Max(minimum, Math.Min(fits, VideoDecoder.MaxHardwarePoolSurfaces - fixedSurfaces));
            reader.Capacity = Math.Clamp(room - (4 * MinReverseRetained), 2, _lookahead);
            reader.Reserved = (fixedSurfaces + room) * surface;
            if (!reader.Closed)
            {
                _reserved += reader.Reserved;
                PeakReservedBytes = Math.Max(PeakReservedBytes, _reserved);
            }

            return fixedSurfaces - references + room;
        }
    }

    internal sealed class Source(SourceKey key)
    {
        public SourceKey Key { get; } = key;

        public List<Reader> Readers { get; } = [];

        public Dictionary<long, Slot> Slots { get; } = [];

        /// <summary>Flujo de video según el primer decodificador que abrió: forma del píxel y giro.</summary>
        public Rodaje.Media.Probe.VideoStreamInfo? Stream { get; set; }

        /// <summary>El archivo no abrió: todo cuadro suyo falla con esto.</summary>
        public Exception? Failure { get; set; }

        /// <summary>Lectores de los clips invertidos, por clip.</summary>
        public Dictionary<string, Reader> Reversed { get; } = [];

        /// <summary>Keyframes del archivo, leídos por el primer lector en reversa; nulo si no se conocen.</summary>
        public IKeyframeIndex? Keyframes { get; set; }

        public bool KeyframesRead { get; set; }
    }

    internal sealed class Reader
    {
        public Reader(OfflineReaders owner, Source source, IDecodeExecutor executor, int capacity)
        {
            Source = source;
            Executor = executor;
            Capacity = capacity;
            Pump = () => owner.Pump(this);
        }

        public Source Source { get; }

        public IDecodeExecutor Executor { get; }

        public Action Pump { get; }

        public IClipVideo? Video { get; set; }

        public Queue<Slot> Queue { get; } = new();

        public bool PumpPosted { get; set; }

        public bool Closed { get; set; }

        /// <summary>Último cuadro asignado: de ahí sigue sin seek si el próximo está cerca y adelante.</summary>
        public long LastIndex { get; set; } = -1;

        /// <summary>Orden de la última asignación, para elegir el usado hace más tiempo.</summary>
        public long Stamp { get; set; }

        /// <summary>Último instante de la secuencia que usa un cuadro suyo.</summary>
        public Time LastUse { get; set; }

        /// <summary>Cuadros pedidos o guardados que todavía no se soltaron.</summary>
        public int Assigned { get; set; }

        public int Capacity { get; set; }

        public long Reserved { get; set; }

        /// <summary>El clip invertido al que sirve este lector; nulo hacia adelante.</summary>
        public VideoClipPlan? Reverse { get; init; }

        // Lo que sigue es del lector en reversa y solo se toca en su hilo (también al cerrarlo).
        public ReverseFrameReader? Backward { get; set; }

        /// <summary>El mayor cuadro que <see cref="Backward"/> todavía puede entregar.</summary>
        public long Top { get; set; } = -1;

        /// <summary>Un cuadro que salió por debajo del pedido y se guarda para la próxima petición.</summary>
        public DecodedFrame? Held { get; set; }

        public bool Primed { get; set; }

        /// <summary>Se pidió un cuadro que el plan no esperaba: el lector hacia atrás entrega todos.</summary>
        public bool EveryFrame { get; set; }
    }

    /// <summary>Un cuadro pedido; se reutiliza cuando se suelta y su decodificador ya no lo tiene en la cola.</summary>
    internal sealed class Slot
    {
        public Source Source { get; private set; } = null!;

        public Reader Reader { get; private set; } = null!;

        public long Index { get; private set; }

        public Time LastUse { get; set; }

        public DecodedFrame? Frame { get; set; }

        public Exception? Error { get; set; }

        public bool Done { get; set; }

        public bool Released { get; set; }

        public void Reuse(Source source, Reader reader, long index, Time lastUse)
        {
            (Source, Reader, Index, LastUse) = (source, reader, index, lastUse);
            (Frame, Error, Done, Released) = (null, null, false, false);
        }
    }
}
