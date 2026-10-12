using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rodaje.Core.Serialization;
using Rodaje.Media.Proxies;

namespace Rodaje.Media.Export;

/// <summary>Opciones de <see cref="FfmpegPipeSink"/>.</summary>
public sealed record FfmpegPipeSinkOptions
{
    /// <summary>Tamaño de cada escritura a la tubería; los cuadros más grandes van en varias.</summary>
    public const int DefaultWriteChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Prioridad de <c>ffmpeg.exe</c>: <see cref="ProcessPriorityClass.Normal"/> en «Alto rendimiento» y
    /// <see cref="ProcessPriorityClass.BelowNormal"/> en «Bajo rendimiento» (<see cref="PriorityFor"/>).
    /// </summary>
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;

    /// <summary>Entre 1 y 16 MB.</summary>
    public int WriteChunkBytes { get; init; } = DefaultWriteChunkBytes;

    /// <summary>Registro de temporales para limpiar tras un apagón; <c>null</c> no registra.</summary>
    public ExportJournal? Journal { get; init; }

    /// <summary>Medios de la secuencia: la salida no puede pisar ninguno.</summary>
    public IReadOnlyCollection<string> SourcePaths { get; init; } = [];

    /// <summary>Tamaño estimado del archivo, para comprobar el espacio antes de empezar; <c>null</c> solo exige la reserva.</summary>
    public long? EstimatedBytes { get; init; }

    /// <summary>Espacio que se deja libre siempre en el disco de destino.</summary>
    public long DiskReserveBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Cada cuánto se vuelve a mirar el espacio libre mientras se escribe.</summary>
    public TimeSpan DiskCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Bytes libres en el disco de una ruta; <c>null</c> usa <see cref="DriveInfo"/>. Las pruebas simulan un disco lleno con él.</summary>
    public Func<string, long>? FreeSpace { get; init; }

    /// <summary>La prioridad de cada modo de rendimiento (D-028).</summary>
    /// <param name="fastest"><c>true</c> en «Alto rendimiento (menor tiempo)».</param>
    public static ProcessPriorityClass PriorityFor(bool fastest) => fastest ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;

    /// <summary>Si el contenedor guarda capítulos (en los demás se omiten).</summary>
    public static bool AllowsChapters(ExportContainer container) => FfmpegPipeArguments.AllowsChapters(container);

    public static bool AllowsSoftSubtitles(ExportContainer container) => FfmpegPipeArguments.AllowsSoftSubtitles(container);
}

/// <summary>
/// Sink de la v1: lanza el <c>ffmpeg.exe</c> LGPL verificado, le pasa los cuadros crudos por la entrada estándar
/// (<c>-f rawvideo -i pipe:0</c>) y el WAV de la mezcla como segunda entrada, y escribe un temporal que renombra al
/// terminar (D-028).
/// </summary>
/// <remarks>
/// Sin reservas por cuadro: un solo búfer de <see cref="FfmpegPipeSinkOptions.WriteChunkBytes"/> se llena fila a fila y
/// se vacía a la tubería en escrituras de 1–4 MB. El proceso va dentro del <c>ChildProcessJob</c> (muere si Rodaje
/// muere). <see cref="Abort"/> lo mata y borra los temporales en menos de 1 s; si no sale, la limpieza queda pendiente.
/// </remarks>
public sealed class FfmpegPipeSink : IExportSink
{
    private const int StderrLimit = 16 * 1024;
    // Entre las dos esperas, menos de 1 s (el criterio de la fase 6); si ffmpeg.exe no salió, la limpieza queda pendiente.
    private static readonly TimeSpan KillWait = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan TreeKillWait = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CleanupRetry = TimeSpan.FromSeconds(2);
    // Lo que Start espera a que termine la limpieza pendiente de una exportación cancelada a la misma salida.
    private static readonly TimeSpan PendingCleanupWait = TimeSpan.FromSeconds(1);

    private readonly FfmpegExecutable? _ffmpeg;
    private readonly string _executablePath;
    private readonly FfmpegPipeSinkOptions _options;
    private readonly Lock _gate = new();
    private readonly StringBuilder _stderr = new();
    private readonly List<string> _temporaries = [];
    private readonly List<string> _diagnostics = [];

    private RunningProcess? _running;
    private Stream? _pipe;
    private byte[]? _buffer;
    private int _buffered;
    private ExportSinkRequest? _request;
    private string? _temporaryOutput;
    private string? _journalId;
    private long _framesWritten;
    private long _framesEncoded;
    private bool _progressEnded;
    private volatile bool _aborted;
    private bool _disposed;
    private bool _completed;
    private bool _committing;
    private CancellationToken _registeredToken;
    private CancellationTokenRegistration _registration;
    private long _nextDiskCheck;
    private TaskCompletionSource? _stdoutClosed;
    private TaskCompletionSource? _abortFinished;
    private Task? _pendingCleanup;

    public FfmpegPipeSink(FfmpegExecutable ffmpeg, FfmpegPipeSinkOptions? options = null)
        : this(ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg)), ffmpeg.Path, options)
    {
    }

    /// <summary>
    /// Solo para las pruebas: lanza <paramref name="unverifiedExecutable"/> sin pasar por <see cref="FfmpegExecutable"/>,
    /// cuya verificación (LGPL, DLL, serie de libavcodec) un ejecutable falso no puede pasar. Así las carreras de
    /// <see cref="Complete"/> y <see cref="Abort"/> se prueban donde no hay ffmpeg.exe.
    /// </summary>
    internal FfmpegPipeSink(string unverifiedExecutable, FfmpegPipeSinkOptions? options = null)
        : this(null, unverifiedExecutable, options)
    {
    }

    private FfmpegPipeSink(FfmpegExecutable? ffmpeg, string executablePath, FfmpegPipeSinkOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _ffmpeg = ffmpeg;
        _executablePath = executablePath;
        _options = options ?? new FfmpegPipeSinkOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.WriteChunkBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.WriteChunkBytes, 16 * 1024 * 1024);
    }

    /// <summary>Cuadros que el codificador ya consumió según su <c>-progress</c>.</summary>
    public long FramesEncoded => Interlocked.Read(ref _framesEncoded);

    /// <summary>Cuadros escritos en la tubería en la pasada actual.</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Línea de comandos de la pasada actual (para el registro y las pruebas).</summary>
    public IReadOnlyList<string> Arguments { get; private set; } = [];

    /// <summary>Proceso de la pasada actual, o <c>null</c>; para comprobar que no queda vivo.</summary>
    internal int? ProcessId { get; private set; }

    /// <summary>Solo para las pruebas: corre en <see cref="Complete"/> justo antes de esperar a que ffmpeg.exe cierre el contenedor.</summary>
    internal Action? BeforeCompleteWaits { get; set; }

    /// <summary>
    /// Solo para las pruebas: reemplaza a <see cref="Kill"/> (en Abort y en los reintentos de la limpieza pendiente) y
    /// devuelve si ffmpeg.exe salió. Sirve para simular un ffmpeg.exe que no muere.
    /// </summary>
    internal Func<Process, bool>? KillOverride { get; set; }

    /// <summary>Limpieza de un Abort cuyo ffmpeg.exe no salió a tiempo, o <c>null</c>; termina cuando salió y se borró todo.</summary>
    internal Task? PendingCleanup
    {
        get
        {
            lock (_gate)
            {
                return _pendingCleanup;
            }
        }
    }

    /// <summary>Lo que salió mal al cancelar (ffmpeg.exe que no murió, temporales que no se pudieron borrar).</summary>
    internal IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_diagnostics)
            {
                return [.. _diagnostics];
            }
        }
    }

    public void Start(ExportSinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running is not null)
        {
            throw new InvalidOperationException("El sink ya tiene una pasada en marcha.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        string output = Path.GetFullPath(request.OutputPath);
        WaitForPendingCleanup(request, output, cancellationToken);
        if (!request.IsAnalysisPass)
        {
            ExportOutputGuard.CheckOutput(output, request.Container, _options.SourcePaths);
            ExportOutputGuard.CheckDiskSpace(output, _options.EstimatedBytes ?? 0, _options.DiskReserveBytes, _options.FreeSpace);
        }

        _request = request;
        lock (_gate)
        {
            _aborted = false;
            _completed = false;
            _committing = false;
            _abortFinished = null;
        }

        _framesWritten = 0;
        _framesEncoded = 0;
        _progressEnded = false;
        _buffered = 0;
        lock (_stderr)
        {
            _stderr.Clear();
        }

        _temporaryOutput = request.IsAnalysisPass ? null : ExportOutputGuard.TemporaryPath(output);
        string? metadataFile = WriteMetadataFile(request, output);
        List<string> subtitleFiles = WriteSubtitleFiles(request, output);
        RegisterTemporaries(output, metadataFile);
        _temporaries.AddRange(subtitleFiles);
        Arguments = FfmpegPipeArguments.Build(request, _temporaryOutput, metadataFile, subtitleFiles);
        _buffer ??= GC.AllocateUninitializedArray<byte>(_options.WriteChunkBytes, pinned: true);
        _nextDiskCheck = Stopwatch.GetTimestamp() + (long)(_options.DiskCheckInterval.TotalSeconds * Stopwatch.Frequency);
        Launch(request);
        Register(cancellationToken);
    }

    public void WriteVideo(in ExportVideoFrame frame, CancellationToken cancellationToken)
    {
        ExportSinkRequest request = _request ?? throw new InvalidOperationException("Falta Start.");
        if (!request.HasVideo)
        {
            throw new InvalidOperationException("Esta exportación no lleva video.");
        }

        Register(cancellationToken);
        ThrowIfStopped(cancellationToken);
        if (frame.Index != _framesWritten)
        {
            throw new ArgumentException($"Llegó el cuadro {frame.Index} y se esperaba el {_framesWritten}.", nameof(frame));
        }

        if (frame.Format.Width != request.Video!.Width || frame.Format.Height != request.Video.Height
            || frame.Format.PixelFormat != request.Video.PixelFormat)
        {
            throw new ArgumentException(
                $"El cuadro es {frame.Format.Width}×{frame.Format.Height} {frame.Format.PixelFormat.FfmpegName()} y la salida espera " +
                $"{request.Video.Width}×{request.Video.Height} {request.Video.PixelFormat.FfmpegName()}.", nameof(frame));
        }

        if (_framesWritten >= request.FrameCount)
        {
            throw new ArgumentException($"Sobra el cuadro {frame.Index}: la salida tiene {request.FrameCount}.", nameof(frame));
        }

        CheckDiskWhileWriting(cancellationToken);
        try
        {
            ExportPixelFormat pixels = frame.Format.PixelFormat;
            for (int plane = 0; plane < frame.PlaneCount; plane++)
            {
                int rows = pixels.PlaneSize(plane, frame.Format.Width, frame.Format.Height).Height;
                int rowBytes = pixels.RowBytes(plane, frame.Format.Width, frame.Format.Height);
                int pitch = frame.Pitch(plane);
                ReadOnlySpan<byte> source = frame.Plane(plane);
                if (pitch == rowBytes)
                {
                    Append(source[..(rowBytes * rows)]);
                    continue;
                }

                for (int y = 0; y < rows; y++)
                {
                    Append(source.Slice(y * pitch, rowBytes));
                }
            }
        }
        catch (IOException ex)
        {
            throw Failure(ex, cancellationToken);
        }
        catch (ObjectDisposedException ex)
        {
            throw Failure(ex, cancellationToken);
        }

        Interlocked.Increment(ref _framesWritten);
    }

    public ExportSinkResult Complete(CancellationToken cancellationToken)
    {
        ExportSinkRequest request = _request ?? throw new InvalidOperationException("Falta Start.");
        Register(cancellationToken);
        // Con el proceso prestado, un Abort desde otro hilo lo mata pero no lo libera mientras se espera aquí.
        RunningProcess? running = Lease();
        try
        {
            ThrowIfStopped(cancellationToken);
            Process process = running?.Process ?? throw new InvalidOperationException("Falta Start.");
            return Finish(request, process, cancellationToken);
        }
        finally
        {
            Return(running);
        }
    }

    private ExportSinkResult Finish(ExportSinkRequest request, Process process, CancellationToken cancellationToken)
    {
        if (request.HasVideo && request.VideoCopy is null && _framesWritten != request.FrameCount)
        {
            Abort();
            throw new ExportException(ExportErrorCode.EncoderFailed,
                $"Se escribieron {_framesWritten} de {request.FrameCount} cuadros; la exportación quedó incompleta.");
        }

        try
        {
            Flush();
            _pipe?.Dispose();
        }
        catch (IOException ex)
        {
            throw Failure(ex, cancellationToken);
        }
        catch (ObjectDisposedException ex)
        {
            // Un Abort desde otro hilo ya cerró la tubería.
            throw Failure(ex, cancellationToken);
        }

        BeforeCompleteWaits?.Invoke();
        while (!process.WaitForExit(100))
        {
            ThrowIfStopped(cancellationToken);
        }

        process.WaitForExit();
        _stdoutClosed?.Task.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
        ThrowIfStopped(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw Failure(null, cancellationToken);
        }

        // En la pasada de análisis algunos codificadores (libvpx) no sacan paquetes: el avance se queda en 0.
        if (request.HasVideo && !request.IsAnalysisPass && _progressEnded && FramesEncoded != request.OutputFrameCount)
        {
            ExportException incomplete = new(ExportErrorCode.EncoderFailed,
                $"ffmpeg.exe codificó {FramesEncoded} de {request.OutputFrameCount} cuadros.");
            Abort();
            throw incomplete;
        }

        // Punto sin vuelta atrás: desde aquí Abort no borra nada y el archivo se entrega. Una cancelación que llegue
        // antes gana; una que llegue durante el renombrado llega tarde y la exportación termina bien.
        bool commit;
        lock (_gate)
        {
            commit = !_aborted && !cancellationToken.IsCancellationRequested;
            _committing = commit;
        }

        if (!commit)
        {
            throw Canceled(null, cancellationToken);
        }

        ExportSinkResult result;
        try
        {
            string output = Path.GetFullPath(request.OutputPath);
            if (request.IsAnalysisPass)
            {
                result = new ExportSinkResult(request.PassLogPath ?? "", 0, _framesWritten);
            }
            else
            {
                long bytes = ExportOutputGuard.Commit(_temporaryOutput!, output, request.Container);
                result = new ExportSinkResult(output, bytes, _framesWritten);
            }

            DeleteTemporaries(keepPassLog: request.IsAnalysisPass);
        }
        catch
        {
            lock (_gate)
            {
                _committing = false;
            }

            Abort();
            throw;
        }

        lock (_gate)
        {
            _completed = true;
            _committing = false;
            ReleaseProcess();
        }

        _request = null;
        return result;
    }

    /// <remarks>
    /// Mata a ffmpeg.exe sin tener <c>_gate</c> tomado. Si no sale a tiempo, no se libera el proceso ni se borran los
    /// temporales que tiene abiertos: lo hace <see cref="PendingCleanup"/> cuando salga, y queda en
    /// <see cref="Diagnostics"/>. Quien llame mientras otro hilo aborta espera a que ese termine.
    /// Los temporales quedan a nombre de este Abort desde que marca la cancelación (dentro de <c>_gate</c>) hasta que
    /// los borra, él o su limpieza pendiente: mientras mata, un Start a la misma salida espera o falla en lugar de
    /// arrancar y perder sus temporales cuando el Abort viejo los borre.
    /// </remarks>
    public void Abort()
    {
        Task? inProgress = null;
        TaskCompletionSource? finished = null;
        RunningProcess? running = null;
        Stream? pipe = null;
        string[] temporaries = [];
        string? journalId = null;
        int? processId = null;
        PendingOwner? owner = null;
        lock (_gate)
        {
            if (_completed || _committing)
            {
                return;
            }

            if (_aborted)
            {
                inProgress = _abortFinished?.Task;
            }
            else
            {
                _aborted = true;
                finished = _abortFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                running = _running;
                if (running is not null)
                {
                    running.Users++;
                }

                pipe = _pipe;
                processId = ProcessId;
                temporaries = [.. _temporaries];
                owner = new PendingOwner(processId);
                PendingTemporaries.Claim(owner, temporaries);
                journalId = _journalId;
                _journalId = null;
                // Nadie más lo toma prestado; Complete y Failure, si ya lo tenían, lo siguen usando hasta devolverlo.
                ReleaseProcess();
            }
        }

        if (finished is null)
        {
            inProgress?.Wait();
            return;
        }

        // Si la limpieza queda pendiente, el proceso prestado lo devuelve ella y suelta las rutas; si no, se hace aquí
        // pase lo que pase.
        bool handedOff = false;
        try
        {
            long start = Stopwatch.GetTimestamp();
            if (running is null || TryStop(running.Process, processId, quiet: false))
            {
                ClosePipe(pipe);
                DeleteFiles(temporaries, journalId, processId, owner!);
            }
            else
            {
                Note($"ffmpeg.exe ({processId}) no salió {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms después de matarlo; " +
                    "los temporales se borran cuando salga.");
                DeferCleanup(running, pipe, temporaries, journalId, processId, start, owner!);
                handedOff = true;
            }
        }
#pragma warning disable CA1031 // Abort corre en el callback de Cancel(): una excepción subiría al hilo que cancela.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Note($"Cancelar ffmpeg.exe ({processId}) falló: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                PendingTemporaries.Release(owner!);
                Return(running);
            }

            finished.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (!_completed)
        {
            Abort();
        }

        _registration.Dispose();
        _buffer = null;
        _disposed = true;
    }

    private static void Validate(ExportSinkRequest request)
    {
        if (!request.HasVideo && !request.HasAudio)
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, "No hay ni video ni audio que exportar.");
        }

        if (request.HasVideo && request.FrameCount <= 0)
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, "El rango a exportar no tiene cuadros.");
        }

        if (request.HasAudio && !File.Exists(request.AudioPath))
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, $"Falta el WAV de la mezcla: {request.AudioPath}.");
        }

        if (request.PassCount is < 1 or > 2 || request.Pass < 1 || request.Pass > request.PassCount)
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, $"Pasada {request.Pass} de {request.PassCount} no válida.");
        }

        if (request.PassCount > 1 && string.IsNullOrWhiteSpace(request.PassLogPath))
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, "Dos pasadas necesitan la ruta del registro de pasadas.");
        }

        if (request.IsAnalysisPass && !request.HasVideo)
        {
            throw new ExportException(ExportErrorCode.InvalidSettings, "La pasada de análisis es solo de video.");
        }
    }

    private void Launch(ExportSinkRequest request)
    {
        _ffmpeg?.EnsureUnchanged();
        var info = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
        };
        // SVT-AV1 escribe su configuración en la salida de error si no se le dice lo contrario.
        info.Environment["SVT_LOG"] = "1";
        foreach (string argument in Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = info };
        var parser = new FfmpegProgressParser();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            if (parser.Feed(e.Data, out FfmpegProgressSnapshot snapshot))
            {
                Interlocked.Exchange(ref _framesEncoded, snapshot.Frame);
                if (snapshot.Ended)
                {
                    _progressEnded = true;
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderr)
            {
                if (_stderr.Length > StderrLimit)
                {
                    _stderr.Remove(0, _stderr.Length - (StderrLimit / 2));
                }

                _stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            DeleteTemporaries(keepPassLog: false);
            throw new ExportException(ExportErrorCode.EncoderMissing, $"No se pudo ejecutar {_executablePath}: {ex.Message}", ex);
        }

        // El job es de Windows; fuera de Windows (solo las pruebas con el ffmpeg falso) no hay a qué asignarlo.
        if (OperatingSystem.IsWindows())
        {
            ChildProcessJob.TryAssign(process);
        }

        try
        {
            process.PriorityClass = _options.Priority;
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_gate)
        {
            _running = new RunningProcess(process);
            _stdoutClosed = stdoutClosed;
            ProcessId = process.Id;
            _pipe = process.StandardInput.BaseStream;
            if (!request.HasVideo)
            {
                // Solo audio: ffmpeg no lee la entrada estándar; se cierra para que no espere nada.
                _pipe.Dispose();
                _pipe = null;
            }
        }
    }

    private void Register(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || cancellationToken == _registeredToken)
        {
            return;
        }

        _registration.Dispose();
        _registeredToken = cancellationToken;
        _registration = cancellationToken.UnsafeRegister(static state => ((FfmpegPipeSink)state!).Abort(), this);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        byte[] buffer = _buffer!;
        while (!data.IsEmpty)
        {
            int count = Math.Min(buffer.Length - _buffered, data.Length);
            data[..count].CopyTo(buffer.AsSpan(_buffered));
            _buffered += count;
            data = data[count..];
            if (_buffered == buffer.Length)
            {
                Flush();
            }
        }
    }

    private void Flush()
    {
        if (_buffered == 0)
        {
            return;
        }

        Stream pipe = _pipe ?? throw new ObjectDisposedException(nameof(FfmpegPipeSink));
        pipe.Write(_buffer!, 0, _buffered);
        _buffered = 0;
    }

    private void CheckDiskWhileWriting(CancellationToken cancellationToken)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextDiskCheck || _temporaryOutput is null)
        {
            return;
        }

        _nextDiskCheck = now + (long)(_options.DiskCheckInterval.TotalSeconds * Stopwatch.Frequency);
        try
        {
            ExportOutputGuard.CheckDiskSpace(_temporaryOutput, 0, _options.DiskReserveBytes, _options.FreeSpace);
        }
        catch (ExportException)
        {
            Abort();
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private void ThrowIfStopped(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _aborted)
        {
            throw Canceled(null, cancellationToken);
        }
    }

    // La cancelación sale siempre como OperationCanceledException, y después de que Abort haya matado a ffmpeg.exe y
    // borrado los temporales (si otro hilo está abortando, Abort espera a que termine).
    private OperationCanceledException Canceled(Exception? inner, CancellationToken cancellationToken)
    {
        Abort();
        return new OperationCanceledException("La exportación se canceló.", inner, cancellationToken);
    }

    // ffmpeg murió o cerró la tubería: cancelación si fue a propósito; si no, su error traducido.
    private Exception Failure(Exception? inner, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _aborted)
        {
            return Canceled(inner, cancellationToken);
        }

        int exitCode = -1;
        RunningProcess? running = Lease();
        try
        {
            if (running?.Process is { } process && process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            Return(running);
        }

        if (_aborted)
        {
            // Un Abort desde otro hilo llegó mientras se esperaba el código de salida.
            return Canceled(inner, cancellationToken);
        }

        string log;
        lock (_stderr)
        {
            log = _stderr.ToString();
        }

        ExportSinkRequest? request = _request;
        string? encoder = request?.VideoEncoder?.Id ?? request?.AudioEncoder?.Id;
        (ExportErrorCode code, string message) = FfmpegMessages.Translate(log, exitCode, encoder);
        Abort();
        return new ExportException(code, message, inner) { Frame = _framesWritten };
    }

    private static string? WriteMetadataFile(ExportSinkRequest request, string output)
    {
        if (request.IsAnalysisPass || request.Metadata.Chapters.Count == 0 || !FfmpegPipeArguments.AllowsChapters(request.Container))
        {
            return null;
        }

        string path = ExportOutputGuard.TemporaryPath(output) + ".ffmeta";
        File.WriteAllText(path, FfmpegPipeArguments.MetadataFile(request.Metadata), new UTF8Encoding(false));
        return path;
    }

    private static List<string> WriteSubtitleFiles(ExportSinkRequest request, string output)
    {
        if (request.IsAnalysisPass || !request.HasVideo || !FfmpegPipeArguments.AllowsSoftSubtitles(request.Container))
        {
            return [];
        }

        var files = new List<string>();
        foreach (ExportSubtitleTrack track in request.Metadata.Subtitles.Where(t => t.Cues.Count > 0))
        {
            string path = ExportOutputGuard.TemporaryPath(output) + string.Create(CultureInfo.InvariantCulture, $".{files.Count}.srt");
            SubtitleFiles.Write(path, track.Cues, SubtitleFormat.Srt);
            files.Add(path);
        }

        return files;
    }

    private void RegisterTemporaries(string output, string? metadataFile)
    {
        _temporaries.Clear();
        if (_temporaryOutput is not null)
        {
            _temporaries.Add(_temporaryOutput);
        }

        if (metadataFile is not null)
        {
            _temporaries.Add(metadataFile);
        }

        if (_request!.PassCount > 1 && _request.PassLogPath is { } passLog)
        {
            _temporaries.Add(passLog);
        }

        if (_options.Journal is { } journal)
        {
            _journalId ??= Guid.NewGuid().ToString("N");
            journal.Register(_journalId, output, _temporaries);
        }
    }

    private void DeleteTemporaries(bool keepPassLog)
    {
        string? passLog = _request?.PassLogPath;
        foreach (string path in _temporaries)
        {
            if (keepPassLog && path == passLog)
            {
                continue;
            }

            ExportOutputGuard.DeleteTemporary(path);
        }

        if (!keepPassLog && _options.Journal is { } journal && _journalId is not null)
        {
            journal.Remove(_journalId);
            _journalId = null;
        }
    }

    // Con _gate tomado. El Process se libera cuando nadie lo tiene prestado (Complete, Failure o una limpieza pendiente).
    private void ReleaseProcess()
    {
        if (_running is { } running)
        {
            running.Released = true;
            if (running.Users == 0)
            {
                running.Process.Dispose();
            }
        }

        _running = null;
        _pipe = null;
        ProcessId = null;
    }

    private RunningProcess? Lease()
    {
        lock (_gate)
        {
            if (_running is { } running)
            {
                running.Users++;
            }

            return _running;
        }
    }

    private void Return(RunningProcess? running)
    {
        if (running is null)
        {
            return;
        }

        lock (_gate)
        {
            running.Users--;
            if (running.Released && running.Users == 0)
            {
                running.Process.Dispose();
            }
        }
    }

    // Matar no es instantáneo: Windows termina el proceso cuando cada uno de sus hilos llega a ejecutar su salida. Con
    // ffmpeg.exe en BelowNormal y la CPU saturada por hilos Normal, esos hilos pasaban segundos sin turno (hasta que el
    // planificador sube a los hilos hambrientos, unos 4 s), la tubería no se rompía y el escritor seguía bloqueado en
    // WriteFile (1,6–6,3 s con VP9 en la CI). Por eso se sube primero a High: un proceso que ya muere no compite con
    // nadie. Después TerminateProcess directo, sin recorrer antes todos los procesos del sistema como hace
    // Kill(entireProcessTree), y la espera; el árbol solo se recorre si a pesar de todo no salió a tiempo.
    // Se llama sin _gate tomado. Devuelve si ffmpeg.exe salió.
    private static bool Kill(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            process.Kill();
            if (process.WaitForExit(KillWait))
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(TreeKillWait);
        }
        catch (InvalidOperationException)
        {
            // Sin proceso asociado: no hay nada que esperar.
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Acceso denegado mientras ya está terminando.
            return WaitQuietly(process, TreeKillWait);
        }
    }

    private bool Stop(Process process) => KillOverride is { } kill ? kill(process) : Kill(process);

    // Stop sin dejar escapar nada: si lanza, se anota (salvo con quiet) y vale lo que diga el proceso en ese momento.
    private bool TryStop(Process process, int? processId, bool quiet)
    {
        try
        {
            return Stop(process);
        }
#pragma warning disable CA1031 // Cualquier fallo al matar se trata como «no se sabe si salió»: se mira y se sigue.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!quiet)
            {
                Note($"Matar a ffmpeg.exe ({processId}) lanzó {ex.GetType().Name}: {ex.Message}");
            }

            return WaitQuietly(process, TimeSpan.Zero);
        }
    }

    // Las rutas que escribiría la pasada (DeleteTemporary borra además sus hermanos «-*»).
    private static List<string> PlannedTemporaries(ExportSinkRequest request, string output)
    {
        var paths = new List<string>();
        if (!request.IsAnalysisPass)
        {
            string temporary = ExportOutputGuard.TemporaryPath(output);
            paths.Add(temporary);
            paths.Add(temporary + ".ffmeta");
        }

        if (request.PassCount > 1 && !string.IsNullOrWhiteSpace(request.PassLogPath))
        {
            paths.Add(Path.GetFullPath(request.PassLogPath));
        }

        return paths;
    }

    // Una exportación cancelada cuyo ffmpeg.exe no salió todavía tiene sus temporales pendientes de borrar, y son los
    // mismos (TemporaryPath es determinista): si esta pasada arrancara, chocaría con el ffmpeg.exe viejo o perdería sus
    // archivos cuando la limpieza vieja los borre. Se espera un tope corto y, si sigue, se falla con un mensaje claro.
    private static void WaitForPendingCleanup(ExportSinkRequest request, string output, CancellationToken cancellationToken)
    {
        List<string> planned = PlannedTemporaries(request, output);
        PendingOwner? owner = PendingTemporaries.FindOwner(planned);
        if (owner is null)
        {
            return;
        }

        owner.Finished.Task.Wait((int)PendingCleanupWait.TotalMilliseconds, cancellationToken);
        owner = PendingTemporaries.FindOwner(planned);
        if (owner is not null)
        {
            throw new ExportException(ExportErrorCode.OutputNotWritable,
                $"Una exportación cancelada a {output} todavía se está cerrando (ffmpeg.exe {owner.ProcessId} no salió). " +
                "Espera unos segundos o elige otro nombre.") { Culprit = output };
        }
    }

    private static bool WaitQuietly(Process process, TimeSpan timeout)
    {
        try
        {
            return process.WaitForExit(timeout);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void ClosePipe(Stream? pipe)
    {
        try
        {
            pipe?.Dispose();
        }
        catch (IOException)
        {
        }
    }

    // Borra los temporales de un Abort. El registro solo se suelta si no quedó nada: si no, al reabrir Rodaje
    // ExportJournal.CleanupStale vuelve a intentarlo.
    // Solo borra las rutas que siguen siendo de owner (el Abort o su limpieza pendiente); las demás no las toca y tampoco
    // se reintentan al reabrir. Se borra fuera del cerrojo global: las rutas siguen a nombre de owner hasta Release.
    private bool DeleteFiles(IEnumerable<string> paths, string? journalId, int? processId, PendingOwner owner)
    {
        var left = new List<string>();
        List<string> owned = PendingTemporaries.Owned(owner, paths, out List<string> foreign);
        foreach (string path in owned)
        {
            if (ExportOutputGuard.DeleteTemporary(path))
            {
                left.Add(path);
            }
        }

        if (foreign.Count > 0)
        {
            Note($"La cancelación de ffmpeg.exe ({processId}) ya no es dueña de {string.Join(", ", foreign)}; no los borra.");
        }

        if (left.Count > 0)
        {
            Note($"No se pudieron borrar los temporales de ffmpeg.exe ({processId}): {string.Join(", ", left)}; " +
                "se reintenta al reabrir Rodaje.");
            return false;
        }

        if (journalId is not null && _options.Journal is { } journal)
        {
            journal.Remove(journalId);
        }

        return true;
    }

    // ffmpeg.exe no salió dentro de Abort: un hilo aparte reintenta matarlo y, cuando sale, cierra la tubería, borra los
    // temporales y devuelve el proceso prestado (que entonces se libera).
    // Mientras tanto, sus rutas siguen a nombre de owner (las reclamó Abort) en PendingTemporaries: Start no reusa la
    // salida y la limpieza solo borra lo que sigue siendo suyo. Desde aquí, soltarlas es cosa de la limpieza.
    private void DeferCleanup(RunningProcess running, Stream? pipe, string[] temporaries, string? journalId, int? processId, long start,
        PendingOwner owner)
    {
        // Si StartNew lanza, no hay hilo de limpieza: Abort sigue siendo el responsable de soltar las rutas y el proceso.
        Task cleanup = Task.Factory.StartNew(() =>
        {
            try
            {
                Process process = running.Process;
                bool quiet = false;
                while (!TryStop(process, processId, quiet))
                {
                    // Un fallo al matar se anota una vez; los reintentos cada 2 s no llenan el diagnóstico.
                    quiet = true;
                    if (WaitQuietly(process, CleanupRetry))
                    {
                        break;
                    }
                }

                ClosePipe(pipe);
                if (DeleteFiles(temporaries, journalId, processId, owner))
                {
                    Note($"ffmpeg.exe ({processId}) salió a los {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms de cancelar; " +
                        "temporales borrados.");
                }
            }
#pragma warning disable CA1031 // Hilo de fondo: una excepción sin capturar se perdería; se anota.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Note($"La limpieza pendiente de ffmpeg.exe ({processId}) falló: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                PendingTemporaries.Release(owner);
                Return(running);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        lock (_gate)
        {
            _pendingCleanup = cleanup;
        }
    }

    private void Note(string message)
    {
        lock (_diagnostics)
        {
            _diagnostics.Add(message);
        }

        Trace.WriteLine($"Rodaje: {message}");
    }

    /// <summary>
    /// Un Abort (y, si ffmpeg.exe no sale a tiempo, su limpieza pendiente), dueño de sus temporales en
    /// <see cref="PendingTemporaries"/> hasta que los borra.
    /// </summary>
    private sealed class PendingOwner(int? processId)
    {
        public int? ProcessId { get; } = processId;

        /// <summary>Se completa cuando la limpieza soltó sus rutas.</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Registro de todo el proceso con las rutas de los Abort en curso y las limpiezas pendientes (de cualquier sink) y su
    /// dueño. Bajo el cerrojo solo se consulta y se anota; los archivos se borran fuera, mientras siguen a nombre del
    /// dueño, así que un Start que pregunte entonces encuentra al dueño y espera a <see cref="PendingOwner.Finished"/>.
    /// </summary>
    private static class PendingTemporaries
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<string, PendingOwner> Owners = new(StringComparer.OrdinalIgnoreCase);

        public static void Claim(PendingOwner owner, IEnumerable<string> paths)
        {
            lock (Gate)
            {
                foreach (string path in paths)
                {
                    Owners.TryAdd(Path.GetFullPath(path), owner);
                }
            }
        }

        public static PendingOwner? FindOwner(IReadOnlyCollection<string> planned)
        {
            lock (Gate)
            {
                foreach ((string pending, PendingOwner owner) in Owners)
                {
                    if (planned.Any(path => Overlaps(path, pending)))
                    {
                        return owner;
                    }
                }

                return null;
            }
        }

        // Las rutas que siguen siendo de owner; en foreign, las que no. No borra nada: eso va fuera del cerrojo.
        public static List<string> Owned(PendingOwner owner, IEnumerable<string> paths, out List<string> foreign)
        {
            var owned = new List<string>();
            foreign = [];
            lock (Gate)
            {
                foreach (string path in paths)
                {
                    if (Owners.TryGetValue(Path.GetFullPath(path), out PendingOwner? current) && current == owner)
                    {
                        owned.Add(path);
                    }
                    else
                    {
                        foreign.Add(path);
                    }
                }
            }

            return owned;
        }

        public static void Release(PendingOwner owner)
        {
            lock (Gate)
            {
                foreach (string path in Owners.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToList())
                {
                    Owners.Remove(path);
                }
            }

            owner.Finished.TrySetResult();
        }

        // La misma ruta, o una que DeleteTemporary borraría como hermana («-*») o que se le cuelga (.ffmeta).
        private static bool Overlaps(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || Extends(a, b) || Extends(b, a);

        private static bool Extends(string path, string prefix) =>
            path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path[prefix.Length] is '-' or '.';
    }

    /// <summary>El <c>ffmpeg.exe</c> de una pasada y cuántos lo usan; los campos se tocan con <c>_gate</c> tomado.</summary>
    private sealed class RunningProcess(Process process)
    {
        public Process Process { get; } = process;

        public int Users { get; set; }

        public bool Released { get; set; }
    }
}

/// <summary>La línea de comandos de <see cref="FfmpegPipeSink"/>; los ajustes del codificador ya vienen armados en la petición.</summary>
internal static class FfmpegPipeArguments
{
    public static IReadOnlyList<string> Build(ExportSinkRequest request, string? temporaryOutput, string? metadataFile,
        IReadOnlyList<string>? subtitleFiles = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "0.5", "-y",
        };
        int inputs = 0;
        int videoInput = -1;
        int audioInput = -1;
        int metadataInput = -1;
        int thumbnailInput = -1;
        if (request.VideoCopy is { } copy)
        {
            args.AddRange(["-ss", copy.SeekSeconds.ToString("0.######", CultureInfo.InvariantCulture), "-i", copy.SourcePath]);
            videoInput = inputs++;
        }
        else if (request.HasVideo)
        {
            ExportVideoFormat video = request.Video!;
            args.AddRange([
                "-f", "rawvideo", "-pix_fmt", video.PixelFormat.FfmpegName(),
                "-s", string.Create(CultureInfo.InvariantCulture, $"{video.Width}x{video.Height}"),
                "-framerate", Rate(video),
            ]);
            if (!video.PixelFormat.IsRgb())
            {
                // En la entrada para que los cuadros lleven el color; en la salida para que lo escriban todos los codificadores.
                args.AddRange(video.Color.FfmpegArguments());
            }

            args.AddRange(["-i", "pipe:0"]);
            videoInput = inputs++;
        }

        if (request.HasAudio && !request.IsAnalysisPass)
        {
            args.AddRange(["-i", request.AudioPath!]);
            audioInput = inputs++;
        }

        if (metadataFile is not null)
        {
            args.AddRange(["-f", "ffmetadata", "-i", metadataFile]);
            metadataInput = inputs++;
        }

        bool thumbnail = !request.IsAnalysisPass && request.HasVideo && request.Metadata.ThumbnailPath is { } thumb
            && File.Exists(thumb) && AllowsAttachedPicture(request.Container);
        if (thumbnail)
        {
            args.AddRange(["-i", request.Metadata.ThumbnailPath!]);
            thumbnailInput = inputs++;
        }

        int subtitleInput = inputs;
        IReadOnlyList<string> subtitles = request.IsAnalysisPass ? [] : subtitleFiles ?? [];
        foreach (string file in subtitles)
        {
            args.AddRange(["-f", "srt", "-i", file]);
            inputs++;
        }

        if (videoInput >= 0)
        {
            args.AddRange(["-map", request.VideoCopy is { } copied
                ? string.Create(CultureInfo.InvariantCulture, $"{videoInput}:{copied.StreamIndex}")
                : $"{videoInput}:v:0"]);
        }

        if (audioInput >= 0)
        {
            args.AddRange(["-map", $"{audioInput}:a:0"]);
        }

        if (thumbnailInput >= 0)
        {
            args.AddRange(["-map", $"{thumbnailInput}:v:0"]);
        }

        for (int i = 0; i < subtitles.Count; i++)
        {
            args.AddRange(["-map", string.Create(CultureInfo.InvariantCulture, $"{subtitleInput + i}:s:0")]);
        }

        args.AddRange(["-map_metadata", metadataInput >= 0 ? metadataInput.ToString(CultureInfo.InvariantCulture) : "-1"]);
        args.AddRange(["-map_chapters", metadataInput >= 0 ? metadataInput.ToString(CultureInfo.InvariantCulture) : "-1"]);
        if (request.VideoCopy is { } streamCopy)
        {
            // Sin -copypriorss el keyframe, que queda a un microsegundo antes de -ss, se descartaría.
            args.AddRange(["-c:v:0", "copy", "-copypriorss", "1"]);
            if (streamCopy.BitstreamFilters.Count > 0)
            {
                args.AddRange(["-bsf:v:0", string.Join(',', streamCopy.BitstreamFilters)]);
            }

            args.AddRange(["-frames:v:0", request.FrameCount.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(request.VideoArguments);
        }
        else if (request.HasVideo)
        {
            ExportVideoFormat video = request.Video!;
            args.AddRange(["-c:v:0", request.VideoEncoder!.Id]);
            args.AddRange(request.VideoEncoder.BaseArguments);
            args.AddRange(request.VideoArguments);
            if (request.VideoFilters.Count > 0)
            {
                args.AddRange(["-filter:v:0", string.Join(',', request.VideoFilters)]);
            }
            else
            {
                args.AddRange(["-pix_fmt:v:0", video.PixelFormat.FfmpegName()]);
            }

            if (!video.PixelFormat.IsRgb())
            {
                args.AddRange(video.Color.FfmpegArguments());
            }

            if (request.EncodedFrameCount is null)
            {
                // Con cuadros tejidos no: el tope cortaría el audio al terminar el video (los filtros ya dan la cuenta).
                args.AddRange(["-frames:v:0", request.FrameCount.ToString(CultureInfo.InvariantCulture)]);
            }
        }

        if (thumbnailInput >= 0)
        {
            args.AddRange(["-c:v:1", "copy", "-disposition:v:1", "attached_pic"]);
        }

        if (audioInput >= 0)
        {
            args.AddRange(["-c:a:0", request.AudioEncoder!.Id]);
            args.AddRange(request.AudioEncoder.BaseArguments);
            args.AddRange(request.AudioArguments);
        }

        if (subtitles.Count > 0)
        {
            args.AddRange(["-c:s", SubtitleCodec(request.Container)]);
            List<ExportSubtitleTrack> tracks = [.. request.Metadata.Subtitles.Where(t => t.Cues.Count > 0)];
            for (int i = 0; i < subtitles.Count && i < tracks.Count; i++)
            {
                args.AddRange([string.Create(CultureInfo.InvariantCulture, $"-metadata:s:s:{i}"), $"title={tracks[i].Name}"]);
            }
        }

        if (!request.IsAnalysisPass)
        {
            AddMetadata(args, request.Metadata);
        }

        if (request.PassCount > 1)
        {
            args.AddRange(["-pass", request.Pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", request.PassLogPath!]);
        }

        if (request.IsAnalysisPass)
        {
            args.AddRange(["-an", "-f", "null", "NUL"]);
            return args;
        }

        args.AddRange(["-f", request.Container.Muxer()]);
        if (request.Container == ExportContainer.MpegTs)
        {
            // Sin el retardo de 1,4 s que MPEG-TS añade por defecto: el primer cuadro en 0, como en los demás contenedores.
            args.AddRange(["-muxdelay", "0", "-muxpreload", "0"]);
        }

        if (request.Container == ExportContainer.ImageSequence)
        {
            Directory.CreateDirectory(temporaryOutput!);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(request.OutputPath));
            string extension = ImageExtension(request.VideoEncoder!.Codec);
            args.AddRange(["-start_number", "0", Path.Combine(temporaryOutput!, $"{name}_%06d.{extension}")]);
        }
        else
        {
            args.Add(temporaryOutput!);
        }

        return args;
    }

    public static bool AllowsChapters(ExportContainer container) =>
        container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.Mkv or ExportContainer.WebM or ExportContainer.M4a
            or ExportContainer.Ogg;

    /// <summary>Contenedores con subtítulos que se activan en el reproductor.</summary>
    public static bool AllowsSoftSubtitles(ExportContainer container) =>
        container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.Mkv or ExportContainer.WebM;

    public static string SubtitleCodec(ExportContainer container) => container switch
    {
        ExportContainer.Mkv => "srt",
        ExportContainer.WebM => "webvtt",
        _ => "mov_text",
    };

    public static bool AllowsAttachedPicture(ExportContainer container) =>
        container is ExportContainer.Mp4 or ExportContainer.Mov or ExportContainer.M4a;

    public static string ImageExtension(ExportCodec codec) => codec switch
    {
        ExportCodec.Png => "png",
        ExportCodec.Tiff => "tiff",
        ExportCodec.Mjpeg => "jpg",
        ExportCodec.Exr => "exr",
        _ => throw new ExportException(ExportErrorCode.InvalidSettings, $"{codec} no sirve para una secuencia de imágenes."),
    };

    /// <summary>Archivo <c>ffmetadata</c> con los capítulos (milisegundos).</summary>
    public static string MetadataFile(ExportMetadata metadata)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        foreach (ExportChapter chapter in metadata.Chapters.OrderBy(c => c.Start.Ticks))
        {
            long start = (long)Math.Round(chapter.Start.ToSeconds() * 1000);
            long end = Math.Max(start + 1, (long)Math.Round(chapter.End.ToSeconds() * 1000));
            text.Append(CultureInfo.InvariantCulture, $"[CHAPTER]\nTIMEBASE=1/1000\nSTART={start}\nEND={end}\ntitle={Escape(chapter.Title)}\n");
        }

        return text.ToString();
    }

    private static void AddMetadata(List<string> args, ExportMetadata metadata)
    {
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.AddRange(["-metadata", $"{key}={value.Replace("\r", "", StringComparison.Ordinal)}"]);
            }
        }

        Add("title", metadata.Title);
        Add("artist", metadata.Author);
        Add("author", metadata.Author);
        Add("comment", metadata.Comment);
        if (metadata.Date is { } date)
        {
            Add("creation_time", date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
            Add("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else
        {
            // Sin fecha, el archivo sale igual en cada exportación.
            args.AddRange(["-fflags", "+bitexact"]);
        }
    }

    private static string Rate(ExportVideoFormat video) => video.FrameRate.Den == 1
        ? video.FrameRate.Num.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{video.FrameRate.Num}/{video.FrameRate.Den}");

    private static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n')
            {
                text.Append('\\');
            }

            if (c != '\r')
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
