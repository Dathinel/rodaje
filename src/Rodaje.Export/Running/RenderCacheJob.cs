using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;
using Rodaje.Render;

namespace Rodaje.Export.Running;

/// <summary>Calcula una sola vez hasta dos segundos de una copia de la secuencia y publica sus huellas vigentes.</summary>
public sealed class RenderCacheJob
{
    private readonly ExportRunner _runner;
    private readonly IRenderCacheStore _store;
    private readonly Func<IReadOnlyList<RenderCacheKey>, bool> _stillCurrent;
    private readonly Project _project;
    private readonly Sequence _sequence;
    private readonly IReadOnlyList<RenderCacheKey> _keys;
    private readonly long _firstFrame;
    private readonly int _frameCount;
    private int _started;
    private ExportJob? _export;

    public RenderCacheJob(Project project, string sequenceId, long firstFrame, int frameCount,
        IReadOnlyList<RenderCacheKey> keys, IRenderCacheStore store, ExportRunnerOptions options,
        Func<IReadOnlyList<RenderCacheKey>, bool> stillCurrent, ExportPauseToken? pause = null, string? projectDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stillCurrent);
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        _sequence = project.FindSequence(sequenceId) ?? throw new ArgumentException("La secuencia no existe.", nameof(sequenceId));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameCount, Math.Ceiling(2 * _sequence.FrameRate.ToDouble()));
        if (keys.Count != frameCount)
        {
            throw new ArgumentException("Cada cuadro necesita su huella, en orden.", nameof(keys));
        }

        long end = checked(firstFrame + frameCount);
        if (Time.FromFrames(end - 1, _sequence.FrameRate) >= _sequence.Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "El tramo se sale de la secuencia.");
        }

        _project = project;
        _store = store;
        _stillCurrent = stillCurrent;
        _keys = Array.AsReadOnly(keys.ToArray());
        _firstFrame = firstFrame;
        _frameCount = frameCount;
        Pause = pause ?? new ExportPauseToken();
        ProjectDirectory = projectDirectory;
        int threads = Math.Max(1, Environment.ProcessorCount / 2);
        _runner = new ExportRunner(options with
        {
            VideoThreads = Math.Min(options.VideoThreads ?? threads, threads),
            Verify = true,
            VerifyMovPackets = true,
            TemporaryDirectory = options.TemporaryDirectory ?? store.Directory,
        });
    }

    /// <summary>Lo que se codifica en cada fragmento: ProRes 422 HQ sin audio, a prioridad baja.</summary>
    public static ExportSettings Settings { get; } = ExportPresets.ProResMaster.Settings with
    {
        Audio = null,
        SmartRender = false,
        Performance = ExportPerformance.Background,
    };

    /// <summary>
    /// La huella de la cadena que produce un fragmento (<see cref="RecipeFormat.Chain"/>): shaders, preajuste del
    /// codificador y versión de FFmpeg. Si cambia cualquiera, los fragmentos viejos dejan de acertar.
    /// </summary>
    public static string Chain(string ffmpegVersion) =>
        string.Join('|', RenderChain.ShaderFingerprint, Settings.Video?.ToString() ?? "", Settings.Container, ffmpegVersion);

    public ExportPauseToken Pause { get; }

    public string? ProjectDirectory { get; }

    public ExportJob? Export => Volatile.Read(ref _export);

    /// <returns>El fragmento publicado, o nulo si sus huellas dejaron de estar vigentes.</returns>
    public async Task<RenderCacheFragment?> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("El fragmento ya se empezó a calcular.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string temporary = _store.CreateTemporaryPath();
        try
        {
            ExportSettings settings = Settings;
            var range = new ExportRange(Time.FromFrames(_firstFrame, _sequence.FrameRate),
                Time.FromFrames(_firstFrame + _frameCount, _sequence.FrameRate));
            var request = new ExportRequest(_project, _sequence.Id, temporary, settings, range)
            {
                Pause = Pause,
                ProjectDirectory = ProjectDirectory,
            };
            using ExportJob export = _runner.Start(request, [ExportEncoderCatalog.Find("prores_ks")!]);
            Volatile.Write(ref _export, export);
            using CancellationTokenRegistration registration = cancellationToken.Register(export.Cancel);
            await export.Completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (export.Progress.State == ExportJobState.Canceled)
            {
                throw new OperationCanceledException("Se canceló el cálculo del fragmento.", cancellationToken);
            }

            if (export.Failure is { } failure)
            {
                throw failure;
            }

            ExportReport report = export.Report ?? ExportVerifier.For(_runner.Options.Ffmpeg).InspectMovPackets(temporary, cancellationToken);
            if (report.Video is not { } video || video.FrameCount != _frameCount || video.Width != _sequence.Width
                || video.Height != _sequence.Height || video.FrameRate != _sequence.FrameRate || report.Audio is not null)
            {
                throw new ExportException(ExportErrorCode.EncoderFailed, "El fragmento no tiene los cuadros o el formato pedidos.");
            }

            var metadata = new RenderCacheFragmentMetadata(_sequence.Width, _sequence.Height, _sequence.FrameRate, _sequence.ColorSpace,
                "prores_hq", false, _keys, new FileInfo(temporary).Length);
            if (!_stillCurrent(_keys))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var measurement = new ExportMeasurement(_runner.Options.Measure, "Commit");
            return _store.Commit(temporary, metadata);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
