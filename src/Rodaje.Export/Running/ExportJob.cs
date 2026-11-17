using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.RenderCache;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.RenderCache;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Export;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Gpu.Vulkan;
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
    private long _cachedFrames;
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

    /// <summary>Cuadros de la salida que se leyeron del render en caché en lugar de componerse (D-060).</summary>
    public long CachedFrames => Volatile.Read(ref _cachedFrames);

    public long FramesWritten => Interlocked.Read(ref _written);

    /// <summary>El último cuadro de la salida reducido (hasta 320 px de ancho), cada medio segundo.</summary>
    public ExportThumbnail? Thumbnail => _thumbnail;

    /// <summary>Lo decidido antes de empezar; <c>null</c> si falló antes.</summary>
    public ExportPlan? Plan { get; private set; }

    public ExportPerformanceProfile? Profile { get; private set; }

    /// <summary>Si el video se copió del original sin recodificar y por qué (o por qué no); <c>null</c> sin video.</summary>
    public StreamCopyDecision? StreamCopy { get; private set; }

    /// <summary>El archivo escrito, al completar.</summary>
    public ExportSinkResult? Result { get; private set; }

    /// <summary>Lo que dijo ffprobe del archivo, si se verificó.</summary>
    public ExportReport? Report { get; private set; }

    /// <summary>La sonoridad medida antes de normalizar y la ganancia aplicada, en dB.</summary>
    public (LoudnessMeasurement Measured, double GainDb)? Loudness { get; private set; }

    /// <summary>MaxCLL y MaxFALL medidos en la pasada previa (<see cref="VideoExportSettings.MeasureLightLevels"/>); <c>null</c> sin pasada.</summary>
    public ContentLight? LightLevels { get; private set; }

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

    // x264 y x265 corren en el ffmpeg.exe GPL del complemento, otro proceso por la misma tubería; lo demás, en el LGPL.
    private Rodaje.Media.Proxies.FfmpegExecutable EncoderExecutable(ExportPlan plan) =>
        plan.VideoEncoder?.License != EncoderLicense.GplAddOn ? _options.Ffmpeg
        : _options.GplFfmpeg ?? throw new ExportException(ExportErrorCode.EncoderMissing,
            $"{plan.VideoEncoder.Id} necesita el complemento de codificadores GPL.") { Culprit = plan.VideoEncoder.Id };

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
            ExportPlan plan;
            using (Measure("Plan"))
            {
                plan = ExportPlan.Create(Request, encoders);
            }
            Plan = plan;
            StreamCopy = plan.Video is null ? null : StreamCopyPlanner.Decide(plan, token);
            ExportStreamCopy? copy = StreamCopy?.Copy;
            GpuDevice? gpu = null;
            IGpu? engine = null;
            ExportDevicePool.Lease? deviceLease = null;
            using (Measure("Device"))
            {
                if (plan.Video is not null && copy is null)
                {
                    engine = OpenGpu(out gpu, out deviceLease);
                }
            }
            try
            {
                Profile = ExportPerformanceProfile.For(Request.Settings.Performance, OperatingSystem.IsWindowsVersionAtLeast(7) ? gpu?.DedicatedVideoMemory ?? 0 : 0);
                Thread.CurrentThread.Priority = Profile.ThreadPriority;
                if (OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is not null && Profile.GpuPriority != 0)
                {
                    ExportGpuPriority.TrySet(gpu, Profile.GpuPriority);
                }
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(work);
                _options.Journal?.Register(journalId, Request.OutputPath, [work]);
                using IDisposable? yielded = plan.Video is null || copy is not null ? null : _options.Preview?.Yield(Profile);
                string? wave = plan.Audio is null ? null : MixAudio(plan, work, token);
                using var sink = new FfmpegPipeSink(EncoderExecutable(plan), new FfmpegPipeSinkOptions
                {
                    Priority = Profile.EncoderPriority,
                    Journal = _options.Journal,
                    SourcePaths = SourcePaths(),
                    EstimatedBytes = EstimateBytes(plan),
                });
                var request = new ExportSinkRequest(Request.OutputPath, plan.Settings.Container, plan.Video, plan.VideoEncoder,
                    copy is null ? VideoArguments(plan) : FfmpegArguments.Container(plan.Settings),
                    plan.Audio, wave, plan.AudioEncoder, plan.Arguments.Audio, plan.Video is null ? 0 : plan.FrameCount)
                {
                    Metadata = plan.Metadata,
                    PassCount = plan.Arguments.PassCount,
                    PassLogPath = Path.Combine(work, "pasadas"),
                    VideoFilters = plan.VideoFilters,
                    EncodedFrameCount = plan.Interlaced ? plan.EncodedFrameCount : null,
                    VideoCopy = copy,
                };
                Result = plan.Video is null || copy is not null ? EncodeAudio(sink, request, token) : EncodeVideo(plan, gpu, engine!, sink, request, token);
            }
            finally
            {
                _converter = null;
                if (gpu is null || !OperatingSystem.IsWindowsVersionAtLeast(7))
                {
                    engine?.Dispose();
                }
                else if (deviceLease is null)
                {
                    gpu?.Dispose();
                }
                else
                {
                    deviceLease.Dispose();
                }
            }

            if (plan.Settings.Video is { Projection: ExportProjection.Equirectangular } && plan.Video is not null)
            {
                SphericalMetadata.Inject(Request.OutputPath);
            }

            using (Measure("Verify"))
            {
                Verify(plan, token);
            }
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

    /// <summary>El motor de la exportación: D3D11 sobre el dispositivo de siempre en Windows; Vulkan en los demás (sin dispositivo D3D11, los
    /// decodificadores dan cuadros de CPU y el compositor los sube).</summary>
    private IGpu OpenGpu(out GpuDevice? gpu, out ExportDevicePool.Lease? lease)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7) && _options.CreateGpu is null)
        {
            gpu = CreateDevice(out lease);
            return D3D11Gpu.For(gpu);
        }

        gpu = null;
        lease = null;
        try
        {
            return _options.CreateGpu?.Invoke() ?? VulkanGpu.Create();
        }
        catch (Exception ex) when (ex is not ExportException)
        {
            throw new ExportException(ExportErrorCode.GpuFailed, $"No se pudo abrir la GPU para exportar: {ex.Message}", ex);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    private GpuDevice CreateDevice(out ExportDevicePool.Lease? lease)
    {
        lease = null;
        try
        {
            if (_options.DevicePool is { } devices)
            {
                lease = devices.Acquire();
                return lease.Device;
            }

            return _options.CreateDevice?.Invoke() ?? GpuDevice.Create();
        }
        catch (Exception ex) when (ex is not ExportException)
        {
            throw new ExportException(ExportErrorCode.GpuFailed, $"No se pudo abrir la GPU para exportar: {ex.Message}", ex);
        }
    }

    private ExportMeasurement Measure(string phase) => new(_options.Measure, phase);

    private IReadOnlyList<string> VideoArguments(ExportPlan plan) => _options.VideoThreads is { } threads
        ? [.. plan.Arguments.Video, "-threads:v:0", threads.ToString(CultureInfo.InvariantCulture)]
        : plan.Arguments.Video;

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

    private ExportSinkResult EncodeVideo(ExportPlan plan, GpuDevice? gpu, IGpu engine, FfmpegPipeSink sink, ExportSinkRequest request,
        CancellationToken token)
    {
        VideoExportSettings settings = plan.Settings.Video!;
        ITitleRasterizer titleResources;
        using (Measure("Titles"))
        {
            titleResources = CreateTitles(engine);
        }
        using ITitleRasterizer titles = titleResources;
        var options = new OfflineTimelineOptions
        {
            Titles = titles,
            ProjectDirectory = Request.ProjectDirectory,
            Content = _options.Content,
            MissingContent = Request.ContinueWithoutMissing ? MissingContentPolicy.ContinueWithout : MissingContentPolicy.Fail,
            LookaheadFrames = Profile!.LookaheadFrames,
            MaxDecodersPerMedia = Profile.MaxDecodersPerMedia,
        };
        OfflineTimeline opened;
        using (Measure("Timeline"))
        {
            opened = OfflineTimeline.Open(plan.Request.Project, Request.SequenceId, gpu, options);
        }
        using OfflineTimeline timeline = opened;
        Time end = plan.End > timeline.Duration ? timeline.Duration : plan.End;
        using (Measure("PrepareDecoders"))
        {
            timeline.Prepare(plan.Start, end, token);
        }
        IReadOnlyList<GeneratedContent> generated = timeline.GeneratedContents(plan.Start, end);
        CheckFonts(titles, generated);

        ExportVideoFormat format = plan.Video!;
        bool alpha = format.PixelFormat.HasAlpha();
        bool blend = plan.ConvertsRate && settings.FrameRateConversion == FrameRateConversion.Blend;
        FrameSource source;
        using (Measure("Compositor"))
        {
            source = new FrameSource(this, plan, timeline, gpu, engine, titles, alpha, blend);
        }
        using var frames = source;
        if (settings.MeasureLightLevels && format.Color.Transfer == ExportTransfer.Pq && timeline.ColorSpace != Rodaje.Core.Model.SequenceColorSpace.Sdr709)
        {
            ContentLight measured = MeasureLight(plan, frames, engine, timeline, token);
            LightLevels = measured;
            format = new ExportVideoFormat(format.Width, format.Height, format.FrameRate, format.PixelFormat,
                format.Color with { MaxCll = measured.MaxCll, MaxFall = measured.MaxFall });
            request = request with { Video = format };
        }

        ExportSourceRegion? region = settings.Crop is { } crop ? new ExportSourceRegion(crop.X, crop.Y, crop.Width, crop.Height) : null;
        ExportFrameConverter conversion;
        using (Measure("Converter"))
        {
            conversion = OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is not null
                ? new ExportFrameConverter(gpu, timeline.Width, timeline.Height, format, settings.Fit, settings.ScaleFilter, region: region,
                    source: timeline.ColorSpace)
                : new ExportFrameConverter(engine, timeline.Width, timeline.Height, format, settings.Fit, settings.ScaleFilter, region: region,
                    source: timeline.ColorSpace);
        }
        using var converter = conversion;
        _converter = converter;
        void Write(in ExportVideoFrame frame)
        {
            Request.Pause?.Wait(token);
            using (Measure("PipeWrite"))
            {
                sink.WriteVideo(in frame, token);
            }
            Interlocked.Increment(ref _written);
        }

        ExportFrameHandler write = Write;
        _passCount = request.PassCount;
        ExportSinkResult? result = null;
        for (int pass = 1; pass <= request.PassCount; pass++)
        {
            _pass = pass;
            Interlocked.Exchange(ref _written, 0);
            Interlocked.Exchange(ref _cachedFrames, 0);
            lock (_gate)
            {
                _rate.Clear();
            }

            using (Measure("StartEncoder"))
            {
                sink.Start(request with { Pass = pass }, token);
            }
            Publish(ExportJobState.Encoding, 0, force: true);
            for (long i = 0; i < plan.FrameCount; i++)
            {
                token.ThrowIfCancellationRequested();
                Request.Pause?.Wait(token);
                _frame = i;
                if (converter.Pending == converter.Capacity)
                {
                    using (Measure("ReadbackAndPipe"))
                    {
                        converter.TryRead(write);
                    }
                }

                using (Measure(i == 0 ? "FirstFrame" : "RemainingFrames"))
                {
                    frames.Submit(converter, i, token);
                }
                Publish(ExportJobState.Encoding, Volatile.Read(ref _written));
            }

            bool Read()
            {
                using var measurement = Measure("ReadbackAndPipe");
                return converter.TryRead(write);
            }
            while (Read())
            {
                Publish(ExportJobState.Encoding, Volatile.Read(ref _written));
            }

            _frame = -1;
            if (pass == request.PassCount)
            {
                Publish(ExportJobState.Finalizing, Volatile.Read(ref _written), force: true);
            }

            using (Measure("FinishEncoder"))
            {
                result = sink.Complete(token);
            }
        }

        _options.Measure?.Invoke("GpuReadWait", converter.ReadWait);
        return result!;
    }

    /// <summary>Compone cada cuadro de la salida y mide max(R, G, B) en la composición lineal entera (sin recorte ni escala).</summary>
    private ContentLight MeasureLight(ExportPlan plan, FrameSource frames, IGpu gpu, OfflineTimeline timeline, CancellationToken token)
    {
        Publish(ExportJobState.MeasuringLight, 0, force: true);
        using var meter = new ContentLightMeter(gpu, timeline.Width, timeline.Height);
        for (long i = 0; i < plan.FrameCount; i++)
        {
            token.ThrowIfCancellationRequested();
            _frame = i;
            frames.Measure(meter, i, token);
            Publish(ExportJobState.MeasuringLight, i + 1);
        }

        _frame = -1;
        return meter.Result;
    }

    private ITitleRasterizer CreateTitles(IGpu gpu)
    {
        var titles = TitleRasterizers.Create(gpu);
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
    private void CheckFonts(ITitleRasterizer titles, IReadOnlyList<GeneratedContent> contents)
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

        ExportReport report = _options.VerifyMovPackets ? verifier.InspectMovPackets(Result.OutputPath, token) : verifier.Inspect(Result.OutputPath, token);
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
    /// <summary>Bytes que ocupará el archivo, para comprobar el disco; <c>null</c> si la tasa no se sabe (calidad constante).</summary>
    /// <remarks>
    /// En double: con un tamaño objetivo y un rango de pocos milisegundos la tasa pasaba de <see cref="int.MaxValue"/>, la
    /// suma con el audio se envolvía en negativo y el disco no se comprobaba.
    /// </remarks>
    internal static long? EstimateBytes(ExportPlan plan)
    {
        double seconds = (plan.End - plan.Start).ToSeconds();
        RateControl? video = plan.Settings.Video?.RateControl;
        double? videoKbps = video switch
        {
            null => 0,
            { TargetSizeBytes: { } bytes } => bytes * 8.0 / 1000 / Math.Max(seconds, 0.001),
            { Mode: RateControlMode.Cbr or RateControlMode.Vbr or RateControlMode.Abr, BitrateKbps: { } kbps } => kbps,
            _ => null,
        };
        int audioKbps = plan.Settings.Audio?.RateControl.BitrateKbps ?? 0;
        return videoKbps is { } v ? (long)Math.Min((v + audioKbps) * 125 * seconds, long.MaxValue) : null;
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
    /// Compone el cuadro <c>i</c> de la salida y lo encola en el conversor: el del fragmento en caché si está disponible y
    /// es equivalente (D-060); si no, el de la secuencia o el instante de la conversión de tasa.
    /// </summary>
    private sealed class FrameSource : IDisposable
    {
        private readonly ExportJob _job;
        private readonly ExportPlan _plan;
        private readonly OfflineTimeline _timeline;
        private readonly GpuDevice? _gpu;
        private readonly Compositor[] _compositors;
        private readonly long[] _held = [-1, -1];
        private readonly Time _sequenceFrame;
        private readonly bool _alpha;
        private readonly IRenderCacheStore? _renderCache;
        private readonly RecipeBuilder? _recipes;
        private readonly bool _canUseCache;
        private RenderCacheHit? _previousHit;
        private VideoDecoder? _activeDecoder;
        private string? _activeDecoderMoviePath;

        public FrameSource(ExportJob job, ExportPlan plan, OfflineTimeline timeline, GpuDevice? gpu, IGpu engine, ITitleRasterizer? titles, bool alpha,
            bool blend)
        {
            _job = job;
            _plan = plan;
            _timeline = timeline;
            _gpu = gpu;
            _alpha = alpha;
            _sequenceFrame = Time.FromFrames(1, timeline.FrameRate);
            _compositors = new Compositor[blend ? 2 : 1];
            for (int c = 0; c < _compositors.Length; c++)
            {
                _compositors[c] = OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is not null
                    ? new Compositor(gpu, timeline.Width, timeline.Height) { Titles = titles }
                    : new Compositor(engine, timeline.Width, timeline.Height) { Titles = titles };
                _compositors[c].ColorSpace = timeline.ColorSpace;
                _compositors[c].Resources = job._options.Content;
                _compositors[c].TransparentBackground = alpha;
            }

            if (_plan.Settings.UseRenderCache)
            {
                // Las huellas tienen que ser las mismas con las que la app calculó los fragmentos: sin quien las dé, no hay caché.
                _renderCache = job._options.RenderCache;
                _recipes = _renderCache is null ? null : job._options.RecipeBuilder?.Invoke(plan.Request);
            }

            _canUseCache = _plan.Settings.UseRenderCache
                && !_plan.ConvertsRate
                && !_plan.Interlaced
                && !_alpha
                && !(_plan.Settings.Video?.Alpha ?? false)
                && _plan.Settings.Video is { Crop: null, BitDepth: <= 10 }
                && !_plan.Video!.Color.FullRange
                && _plan.Video.Width == _timeline.Width
                && _plan.Video.Height == _timeline.Height
                && IsMatchingColorSpace(_plan.Video.Color, _timeline.ColorSpace)
                && _renderCache is not null
                && _recipes is not null;
        }

        public void Submit(ExportFrameConverter converter, long index, CancellationToken token)
        {
            if (_canUseCache)
            {
                long sequenceFrame = _plan.FirstFrame + index;
                if (sequenceFrame >= 0 && sequenceFrame < _timeline.FrameCount)
                {
                    RenderCacheKey? key = _recipes!.KeyAt(sequenceFrame, token);
                    if (key is { } validKey && _renderCache!.TryGet(validKey, _previousHit, out RenderCacheHit hit))
                    {
                        if (!hit.Fragment.Metadata.PlaybackOnly
                            && hit.Fragment.Metadata.Width == _plan.Video!.Width
                            && hit.Fragment.Metadata.Height == _plan.Video.Height
                            && hit.Fragment.Metadata.ColorSpace == _timeline.ColorSpace)
                        {
                            if (TrySubmitCached(converter, hit, index, sequenceFrame, token))
                            {
                                return;
                            }
                        }
                    }
                }
            }

            CloseActiveDecoder();

            (Compositor a, Compositor? b, float weight) = Compose(index, token);
            using var measurement = _job.Measure("ConvertSubmit");
            if (b is null)
            {
                converter.Submit(a.Output!, index);
            }
            else
            {
                converter.SubmitBlend(a.Output!, b.Output!, weight, index);
            }
        }

        private bool TrySubmitCached(ExportFrameConverter converter, RenderCacheHit hit, long index, long sequenceFrame, CancellationToken token)
        {
            DecodedFrame? decoded = null;
            try
            {
                if (_activeDecoder is not null && !string.Equals(_activeDecoderMoviePath, hit.Fragment.MoviePath, StringComparison.OrdinalIgnoreCase))
                {
                    CloseActiveDecoder();
                }

                if (_activeDecoder is null)
                {
                    _activeDecoder = VideoDecoder.Open(hit.Fragment.MoviePath, new VideoDecoderOptions
                    {
                        Device = _gpu,
                        AllowHardwareDecoding = false,
                    });
                    _activeDecoderMoviePath = hit.Fragment.MoviePath;
                }

                decoded = _activeDecoder.GetFrame(hit.FrameIndex, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _renderCache!.Forget(hit.Fragment);
                CloseActiveDecoder();
                decoded = null;
            }

            if (decoded is null)
            {
                _renderCache!.Forget(hit.Fragment);
                CloseActiveDecoder();
                return false;
            }

            _previousHit = hit;
            _held[0] = -1;
            Interlocked.Increment(ref _job._cachedFrames);

            var layer = new LayerFrame(0, "cache", decoded!, LayerTransform.Identity, new Rational(1, 1), 0);
            using var compFrame = new CompositionFrame(Time.FromFrames(sequenceFrame, _timeline.FrameRate), _timeline.Width, _timeline.Height, [layer]);
            _compositors[0].RenderStrict(compFrame, Geometry);
            converter.Submit(_compositors[0].Output!, index);
            return true;
        }

        private void CloseActiveDecoder()
        {
            if (_activeDecoder is not null)
            {
                _activeDecoder.Dispose();
                _activeDecoder = null;
                _activeDecoderMoviePath = null;
            }
        }

        private static bool IsMatchingColorSpace(ExportColor color, SequenceColorSpace sequenceColor) => sequenceColor switch
        {
            SequenceColorSpace.Sdr709 => color.Transfer == ExportTransfer.Bt709 && !color.IsHdr && color.Matrix is ExportMatrix.Bt709 or ExportMatrix.Bt601,
            SequenceColorSpace.HdrPq => color.Transfer == ExportTransfer.Pq,
            SequenceColorSpace.HdrHlg => color.Transfer == ExportTransfer.Hlg,
            _ => false,
        };

        /// <summary>Compone el cuadro <c>index</c> y lo mide; con mezcla de dos cuadros se miden los dos (cota superior de la mezcla).</summary>
        public void Measure(ContentLightMeter meter, long index, CancellationToken token)
        {
            (Compositor a, Compositor? b, _) = Compose(index, token);
            meter.Measure(a.Output!);
            if (b is not null)
            {
                meter.Measure(b.Output!);
            }
        }

        private (Compositor First, Compositor? Second, float Weight) Compose(long index, CancellationToken token)
        {
            Time time = Time.FromFrames(_plan.FirstFrame + index, _plan.OutputRate);
            if (!_plan.ConvertsRate)
            {
                CompositionFrame frame;
                using (_job.Measure("Decode"))
                {
                    frame = _timeline.Render(Math.Min(_plan.FirstFrame + index, _timeline.FrameCount - 1), token);
                }
                Compose(_compositors[0], frame);
                return (_compositors[0], null, 0);
            }

            Time last = _timeline.Duration - Time.FromTicks(1);
            if (time > last)
            {
                time = last;
            }

            if (_compositors.Length == 1)
            {
                Compose(_compositors[0], _timeline.RenderAt(time, token));
                return (_compositors[0], null, 0);
            }

            long first = time.ToFrames(_timeline.FrameRate);
            double weight = (time - Time.FromFrames(first, _timeline.FrameRate)).ToSeconds() / _sequenceFrame.ToSeconds();
            if (weight < 1e-6 || first + 1 >= _timeline.FrameCount)
            {
                return (Hold(first, keep: -1, token), null, 0);
            }

            Compositor a = Hold(first, keep: first + 1, token);
            Compositor b = Hold(first + 1, keep: first, token);
            return (a, b, (float)Math.Clamp(weight, 0, 1));
        }

        public void Dispose()
        {
            CloseActiveDecoder();
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
            using var measurement = _job.Measure("Compose");
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
