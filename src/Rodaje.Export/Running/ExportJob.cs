using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Export;
using Rodaje.Render.Text;

namespace Rodaje.Export.Running;

/// <summary>Una exportación en marcha o terminada. Todo lo que publica se puede leer desde cualquier hilo.</summary>
public sealed class ExportJob : IDisposable
{
    private static readonly Func<LayerFrame, SourceGeometry> Geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);
    private static readonly long PublishInterval = Stopwatch.Frequency / 10;
    private static readonly long RateWindow = Stopwatch.Frequency * 2;

    private readonly IReadOnlyList<EncoderSpec>? _encoders;
    private readonly ExportRunnerOptions _options;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly Queue<(long Ticks, long Done)> _rate = new();
    private readonly string _id = Guid.NewGuid().ToString("N");
    private ExportProgress _progress;
    private ExportException? _failure;
    private volatile ExportThumbnail? _thumbnail;
    private ExportFrameConverter? _converter;
    private long _lastPublish;
    private long _frame = -1;
    private long _written;
    private int _pass = 1;
    private int _passCount = 1;

    internal ExportJob(ExportRequest request, IReadOnlyList<EncoderSpec>? encoders, ExportRunnerOptions options)
    {
        Request = request;
        _encoders = encoders;
        _options = options;
        _progress = new ExportProgress(ExportJobState.Queued, 0, 0, 0, TimeSpan.Zero, null, 0);
    }

    public ExportRequest Request { get; }

    public ExportProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public ExportException? Failure => Volatile.Read(ref _failure);

    /// <summary>El último cuadro de la salida reducido (hasta 320 px de ancho), cada medio segundo.</summary>
    public ExportThumbnail? Thumbnail => _thumbnail;

    /// <summary>Lo decidido antes de empezar; <c>null</c> si falló antes.</summary>
    public ExportPlan? Plan { get; private set; }

    public ExportPerformanceProfile? Profile { get; private set; }

    /// <summary>El archivo escrito, al completar.</summary>
    public ExportSinkResult? Result { get; private set; }

    /// <summary>Lo que dijo ffprobe del archivo, si se verificó.</summary>
    public ExportReport? Report { get; private set; }

    /// <summary>La sonoridad medida antes de normalizar y la ganancia aplicada, en dB.</summary>
    public (LoudnessMeasurement Measured, double GainDb)? Loudness { get; private set; }

    /// <summary>Cambió el avance, el estado o la miniatura. Llega desde el hilo de la exportación.</summary>
    public event EventHandler? Changed;

    /// <summary>Termina (sin excepción) cuando el trabajo llega a un estado final.</summary>
    public Task Completion => _done.Task;

    /// <summary>Pide cancelar: el sink mata <c>ffmpeg.exe</c> y borra el temporal; el estado pasa a <see cref="ExportJobState.Canceled"/>.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (!_done.Task.IsCompleted)
        {
            Cancel();
        }
    }

    internal void Launch()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Rodaje: exportación" };
        thread.Start();
    }

    private void Run()
    {
        _clock.Start();
        CancellationToken token = _cancel.Token;
        string work = Path.Combine(_options.TemporaryDirectory ?? Path.GetTempPath(), $"rodaje-exportar-{_id}{ExportOutputGuard.TemporaryMarker}");
        string journalId = "exportar-" + _id;
        try
        {
            Publish(ExportJobState.Preparing, 0, force: true);
            IReadOnlyList<EncoderSpec> encoders = _encoders ?? _options.Encoders!.GetAsync(token).GetAwaiter().GetResult();
            ExportPlan plan = ExportPlan.Create(Request, encoders);
            Plan = plan;
            GpuDevice? gpu = plan.Video is null ? null : CreateDevice();
            try
            {
                Profile = ExportPerformanceProfile.For(Request.Settings.Performance, gpu?.DedicatedVideoMemory ?? 0);
                Thread.CurrentThread.Priority = Profile.ThreadPriority;
                if (gpu is not null && Profile.GpuPriority != 0)
                {
                    ExportGpuPriority.TrySet(gpu, Profile.GpuPriority);
                }
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(work);
                _options.Journal?.Register(journalId, Request.OutputPath, [work]);
                using IDisposable? yielded = plan.Video is null ? null : _options.Preview?.Yield(Profile);
                string? wave = plan.Audio is null ? null : MixAudio(plan, work, token);
                using var sink = new FfmpegPipeSink(_options.Ffmpeg, new FfmpegPipeSinkOptions
                {
                    Priority = Profile.EncoderPriority,
                    Journal = _options.Journal,
                    SourcePaths = SourcePaths(),
                    EstimatedBytes = EstimateBytes(plan),
                });
                var request = new ExportSinkRequest(Request.OutputPath, plan.Settings.Container, plan.Video, plan.VideoEncoder, plan.Arguments.Video,
                    plan.Audio, wave, plan.AudioEncoder, plan.Arguments.Audio, plan.Video is null ? 0 : plan.FrameCount)
                {
                    Metadata = plan.Metadata,
                    PassCount = plan.Arguments.PassCount,
                    PassLogPath = Path.Combine(work, "pasadas"),
                    VideoFilters = plan.VideoFilters,
                };
                Result = plan.Video is null ? EncodeAudio(sink, request, token) : EncodeVideo(plan, gpu!, sink, request, token);
            }
            finally
            {
                _converter = null;
                gpu?.Dispose();
            }

            Verify(plan, token);
            Publish(ExportJobState.Completed, Volatile.Read(ref _written), force: true);
        }
#pragma warning disable CA1031 // Todo error del hilo termina en un estado final con su código; nada se escapa del hilo.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ExportException error = ExportErrors.Map(ex, _frame >= 0 ? _frame : null);
            if (token.IsCancellationRequested || error.Code == ExportErrorCode.Canceled)
            {
                Publish(ExportJobState.Canceled, Volatile.Read(ref _written), force: true);
            }
            else
            {
                Volatile.Write(ref _failure, error);
                Publish(ExportJobState.Failed, Volatile.Read(ref _written), force: true);
            }
        }
        finally
        {
            ExportOutputGuard.DeleteTemporary(work);
            try
            {
                _options.Journal?.Remove(journalId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            _done.TrySetResult();
        }
    }

    private GpuDevice CreateDevice()
    {
        try
        {
            return _options.CreateDevice?.Invoke() ?? GpuDevice.Create();
        }
        catch (Exception ex) when (ex is not ExportException)
        {
            throw new ExportException(ExportErrorCode.GpuFailed, $"No se pudo abrir la GPU para exportar: {ex.Message}", ex);
        }
    }

    private string MixAudio(ExportPlan plan, string work, CancellationToken token)
    {
        using OfflineAudio audio = OfflineAudio.Open(Request.Project, Request.SequenceId, Request.ProjectDirectory);
        double gain = 0;
        if (plan.Settings.Audio!.Loudness is { } target)
        {
            Publish(ExportJobState.MeasuringLoudness, 0, force: true);
            LoudnessMeasurement measured = audio.MeasureLoudness(plan.Start, plan.End, new Fraction(this, ExportJobState.MeasuringLoudness), token);
            gain = measured.GainTo(target.Lufs, target.MaxTruePeakDbtp);
            Loudness = (measured, gain);
        }

        Publish(ExportJobState.MixingAudio, 0, force: true);
        string path = Path.Combine(work, "mezcla.wav");
        audio.WriteWave(path, plan.Start, plan.End, plan.Audio!, gain, new Fraction(this, ExportJobState.MixingAudio), token);
        return path;
    }

    private ExportSinkResult EncodeAudio(FfmpegPipeSink sink, ExportSinkRequest request, CancellationToken token)
    {
        Publish(ExportJobState.Encoding, 0, force: true);
        sink.Start(request, token);
        Publish(ExportJobState.Finalizing, 0, force: true);
        return sink.Complete(token);
    }

    private ExportSinkResult EncodeVideo(ExportPlan plan, GpuDevice gpu, FfmpegPipeSink sink, ExportSinkRequest request, CancellationToken token)
    {
        VideoExportSettings settings = plan.Settings.Video!;
        using TitleRasterizer titles = CreateTitles(gpu);
        var options = new OfflineTimelineOptions
        {
            Titles = titles,
            ProjectDirectory = Request.ProjectDirectory,
            Content = _options.Content,
            MissingContent = Request.ContinueWithoutMissing ? MissingContentPolicy.ContinueWithout : MissingContentPolicy.Fail,
            LookaheadFrames = Profile!.LookaheadFrames,
            MaxDecodersPerMedia = Profile.MaxDecodersPerMedia,
        };
        using OfflineTimeline timeline = OfflineTimeline.Open(Request.Project, Request.SequenceId, gpu, options);
        Time end = plan.End > timeline.Duration ? timeline.Duration : plan.End;
        timeline.Prepare(plan.Start, end, token);
        CheckFonts(titles, timeline.GeneratedContents(plan.Start, end));
        ExportVideoFormat format = plan.Video!;
        bool alpha = format.PixelFormat.HasAlpha();
        bool blend = plan.ConvertsRate && settings.FrameRateConversion == FrameRateConversion.Blend;
        using var frames = new FrameSource(this, plan, timeline, gpu, titles, alpha, blend);
        ExportSourceRegion? region = settings.Crop is { } crop ? new ExportSourceRegion(crop.X, crop.Y, crop.Width, crop.Height) : null;
        using var converter = new ExportFrameConverter(gpu, timeline.Width, timeline.Height, format, settings.Fit, settings.ScaleFilter, region: region);
        _converter = converter;
        void Write(in ExportVideoFrame frame)
        {
            sink.WriteVideo(in frame, token);
            Interlocked.Increment(ref _written);
        }

        ExportFrameHandler write = Write;
        _passCount = request.PassCount;
        ExportSinkResult? result = null;
        for (int pass = 1; pass <= request.PassCount; pass++)
        {
            _pass = pass;
            Interlocked.Exchange(ref _written, 0);
            lock (_gate)
            {
                _rate.Clear();
            }

            sink.Start(request with { Pass = pass }, token);
            Publish(ExportJobState.Encoding, 0, force: true);
            for (long i = 0; i < plan.FrameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                _frame = i;
                if (converter.Pending == converter.Capacity)
                {
                    converter.TryRead(write);
                }

                frames.Submit(converter, i, token);
                Publish(ExportJobState.Encoding, Volatile.Read(ref _written));
            }

            while (converter.TryRead(write))
            {
                Publish(ExportJobState.Encoding, Volatile.Read(ref _written));
            }

            _frame = -1;
            if (pass == request.PassCount)
            {
                Publish(ExportJobState.Finalizing, Volatile.Read(ref _written), force: true);
            }

            result = sink.Complete(token);
        }

        return result!;
    }

    private TitleRasterizer CreateTitles(GpuDevice gpu)
    {
        var titles = new TitleRasterizer(gpu);
        try
        {
            if (_options.FontFiles?.Invoke() is { Count: > 0 } fonts)
            {
                titles.RegisterFonts(fonts);
            }
        }
        catch
        {
            titles.Dispose();
            throw;
        }

        return titles;
    }

    /// <summary>
    /// La política de contenido que falta para las fuentes de los títulos, antes del primer cuadro: sin permiso para
    /// seguir, para con <see cref="ExportErrorCode.ContentMissing"/>; con él, los títulos salen con la fuente de respaldo.
    /// </summary>
    private void CheckFonts(TitleRasterizer titles, IReadOnlyList<GeneratedContent> contents)
    {
        if (contents.Count == 0 || Request.ContinueWithoutMissing)
        {
            return;
        }

        IReadOnlyList<MissingFont> missing = titles.FindMissingFonts(contents);
        if (missing.Count > 0)
        {
            string families = string.Join(", ", missing.Select(m => $"«{m.Family}»").Distinct(StringComparer.Ordinal));
            throw new ExportException(ExportErrorCode.ContentMissing,
                $"Falta{(missing.Count > 1 ? "n" : "")} fuente{(missing.Count > 1 ? "s" : "")} de títulos: {families}. Instala su paquete o exporta con la fuente de respaldo ({missing[0].UsedFamily}).")
            {
                Culprit = $"fuente «{missing[0].Family}»",
            };
        }
    }

    private void Verify(ExportPlan plan, CancellationToken token)
    {
        if (!_options.Verify || plan.Expectation() is not { } expected || Result is null)
        {
            return;
        }

        var verifier = ExportVerifier.For(_options.Ffmpeg);
        if (!File.Exists(verifier.FfprobePath))
        {
            // Una distribución sin ffprobe.exe exporta igual; la comprobación queda para cuando esté.
            return;
        }

        ExportReport report = verifier.Inspect(Result.OutputPath, token);
        Report = report;
        IReadOnlyList<string> problems = ExportVerifier.Check(report, expected);
        if (problems.Count > 0)
        {
            throw new ExportException(ExportErrorCode.EncoderFailed,
                $"El archivo se escribió, pero no es lo que se pidió: {string.Join(" ", problems)}") { Culprit = Result.OutputPath };
        }
    }

    private List<string> SourcePaths()
    {
        string folder = Request.ProjectDirectory ?? Environment.CurrentDirectory;
        return [.. Request.Project.Media.Where(m => !string.IsNullOrEmpty(m.Path)).Select(m => Path.GetFullPath(m.Path, folder))];
    }

    // Solo con tasa conocida; en calidad constante no se sabe y el sink comprueba el disco mientras escribe.
    private static long? EstimateBytes(ExportPlan plan)
    {
        double seconds = (plan.End - plan.Start).ToSeconds();
        RateControl? video = plan.Settings.Video?.RateControl;
        int? videoKbps = video switch
        {
            null => 0,
            { TargetSizeBytes: { } bytes } => (int)(bytes * 8 / 1000 / Math.Max(seconds, 0.001)),
            { Mode: RateControlMode.Cbr or RateControlMode.Vbr or RateControlMode.Abr, BitrateKbps: { } kbps } => kbps,
            _ => null,
        };
        int audioKbps = plan.Settings.Audio?.RateControl.BitrateKbps ?? 0;
        return videoKbps is { } v ? (long)((v + audioKbps) * 125L * seconds) : null;
    }

    private void Publish(ExportJobState state, long done, bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        ExportFrameConverter? converter = _converter;
        if (converter?.LatestThumbnail is { } thumbnail && !ReferenceEquals(thumbnail, _thumbnail))
        {
            _thumbnail = thumbnail;
        }

        lock (_gate)
        {
            if (!force && state == _progress.State && now - _lastPublish < PublishInterval)
            {
                return;
            }

            _lastPublish = now;
            long total = Plan?.FrameCount ?? 0;
            double fps = 0;
            TimeSpan? remaining = null;
            if (state == ExportJobState.Encoding)
            {
                _rate.Enqueue((now, done));
                while (_rate.Count > 1 && now - _rate.Peek().Ticks > RateWindow)
                {
                    _rate.Dequeue();
                }

                (long ticks, long first) = _rate.Peek();
                double seconds = (double)(now - ticks) / Stopwatch.Frequency;
                fps = seconds > 0.25 ? (done - first) / seconds : 0;
                if (fps > 0)
                {
                    long left = (total - done) + ((long)(_passCount - _pass) * total);
                    remaining = TimeSpan.FromSeconds(left / fps);
                }
            }

            _progress = new ExportProgress(state, done, total, fps, _clock.Elapsed, remaining,
                state == ExportJobState.Completed ? Result?.Bytes ?? 0 : 0, _pass, _passCount);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>El avance del audio como cuadros de la salida.</summary>
    private sealed class Fraction(ExportJob job, ExportJobState state) : IProgress<double>
    {
        public void Report(double value) => job.Publish(state, (long)(Math.Clamp(value, 0, 1) * (job.Plan?.FrameCount ?? 0)));
    }

    /// <summary>
    /// Compone el cuadro <c>i</c> de la salida y lo encola en el conversor: el de la secuencia si la tasa es la misma; si
    /// no, el instante más cercano o la mezcla de los dos vecinos (con dos compositores que guardan qué cuadro tienen).
    /// </summary>
    private sealed class FrameSource : IDisposable
    {
        private readonly ExportJob _job;
        private readonly ExportPlan _plan;
        private readonly OfflineTimeline _timeline;
        private readonly Compositor[] _compositors;
        private readonly long[] _held = [-1, -1];
        private readonly Time _sequenceFrame;

        public FrameSource(ExportJob job, ExportPlan plan, OfflineTimeline timeline, GpuDevice gpu, TitleRasterizer titles, bool alpha, bool blend)
        {
            _job = job;
            _plan = plan;
            _timeline = timeline;
            _sequenceFrame = Time.FromFrames(1, timeline.FrameRate);
            _compositors = new Compositor[blend ? 2 : 1];
            for (int c = 0; c < _compositors.Length; c++)
            {
                _compositors[c] = new Compositor(gpu, timeline.Width, timeline.Height)
                {
                    Resources = job._options.Content,
                    Titles = titles,
                    TransparentBackground = alpha,
                };
            }
        }

        public void Submit(ExportFrameConverter converter, long index, CancellationToken token)
        {
            Time time = Time.FromFrames(_plan.FirstFrame + index, _plan.OutputRate);
            if (!_plan.ConvertsRate)
            {
                Compose(_compositors[0], _timeline.Render(Math.Min(_plan.FirstFrame + index, _timeline.FrameCount - 1), token));
                converter.Submit(_compositors[0].Texture, index);
                return;
            }

            Time last = _timeline.Duration - Time.FromTicks(1);
            if (time > last)
            {
                time = last;
            }

            if (_compositors.Length == 1)
            {
                Compose(_compositors[0], _timeline.RenderAt(time, token));
                converter.Submit(_compositors[0].Texture, index);
                return;
            }

            long first = time.ToFrames(_timeline.FrameRate);
            double weight = (time - Time.FromFrames(first, _timeline.FrameRate)).ToSeconds() / _sequenceFrame.ToSeconds();
            if (weight < 1e-6 || first + 1 >= _timeline.FrameCount)
            {
                converter.Submit(Hold(first, keep: -1, token).Texture, index);
                return;
            }

            Compositor a = Hold(first, keep: first + 1, token);
            Compositor b = Hold(first + 1, keep: first, token);
            converter.SubmitBlend(a.Texture, b.Texture, (float)Math.Clamp(weight, 0, 1), index);
        }

        public void Dispose()
        {
            foreach (Compositor compositor in _compositors)
            {
                compositor.Dispose();
            }
        }

        // El compositor con el cuadro de la secuencia frame; si ninguno lo tiene, lo compone en el que no guarda keep.
        private Compositor Hold(long frame, long keep, CancellationToken token)
        {
            int slot = Array.IndexOf(_held, frame);
            if (slot >= 0)
            {
                return _compositors[slot];
            }

            slot = _held[0] == keep ? 1 : 0;
            _held[slot] = -1;
            Compose(_compositors[slot], _timeline.Render(frame, token));
            _held[slot] = frame;
            return _compositors[slot];
        }

        private void Compose(Compositor compositor, CompositionFrame frame)
        {
            using (frame)
            {
                CompositeResult result;
                try
                {
                    result = compositor.RenderStrict(frame, Geometry);
                }
                catch (ExportException ex) when (ex.Code == ExportErrorCode.EffectFailed && _job.Request.ContinueWithoutMissing)
                {
                    result = compositor.Render(frame, Geometry);
                }

                if (result.MissingContent > 0 && !_job.Request.ContinueWithoutMissing)
                {
                    throw new ExportException(ExportErrorCode.ContentMissing,
                        $"Falta contenido de paquetes en {result.MissingContent} capa(s): instala los paquetes o exporta sin él.");
                }
            }
        }
    }
}
