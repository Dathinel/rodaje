using System.Diagnostics;
using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Media;
using Rodaje.Media.Audio;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine;

/// <summary>Reproduce un archivo: video decodificado por delante, audio como reloj maestro y el cuadro de cada refresco.</summary>
/// <remarks>
/// <para>
/// El reloj es el del dispositivo de audio cuando el archivo tiene audio y hay salida; si no, el del
/// sistema. El video siempre lo sigue a través de <see cref="PlaybackScheduler"/>.
/// </para>
/// <para>
/// No es seguro entre hilos: transporte, <see cref="Tick"/> y <see cref="Dispose"/> se llaman desde un mismo
/// hilo (el de la ventana). Los cuadros que entrega <see cref="Tick"/> valen hasta el siguiente <see cref="Tick"/>.
/// </para>
/// </remarks>
public sealed class ClipPlayer : IDisposable
{
    private readonly IClipVideo _video;
    private readonly BufferedAudioSource? _audioSource;
    private readonly IAudioOutput? _output;
    private readonly IAudioClock _clock;
    private readonly FrameCache _cache;
    private readonly IDecodeExecutor _executor;
    private readonly PlaybackScheduler _scheduler;
    private readonly bool _hasDevice;
    private readonly bool _gpuPayloads;

    private PlaybackStats _folded;
    private bool _settling;
    private long _seekStarted;
    private long _seeks;
    private long _settledSeeks;
    private long _maxSeekTicks;
    private long _totalSeekTicks;
    private bool _disposed;

    internal ClipPlayer(string path, ClipPlayerOptions options, IClipBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        options.Validate();
        Path = path;

        var created = new Stack<IDisposable>();
        try
        {
            _video = backend.OpenVideo(path, new VideoDecoderOptions
            {
                Device = options.Device,
                AllowHardwareDecoding = options.AllowHardwareDecoding,
                ExtraHardwareFrames = options.ExtraHardwareFrames,
            });
            created.Push(_video);
            Info = _video.Info;
            Video = _video.Stream;
            Time oneFrame = Time.FromFrames(1, Video.FrameRate);
            Timing = new ClipTiming(Time.Zero, Time.Zero, Time.Max(Video.Duration, oneFrame), Video.FrameRate);

            _gpuPayloads = options.Device is { IsDisposed: false };
            (_audioSource, _output, AudioStatus) = OpenAudio(path, Info.HasAudio, options, backend, created);
            _clock = _output?.Clock ?? new SystemClock();
            _hasDevice = _output?.HasDevice ?? false;

            CachePlan plan = PlanCache(Video, options);
            Cache = plan;
            _cache = new FrameCache(plan.BudgetBytes);
            created.Push(_cache);
            _executor = backend.CreateExecutor();
            if (_executor is IDisposable disposableExecutor)
            {
                created.Push(disposableExecutor);
            }

            _scheduler = new PlaybackScheduler(Timing, _clock, _video, _cache, _executor,
                new PlaybackOptions { PrefetchFrames = plan.PrefetchFrames });
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

    /// <summary>La reproducción llegó al final y quedó en pausa. Llega desde <see cref="Tick"/>.</summary>
    public event EventHandler? EndReached;

    /// <summary>Un cuadro no se pudo decodificar; puede llegar desde el hilo de decodificación.</summary>
    public event EventHandler<DecodeFailedEventArgs>? DecodeFailed;

    public string Path { get; }

    public MediaInfo Info { get; }

    public VideoStreamInfo Video { get; }

    public ClipTiming Timing { get; }

    public Time Duration => Timing.Duration;

    public Rational FrameRate => Timing.FrameRate;

    public long FrameCount => Timing.LastFrame + 1;

    /// <summary>Nombre del dispositivo de audio o, si no suena, el motivo en español.</summary>
    public string AudioStatus { get; }

    public bool HasAudioDevice => _hasDevice;

    public Time Position => _scheduler.Position;

    /// <summary>Cuadro que corresponde a la posición del reloj.</summary>
    public long CurrentFrame => Timing.FrameAt(Position);

    public bool IsPlaying => _scheduler.IsPlaying;

    public bool IsSeekPending => _scheduler.IsSeekPending;

    public ClipPlayerStats Stats
    {
        get
        {
            PlaybackStats live = _scheduler.Stats;
            var playback = new PlaybackStats(
                _folded.FramesShown + live.FramesShown,
                _folded.FramesDropped + live.FramesDropped,
                _folded.LateRefreshes + live.LateRefreshes,
                Time.Max(_folded.MaxAvOffset, live.MaxAvOffset),
                _folded.CacheHits + live.CacheHits,
                _folded.CacheMisses + live.CacheMisses,
                _folded.DecodeErrors + live.DecodeErrors);
            DecodeMode mode = _video.UsesHardwareDecoding ? DecodeMode.Gpu
                : _gpuPayloads ? DecodeMode.CpuUploaded : DecodeMode.Cpu;
            AudioOutputStats audio = _output?.Stats ?? default;
            return new ClipPlayerStats(
                playback,
                mode,
                _video.Statistics,
                _seeks,
                Stopwatch.GetElapsedTime(0, _maxSeekTicks),
                _settledSeeks == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, _totalSeekTicks / _settledSeeks),
                _hasDevice,
                AudioStatus,
                audio,
                _audioSource?.Underruns ?? 0);
        }
    }

    /// <summary>Abre el archivo con FFmpeg, el dispositivo de audio predeterminado y un hilo de decodificación.</summary>
    /// <exception cref="FfmpegNotFoundException">No se pudo cargar FFmpeg.</exception>
    /// <exception cref="MediaException">El archivo no se puede abrir o no tiene video.</exception>
    public static ClipPlayer Open(string path, ClipPlayerOptions? options = null)
    {
        return new ClipPlayer(path, options ?? new ClipPlayerOptions(), MediaClipBackend.Instance);
    }

    /// <summary>Cuántos cuadros guarda la caché y cuántos se adelantan para este archivo.</summary>
    internal CachePlan Cache { get; }

    /// <summary>
    /// <see cref="ClipPlayerOptions.CacheFrames"/> cuadros del tamaño visible, sin pasar de
    /// <see cref="ClipPlayerOptions.MaxCacheBytes"/>; si el tope deja menos, se adelantan menos cuadros.
    /// </summary>
    /// <remarks>
    /// Se cuenta sin el relleno del decodificador, que hace las texturas reales un poco mayores: así nunca caben
    /// más cuadros de los previstos y el pool de D3D11VA no se agota.
    /// </remarks>
    internal static CachePlan PlanCache(VideoStreamInfo video, ClipPlayerOptions options)
    {
        long bytesPerSample = video.BitDepth > 8 ? 2 : 1;
        long frame = Math.Max(1, (long)video.Width * video.Height * bytesPerSample * 3 / 2);
        int frames = (int)Math.Clamp(options.MaxCacheBytes / frame, 1, options.CacheFrames);
        int prefetch = Math.Clamp((frames - 2) / 2, 1, options.PrefetchFrames);
        return new CachePlan(frame * frames, frames, prefetch);
    }

    public void Play()
    {
        ThrowIfDisposed();
        if (Position >= Timing.End)
        {
            BeginSeek();
            _scheduler.Seek(Timing.Start);
        }

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

    /// <summary>Lleva la reproducción a <paramref name="position"/>, acotada al clip; si sonaba, sigue sonando.</summary>
    public void Seek(Time position)
    {
        ThrowIfDisposed();
        BeginSeek();
        _scheduler.Seek(position);
    }

    public void SeekBy(Time delta) => Seek(Position + delta);

    public void SeekToFrame(long frameIndex)
    {
        ThrowIfDisposed();
        BeginSeek();
        _scheduler.SeekToFrame(Math.Clamp(frameIndex, Timing.FirstFrame, Timing.LastFrame));
    }

    /// <summary>Pausa y avanza (o retrocede) <paramref name="frames"/> cuadros desde el que corresponde ahora.</summary>
    public void StepFrame(int frames)
    {
        ThrowIfDisposed();
        if (IsPlaying)
        {
            _scheduler.Pause();
        }

        SeekToFrame(CurrentFrame + frames);
    }

    /// <summary>Elige el cuadro de este refresco; nunca espera al decodificador.</summary>
    public FramePresentation Tick()
    {
        ThrowIfDisposed();
        FramePresentation presentation = _scheduler.Tick();
        if (_settling && presentation.IsExact)
        {
            _settling = false;
            long elapsed = Stopwatch.GetTimestamp() - _seekStarted;
            _settledSeeks++;
            _totalSeekTicks += elapsed;
            _maxSeekTicks = Math.Max(_maxSeekTicks, elapsed);
            // Lo contado mientras se esperaba el cuadro del seek no es retraso de sincronía.
            _scheduler.ResetStats();
        }

        return presentation;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.Dispose();
        bool releasedByThread = false;
        switch (_executor)
        {
            case DecodeThread thread:
                // Si un cuadro sigue colgado en FFmpeg al vencer el plazo, el decodificador lo libera su hilo al volver.
                thread.Stop(thread.JoinTimeout, _video.Dispose);
                releasedByThread = true;
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        _cache.Dispose();
        _output?.Dispose();
        _audioSource?.Dispose();
        if (!releasedByThread)
        {
            _video.Dispose();
        }
    }

    private static (BufferedAudioSource? Source, IAudioOutput? Output, string Status) OpenAudio(
        string path, bool hasAudio, ClipPlayerOptions options, IClipBackend backend, Stack<IDisposable> created)
    {
        if (!hasAudio)
        {
            return (null, null, "El archivo no tiene audio; el reloj es el del sistema.");
        }

        if (!options.EnableAudio)
        {
            return (null, null, "Audio desactivado; el reloj es el del sistema.");
        }

        IPcmSource pcm;
        try
        {
            pcm = backend.OpenAudio(path);
        }
        catch (MediaException ex)
        {
            return (null, null, "No se pudo decodificar el audio (" + ex.Message + "); el reloj es el del sistema.");
        }

        BufferedAudioSource source;
        try
        {
            int capacity = (int)Math.Ceiling(options.AudioReadAhead.TotalSeconds * AudioDecoder.OutputSampleRate);
            source = new BufferedAudioSource(pcm, capacity);
        }
        catch
        {
            pcm.Dispose();
            throw;
        }

        created.Push(source);
        IAudioOutput output = backend.OpenOutput(source, options.AudioOutput ?? new AudioOutputOptions());
        created.Push(output);
        string status = output.HasDevice
            ? output.Device?.Name ?? "Dispositivo de audio predeterminado"
            : output.FallbackReason ?? "Sin dispositivo de audio; el reloj es el del sistema.";
        return (source, output, status);
    }

    private void BeginSeek()
    {
        PlaybackStats s = _scheduler.Stats;
        _folded = new PlaybackStats(
            _folded.FramesShown + s.FramesShown,
            _folded.FramesDropped + s.FramesDropped,
            _folded.LateRefreshes + s.LateRefreshes,
            Time.Max(_folded.MaxAvOffset, s.MaxAvOffset),
            _folded.CacheHits + s.CacheHits,
            _folded.CacheMisses + s.CacheMisses,
            _folded.DecodeErrors + s.DecodeErrors);
        _scheduler.ResetStats();
        _settling = true;
        _seekStarted = Stopwatch.GetTimestamp();
        _seeks++;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal readonly record struct CachePlan(long BudgetBytes, int Frames, int PrefetchFrames);
