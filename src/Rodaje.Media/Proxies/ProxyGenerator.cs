using System.Diagnostics;
using System.Text;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

using Rodaje.Media.Gpu;

namespace Rodaje.Media.Proxies;

/// <summary>Etapa de un trabajo de proxy.</summary>
public enum ProxyJobPhase
{
    /// <summary>Esperando turno (hay otros proxies generándose).</summary>
    Queued,

    /// <summary>Leyendo el original y eligiendo codificador.</summary>
    Analyzing,

    Encoding,

    /// <summary>Comprobando que el proxy tiene los mismos cuadros y marcas que el original.</summary>
    Verifying,

    Completed,
}

/// <summary>Avance de un proxy, para mostrar en la interfaz.</summary>
/// <param name="SourcePath">Original.</param>
/// <param name="Phase">Etapa.</param>
/// <param name="Fraction">De 0 a 1.</param>
/// <param name="FramesDone">Cuadros codificados.</param>
/// <param name="FramesTotal">Cuadros del original (estimados si su contenedor no los declara).</param>
/// <param name="Speed">Velocidad en veces el tiempo real, o 0 si todavía no se sabe.</param>
/// <param name="Encoder">Codificador en uso, o <c>null</c> antes de elegirlo.</param>
public sealed record ProxyProgress(string SourcePath, ProxyJobPhase Phase, double Fraction, long FramesDone, long FramesTotal, double Speed, string? Encoder);

/// <summary>Opciones de <see cref="ProxyGenerator"/>.</summary>
public sealed record ProxyGeneratorOptions
{
    /// <summary>Proxies que se generan a la vez; el resto espera en cola.</summary>
    public int MaxConcurrent { get; init; } = 2;

    /// <summary>Codificadores en orden de preferencia; se usa el primero que funcione en este equipo.</summary>
    public IReadOnlyList<string> EncoderPreference { get; init; } = ProxyEncoders.PreferenceFor(GpuProfileReader.Current);

    /// <summary>Decodifica el original con D3D11VA dentro de ffmpeg; si falla, se repite por software.</summary>
    public bool HardwareDecoding { get; init; } = true;

    /// <summary>Prioridad de <c>ffmpeg.exe</c>: por debajo de lo normal para no quitarle CPU a la reproducción.</summary>
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;
}

/// <summary>No se pudo generar un proxy; el mensaje explica por qué en español.</summary>
public sealed class ProxyGenerationException : Exception
{
    public ProxyGenerationException()
    {
    }

    public ProxyGenerationException(string message)
        : base(message)
    {
    }

    public ProxyGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Genera proxies H.264 con el <c>ffmpeg.exe</c> LGPL como proceso aparte (sin audio: el audio se decodifica siempre
/// del original), con GOP corto, sin cuadros B y con exactamente los mismos cuadros y marcas que el original.
/// </summary>
public sealed class ProxyGenerator : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly Lazy<IReadOnlyList<string>> _encoders;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SharedJob> _running = new(StringComparer.Ordinal);

    public ProxyGenerator(ProxyStore store, FfmpegExecutable ffmpeg, ProxyGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        Store = store;
        Ffmpeg = ffmpeg;
        Options = options ?? new ProxyGeneratorOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.MaxConcurrent, 1);
        _slots = new SemaphoreSlim(Options.MaxConcurrent, Options.MaxConcurrent);
        _encoders = new Lazy<IReadOnlyList<string>>(() => Options.EncoderPreference.Where(Ffmpeg.IsEncoderUsable).ToList());
    }

    public ProxyStore Store { get; }

    public FfmpegExecutable Ffmpeg { get; }

    public ProxyGeneratorOptions Options { get; }

    /// <summary>Codificadores de <see cref="ProxyGeneratorOptions.EncoderPreference"/> que funcionan en este equipo.</summary>
    public IReadOnlyList<string> UsableEncoders => _encoders.Value;

    /// <summary>
    /// Devuelve el proxy del original con esos ajustes: el que ya hay en el almacén o uno nuevo. Espera turno si
    /// ya hay <see cref="ProxyGeneratorOptions.MaxConcurrent"/> generándose.
    /// </summary>
    /// <remarks>
    /// Las peticiones con la misma huella y los mismos ajustes comparten un único trabajo, aunque lleguen con rutas
    /// escritas distinto; el trabajo solo se cancela cuando se cancelan todas las peticiones que lo esperan.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Se canceló; ffmpeg muere y no queda ningún archivo parcial.</exception>
    /// <exception cref="ProxyGenerationException">Ningún codificador pudo generar un proxy válido.</exception>
    public async Task<ProxyEntry> GenerateAsync(
        string sourcePath,
        ProxySettings? settings = null,
        IProgress<ProxyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        settings ??= ProxySettings.Default;
        settings.Validate();
        string source = Path.GetFullPath(sourcePath);
        progress?.Report(new ProxyProgress(source, ProxyJobPhase.Queued, 0, 0, 0, 0, null));
        SourceFingerprint fingerprint = await Task.Run(() => SourceFingerprint.Compute(source), cancellationToken).ConfigureAwait(false);
        string key = $"{fingerprint.Key}-{settings.Tag}";
        var listener = new Listener(source, progress);
        SharedJob? job;
        lock (_gate)
        {
            if (!_running.TryGetValue(key, out job) || job.Abandoned)
            {
                var created = new SharedJob();
                _running[key] = created;
                created.Task = Task.Run(() => RunShared(key, created, fingerprint, settings), CancellationToken.None);
                job = created;
            }

            job.Add(listener);
        }

        try
        {
            return await job.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (Leave(job, listener))
            {
                // Era la última petición: se espera a que ffmpeg muera y se borren sus temporales.
                await job.Task.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            job.Remove(listener);
        }
    }

    public void Dispose() => _slots.Dispose();

    private async Task<ProxyEntry> RunShared(string key, SharedJob job, SourceFingerprint fingerprint, ProxySettings settings)
    {
        try
        {
            await _slots.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
            try
            {
                return await Generate(fingerprint, settings, job, job.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_running.TryGetValue(key, out SharedJob? current) && current == job)
                {
                    _running.Remove(key);
                }
            }
        }
    }

    // Devuelve true si era la última petición; entonces cancela el trabajo.
    private bool Leave(SharedJob job, Listener listener)
    {
        lock (_gate)
        {
            if (job.Remove(listener) > 0 || job.Task.IsCompleted)
            {
                return false;
            }

            job.Abandoned = true;
        }

        job.Cancellation.Cancel();
        return true;
    }

    private async Task<ProxyEntry> Generate(SourceFingerprint fingerprint, ProxySettings settings, IProgress<ProxyProgress> progress, CancellationToken cancellationToken)
    {
        string source = fingerprint.Path;
        progress.Report(new ProxyProgress(source, ProxyJobPhase.Analyzing, 0, 0, 0, 0, null));
        if (Store.TryGet(fingerprint, settings) is { } existing)
        {
            progress.Report(new ProxyProgress(source, ProxyJobPhase.Completed, 1, existing.Metadata.FrameCount, existing.Metadata.FrameCount, 0, existing.Metadata.Encoder));
            return existing;
        }

        MediaInfo info = MediaProbe.Probe(source);
        VideoStreamInfo video = info.Video ?? throw new ProxyGenerationException($"{source} no tiene video; no necesita proxy.");
        IReadOnlyList<string> encoders = UsableEncoders;
        if (encoders.Count == 0)
        {
            throw new ProxyGenerationException(
                $"Ningún codificador de proxies funciona con {Ffmpeg.Path} (se probaron {string.Join(", ", Options.EncoderPreference)}).");
        }

        var input = new ProxyEncodeInput(source, video.StreamIndex, video.DisplayWidth, video.DisplayHeight, video.FrameRate);
        var failures = new List<string>();
        bool hardware = Options.HardwareDecoding;
        for (int i = 0; i < encoders.Count;)
        {
            string encoder = encoders[i];
            string temporary = Store.CreateTemporaryPath(fingerprint, settings);
            try
            {
                (long frames, TimeSpan elapsed) = await RunFfmpeg(input, settings, encoder, temporary, hardware, video, progress, cancellationToken)
                    .ConfigureAwait(false);
                progress.Report(new ProxyProgress(source, ProxyJobPhase.Verifying, 1, frames, video.FrameCount, 0, encoder));
                ProxyMetadata metadata = Verify(temporary, fingerprint, settings, encoder, info, frames, elapsed);
                if (!fingerprint.MatchesQuick(source))
                {
                    throw new ProxyGenerationException($"El original {source} cambió mientras se generaba su proxy.");
                }

                ProxyEntry entry = Store.Commit(temporary, metadata, settings);
                progress.Report(new ProxyProgress(source, ProxyJobPhase.Completed, 1, frames, frames, metadata.Speed, encoder));
                return entry;
            }
            catch (ProxyEncodeException ex)
            {
                failures.Add($"{encoder}{(hardware ? " con decodificación D3D11VA" : "")}: {ex.Message}");
                if (hardware)
                {
                    hardware = false;
                }
                else
                {
                    i++;
                }
            }
            finally
            {
                DeleteWithRetry(temporary);
            }
        }

        throw new ProxyGenerationException($"No se pudo generar el proxy de {source}. {string.Join(" / ", failures)}");
    }

    private async Task<(long Frames, TimeSpan Elapsed)> RunFfmpeg(
        ProxyEncodeInput input,
        ProxySettings settings,
        string encoder,
        string output,
        bool hardwareDecoding,
        VideoStreamInfo video,
        IProgress<ProxyProgress> progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = ProxyEncoders.BuildArguments(input, settings, encoder, output, hardwareDecoding);
        Ffmpeg.EnsureUnchanged();
        var info = new ProcessStartInfo(Ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Ffmpeg.Path)!,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    if (errors.Length < 8192)
                    {
                        errors.AppendLine(e.Data);
                    }
                }
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        process.Start();
        ChildProcessJob.TryAssign(process);
        try
        {
            process.PriorityClass = Options.Priority;
        }
        catch (InvalidOperationException)
        {
        }

        process.BeginErrorReadLine();
        process.StandardInput.Close();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(process));

        var parser = new FfmpegProgressParser();
        double rate = video.FrameRate.ToDouble();
        long total = Math.Max(1, video.FrameCount);
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (parser.Feed(line, out FfmpegProgressSnapshot snapshot))
            {
                double seconds = watch.Elapsed.TotalSeconds;
                double speed = seconds > 0 ? snapshot.Frame / rate / seconds : 0;
                progress.Report(new ProxyProgress(input.SourcePath, ProxyJobPhase.Encoding, Math.Clamp((double)snapshot.Frame / total, 0, 1),
                    snapshot.Frame, total, speed, encoder));
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        watch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string log;
            lock (errors)
            {
                log = FfmpegExecutable.Tail(errors.ToString());
            }

            throw new ProxyEncodeException($"ffmpeg terminó con código {process.ExitCode}: {log}");
        }

        return (parser.Last.Frame, watch.Elapsed);
    }

    private ProxyMetadata Verify(string temporary, SourceFingerprint fingerprint, ProxySettings settings, string encoder,
        MediaInfo source, long framesWritten, TimeSpan elapsed)
    {
        VideoStreamInfo original = source.Video!;
        MediaInfo proxy;
        try
        {
            proxy = MediaProbe.Probe(temporary);
        }
        catch (MediaException ex)
        {
            throw new ProxyEncodeException($"el proxy generado no se puede abrir: {ex.Message}");
        }

        VideoStreamInfo video = proxy.Video ?? throw new ProxyEncodeException("el proxy generado no tiene video.");
        if (framesWritten > 0 && video.FrameCount != framesWritten)
        {
            throw new ProxyEncodeException($"ffmpeg escribió {framesWritten} cuadros y el proxy declara {video.FrameCount}.");
        }

        Time halfFrame = Time.FromTicks(Time.FromFrames(1, original.FrameRate).Ticks / 2);
        if (Time.FromTicks(Math.Abs((video.StartTime - original.StartTime).Ticks)) > halfFrame)
        {
            throw new ProxyEncodeException(
                $"el proxy empieza en {video.StartTime} y el original en {original.StartTime}; sus cuadros no coincidirían.");
        }

        if (!original.IsVariableFrameRate && video.FrameRate != original.FrameRate)
        {
            throw new ProxyEncodeException($"el proxy quedó a {video.FrameRate} fps y el original es de {original.FrameRate}.");
        }

        double seconds = Math.Max(elapsed.TotalSeconds, 1e-6);
        return new ProxyMetadata
        {
            SourcePath = fingerprint.Path,
            SourceKey = fingerprint.Key,
            SourceSize = fingerprint.Size,
            SourceLastWriteUtc = fingerprint.LastWriteUtc,
            SettingsTag = settings.Tag,
            TargetHeight = settings.TargetHeight,
            GopLength = settings.GopLength,
            Encoder = encoder,
            FfmpegVersion = Ffmpeg.Version,
            Width = video.DisplayWidth,
            Height = video.DisplayHeight,
            FrameRateNum = original.FrameRate.Num,
            FrameRateDen = original.FrameRate.Den,
            FrameCount = video.FrameCount,
            SourceFrameCount = original.FrameCount,
            SourceStartTicks = original.StartTime.Ticks,
            ProxyStartTicks = video.StartTime.Ticks,
            SourceDurationTicks = original.Duration.Ticks,
            ProxyDurationTicks = video.Duration.Ticks,
            CreatedUtc = DateTime.UtcNow,
            GenerationSeconds = elapsed.TotalSeconds,
            Speed = video.FrameCount / original.FrameRate.ToDouble() / seconds,
        };
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void DeleteWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed record Listener(string SourcePath, IProgress<ProxyProgress>? Progress);

    // Un proxy en marcha y las peticiones que lo esperan; el avance llega a cada una con su propia ruta.
    private sealed class SharedJob : IProgress<ProxyProgress>
    {
        private readonly List<Listener> _listeners = [];

        public CancellationTokenSource Cancellation { get; } = new();

        public bool Abandoned { get; set; }

        public Task<ProxyEntry> Task { get; set; } = null!;

        public void Add(Listener listener)
        {
            lock (_listeners)
            {
                _listeners.Add(listener);
            }
        }

        /// <summary>Quita la petición y devuelve cuántas quedan.</summary>
        public int Remove(Listener listener)
        {
            lock (_listeners)
            {
                _listeners.Remove(listener);
                return _listeners.Count;
            }
        }

        public void Report(ProxyProgress value)
        {
            Listener[] listeners;
            lock (_listeners)
            {
                listeners = [.. _listeners];
            }

            foreach (Listener listener in listeners)
            {
                listener.Progress?.Report(value with { SourcePath = listener.SourcePath });
            }
        }
    }

    // Falla de un intento concreto (codificador o decodificación); el generador prueba la siguiente opción.
    private sealed class ProxyEncodeException(string message) : Exception(message);
}
