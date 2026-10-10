using Rodaje.Audio;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Reproduce una secuencia completa: varias pistas de video como capas y la mezcla de las pistas de audio.</summary>
/// <remarks>
/// <para>
/// El reloj maestro es el del dispositivo de audio, que suena la mezcla (silencio si no hay clips de audio); sin
/// dispositivo o con el audio desactivado, el del sistema. El transporte se mueve a la tasa de la secuencia.
/// </para>
/// <para>
/// Hilos: transporte, <see cref="Tick"/>, <see cref="Reload"/> y <see cref="Dispose"/> se llaman desde un mismo hilo
/// (el de la ventana). Cada decodificador de video trabaja en su propio hilo (puede haber varios por medio, dentro de
/// un presupuesto de VRAM); la mezcla de audio, en otro. Ninguna de esas llamadas espera a un decodificador, salvo
/// <see cref="Dispose"/>, que los cierra todos antes de volver. La composición que entrega <see cref="Tick"/> es de
/// quien la recibe: debe desecharla cuando termine de dibujarla.
/// </para>
/// </remarks>
public sealed class TimelineSession : IDisposable
{
    private readonly string _projectDirectory;
    private readonly AudioMixer? _mixer;
    private readonly IAudioOutput? _output;
    private readonly TimelineScheduler _scheduler;
    private readonly string _audioStatus;
    private Project _project;
    private bool _disposed;

    internal TimelineSession(Project project, string sequenceId, GpuDevice? device, TimelineSessionOptions options, IClipBackend backend)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        options.Validate();
        _project = project;
        SequenceId = sequenceId;
        _projectDirectory = options.ProjectDirectory ?? Environment.CurrentDirectory;
        TimelinePlan plan = TimelinePlan.Build(project, sequenceId, _projectDirectory);

        var created = new Stack<IDisposable>();
        try
        {
            IAudioClock clock;
            if (options.EnableAudio)
            {
                _mixer = new AudioMixer(plan.Audio, backend.OpenAudio, options.AudioReadAhead, options.DecoderIdleTimeout, options.TimeProvider);
                created.Push(_mixer);
                _output = backend.OpenOutput(_mixer, (options.AudioOutput ?? new AudioOutputOptions()) with { SampleRate = TimelinePlan.AudioSampleRate });
                created.Push(_output);
                clock = _output.Clock;
                _audioStatus = _output.HasDevice
                    ? _output.Device?.Name ?? "Dispositivo de audio predeterminado"
                    : _output.FallbackReason ?? "Sin dispositivo de audio; el reloj es el del sistema.";
            }
            else
            {
                clock = new SystemClock(options.TimeProvider);
                _audioStatus = "Audio desactivado; el reloj es el del sistema.";
            }

            var decoderOptions = new VideoDecoderOptions
            {
                Device = device,
                AllowHardwareDecoding = options.AllowHardwareDecoding,
                CompactReferencePool = options.CompactReferencePool,
            };
            long vramBudget = options.ResolveVramBudget(device is { IsDisposed: false } gpu ? gpu.DedicatedVideoMemory : 0);
            _scheduler = new TimelineScheduler(plan, clock, backend, decoderOptions, options, vramBudget);
            _scheduler.EndReached += (_, e) => EndReached?.Invoke(this, e);
            _scheduler.DecodeFailed += (_, e) => DecodeFailed?.Invoke(this, e);
        }
        catch
        {
            while (created.Count > 0)
            {
                created.Pop().Dispose();
            }

            throw;
        }
    }

    /// <summary>La reproducción llegó al final de la secuencia y quedó en pausa. Llega desde <see cref="Tick"/>.</summary>
    public event EventHandler? EndReached;

    /// <summary>Un medio no se pudo abrir o un cuadro no se pudo decodificar; puede llegar desde un hilo de decodificación.</summary>
    public event EventHandler<TimelineDecodeFailedEventArgs>? DecodeFailed;

    public string SequenceId { get; }

    public int Width => _scheduler.Plan.Width;

    public int Height => _scheduler.Plan.Height;

    public Rational FrameRate => _scheduler.Plan.FrameRate;

    public Time Duration => _scheduler.Plan.Duration;

    /// <summary>Cuadros de la secuencia, contando el último aunque esté incompleto.</summary>
    public long FrameCount => _scheduler.Plan.FrameCount;

    public Time Position => _scheduler.Position;

    /// <summary>Cuadro de la secuencia que corresponde a la posición del reloj.</summary>
    public long CurrentFrame => _scheduler.Plan.FrameStart(Position).ToFrames(FrameRate);

    public bool IsPlaying => _scheduler.IsPlaying;

    /// <summary>Tras un seek, hasta que todas las capas muestran su cuadro exacto.</summary>
    public bool IsSeekPending => _scheduler.IsSeekPending;

    public bool HasAudioDevice => _output?.HasDevice ?? false;

    /// <summary>Nombre del dispositivo de audio o, si no suena, el motivo en español.</summary>
    public string AudioStatus => _audioStatus;

    public TimelineStats Stats => _scheduler.Stats(new TimelineAudioStats(
        HasAudioDevice,
        _audioStatus,
        _mixer?.DecodersOpen ?? 0,
        _mixer?.Underruns ?? 0,
        _mixer?.Errors ?? 0));

    /// <summary>Abre la secuencia con FFmpeg, el dispositivo de audio predeterminado y un hilo de decodificación por medio.</summary>
    /// <param name="device">Dispositivo del compositor; los cuadros llegan como texturas suyas. Sin él se decodifica en la CPU.</param>
    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static TimelineSession Open(Project project, string sequenceId, GpuDevice? device, TimelineSessionOptions? options = null) =>
        new(project, sequenceId, device, options ?? new TimelineSessionOptions(), MediaClipBackend.Instance);

    public void Play()
    {
        ThrowIfDisposed();
        _scheduler.Play();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        _scheduler.Pause();
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Lleva la reproducción a <paramref name="position"/>, acotada a la secuencia; si sonaba, sigue sonando.</summary>
    public void Seek(Time position)
    {
        ThrowIfDisposed();
        _scheduler.Seek(position);
    }

    public void SeekBy(Time delta) => Seek(Position + delta);

    /// <summary>Lleva la reproducción al inicio del cuadro <paramref name="frame"/> de la secuencia.</summary>
    public void SeekToFrame(long frame)
    {
        ThrowIfDisposed();
        long last = Math.Max(0, FrameCount - 1);
        _scheduler.Seek(Time.FromFrames(Math.Clamp(frame, 0, last), FrameRate));
    }

    /// <summary>Pausa y avanza (o retrocede) <paramref name="frames"/> cuadros de la secuencia.</summary>
    public void StepFrame(int frames)
    {
        ThrowIfDisposed();
        if (IsPlaying)
        {
            _scheduler.Pause();
        }

        SeekToFrame(CurrentFrame + frames);
    }

    /// <summary>Las capas de este refresco, de abajo hacia arriba. Nunca espera al decodificador.</summary>
    public CompositionFrame Tick()
    {
        ThrowIfDisposed();
        return _scheduler.Tick();
    }

    /// <summary>Toma el proyecto editado y actualiza lo que se ve y se oye sin detener la reproducción ni mover la posición.</summary>
    /// <exception cref="KeyNotFoundException">La secuencia ya no existe en el proyecto.</exception>
    public void Reload(Project project)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(project);
        TimelinePlan plan = TimelinePlan.Build(project, SequenceId, _projectDirectory);
        AudioPlan previous = _scheduler.Plan.Audio;
        _project = project;
        _scheduler.Reload(plan);
        if (!plan.Audio.SoundsLike(previous))
        {
            // Rehacer la mezcla por delante solo si cambió lo que suena: mover una capa no toca el audio.
            _mixer?.Reload(plan.Audio);
        }
    }

    /// <summary>El proyecto que ya se reproduce cambió en el lugar (por ejemplo, con <c>UndoStack</c>): se vuelve a leer.</summary>
    public void Invalidate() => Reload(_project);

    /// <summary>Esperas de un decodificador a que otros liberen su pool antes de crear el suyo, para las pruebas de carga.</summary>
    internal (long Count, double Milliseconds) PoolWaits => _scheduler.PoolWaits;

    /// <summary>Audio ya mezclado por delante de lo que suena, para las pruebas.</summary>
    internal int BufferedAudioFrames => _mixer?.BufferedFrames ?? 0;

    public void ResetStats()
    {
        ThrowIfDisposed();
        _scheduler.ResetStats();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.Dispose();
        _output?.Dispose();
        _mixer?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
